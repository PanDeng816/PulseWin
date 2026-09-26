namespace PulseWin;

/// <summary>
/// 一个模型的用量画像（"用过的 AI 模型"那一页的一行，也是详情页的数据源）。
///
/// **它是聚合结果，不是明细**：构建完只留下按天/按项目/按来源的小数组与几个计数器，
/// 原始条目一条都不留。详情页要展示"这个模型用在哪、什么时候用的"，但这些都可以
/// 边扫边累加，没必要把几万条明细挂在内存里等界面来问——那正是之前开一次用量统计
/// 就多占几十上百 MB 的原因。
/// </summary>
public sealed class ModelUsage
{
    /// <summary>显示名（去掉路由前缀的规范化写法，同一个模型的两种写法归成一行）。</summary>
    public required string Model { get; init; }

    /// <summary>价目来自哪个厂商条目；null = 没有公开价（只计 token，不算钱）。</summary>
    public string? VendorName { get; init; }

    /// <summary>四类 token 合计（不含未分类部分）。</summary>
    public TokenTally Tally { get; init; }

    /// <summary>来源报了总量但归不进四类的部分。计入总数，绝不参与计价。</summary>
    public long UnclassifiedTokens { get; init; }

    /// <summary>算不出价的 token 数（0 = 全部有价）。</summary>
    public long UnpricedTokens { get; init; }

    /// <summary>估算金额（美元）。<see cref="HasUnpriced"/> 为真时它只是有价部分的小计。</summary>
    public double Cost { get; init; }

    public long Requests { get; init; }

    /// <summary>用过的会话数（按会话号去重；聚合行没有会话号，故只数得到明细里的）。</summary>
    public int Sessions { get; init; }

    /// <summary>用过的项目数。</summary>
    public int Projects { get; init; }

    /// <summary>这个模型出现在哪些来源（ZCode / OpenCode）。</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>
    /// 是否**算得出金额**（有价 token 存在）。注意：为真不代表 <see cref="Price"/> 非空——
    /// 已归档的聚合行金额是建仓时按当时牌价算好的，单价本身没存下来。
    /// </summary>
    public bool IsPriced { get; init; }

    /// <summary>首次 / 最后一次使用（本地时间）。</summary>
    public DateTime? FirstUsed { get; init; }
    public DateTime? LastUsed { get; init; }

    /// <summary>该模型的公开牌价（每百万 token 美元）；无公开价时为 null。</summary>
    public ModelPrice? Price { get; init; }

    /// <summary>逐日 token（按天，升序，含空白日）。详情页的趋势条用它。</summary>
    public IReadOnlyList<(DateOnly Day, long Tokens, double Cost)> Daily { get; init; } = [];

    /// <summary>top 项目（按 token 降序，最多 8 个）。</summary>
    public IReadOnlyList<(string Project, long Tokens, double Cost)> TopProjects { get; init; } = [];

    /// <summary>token 总量（含未分类）。</summary>
    public long TotalTokens => Tally.Total + UnclassifiedTokens;

    /// <summary>有价部分的 token 数（有价 + 无价 = 总数，两者互斥）。</summary>
    public long PricedTokens => TotalTokens - UnpricedTokens;

    public bool HasUnpriced => UnpricedTokens > 0;

    /// <summary>全部 token 都算不出价（界面金额显示 "—" 而不是 ¥0.00）。</summary>
    public bool FullyUnpriced => UnpricedTokens >= TotalTokens && TotalTokens > 0;

    /// <summary>
    /// 缓存命中率（输入侧）：缓存读 / 全部输入。没有输入时为 null——
    /// "不适用"不能显示成 0%。
    /// </summary>
    public double? CacheHit
    {
        get
        {
            long inputTotal = Tally.Input + Tally.CacheWrite + Tally.CacheRead;
            return inputTotal > 0 ? (double)Tally.CacheRead / inputTotal : null;
        }
    }
}

/// <summary>
/// 把账本聚合成"每模型一行"。**按需构建**：只在"模型"页被打开时调用，
/// 用完即可丢掉（见 <see cref="SpendLedgerCache"/>）。
/// </summary>
public static class ModelUsageIndex
{
    /// <summary>
    /// 从账本里按时间区间聚合出每模型的用量。
    /// 口径与用量统计页完全一致：先按区间筛条目，再按显示名归并（同一个模型的
    /// <c>deepseek/deepseek-v4.1-flash</c> 与 <c>deepseek-v4.1-flash</c> 合成一行）。
    /// </summary>
    public static List<ModelUsage> Build(SpendLedger ledger, SpendSpan span)
    {
        var (from, to) = SpendSummary.Range(span, DateTime.Now);

        // 边扫边累加：每个模型一个累加器，扫完只留累加器（没有明细）。
        var acc = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var entry in ledger.Entries)
        {
            if (entry.Timestamp < from || entry.Timestamp >= to) continue;
            // 小时聚合行没有模型维度（模型写成 "(聚合)"），喂进来会把所有模型混成一行
            if (entry.Kind == SpendAggKind.Hour) continue;

            string key = ModelPrices.DisplayName(entry.Model);
            if (!acc.TryGetValue(key, out var a)) acc[key] = a = new Accumulator(key);
            a.Add(entry);
        }

