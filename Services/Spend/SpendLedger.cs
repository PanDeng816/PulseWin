using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>
/// 一条已经定好价的用量记录(或"定了价"的否定:Price 为 null = 该模型无公开价,
/// 只计 token 不显示金额)。
/// </summary>
public sealed record SpendEntry(
    DateTime Timestamp,
    string Agent,
    string Model,
    string? ProviderId,
    string? Project,
    string? SessionId,
    string? Title,
    TokenTally Tally,
    long UnclassifiedTokens,
    ModelPrice? Price)
{
    public long TotalTokens => Tally.Total + UnclassifiedTokens;

    /// <summary>只有四类被计价;未分类部分**永不参与金额**(它没有类别可算)。</summary>
    public TokenCost Cost => Price?.Cost(Tally) ?? TokenCost.Zero;

    /// <summary>金额字段怎么显示,取决于这个模型有没有价。</summary>
    public double? Amount => Price is null ? null : Cost.Total;

    public DateOnly Day => DateOnly.FromDateTime(Timestamp);
}

/// <summary>
/// 从本机各来源读一遍、定好价、按去重键合并后的账本。
///
/// 它**不负责**任何显示口径:区间选择、按谁分组都是 <see cref="SpendSummary"/> 的事,
/// 这样"换个区间看"只是重算几个加法,不用重读几百 MB 的库(上游也是这么分的)。
/// </summary>
public sealed class SpendLedger
{
    public IReadOnlyList<SpendEntry> Entries { get; }
    /// <summary>读不出来的来源(库被锁、坏了):说出来,不要静默少一块。</summary>
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyList<string> PresentStores { get; }
    public IReadOnlyList<string> MissingStores { get; }
    public DateTimeOffset BuiltAt { get; }

    private SpendLedger(
        List<SpendEntry> entries,
        List<string> notes,
        List<string> present,
        List<string> missing)
    {
        Entries = entries;
        Notes = notes;
        PresentStores = present;
        MissingStores = missing;
        BuiltAt = DateTimeOffset.Now;
    }

    /// <summary>本机所有已知来源(顺序即界面显示顺序)。</summary>
    public static IReadOnlyList<IUsageStore> AllStores { get; } = new IUsageStore[]
    {
        new ZCodeUsageStore(),
        new OpenCodeUsageStore()
    };

    public static SpendLedger Build(ModelPrices prices)
    {
        var entries = new List<SpendEntry>();
        var notes = new List<string>();
        var present = new List<string>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;

        foreach (var store in AllStores)
        {
            if (!store.IsPresent)
            {
                missing.Add(store.Name);
                continue;
            }
            present.Add(store.Name);
            IReadOnlyList<AgentUsageRecord> records;
            try
            {
                records = store.Read();
            }
            catch (Exception ex)
            {
                // 库锁着或结构变了:说出来,这一块不参与统计(而不是当成 0)
                notes.Add($"{store.Name}:{ex.Message}");
                Diagnostics.Note($"读取 {store.Name} 用量失败", ex);
                continue;
            }

            foreach (var record in records)
            {
                if (record.Tally.HasNegative) continue;      // 坏数据:整条跳过,不夹到 0
                if (record.TotalTokens <= 0) continue;
                if (!seen.Add(record.DedupKey))
                {
                    duplicates++;
                    continue;
                }
                prices.TryPrice(record.Model, record.ProviderId, out var price);
                entries.Add(new SpendEntry(
                    record.Timestamp,
                    record.Agent,
                    record.Model,
                    record.ProviderId,
                    record.Project,
                    record.SessionId,
                    record.Title,
                    record.Tally,
                    record.UnclassifiedTokens,
                    price));
            }
        }

        if (duplicates > 0)
            notes.Add($"忽略 {duplicates} 条重复记录");
        entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return new SpendLedger(entries, notes, present, missing);
    }

    /// <summary>最早的记录时间(界面用来判断"全部"到底覆盖多久)。</summary>
    public DateTime? Earliest => Entries.Count == 0 ? null : Entries[0].Timestamp;
    public DateTime? Latest => Entries.Count == 0 ? null : Entries[^1].Timestamp;
}

