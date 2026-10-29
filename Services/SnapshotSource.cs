using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>一个额度池(5小时/本周/总额度/余额)。</summary>
public sealed class PoolData
{
    public required string Label { get; init; }
    public required string PoolKind { get; init; }
    public double? Used { get; set; }
    public double? Cap { get; set; }
    public string Unit { get; init; } = "$";
    public DateTimeOffset? ResetAt { get; set; }
    public bool IsAvailable { get; set; }

    /// <summary>余额型池(DeepSeek)的当前金额读数。</summary>
    public double? Amount { get; init; }

    /// <summary>分母是估算出来的,这里写明来源("自上次充值"/"自设预算")。</summary>
    public string? EstimateFrom { get; init; }

    /// <summary>服务端/客户端的注记(如"未设置预算""开始观察")。</summary>
    public string? Note { get; init; }

    /// <summary>赠送余额(DeepSeek 的 granted_balance)。</summary>
    public double? GrantedAmount { get; init; }

    /// <summary>自己充值的余额(DeepSeek 的 topped_up_balance)。</summary>
    public double? ToppedUpAmount { get; init; }

    /// <summary>
    /// 这个读数的口径。非空 = 来自网页控制台(与 opencode.ai 网页同一个公式);
    /// null = 默认的 API Key 接口(整数、向下取整,可能与网页差 1 个百分点)。
    /// </summary>
    public string? ReadingFrom { get; init; }

    /// <summary>已用金额(网页口径才有)。</summary>
    public double? UsedAmount { get; init; }

    /// <summary>额度上限金额(网页口径才有)。</summary>
    public double? LimitAmount { get; init; }

    public double Fraction =>
        Used is { } u && Cap is > 0 ? Math.Clamp(u / Cap.Value, 0, 1.02) : 0;

    public bool IsSpent => IsAvailable && Used is { } u && Cap is { } c && u >= c;

    public string PercentText => !IsAvailable ? "--" : $"{Fraction * 100:0}%";

    /// <summary>有分母才画得出百分比。</summary>
    public bool HasPercent => IsAvailable && Used is not null && Cap is > 0;

    /// <summary>
    /// 环上那一行读数:有百分比用百分比;没有则退回金额——DeepSeek 的"只看余额"
    /// 模式就是这样,环上没有分数可画,钱本身就是读数。
    /// </summary>
    public string DisplayText => HasPercent ? $"{Fraction * 100:0}%"
        : Amount is { } amount ? Money.Short(amount, Unit)
        : "--";

    /// <summary>这一池是否至少有东西可显示(金额也算读数)。</summary>
    public bool HasReading => IsAvailable && (Used is not null || Amount is not null);

    public string ResetText => ResetAt is { } t
        ? "重置 " + t.ToLocalTime().ToString("MM-dd HH:mm")
        : "尚未重置";

    /// <summary>实时剩余时间:≥1 天显示"剩 X天 X小时",否则"剩 X小时 X分"。</summary>
    public string RemainingText(DateTimeOffset? now = null)
    {
        if (ResetAt is not { } reset) return "尚未重置";
        var diff = reset - (now ?? DateTimeOffset.UtcNow);
        if (diff <= TimeSpan.Zero) return "即将重置";
        if (diff.TotalDays >= 1)
            return $"剩 {(int)diff.TotalDays}天 {(int)diff.Hours}小时";
        if (diff.TotalHours >= 1)
            return $"剩 {(int)diff.TotalHours}小时 {(int)diff.Minutes}分";
        return $"剩 {(int)Math.Max(diff.TotalMinutes, 1)}分";
    }
}