        // 会话数只在源库明细里有会话号，单独再扫一遍 LiveEntries（聚合行没有会话号）
        foreach (var entry in ledger.LiveEntries)
        {
            if (entry.Kind != SpendAggKind.Live) continue;
            if (entry.Timestamp < from || entry.Timestamp >= to) continue;
            if (entry.SessionId is null) continue;
            if (!acc.TryGetValue(ModelPrices.DisplayName(entry.Model), out var a)) continue;
            a.SessionIds.Add(entry.SessionId);
        }

        var rows = acc.Values
            .Where(a => a.TotalTokens > 0)
            .Select(a => a.ToRow())
            .OrderByDescending(m => m.TotalTokens)
            .ToList();
        return rows;
    }

    /// <summary>所有区间用过的模型名（设置页"有哪些模型"用；不含区间筛选）。</summary>
    public static List<string> AllModelNames(SpendLedger ledger)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ledger.Entries)
        {
            if (entry.Kind == SpendAggKind.Hour) continue;
            names.Add(ModelPrices.DisplayName(entry.Model));
        }
        var list = names.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private sealed class Accumulator
    {
        private readonly string _model;
        private readonly Dictionary<DateOnly, (long Tokens, double Cost)> _daily = new();
        private readonly Dictionary<string, (long Tokens, double Cost)> _projects = new(StringComparer.Ordinal);
        private readonly HashSet<string> _agents = new(StringComparer.Ordinal);
        private TokenTally _tally;
        private long _unclassified;
        private long _unpriced;
        private double _cost;
        private long _requests;
        private string? _vendor;
        private bool _anyPriced;
        private ModelPrice? _price;
        private DateTime? _first;
        private DateTime? _last;

        public readonly HashSet<string> SessionIds = new(StringComparer.Ordinal);

        public Accumulator(string model) => _model = model;

        public long TotalTokens => _tally.Total + _unclassified;

        public void Add(SpendEntry e)
        {
            long tokens = e.TotalTokens;
            _tally += e.Tally;
            _unclassified += e.UnclassifiedTokens;
            _unpriced += e.UnpricedTokens;
            _requests += e.Requests;

            // **有价那部分存在就算已定价**：仓库聚合行的 Price 是 null，金额却早在建仓时
            // 按当时牌价算好了（PrecomputedCost）——只认 Price 会把已归档模型全判成"无公开价"。
            if (e.HasPriced)
            {
                _anyPriced = true;
                _cost += e.Cost.Total;
                _vendor ??= e.Price?.Vendor;
                _price ??= e.Price;
            }

            _agents.Add(e.Agent);

            var day = e.Day;
            var d = _daily.TryGetValue(day, out var cur) ? cur : (0L, 0d);
            _daily[day] = (d.Item1 + tokens, d.Item2 + e.Cost.Total);

            if (e.Project is { } project)
            {
                var p = _projects.TryGetValue(project, out var pc) ? pc : (0L, 0d);
                _projects[project] = (p.Item1 + tokens, p.Item2 + e.Cost.Total);
            }

            if (_first is null || e.Timestamp < _first) _first = e.Timestamp;
            if (_last is null || e.Timestamp > _last) _last = e.Timestamp;
        }

        public ModelUsage ToRow()
        {
            // 逐日补齐空白日（趋势图要有连续的横轴），范围取该模型实际用到的天数
            var daily = new List<(DateOnly, long, double)>();
            if (_daily.Count > 0)
            {
                var firstDay = _daily.Keys.Min();
                var lastDay = _daily.Keys.Max();
                for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
                {
                    var v = _daily.TryGetValue(day, out var d) ? d : (0L, 0d);
                    daily.Add((day, v.Item1, v.Item2));
                }
            }

            var projects = _projects
                .Select(kv => (Project: kv.Key, Tokens: kv.Value.Item1, Cost: kv.Value.Item2))
                .OrderByDescending(p => p.Tokens)
                .Take(8)
                .ToList();

            return new ModelUsage
            {
                Model = _model,
                VendorName = _vendor,
                Tally = _tally,
                UnclassifiedTokens = _unclassified,
                UnpricedTokens = _unpriced,
                Cost = _cost,
                Requests = _requests,
                Sessions = SessionIds.Count,
                Projects = _projects.Count,
                Agents = _agents.OrderBy(a => a, StringComparer.Ordinal).ToList(),
                FirstUsed = _first,
                LastUsed = _last,
                // 有价部分存在就算已定价(金额可能来自建仓时的 PrecomputedCost,
                // 此时 Price 为 null——不能只认 Price,否则已归档模型全被判成"无公开价")
                IsPriced = _anyPriced,
                Price = _price,
                Daily = daily.Select(d => (d.Item1, d.Item2, d.Item3)).ToList(),
                TopProjects = projects,
            };
        }
    }
}
