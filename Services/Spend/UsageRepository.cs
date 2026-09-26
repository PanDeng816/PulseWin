using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace PulseWin;

/// <summary>
/// 本地用量仓库(<c>Data\usage-history.sqlite</c>)。
///
/// **为什么存在**:用量统计直接读各客户端的库,而那些库不是档案库——
/// 客户端清理/卸载/换机之后,明细就没了,"全部"区间跟着缩水。这里留一份
/// 自己的逐日聚合,源库少一天,仓库的那一天还在。
///
/// **分级保留**(与 WhereMyTokens 同思路):天级永久(模型/项目/金额分布),
/// 小时级 35 天(只够 24 小时分布图用),去重键 95 天(防同一条记录被累加两次)。
///
/// **口径(必须记牢)**:统计 = 仓库聚合行 ∪ 未进仓库的明细行。一条明细要么
/// 已在仓库的聚合里(去重键命中),要么原样进明细路径——两边互斥,所以
/// 不重不漏;仓库里没有会话号,聚合行不参与"最近会话"和下钻。
/// 聚合行的金额是**建仓当时的牌价**算的,历史不随价目表更新重算。
/// </summary>
public static class UsageRepository
{
    private sealed record DayRow(string Day, string Agent, string Model, string? Provider,
        string? Project, long Input, long CacheWrite, long CacheRead, long Output,
        long Unclassified, long Unpriced, long Requests, double Cost,
        long Reasoning, long DurationMs, long DurationN, long TtftMs, long TtftN,
        long Tools, long Retries, long Failures);

    private sealed record HourRow(string Hour, string Agent, long Tokens, long Unclassified, long Requests, double Cost);

    private const int DayRetentionDays = 3650;   // 天级:十年,等于"永久"
    private const int HourRetentionDays = 35;    // 小时级:24 小时分布图只看近期
    private const int SeenRetentionDays = 95;    // 去重键:必须 ≥ 灌入窗口,否则旧记录会被重复累加
    private const int IngestWindowDays = 90;     // 首次建仓往回灌多少天(更早的不要:拖慢且用户看不到那么久远)

    /// <summary>
    /// 仓库的文件格式版本。**改了聚合列的含义就要加一**——旧行没法就地修补,只能整份重建
    /// (明细还在源库里,重灌一遍口径就对了)。
    /// v2:<c>project</c> 改存项目身份(完整路径),新增 <c>unpriced</c> 列(桶里算不出价的 token 数)。
    /// v3:新增遥测列(推理 token / 耗时 / 首字延迟 / 工具调用 / 重试 / 失败)。
    ///    **为什么必须存**:这些字段只在源库明细里,而明细一旦归档进仓库就没了——
    ///    不加列的话,界面上"推理占比/平均耗时"这类详细数据只有最近几天的有值,
    ///    历史全空(本机实测:不存的话推理/耗时全是 0)。
    /// </summary>
    private const int SchemaVersion = 3;

    private const string CreateTables = """
        CREATE TABLE IF NOT EXISTS agg_day(
            day TEXT, agent TEXT, model TEXT, provider TEXT, project TEXT,
            inp INTEGER, cache_w INTEGER, cache_r INTEGER, out INTEGER,
            unc INTEGER, unpriced INTEGER, requests INTEGER, cost REAL,
            reasoning INTEGER, duration_ms INTEGER, duration_n INTEGER,
            ttft_ms INTEGER, ttft_n INTEGER, tools INTEGER, retries INTEGER, failures INTEGER,
            PRIMARY KEY(day, agent, model, provider, project));
        CREATE TABLE IF NOT EXISTS agg_hour(
            hour TEXT, agent TEXT,
            inp INTEGER, cache_w INTEGER, cache_r INTEGER, out INTEGER,
            unc INTEGER, requests INTEGER, cost REAL,
            PRIMARY KEY(hour, agent));
        CREATE TABLE IF NOT EXISTS seen(key TEXT PRIMARY KEY, ts TEXT);
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
        """;