/// <summary>一个订阅(套餐)组。</summary>
public sealed class SubData
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string AccountLabel { get; init; }
    public long? PeriodTokens { get; init; }   // 服务端计量的已用 token(覆盖所有设备/key);窗口随源而变,见 PeriodTokensCaption
    /// <summary>
    /// 头部 token 读数下方的窗口说明。GOAT = 官方**账期**(重置会归零);
    /// OpenCode Go = 官方**滚动 30 天**(配额重置不影响它——它数的是实际消耗,不是账期扣减)。
    /// </summary>
    public string PeriodTokensCaption { get; init; } = "本月已用 Token";

    /// <summary>
    /// 这条订阅**本账期的起点**(服务端给的确切时刻)。null = 拿不到。
    /// 用量窗的「本账期」区间用它把时段分布、逐模型、性能统计切在重置那一刻。
    /// </summary>
    public DateTimeOffset? PeriodStartAt { get; init; }
    /// <summary>服务端计量的已用请求数(窗口同 <see cref="PeriodTokensCaption"/>)。</summary>
    public long? PeriodRequests { get; init; }
    /// <summary>账期已扣费用(仅 GOAT 有,单位与额度池相同 = credits)。</summary>
    public double? PeriodCost { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }  // 引擎最后一次成功同步的时刻
    public List<PoolData> Pools { get; init; } = new();

    /// <summary>
    /// 最近一次同步失败的原因(一句人话)。空 = 没失败或已恢复。
    /// 快照文件只记成功时刻,错误只活在引擎的内存状态里——这一层由
    /// <see cref="SourceStatusHub"/> 在构建时填进来,让 rail 卡片能回答"为什么停更",
    /// 而不只是"多久没更新"。
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>失败原因是凭据问题时为真:卡片据此给"去重新登录"的提示。</summary>
    public bool NeedsAuth { get; init; }

    /// <summary>
    /// 今日 0~23 点每小时的消耗金额(索引 = 小时),仅 DeepSeek 有。
    /// 来自本程序自己的余额采样,单位与账户币种一致。
    /// </summary>
    public double[]? HourlySpend { get; set; }

    /// <summary>
    /// 数据是否已过期:按同步间隔的 3 倍算(下限 3 分钟)。写死 3 分钟是错的——同步
    /// 间隔调到 5 分钟以上时,刚取回来的数据也会立刻被标成"未能刷新"。
    /// </summary>
    public bool IsStale(DateTimeOffset now) =>
        FetchedAt is { } t
        && now - t > TimeSpan.FromSeconds(Math.Max(180, AppSettings.Current.SyncIntervalSeconds * 3));
}

/// <summary>
/// 各数据源最近一次同步的失败原因(内存态,不落盘)。
/// 快照文件只记"最后一次成功"的时刻,错误只活在引擎的内存状态里;rail 卡片要回答
/// "为什么停更",就需要这一层把内存状态交给渲染(见 <see cref="SubData.LastError"/>)。
/// </summary>
public static class SourceStatusHub
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string? Error, bool NeedsAuth)> Map = new(StringComparer.Ordinal);

    /// <summary>error 传 null/空表示已恢复:清掉该源的失败记录。</summary>
    public static void Publish(string key, string? error, bool needsAuth)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(error)) Map.Remove(key);
            else Map[key] = (error, needsAuth);
        }
    }

    public static (string? Error, bool NeedsAuth) Get(string key)
    {
        lock (Gate) return Map.TryGetValue(key, out var v) ? v : (null, false);
    }
}

