using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PulseWin;

/// <summary>
/// 本机用量来源的公共部分:都从 SQLite 只读读,都要拿 session 表的目录/标题。
/// </summary>
public static class UsageStoreSupport
{
    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>只读打开一个库。**只读**是硬要求:这是别人正在用的数据库。</summary>
    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>取一个路径的最后一段作为项目名(与上游一致:文件夹名只是兜底,真名从来源读)。</summary>
    public static string? ProjectName(string? directoryOrPath)
    {
        if (string.IsNullOrWhiteSpace(directoryOrPath)) return null;
        string trimmed = directoryOrPath.TrimEnd('/', '\\');
        int index = trimmed.LastIndexOfAny(['/', '\\']);
        string name = index >= 0 ? trimmed[(index + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>库里的列名集合(schema 版本可能不同,只查确实存在的列)。</summary>
    public static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1)) names.Add(reader.GetString(1));
        }
        return names;
    }

    public static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$n";
        command.Parameters.AddWithValue("$n", table);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>毫秒时间戳 → 本地时间;拿不到就返回 null。</summary>
    public static DateTime? FromUnixMilliseconds(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (!long.TryParse(value.ToString(), out long ms) || ms <= 0) return null;
        // 秒级时间戳(10 位)与毫秒(13 位)都见过,按量级区分
        if (ms < 100_000_000_000L) ms *= 1000;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        }
        catch
        {
            return null;
        }
    }

    public static long ParseCount(object? value) =>
        value is null || !long.TryParse(value.ToString(), out long parsed) ? 0 : parsed;
}

/// <summary>
/// ZCode 的用量库(<c>~/.zcode/cli/db/db.sqlite</c> 的 <c>model_usage</c> 表)。
///
/// **口径(必须记牢,写错了数字会静默偏移):** 这份 schema 里
/// <c>input_tokens</c> 是**含缓存**的、<c>output_tokens</c> 是**含推理**的。
/// 所以输入侧要减掉缓存重叠、推理**不能**再加一次;四类之和恰等于
/// <c>computed_total_tokens</c>(实测恒等,如 1092+0+102912+930=104934),
/// 超出部分才记未分类。
/// </summary>
public sealed class ZCodeUsageStore : IUsageStore
{
    public string Name => "ZCode";

    public string Location => Path.Combine(UsageStoreSupport.HomeDirectory, ".zcode", "cli", "db", "db.sqlite");

    public bool IsPresent => File.Exists(Location);