/// <summary>统计区间。</summary>
public enum SpendSpan
{
    Today,
    Week,
    Month,
    All
}

/// <summary>
/// 某个区间上的汇总。所有数字都由 <see cref="SpendLedger"/> 里已经定好价的条目算出来,
/// 换个区间只是重新加一遍。
/// </summary>
public sealed class SpendSummary
{
    public SpendSpan Span { get; }
    public DateTime From { get; }
    public DateTime To { get; }
    /// <summary>区间实际覆盖的第一天(All 的时候 From 是 MinValue,图上要用真实最早的一天)。</summary>
    public DateOnly FirstDay { get; }

    public long TotalTokens { get; }
    public double TotalCost { get; }
    /// <summary>四类明细(只含已计价部分)。</summary>
    public TokenTally Tally { get; }
    public TokenCost Cost { get; }
    public long UnclassifiedTokens { get; }
    /// <summary>没有公开价、只能计 token 的 token 数(金额里没有它们)。</summary>
    public long UnpricedTokens { get; }
    /// <summary>无公开价的模型个数。</summary>
    public int UnpricedModels { get; }
    public int Requests { get; }
    public int Sessions { get; }
    public int Projects { get; }

    public IReadOnlyList<DayRow> Days { get; }
    public IReadOnlyList<ModelRow> Models { get; }
    public IReadOnlyList<ProjectRow> ProjectRows { get; }
    public IReadOnlyList<SessionRow> SessionRows { get; }
    public IReadOnlyList<AgentRow> Agents { get; }
    /// <summary>24 小时分布(按本地时间)。</summary>
    public IReadOnlyList<long> HourlyTokens { get; }
    /// <summary>金额是否只覆盖了一部分 token(有模型查不到价)。</summary>
    public bool CostIsPartial => UnpricedTokens > 0;
    /// <summary>峰值小时(0-23),没有用量时为 null。</summary>
    public int? PeakHour => HourlyTokens.All(v => v == 0)
        ? null
        : Enumerable.Range(0, 24).OrderByDescending(h => HourlyTokens[h]).First();

    public sealed record DayRow(DateOnly Day, long Tokens, double Cost, bool HasUnpriced);
    public sealed record ModelRow(string Model, string? VendorName, long Tokens, double? Amount, TokenTally Tally, long Unclassified, int Requests, bool Priced);
    public sealed record ProjectRow(string Project, long Tokens, double Cost, int Sessions);
    public sealed record SessionRow(string SessionId, string? Title, string Agent, string? Project, long Tokens, double Cost, DateTime Last);
    public sealed record AgentRow(string Agent, long Tokens, double Cost, int Requests);

    private SpendSummary(
        SpendSpan span, DateTime from, DateTime to, DateOnly firstDay,
        long totalTokens, double totalCost, TokenTally tally, TokenCost cost,
        long unclassified, long unpricedTokens, int unpricedModels, int requests,
        int sessions, int projects,
        List<DayRow> days, List<ModelRow> models, List<ProjectRow> projectRows,
        List<SessionRow> sessionRows, List<AgentRow> agents, long[] hourly)
    {
        Span = span;
        From = from;
        To = to;
        FirstDay = firstDay;
        TotalTokens = totalTokens;
        TotalCost = totalCost;
        Tally = tally;
        Cost = cost;
        UnclassifiedTokens = unclassified;
        UnpricedTokens = unpricedTokens;
        UnpricedModels = unpricedModels;
        Requests = requests;
        Sessions = sessions;
        Projects = projects;
        Days = days;
        Models = models;
        ProjectRows = projectRows;
        SessionRows = sessionRows;
        Agents = agents;
        HourlyTokens = hourly;
    }

    public static (DateTime From, DateTime To) Range(SpendSpan span, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        return span switch
        {
            SpendSpan.Today => (today, today.AddDays(1)),
            SpendSpan.Week => (today.AddDays(-6), today.AddDays(1)),
            SpendSpan.Month => (today.AddDays(-29), today.AddDays(1)),
            _ => (DateTime.MinValue, today.AddDays(1))
        };
    }

