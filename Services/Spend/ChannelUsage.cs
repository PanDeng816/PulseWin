namespace PulseWin;

/// <summary>
/// 一个"套餐/渠道"在某个时间区间里的用量画像——这是"每个套餐一个页面"那一页的数据源。
///
/// 与 <see cref="ModelUsage"/> 同一思路：**只留聚合结果，不留明细**。按小时/按天/按模型
/// 都是边扫边累加的小数组，扫完原始条目就丢，所以这一页不会因为库变大而吃掉内存。
/// </summary>
public sealed class ChannelUsage
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>
    /// 用量的来源客户端（ZCode / OpenCode / "ZCode + OpenCode"）。同一个套餐可能被多个
    /// 客户端用过（如 OpenCode Go 既在 ZCode 里配过、也在 OpenCode 客户端里用过），
    /// 那些记录会**合并到同一个套餐行**——用户要的是"每个套餐一个"，
    /// 而不是按客户端裂成几行。
    /// </summary>
    public required string Agent { get; init; }
    /// <summary>对应的数据源键（goat / opencode / deepseek / …），有额度环的渠道可跳去看环。</summary>
    public string? SourceKey { get; init; }
    /// <summary>本机见过的 provider id（详情里如实列出，便于对账）。</summary>
    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    /// <summary>这个套餐用到的客户端（可能不止一个，如 ZCode 与 OpenCode 都配过 OpenCode Go）。</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>
    /// 这个套餐是本机客户端**配置里就有的**（订阅配置），与"这段时间有没有用量"无关。
    /// 配置里有的套餐即使当前区间零用量也会出现在列表里。
    /// </summary>
    public bool Configured { get; init; }

    /// <summary>配置里是否启用（订阅中）。null = 配置里没记/无从判断。</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// 全时（不受区间限制）的首次/末次使用与总量——给"本区间没有用量"的套餐用，
    /// 让用户能看到它历史上用过多少、最后一次是什么时候。
    /// </summary>
    public DateTime? LifetimeFirst { get; init; }
    public DateTime? LifetimeLast { get; init; }
    public long LifetimeTokens { get; init; }
    public long LifetimeRequests { get; init; }

    public TokenTally Tally { get; init; }
    public long UnclassifiedTokens { get; init; }
    public long UnpricedTokens { get; init; }

    /// <summary>估算金额（美元，汇总时按牌价算）。</summary>
    public double Cost { get; init; }
    /// <summary>分类金额（供"金额构成"用）。</summary>
    public TokenCost CostBreakdown { get; init; }

    public long Requests { get; init; }
    public int Sessions { get; init; }

    public DateTime? FirstUsed { get; init; }
    public DateTime? LastUsed { get; init; }

    /// <summary>逐日（升序，含空白日）。</summary>
    public IReadOnlyList<(DateOnly Day, long Tokens, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Requests)> Daily { get; init; } = [];

    /// <summary>
    /// 逐小时（24 项，索引 = 本地小时；只统计**当天**，用于"今日 24 小时"图）。
    /// 与 goat-gauge / GoGauge 的"今日趋势"同款。
    /// </summary>
    public IReadOnlyList<HourBucket> TodayHourly { get; init; } = [];

    /// <summary>该渠道用过的模型（按 token 降序）。</summary>
    public IReadOnlyList<ChannelModelRow> Models { get; init; } = [];

    public long TotalTokens => Tally.Total + UnclassifiedTokens;
    public bool HasUnpriced => UnpricedTokens > 0;

    /// <summary>缓存命中率（输入侧）：缓存读 / 全部输入。没有输入时为 null。</summary>
    public double? CacheHit
    {
        get
        {
            long inputTotal = Tally.Input + Tally.CacheWrite + Tally.CacheRead;
            return inputTotal > 0 ? (double)Tally.CacheRead / inputTotal : null;
        }
    }

    /// <summary>这一页有没有东西可显示。</summary>
    public bool HasData => TotalTokens > 0 || Requests > 0;
}

/// <summary>一个小时的用量（给"今日 24 小时"图用）。</summary>
public sealed record HourBucket(int Hour, long Input, long Output, long CacheRead, long CacheWrite, long Requests)
{
    public long Tokens => Input + Output + CacheRead + CacheWrite;
}

/// <summary>渠道页里的一行模型。</summary>
public sealed record ChannelModelRow(
    string Model, long Tokens, long Requests, double Cost, bool HasUnpriced, double? CacheHit);