    public IReadOnlyList<AgentUsageRecord> Read()
    {
        var records = new List<AgentUsageRecord>();
        using var connection = UsageStoreSupport.OpenReadOnly(Location);
        var columns = UsageStoreSupport.Columns(connection, "model_usage");
        if (columns.Count == 0) return records;

        // 会话索引先读:子代理记录要归根到主会话,项目与标题都取根会话的。
        var sessions = UsageStoreSupport.TableExists(connection, "session")
            ? ZCodeSessionIndex.Read(connection)
            : ZCodeSessionIndex.Empty;

        var selected = new List<string>();
        foreach (var name in new[]
                 {
                     "id", "session_id", "model_id", "provider_id", "started_at", "completed_at",
                     "input_tokens", "output_tokens", "reasoning_tokens",
                     "cache_read_input_tokens", "cache_creation_input_tokens", "computed_total_tokens"
                 })
        {
            if (columns.Contains(name)) selected.Add(name);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join(", ", selected) + " FROM model_usage";
        using var reader = command.ExecuteReader();

        // SELECT 的列顺序就是 selected 的顺序,序号直接按它取。
        int iId = selected.IndexOf("id");
        int iSession = selected.IndexOf("session_id");
        int iModel = selected.IndexOf("model_id");
        int iProvider = selected.IndexOf("provider_id");
        int iStarted = selected.IndexOf("started_at");
        int iCompleted = selected.IndexOf("completed_at");
        int iInput = selected.IndexOf("input_tokens");
        int iOutput = selected.IndexOf("output_tokens");
        int iCacheRead = selected.IndexOf("cache_read_input_tokens");
        int iCacheWrite = selected.IndexOf("cache_creation_input_tokens");
        int iComputed = selected.IndexOf("computed_total_tokens");

        while (reader.Read())
        {
            var timestamp = ReadTime(reader, iStarted) ?? ReadTime(reader, iCompleted);
            if (timestamp is null) continue;
            string sessionId = ReadText(reader, iSession) ?? "";
            if (sessionId.Length == 0) continue;

            long rawInput = ReadCount(reader, iInput);
            long rawOutput = ReadCount(reader, iOutput);
            long cacheRead = ReadCount(reader, iCacheRead);
            long cacheWrite = ReadCount(reader, iCacheWrite);
            long? computed = ReadNullableCount(reader, iComputed);

            // input 含缓存 → 减掉缓存重叠;output 含推理 → 不再二次相加。
            long freshInput = Math.Max(0, rawInput - cacheRead - cacheWrite);
            long unclassified = computed is { } c && c > rawInput + rawOutput
                ? c - (rawInput + rawOutput)
                : 0;

            var tally = new TokenTally(freshInput, cacheWrite, cacheRead, rawOutput);
            if (tally.Total <= 0 && unclassified <= 0) continue;

            var (rootId, project, title) = sessions.Resolve(sessionId);
            records.Add(new AgentUsageRecord(
                timestamp.Value,
                ReadText(reader, iModel) ?? "auto",
                tally,
                unclassified,
                Name,
                ReadText(reader, iProvider),
                rootId,
                project,
                title));
        }
        return records;
    }

    private static string? ReadText(SqliteDataReader reader, int index) =>
        index < 0 || reader.IsDBNull(index) ? null : reader.GetValue(index)?.ToString();

    private static long ReadCount(SqliteDataReader reader, int index) =>
        index < 0 || reader.IsDBNull(index) ? 0 : UsageStoreSupport.ParseCount(reader.GetValue(index));

    private static long? ReadNullableCount(SqliteDataReader reader, int index) =>
        index < 0 || reader.IsDBNull(index) ? null : UsageStoreSupport.ParseCount(reader.GetValue(index));

    private static DateTime? ReadTime(SqliteDataReader reader, int index) =>
        index < 0 ? null : UsageStoreSupport.FromUnixMilliseconds(reader.IsDBNull(index) ? null : reader.GetValue(index));
}

/// <summary>
/// ZCode <c>session</c> 表的内存索引。**子会话沿 parent_id 链归根到主会话**:
/// 子代理在库里是独立 session 行,directory 记的是它当时干活的子目录(比如
/// <c>06Learning\03_工作资料</c>),直接取最后一段当项目名会拆出一堆并不存在的
/// "项目",标题也是任务描述的开头而不是会话框的名字。项目、标题、会话分组
/// 一律以根会话为准,子会话的用量在统计里跟着主会话走。
/// </summary>
internal sealed class ZCodeSessionIndex
{
    private sealed record SessionInfo(string? Directory, string? Path, string? Title, string? ParentId);

    public static ZCodeSessionIndex Empty { get; } = new(new Dictionary<string, SessionInfo>());

    private readonly Dictionary<string, SessionInfo> rows;
    private readonly Dictionary<string, string> rootCache = new(StringComparer.Ordinal);

    private ZCodeSessionIndex(Dictionary<string, SessionInfo> rows) => this.rows = rows;

    public static ZCodeSessionIndex Read(SqliteConnection connection)
    {
        var columns = UsageStoreSupport.Columns(connection, "session");
        if (!columns.Contains("id")) return Empty;

        bool hasParent = columns.Contains("parent_id");
        bool hasDirectory = columns.Contains("directory");
        bool hasPath = columns.Contains("path");
        bool hasTitle = columns.Contains("title");

        var selected = new List<string> { "id" };
        if (hasParent) selected.Add("parent_id");
        if (hasDirectory) selected.Add("directory");
        if (hasPath) selected.Add("path");
        if (hasTitle) selected.Add("title");

        var rows = new Dictionary<string, SessionInfo>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + string.Join(", ", selected) + " FROM session";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            if (string.IsNullOrEmpty(id)) continue;
            int i = 1;
            string? parent = hasParent ? ReadText(reader, i++) : null;
            string? directory = hasDirectory ? ReadText(reader, i++) : null;
            string? path = hasPath ? ReadText(reader, i++) : null;
            string? title = hasTitle ? ReadText(reader, i) : null;
            rows[id] = new SessionInfo(directory, path, title, parent);
        }
        return new ZCodeSessionIndex(rows);
    }

    /// <summary>
    /// 归根:返回 (根会话 id, 项目名, 标题)。parent 链缺行、成环都停在链上最后一个
    /// 可用节点;根会话缺目录/标题时用子会话自己的兜底;库里没有这个会话时原样返回。
    /// </summary>
    public (string RootId, string? Project, string? Title) Resolve(string sessionId)
    {
        string rootId = ResolveRoot(sessionId);
        rows.TryGetValue(rootId, out var root);
        rows.TryGetValue(sessionId, out var self);

        string? project = root is not null
            ? UsageStoreSupport.ProjectName(root.Directory) ?? UsageStoreSupport.ProjectName(root.Path)
            : null;
        if (project is null && self is not null)
            project = UsageStoreSupport.ProjectName(self.Directory) ?? UsageStoreSupport.ProjectName(self.Path);

        string? title = NonEmpty(root?.Title) ?? NonEmpty(self?.Title);
        return (rootId, project, title);
    }