    public static SpendSummary Build(SpendLedger ledger, SpendSpan span)
    {
        var (from, to) = Range(span, DateTime.Now);
        var selected = ledger.Entries
            .Where(e => e.Timestamp >= from && e.Timestamp < to)
            .ToList();

        long totalTokens = 0, unclassified = 0, unpricedTokens = 0;
        var tally = TokenTally.Zero;
        var cost = TokenCost.Zero;
        var unpricedModels = new HashSet<string>(StringComparer.Ordinal);

        var dayBuckets = new Dictionary<DateOnly, (long Tokens, double Cost, bool HasUnpriced)>();
        var modelBuckets = new Dictionary<string, ModelAccumulator>(StringComparer.Ordinal);
        var projectBuckets = new Dictionary<string, (long Tokens, double Cost, HashSet<string> Sessions)>(StringComparer.Ordinal);
        var sessionBuckets = new Dictionary<string, SessionAccumulator>(StringComparer.Ordinal);
        var agentBuckets = new Dictionary<string, (long Tokens, double Cost, int Requests)>(StringComparer.Ordinal);
        var hourly = new long[24];

        foreach (var entry in selected)
        {
            long tokens = entry.TotalTokens;
            totalTokens += tokens;
            unclassified += entry.UnclassifiedTokens;
            tally += entry.Tally;
            cost += entry.Cost;
            if (entry.Price is null)
            {
                unpricedTokens += tokens;
                unpricedModels.Add(ModelPrices.DisplayName(entry.Model));
            }

            var day = entry.Day;
            var dayAcc = dayBuckets.TryGetValue(day, out var d) ? d : (0L, 0d, false);
            dayBuckets[day] = (dayAcc.Item1 + tokens, dayAcc.Item2 + entry.Cost.Total, dayAcc.Item3 || entry.Price is null);

            // 按显示名归并:`deepseek/deepseek-v4-flash` 与 `deepseek-v4-flash` 是同一个模型。
            string modelKey = ModelPrices.DisplayName(entry.Model);
            if (!modelBuckets.TryGetValue(modelKey, out var modelAcc))
                modelAcc = new ModelAccumulator();
            modelAcc.Add(entry);
            modelBuckets[modelKey] = modelAcc;

            string projectKey = entry.Project ?? "（无项目）";
            var projectAcc = projectBuckets.TryGetValue(projectKey, out var p)
                ? p
                : (0L, 0d, new HashSet<string>(StringComparer.Ordinal));
            projectAcc.Item1 += tokens;
            projectAcc.Item2 += entry.Cost.Total;
            if (entry.SessionId is not null) projectAcc.Item3.Add(entry.SessionId);
            projectBuckets[projectKey] = projectAcc;

            string sessionKey = entry.SessionId ?? $"{entry.Agent}|{entry.Timestamp:yyyyMMddHH}";
            if (!sessionBuckets.TryGetValue(sessionKey, out var sessionAcc))
                sessionAcc = new SessionAccumulator(entry);
            sessionAcc.Add(entry);
            sessionBuckets[sessionKey] = sessionAcc;

            var agentAcc = agentBuckets.TryGetValue(entry.Agent, out var a) ? a : (0L, 0d, 0);
            agentBuckets[entry.Agent] = (agentAcc.Item1 + tokens, agentAcc.Item2 + entry.Cost.Total, agentAcc.Item3 + 1);

            hourly[entry.Timestamp.Hour] += tokens;
        }

        // 空白的日子也要在图上占一格(否则柱状图会把稀疏的用量画成连着的)
        var firstDay = DateOnly.FromDateTime(
            from == DateTime.MinValue ? (ledger.Earliest ?? DateTime.Now).Date : from);
        var lastDay = DateOnly.FromDateTime(to.AddDays(-1));
        var days = new List<DayRow>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            var acc = dayBuckets.TryGetValue(day, out var d) ? d : (0L, 0d, false);
            days.Add(new DayRow(day, acc.Item1, acc.Item2, acc.Item3));
        }