    private const string DropTables = """
        DROP TABLE IF EXISTS agg_day;
        DROP TABLE IF EXISTS agg_hour;
        DROP TABLE IF EXISTS seen;
        """;

    public static string Location => Path.Combine(
        SnapshotSource.DataPaths.RootDirectory, "usage-history.sqlite");

    public static bool Exists => File.Exists(Location);

    /// <summary>
    /// 增量把源库明细吸进仓库,返回 (聚合行, 未进仓库的明细行)。统计侧的输入 =
    /// 聚合行(仓库覆盖的全史) + 未进仓库的明细(比灌入窗口更早的老行),恰好
    /// 不重不漏:**一条明细要么已经在仓库的聚合里,要么原样走明细路径**,没有第三种。
    /// 同一条记录跨轮重复出现由 seen 表去重——它只会被累加一次。
    /// </summary>
    public static (IReadOnlyList<SpendEntry> Aggregated, IReadOnlyList<SpendEntry> Remaining) MergeIn(
        IReadOnlyList<SpendEntry> live)
    {
        var aggregated = new List<SpendEntry>();
        var remaining = new List<SpendEntry>();
        try
        {
            using var connection = Open();
            EnsureSchema(connection);
            var seen = LoadSeen(connection);
            DateTime cutoff = DateTime.Now.AddDays(-IngestWindowDays);
            DateTime maxSeen = DateTime.MinValue;

            using (var tx = connection.BeginTransaction())
            {
                int ingested = 0;
                foreach (var entry in live)
                {
                    // 比灌入窗口更早的老行不进仓库,原样保留在明细路径里
                    if (entry.Timestamp <= cutoff)
                    {
                        remaining.Add(entry);
                        continue;
                    }
                    // 已在仓库的行:统计侧由聚合行代表,明细不再参与(否则双算)
                    if (!seen.Add(DedupKey(entry))) continue;

                    UpsertDay(connection, entry);
                    UpsertHour(connection, entry);
                    InsertSeen(connection, DedupKey(entry), entry.Timestamp);
                    if (entry.Timestamp > maxSeen) maxSeen = entry.Timestamp;
                    ingested++;
                }

                WriteMeta(connection, "watermark",
                    (maxSeen == DateTime.MinValue ? DateTime.Now : maxSeen).ToString("O", CultureInfo.InvariantCulture));
                PruneIfDue(connection, DateTime.Now);
                tx.Commit();
                if (ingested > 0)
                    Diagnostics.Note($"用量仓库:新增 {ingested} 条明细,覆盖至 {maxSeen:MM-dd HH:mm}");
            }

            aggregated.AddRange(LoadDayRows(connection));
            aggregated.AddRange(LoadHourRows(connection));
        }
        catch (Exception ex)
        {
            // 仓库坏了不致命:退回"只用源库明细"的旧口径,把原因说出来
            Diagnostics.Note("用量仓库读写失败,本次统计只用源库明细", ex);
            aggregated.Clear();
            foreach (var entry in live) remaining.Add(entry);
        }
        return (aggregated, remaining);
    }