    private string ResolveRoot(string sessionId)
    {
        if (rootCache.TryGetValue(sessionId, out var cached)) return cached;
        var chain = new List<string> { sessionId };
        var visited = new HashSet<string>(StringComparer.Ordinal) { sessionId };
        string current = sessionId;
        while (rows.TryGetValue(current, out var row)
               && !string.IsNullOrWhiteSpace(row.ParentId)
               && visited.Add(row.ParentId))
        {
            current = row.ParentId;
            chain.Add(current);
        }
        foreach (var id in chain) rootCache[id] = current;
        return current;
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ReadText(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetValue(index)?.ToString();
}

/// <summary>
/// OpenCode 的库(<c>~/.local/share/opencode/opencode.db</c>)。用量在
/// <c>message.data</c> 的 JSON 里,助手消息带一个 <c>tokens</c> 对象。
///
/// **它自己记的 <c>cost</c> 一律忽略**:那是 OpenCode 当时的理解(套餐没有价目时就是 0),
/// 混进总额会让同一页出现两个来源不同的钱。这里只取 token,钱一律由牌价算。
/// </summary>
public sealed class OpenCodeUsageStore : IUsageStore
{
    public string Name => "OpenCode";

    public string Location => Path.Combine(
        UsageStoreSupport.HomeDirectory, ".local", "share", "opencode", "opencode.db");

    public bool IsPresent => File.Exists(Location);

    public IReadOnlyList<AgentUsageRecord> Read()
    {
        var records = new List<AgentUsageRecord>();
        using var connection = UsageStoreSupport.OpenReadOnly(Location);
        if (!UsageStoreSupport.TableExists(connection, "message")) return records;
        bool hasSession = UsageStoreSupport.TableExists(connection, "session");

        string sql = hasSession
            ? "SELECT m.id, m.session_id, m.time_created, m.data, s.directory, s.title "
              + "FROM message m LEFT JOIN session s ON s.id = m.session_id"
            : "SELECT id, session_id, time_created, data, NULL, NULL FROM message";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(3)) continue;
            string payload = reader.GetValue(3).ToString() ?? "";
            if (payload.Length == 0 || !payload.Contains("\"tokens\"", StringComparison.Ordinal)) continue;

            var parsed = ParseMessage(payload);
            if (parsed is null) continue;
            var (model, provider, tally, createdMs) = parsed.Value;
            if (tally.Total <= 0) continue;

            var timestamp = UsageStoreSupport.FromUnixMilliseconds(createdMs)
                ?? UsageStoreSupport.FromUnixMilliseconds(reader.IsDBNull(2) ? null : reader.GetValue(2));
            if (timestamp is null) continue;

            records.Add(new AgentUsageRecord(
                timestamp.Value,
                model,
                tally,
                0,
                Name,
                provider,
                reader.IsDBNull(1) ? null : reader.GetValue(1).ToString(),
                hasSession ? UsageStoreSupport.ProjectName(reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString()) : null,
                hasSession && !reader.IsDBNull(5) ? reader.GetValue(5).ToString() : null));
        }
        return records;
    }

    private static (string Model, string? Provider, TokenTally Tally, long CreatedMs)? ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("role", out var role) || role.GetString() != "assistant") return null;
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return null;

            long created = 0;
            if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object
                && time.TryGetProperty("created", out var createdElement))
            {
                created = createdElement.ValueKind == JsonValueKind.Number ? createdElement.GetInt64() : 0;
            }

            long cacheRead = 0, cacheWrite = 0;
            if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                cacheRead = ReadCount(cache, "read");
                cacheWrite = ReadCount(cache, "write");
            }

            // OpenCode 的 input 是**不含缓存**的新鲜输入;reasoning 要并进 output
            // (所有价目表都把它按输出计费,上游也是这么合的)。
            var tally = new TokenTally(
                ReadCount(tokens, "input"),
                cacheWrite,
                cacheRead,
                ReadCount(tokens, "output") + ReadCount(tokens, "reasoning"));

            string model = root.TryGetProperty("modelID", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? "auto"
                : "auto";
            string? provider = root.TryGetProperty("providerID", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            return (model, provider, tally, created);
        }
        catch
        {
            return null;
        }
    }

    private static long ReadCount(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long parsed) => parsed,
            JsonValueKind.String when long.TryParse(value.GetString(), out long parsed) => parsed,
            _ => 0
        };
    }
}