/// <summary>
/// 把账本聚合成"每个渠道（套餐）一份画像"。**按需构建**，与模型页同样只在打开那一页时跑。
/// </summary>
public static class ChannelUsageIndex
{
    public static List<ChannelUsage> Build(SpendLedger ledger, SpendSpan span)
    {
        var (from, to) = SpendSummary.Range(span, DateTime.Now);
        var registry = ChannelRegistry.Current;

        // 按**套餐名**分桶（不是 provider id）：同一个套餐的多个 provider id
        // （OpenCode Go 的 opencode-go / -glm / -responses；BigModel 的 builtin: 与 account:）
        // 必须合成一行，否则界面上同一个套餐会裂成好几行——用户要的是"每个套餐一个"。
        var acc = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        // **先给本机配置里的每个套餐预建一个桶**（即使当前区间内零用量）。
        // 这是用户点名要的：订阅过、以后还会订阅的套餐（如 OpenCode Go）不该因为
        // "最近 7 天没用"就整个从列表里消失——那会让人以为工具没统计到它。
        foreach (var known in registry.KnownChannels)
        {
            string key = BucketKey(known);
            if (!acc.ContainsKey(key)) acc[key] = new Accumulator(known, configured: true);
        }

        foreach (var entry in ledger.Entries)
        {
            if (entry.Timestamp < from || entry.Timestamp >= to) continue;
            if (entry.Kind == SpendAggKind.Hour) continue;   // 小时行没有渠道/模型维度

            var channel = registry.Resolve(entry.ProviderId, entry.Agent);
            string key = BucketKey(channel);
            if (!acc.TryGetValue(key, out var a))
                acc[key] = a = new Accumulator(channel, configured: false);
            a.Add(entry);
        }

        // 全时历史：不按区间筛，专门给"本区间没有用量"的套餐显示"以前用过多少"。
        // 只统计每个桶一次（同一套餐的桶已合并），所以直接遍历全部条目累加即可。
        foreach (var entry in ledger.Entries)
        {
            if (entry.Kind == SpendAggKind.Hour) continue;
            var channel = registry.Resolve(entry.ProviderId, entry.Agent);
            if (acc.TryGetValue(BucketKey(channel), out var a)) a.AddLifetime(entry);
        }

        // 会话去重
        foreach (var entry in ledger.LiveEntries)
        {
            if (entry.Kind != SpendAggKind.Live) continue;
            if (entry.Timestamp < from || entry.Timestamp >= to) continue;
            if (entry.SessionId is null) continue;
            var channel = registry.Resolve(entry.ProviderId, entry.Agent);
            if (acc.TryGetValue(BucketKey(channel), out var a))
                a.SessionIds.Add(entry.SessionId);
        }

        // 排序:有数据的按用量降序在前,配置里存在但本区间没用过的排后面
        return acc.Values
            .Select(a => a.ToUsage(ledger))
            .Where(c => c.HasData || c.Configured)
            .OrderByDescending(c => c.HasData)
            .ThenByDescending(c => c.TotalTokens)
            .ToList();
    }

    /// <summary>分桶键 = 套餐名 + 数据源键（同一套餐的多个 provider id 落进同一个桶）。</summary>
    private static string BucketKey(ChannelInfo c) => c.Name + "\u0001" + (c.SourceKey ?? "");

    /// <summary>所有区间见过的渠道（不含区间筛选；列表用它显示"这台机器用过哪些套餐"）。</summary>
    public static List<ChannelUsage> BuildAllTime(SpendLedger ledger) => Build(ledger, SpendSpan.All);

    private sealed class Accumulator
    {
        private readonly ChannelInfo _channel;
        private readonly bool _configured;
        private readonly HashSet<string> _agents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _providerIds = new(StringComparer.Ordinal);
        private readonly Dictionary<DateOnly, DayAgg> _daily = new();
        private readonly Dictionary<int, HourAgg> _today = new();
        private readonly Dictionary<string, ModelAgg> _models = new(StringComparer.Ordinal);

        private TokenTally _tally;
        private long _unclassified;
        private long _unpriced;
        private double _cost;
        private TokenCost _costBreak;
        private long _requests;
        private DateTime? _first;
        private DateTime? _last;
        private DateOnly _todayDay;

        /// <summary>全时（不受区间限制）的历史:给本区间零用量的套餐显示"以前用过多少"。</summary>
        private long _lifeTokens;
        private long _lifeRequests;
        private DateTime? _lifeFirst;
        private DateTime? _lifeLast;

        public readonly HashSet<string> SessionIds = new(StringComparer.Ordinal);

        public Accumulator(ChannelInfo channel, bool configured)
        {
            _channel = channel;
            _configured = configured;
            foreach (var id in channel.ProviderIds) _providerIds.Add(id);
            _todayDay = DateOnly.FromDateTime(DateTime.Now);
        }

        public bool HasData => _tally.Total + _unclassified > 0 || _requests > 0;

        /// <summary>全时统计（区间外的记录也累加，只为"以前用过"这一行）。</summary>
        public void AddLifetime(SpendEntry e)
        {
            _lifeTokens += e.TotalTokens;
            _lifeRequests += e.Requests;
            if (_lifeFirst is null || e.Timestamp < _lifeFirst) _lifeFirst = e.Timestamp;
            if (_lifeLast is null || e.Timestamp > _lifeLast) _lifeLast = e.Timestamp;
        }