    /// <summary>删掉仓库文件即可触发全量重建(下次 MergeIn 会把源库现有的全部重新灌入)。</summary>
    public static void DeleteForRebuild()
    {
        try { File.Delete(Location); Diagnostics.Note("用量仓库已删除,下次统计时重建"); }
        catch (Exception ex) { Diagnostics.Note("用量仓库删除失败", ex); }
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Location,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        // 先保证四张表存在(旧库会保留它原来的结构,下面按版本判断要不要重建)
        Execute(connection, CreateTables);

        string want = SchemaVersion.ToString(CultureInfo.InvariantCulture);
        if (ReadMeta(connection, "schema_version") == want) return;

        // 版本对不上就整份重建。**为什么是删而不是迁移**:v2 之前 project 存的是目录末段名,
        // 完整路径已经丢了,从旧行推不回来;新增的 unpriced 列同理(旧行把"无价"和
        // "真实 0 元"都写成了 0,分不出来)。而源库里的明细本来就还在,重灌一遍口径全对。
        Execute(connection, DropTables);
        Execute(connection, CreateTables);
        WriteMeta(connection, "schema_version", want);
        Diagnostics.Note($"用量仓库 schema 升到 v{SchemaVersion}:已重建,源库明细会重新灌入");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void UpsertDay(SqliteConnection connection, SpendEntry e)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agg_day(day, agent, model, provider, project, inp, cache_w, cache_r, out, unc, unpriced, requests, cost,
                                reasoning, duration_ms, duration_n, ttft_ms, ttft_n, tools, retries, failures)
            VALUES($day, $agent, $model, $provider, $project, $inp, $cw, $cr, $out, $unc, $unpriced, $req, $cost,
                   $reasoning, $dur, $durn, $ttft, $ttftn, $tools, $retries, $failures)
            ON CONFLICT(day, agent, model, provider, project) DO UPDATE SET
                inp = inp + $inp, cache_w = cache_w + $cw, cache_r = cache_r + $cr,
                out = out + $out, unc = unc + $unc, unpriced = unpriced + $unpriced,
                requests = requests + $req, cost = cost + $cost,
                reasoning = reasoning + $reasoning,
                duration_ms = duration_ms + $dur, duration_n = duration_n + $durn,
                ttft_ms = ttft_ms + $ttft, ttft_n = ttft_n + $ttftn,
                tools = tools + $tools, retries = retries + $retries, failures = failures + $failures
            """;
        command.Parameters.AddWithValue("$day", e.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$agent", e.Agent);
        command.Parameters.AddWithValue("$model", e.Model);
        command.Parameters.AddWithValue("$provider", (object?)e.ProviderId ?? DBNull.Value);
        // 项目存的是**身份**(完整目录):同名目录是两个项目,末段名只是显示时才用
        command.Parameters.AddWithValue("$project", (object?)e.Project ?? DBNull.Value);
        BindTally(command, e);
        // 无价 token 数必须单独存:金额那一列把"无价"和"真实 0 元"都写成了 0,分不出来
        command.Parameters.AddWithValue("$unpriced", e.UnpricedTokens);
        // 遥测:耗时/首字延迟只在**有样本**时才累加样本数,平均值才不会被 0 稀释
        command.Parameters.AddWithValue("$reasoning", e.ReasoningTokens);
        command.Parameters.AddWithValue("$dur", e.DurationMs);
        command.Parameters.AddWithValue("$durn", e.DurationMs > 0 ? 1 : 0);
        command.Parameters.AddWithValue("$ttft", e.TimeToFirstTokenMs);
        command.Parameters.AddWithValue("$ttftn", e.TimeToFirstTokenMs > 0 ? 1 : 0);
        command.Parameters.AddWithValue("$tools", e.ToolCalls);
        command.Parameters.AddWithValue("$retries", e.Retries);
        command.Parameters.AddWithValue("$failures", e.Failures);
        command.ExecuteNonQuery();
    }

    private static void UpsertHour(SqliteConnection connection, SpendEntry e)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agg_hour(hour, agent, inp, cache_w, cache_r, out, unc, requests, cost)
            VALUES($hour, $agent, $inp, $cw, $cr, $out, $unc, $req, $cost)
            ON CONFLICT(hour, agent) DO UPDATE SET
                inp = inp + $inp, cache_w = cache_w + $cw, cache_r = cache_r + $cr,
                out = out + $out, unc = unc + $unc, requests = requests + $req, cost = cost + $cost
            """;
        command.Parameters.AddWithValue("$hour", e.Timestamp.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$agent", e.Agent);
        BindTally(command, e);
        command.ExecuteNonQuery();
    }

    private static void BindTally(SqliteCommand command, SpendEntry e)
    {
        command.Parameters.AddWithValue("$inp", e.Tally.Input);
        command.Parameters.AddWithValue("$cw", e.Tally.CacheWrite);
        command.Parameters.AddWithValue("$cr", e.Tally.CacheRead);
        command.Parameters.AddWithValue("$out", e.Tally.Output);
        command.Parameters.AddWithValue("$unc", e.UnclassifiedTokens);
        command.Parameters.AddWithValue("$req", e.Requests);
        command.Parameters.AddWithValue("$cost", e.Amount ?? 0d);
    }

    private static void InsertSeen(SqliteConnection connection, string key, DateTime timestamp)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO seen(key, ts) VALUES($key, $ts)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$ts", timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static HashSet<string> LoadSeen(SqliteConnection connection)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key FROM seen";
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>清理按水位时间节流:一小时最多跑一次,别每轮统计都全表删一遍。</summary>
    private static void PruneIfDue(SqliteConnection connection, DateTime now)
    {
        if (ReadMeta(connection, "last_prune") is { } last &&
            DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var due) &&
            now - due < TimeSpan.FromHours(1)) return;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM agg_hour WHERE hour < '{now.AddDays(-HourRetentionDays):yyyyMMddHH}';
            DELETE FROM seen WHERE ts < '{now.AddDays(-SeenRetentionDays):O}';
            DELETE FROM agg_day WHERE day < '{now.AddDays(-DayRetentionDays):yyyy-MM-dd}';
            """;
        command.ExecuteNonQuery();
        WriteMeta(connection, "last_prune", now.ToString("O", CultureInfo.InvariantCulture));
    }

    private static List<SpendEntry> LoadDayRows(SqliteConnection connection)
    {
        var rows = new List<SpendEntry>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT day, agent, model, provider, project, inp, cache_w, cache_r, out, unc, unpriced, requests, cost,"
            + " reasoning, duration_ms, duration_n, ttft_ms, ttft_n, tools, retries, failures FROM agg_day";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new DayRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                reader.GetInt64(11), reader.GetDouble(12),
                reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
                reader.GetInt64(16), reader.GetInt64(17),
                reader.GetInt64(18), reader.GetInt64(19), reader.GetInt64(20));
            if (!DateTime.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)) continue;
            rows.Add(new SpendEntry(
                day.AddHours(12), row.Agent, row.Model, row.Provider, row.Project,
                SessionId: null, Title: null,
                new TokenTally(row.Input, row.CacheWrite, row.CacheRead, row.Output),
                row.Unclassified, Price: null,
                PrecomputedCost: row.Cost, Kind: SpendAggKind.Day, RequestCount: row.Requests,
                UnpricedTokens: row.Unpriced,
                // 聚合行保留"总量 + 样本数",让上层用与明细行相同的公式算平均:
                // 上层把 DurationMs 累加、把 DurationSamples 累加再相除。
                // 之前误写成"直接还原平均值"会让聚合行只贡献 1 个样本,平均被带偏。
                ReasoningTokens: row.Reasoning,
                DurationMs: row.DurationMs,
                TimeToFirstTokenMs: row.TtftMs,
                DurationSamples: row.DurationN,
                TtftSamples: row.TtftN,
                ToolCalls: row.Tools, Retries: row.Retries, Failures: row.Failures));
        }
        return rows;
    }

    private static List<SpendEntry> LoadHourRows(SqliteConnection connection)
    {
        var rows = new List<SpendEntry>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hour, agent, inp, cache_w, cache_r, out, unc, requests, cost FROM agg_hour";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new HourRow(
                reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2) + reader.GetInt64(3) + reader.GetInt64(4) + reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), reader.GetDouble(8));
            if (!DateTime.TryParseExact(row.Hour, "yyyyMMddHH", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var hour)) continue;
            rows.Add(new SpendEntry(
                hour, row.Agent, Model: "(聚合)", ProviderId: null, Project: null,
                SessionId: null, Title: null, TokenTally.Zero,
                UnclassifiedTokens: row.Tokens + row.Unclassified, Price: null,
                PrecomputedCost: row.Cost, Kind: SpendAggKind.Hour, RequestCount: row.Requests));
        }
        return rows;
    }

    /// <summary>跨轮去重键:与跨源去重同形,再加仓库前缀防止与账本层的键撞车。</summary>
    private static string DedupKey(SpendEntry e) =>
        $"repo|{e.Agent}|{e.SessionId}|{e.Timestamp:yyyyMMddHHmmssfff}|{e.Model}|{e.TotalTokens}";
}