        var models = modelBuckets
            .Select(kv => kv.Value.ToRow(kv.Key))
            .OrderByDescending(m => m.Tokens)
            .ToList();

        var projectRows = projectBuckets
            .Select(kv => new ProjectRow(kv.Key, kv.Value.Item1, kv.Value.Item2, kv.Value.Item3.Count))
            .OrderByDescending(p => p.Tokens)
            .ToList();

        var sessionRows = sessionBuckets
            .Select(kv => kv.Value.ToRow())
            .OrderByDescending(s => s.Last)
            .ToList();

        var agentRows = agentBuckets
            .Select(kv => new AgentRow(kv.Key, kv.Value.Item1, kv.Value.Item2, kv.Value.Item3))
            .OrderByDescending(a => a.Tokens)
            .ToList();

        return new SpendSummary(
            span, from, to, firstDay, totalTokens, cost.Total, tally, cost, unclassified,
            unpricedTokens, unpricedModels.Count, selected.Count,
            sessionBuckets.Count, projectBuckets.Count(p => p.Key != "（无项目）"),
            days, models, projectRows, sessionRows, agentRows, hourly);
    }

    private sealed class ModelAccumulator
    {
        private TokenTally _tally;
        private long _unclassified;
        private int _requests;
        private double? _amount;
        private string? _vendor;
        private bool _anyPriced;

        public void Add(SpendEntry entry)
        {
            _tally += entry.Tally;
            _unclassified += entry.UnclassifiedTokens;
            _requests++;
            if (entry.Price is not null)
            {
                _anyPriced = true;
                _amount = (_amount ?? 0) + entry.Cost.Total;
                _vendor ??= entry.Price.Vendor;
            }
        }

        public ModelRow ToRow(string model) => new(
            model,
            _vendor,
            _tally.Total + _unclassified,
            _anyPriced ? _amount ?? 0 : null,
            _tally,
            _unclassified,
            _requests,
            _anyPriced);
    }

    private sealed class SessionAccumulator
    {
        private readonly string _id;
        private string? _title;
        private string? _project;
        private readonly string _agent;
        private long _tokens;
        private double _cost;
        private DateTime _last;

        public SessionAccumulator(SpendEntry first)
        {
            _id = first.SessionId ?? "";
            _title = first.Title;
            _project = first.Project;
            _agent = first.Agent;
            _last = first.Timestamp;
        }

        public void Add(SpendEntry entry)
        {
            _tokens += entry.TotalTokens;
            _cost += entry.Cost.Total;
            if (entry.Timestamp > _last) _last = entry.Timestamp;
            if (!string.IsNullOrWhiteSpace(entry.Title)) _title = entry.Title;
            if (_project is null) _project = entry.Project;
        }

        public SessionRow ToRow() => new(_id, _title, _agent, _project, _tokens, _cost, _last);
    }
}

/// <summary>数字的显示格式(与上游一致:大数按中文单位缩写,金额不足一分显示"&lt; $0.01")。</summary>
public static class SpendFormat
{
    /// <summary>token 数的短写法:1.2万 / 3.4亿。</summary>
    public static string Tokens(long value)
    {
        if (value < 10_000) return value.ToString("N0");
        if (value < 100_000_000) return (value / 10_000d).ToString("0.#") + "万";
        return (value / 100_000_000d).ToString("0.##") + "亿";
    }

    public static string TokensExact(long value) => value.ToString("N0");

    /// <summary>
    /// 金额。**正的、但不足一分的显示 "&lt; $0.01"**,真正的 0 才显示 $0.00——
    /// 把 0.004 四舍五入成 $0.00 是在说"没花钱",那是错的。
    /// </summary>
    public static string Money(double? value)
    {
        if (value is null) return "—";
        if (value.Value <= 0) return "$0.00";
        if (value.Value < 0.01) return "< $0.01";
        return "$" + value.Value.ToString(value.Value < 1 ? "0.000" : "0.00");
    }

    public static string MoneyExact(double? value) =>
        value is null ? "—" : "$" + value.Value.ToString("0.0000");
}