        public void Add(SpendEntry e)
        {
            _agents.Add(e.Agent);
            if (!string.IsNullOrWhiteSpace(e.ProviderId)) _providerIds.Add(e.ProviderId!);
            long tokens = e.TotalTokens;
            _tally += e.Tally;
            _unclassified += e.UnclassifiedTokens;
            _unpriced += e.UnpricedTokens;
            _requests += e.Requests;
            _cost += e.Cost.Total;
            _costBreak += e.Cost;

            var day = e.Day;
            if (!_daily.TryGetValue(day, out var d)) _daily[day] = d = new DayAgg();
            d.Add(e, tokens);

            // 今日逐小时（只留当天）
            if (day == _todayDay)
            {
                if (!_today.TryGetValue(e.Timestamp.Hour, out var h)) _today[e.Timestamp.Hour] = h = new HourAgg();
                h.Add(e);
            }

            string model = ModelPrices.DisplayName(e.Model);
            if (!_models.TryGetValue(model, out var m)) _models[model] = m = new ModelAgg();
            m.Add(e, tokens);

            if (_first is null || e.Timestamp < _first) _first = e.Timestamp;
            if (_last is null || e.Timestamp > _last) _last = e.Timestamp;
        }

        public ChannelUsage ToUsage(SpendLedger ledger)
        {            // 逐日补空白
            var daily = new List<(DateOnly, long, long, long, long, long, double, long)>();
            if (_daily.Count > 0)
            {
                var first = _daily.Keys.Min();
                var last = _daily.Keys.Max();
                for (var day = first; day <= last; day = day.AddDays(1))
                {
                    var d = _daily.TryGetValue(day, out var v) ? v : DayAgg.Empty;
                    daily.Add((day, d.Tokens, d.Input, d.Output, d.CacheRead, d.CacheWrite, d.Cost, d.Requests));
                }
            }

            // 今日 24 小时（0~23 全给，缺的补 0）
            var hours = new List<HourBucket>(24);
            for (int h = 0; h < 24; h++)
            {
                var v = _today.TryGetValue(h, out var x) ? x : HourAgg.Empty;
                hours.Add(new HourBucket(h, v.Input, v.Output, v.CacheRead, v.CacheWrite, v.Requests));
            }

            var models = _models
                .Select(kv => kv.Value.ToRow(kv.Key))
                .OrderByDescending(m => m.Tokens)
                .ToList();

            return new ChannelUsage
            {
                // Key 用渠道名（合并后的稳定标识，不是 provider id——那样同一个套餐会有多个 id）
                Key = _channel.Name + "\u0001" + (_channel.SourceKey ?? ""),
                Name = _channel.Name,
                Agent = _agents.Count switch
                {
                    // 没有任何用量记录时(配置里有、本机没用过)用配置来源兜底,
                    // 不要显示成"未知来源"
                    0 => _channel.Agent,
                    1 => _agents.First(),
                    _ => string.Join(" + ", _agents.OrderBy(x => x, StringComparer.Ordinal)),
                },
                SourceKey = _channel.SourceKey,
                ProviderIds = _providerIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Agents = _agents.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Configured = _configured,
                Enabled = _channel.Enabled,
                LifetimeFirst = _lifeFirst,
                LifetimeLast = _lifeLast,
                LifetimeTokens = _lifeTokens,
                LifetimeRequests = _lifeRequests,
                Tally = _tally,
                UnclassifiedTokens = _unclassified,
                UnpricedTokens = _unpriced,
                Cost = _cost,
                CostBreakdown = _costBreak,
                Requests = _requests,
                Sessions = SessionIds.Count,
                FirstUsed = _first,
                LastUsed = _last,
                Daily = daily.Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5, x.Item6, x.Item7, x.Item8)).ToList(),
                TodayHourly = hours,
                Models = models,
            };
        }

        private sealed class DayAgg
        {
            public long Tokens, Input, Output, CacheRead, CacheWrite, Requests;
            public double Cost;
            public static readonly DayAgg Empty = new();
            public void Add(SpendEntry e, long tokens)
            {
                Tokens += tokens; Input += e.Tally.Input; Output += e.Tally.Output;
                CacheRead += e.Tally.CacheRead; CacheWrite += e.Tally.CacheWrite;
                Cost += e.Cost.Total; Requests += e.Requests;
            }
        }

        private sealed class HourAgg
        {
            public long Input, Output, CacheRead, CacheWrite, Requests;
            public static readonly HourAgg Empty = new();
            public void Add(SpendEntry e)
            {
                Input += e.Tally.Input; Output += e.Tally.Output;
                CacheRead += e.Tally.CacheRead; CacheWrite += e.Tally.CacheWrite;
                Requests += e.Requests;
            }
        }

        private sealed class ModelAgg
        {
            public long Tokens, Requests;
            public double Cost;
            public bool HasUnpriced;
            private long _in, _cw, _cr;

            public void Add(SpendEntry e, long tokens)
            {
                Tokens += tokens; Requests += e.Requests; Cost += e.Cost.Total;
                if (e.HasUnpriced) HasUnpriced = true;
                _in += e.Tally.Input; _cw += e.Tally.CacheWrite; _cr += e.Tally.CacheRead;
            }

            public ChannelModelRow ToRow(string model)
            {
                long inputTotal = _in + _cw + _cr;
                return new ChannelModelRow(model, Tokens, Requests, Cost, HasUnpriced,
                    inputTotal > 0 ? (double)_cr / inputTotal : null);
            }
        }
    }
}