/// <summary>
/// 金额短格式:环上只有一行的位置,而钱是没有上限的(¥5,000.00 要 64pt,
/// 百分比"100%"只要 38pt)。所以 rail 用短写法,精确值留给 hover 卡。
/// **截断而非四舍五入**:显示得比实际多是错错了方向(999,999 是 ¥999k,
/// 不是进位后的 ¥1,000k)。
/// </summary>
public static class Money
{
    public static string Short(double value, string unit)
    {
        double magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000) return unit + Trim(value / 1_000_000, 1) + "M";
        if (magnitude >= 1_000) return unit + Trim(value / 1_000, 0) + "k";
        if (magnitude >= 100) return unit + Trim(value, 0);
        return unit + Trim(value, 1);
    }

    /// <summary>精确到分,用于卡片。</summary>
    public static string Exact(double? value, string unit) =>
        value is { } v ? unit + v.ToString("0.00", CultureInfo.InvariantCulture) : "--";

    private static string Trim(double value, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        double truncated = Math.Truncate(value * factor) / factor;
        return truncated.ToString(decimals == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 一个数据源的显示身份。三个源的差别只在标题、"月池"的叫法和头部 token 的窗口上,
/// 加载逻辑是同一套。
/// </summary>
public sealed record SourceProfile(
    string Key, string Name, string MonthlyLabel, string PeriodTokenCaption);

/// <summary>
/// 把内置引擎(移植自 GOAT-Go-Usage-Monitor)落盘的快照映射到 UI 模型。
/// 数据目录是便携的 exe\Data\(引擎构造时已自动迁移旧目录数据)。
/// </summary>
public static class SnapshotSource
{
    private static readonly AppDataPaths Paths = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string DataDirectory => Paths.RootDirectory;

    /// <summary>数据目录路径对象(用量统计等模块要拿同一份,避免各建各的)。</summary>
    public static AppDataPaths DataPaths => Paths;

    /// <summary>
    /// 各快照文件修改时间戳之和。引擎每 60s 才落盘一次,主循环每 15s 轮询时
    /// 先用它短路,可以省掉无谓的读文件+反序列化+重建对象。
    /// OpenCode Go 的**附加账号快照**(opencode-go-snapshot-*.json)也算进来,
    /// 否则附加账号同步成功后 rail 感知不到变化。
    /// </summary>
    public static long Stamp()
    {
        long stamp = 0;
        foreach (var source in SourceCatalog.All)
        {
            string file = source.SnapshotFile(Paths);
            try
            {
                if (File.Exists(file)) stamp += File.GetLastWriteTimeUtc(file).Ticks;
            }
            catch (IOException)
            {
                // 文件正被替换:当作未变化,下轮再试
            }
        }
        foreach (var (_, file) in Paths.OpenCodeInstanceSnapshots())
        {
            try
            {
                stamp += File.GetLastWriteTimeUtc(file).Ticks;
            }
            catch (IOException)
            {
            }
        }
        return stamp;
    }

    /// <summary>
    /// 按设置里勾选的源加载。关掉的源连高度都不占——rail 长度直接跟着走,
    /// 所以"只显示某几个"不需要 UI 另做隐藏逻辑。
    /// **顺序 = 设置里 VisibleSources 的顺序**(从前是三个 if + 事后排序,现在是直接遍历)。
    /// OpenCode Go 的附加账号(opencode:2…)是同一条循环里的实例键,各有各的快照。
    /// </summary>
    public static List<SubData> LoadAll()
    {
        var settings = AppSettings.Current;
        var subs = new List<SubData>(SourceCatalog.All.Count);
        foreach (var key in settings.VisibleSources)
        {
            // 解析实例键:主账号(opencode)与附加账号(opencode:2)都从这走
            if (SourceKeys.TryParse(key) is not { } parsed) continue;
            var (baseKey, instance) = parsed;
            if (SourceCatalog.ByKey(baseKey) is not { } source) continue;

            if (instance >= 2)
            {
                // 附加账号:清单里还在 + 快照已落盘才显示(没同步过就不占位)
                if (source.Key != SourceKeys.OpenCode) continue;
                if (!settings.HasOpenCodeAccount(instance)) continue;
                if (LoadOne(Paths.OpenCodeSnapshotFileFor(instance), ProfileFor(source, instance)) is not { } accountSub) continue;
                subs.Add(accountSub);
                continue;
            }

            if (LoadOne(source.SnapshotFile(Paths), ProfileFor(source, 1)) is not { } sub) continue;

            // 余额型数据源额外带上"今日每小时消耗"——那不在快照里,
            // 是本程序自己按小时采样攒出来的(见 DeepSeekLedger)
            if (source.IsBalance)
            {
                sub.HourlySpend = new DeepSeekLedger(Paths)
                    .TodayHourlySpend(sub.AccountLabel, DateTimeOffset.UtcNow);
            }
            subs.Add(sub);
        }
        return subs;
    }

    /// <summary>
    /// 加载**全部**有快照的数据源,不受 rail 可见性限制——用量窗口的额度条要列出
    /// 每个订阅的剩余与重置,哪怕这个源没在悬浮环上显示。附加账号同理:有快照、
    /// 账号还在清单里就列出来。
    /// </summary>
    public static List<SubData> LoadAllSources()
    {
        var settings = AppSettings.Current;
        var subs = new List<SubData>(SourceCatalog.All.Count);
        foreach (var source in SourceCatalog.All)
        {
            if (LoadOne(source.SnapshotFile(Paths), ProfileFor(source, 1)) is { } sub)
            {
                if (source.IsBalance)
                {
                    sub.HourlySpend = new DeepSeekLedger(Paths)
                        .TodayHourlySpend(sub.AccountLabel, DateTimeOffset.UtcNow);
                }
                subs.Add(sub);
            }

            if (source.Key != SourceKeys.OpenCode) continue;
            foreach (var (instance, file) in Paths.OpenCodeInstanceSnapshots())
            {
                if (!settings.HasOpenCodeAccount(instance)) continue;
                if (LoadOne(file, ProfileFor(source, instance)) is not { } accountSub) continue;
                subs.Add(accountSub);
            }
        }
        return subs;
    }

    /// <summary>注册表条目 → 加载用的显示身份。附加账号的键带实例号、名字带标签。</summary>
    private static SourceProfile ProfileFor(SourceDescriptor source, int instance)
    {
        string key = SourceKeys.InstanceKey(source.Key, instance);
        string name = source.RailName;
        if (instance >= 2) name = $"{source.RailName}·{AppSettings.Current.OpenCodeAccountLabel(instance)}";
        return new SourceProfile(key, name, source.MonthlyLabel, source.PeriodTokenCaption);
    }

    private static SubData? LoadOne(string file, SourceProfile profile)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(AtomicFile.ReadAllTextShared(file), JsonOptions);
            if (snapshot is null || snapshot.Windows.Count == 0) return null;

            var pools = new List<PoolData>(3);
            foreach (var w in snapshot.Windows.OrderBy(w => (int)w.Kind))
            {
                string kind = w.Kind switch
                {
                    QuotaKind.FiveHour => "FiveHour",
                    QuotaKind.Weekly => "Weekly",
                    QuotaKind.Balance => "Balance",
                    _ => "Monthly",
                };
                string label = kind switch
                {
                    "FiveHour" => "5小时",
                    "Weekly" => "本周",
                    "Balance" => "余额",
                    _ => profile.MonthlyLabel,
                };
                pools.Add(new PoolData
                {
                    Label = label,
                    PoolKind = kind,
                    Used = w.Used,
                    Cap = w.Cap,
                    Unit = w.Unit,
                    ResetAt = w.ResetAt,
                    IsAvailable = w.IsAvailable,
                    Amount = w.Amount,
                    EstimateFrom = w.EstimateFrom,
                    Note = w.Note,
                    GrantedAmount = w.GrantedAmount,
                    ToppedUpAmount = w.ToppedUpAmount,
                    ReadingFrom = w.ReadingFrom,
                    UsedAmount = w.UsedAmount,
                    LimitAmount = w.LimitAmount,
                });
            }
            return new SubData
            {
                Key = profile.Key,
                Name = profile.Name,
                AccountLabel = snapshot.AccountLabel,
                PeriodTokens = snapshot.PeriodTokens,
                // 账期口径(重置即归零)说"本账期";只有滚动 30 天时如实说"近30天"。
                // 两个标签对应的数字不是一回事,混用就会出现"配额 0% 却已用 4458M"。
                PeriodTokensCaption = snapshot.PeriodTokensFromAccountPeriod
                    ? "本账期已用 Token"
                    : profile.PeriodTokenCaption,
                PeriodStartAt = snapshot.PeriodStartAt,
                PeriodRequests = snapshot.PeriodRequests,
                PeriodCost = snapshot.PeriodCost,
                FetchedAt = snapshot.FetchedAt,
                Pools = pools,
                LastError = SourceStatusHub.Get(profile.Key).Error,
                NeedsAuth = SourceStatusHub.Get(profile.Key).NeedsAuth,
            };
        }
        catch (Exception ex)
        {
            // 不再静默吞掉:快照读取失败(文件被占/JSON 结构变化)留下痕迹,
            // 否则只表现为"没有数据",排查全靠猜。
            Diagnostics.Note($"读取快照失败 {Path.GetFileName(file)}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }
}
