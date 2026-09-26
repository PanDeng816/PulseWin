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
    public long? PeriodTokens { get; init; }   // 账期已用 token(仅 GOAT 有;服务端计量,覆盖所有设备/key)
    /// <summary>账期已用请求数(仅 GOAT 有,服务端计量)。</summary>
    public long? PeriodRequests { get; init; }
    /// <summary>账期已扣费用(仅 GOAT 有,单位与额度池相同 = credits)。</summary>
    public double? PeriodCost { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }  // 引擎最后一次成功同步的时刻
    public List<PoolData> Pools { get; init; } = new();

    /// <summary>
    /// 今日 0~23 点每小时的消耗金额(索引 = 小时),仅 DeepSeek 有。
    /// 来自本程序自己的余额采样,单位与账户币种一致。
    /// </summary>
    public double[]? HourlySpend { get; set; }

    /// <summary>数据是否已过期(超过 3 个同步周期没有更新)。</summary>
    public bool IsStale(DateTimeOffset now) =>
        FetchedAt is { } t && now - t > TimeSpan.FromMinutes(3);
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
/// 一个数据源的显示身份。三个源的差别只在标题和"月池"的叫法上,
/// 加载逻辑是同一套。
/// </summary>
public sealed record SourceProfile(string Key, string Name, string MonthlyLabel);

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
        return stamp;
    }

    /// <summary>
    /// 按设置里勾选的源加载。关掉的源连高度都不占——rail 长度直接跟着走,
    /// 所以"只显示某几个"不需要 UI 另做隐藏逻辑。
    /// **顺序 = 设置里 VisibleSources 的顺序**(从前是三个 if + 事后排序,现在是直接遍历)。
    /// </summary>
    public static List<SubData> LoadAll()
    {
        var settings = AppSettings.Current;
        var subs = new List<SubData>(SourceCatalog.All.Count);
        foreach (var key in settings.VisibleSources)
        {
            if (SourceCatalog.ByKey(key) is not { } source) continue;
            if (LoadOne(source.SnapshotFile(Paths), ProfileFor(source)) is not { } sub) continue;

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
    /// 每个订阅的剩余与重置,哪怕这个源没在悬浮环上显示。
    /// </summary>
    public static List<SubData> LoadAllSources()
    {
        var subs = new List<SubData>(SourceCatalog.All.Count);
        foreach (var source in SourceCatalog.All)
        {
            if (LoadOne(source.SnapshotFile(Paths), ProfileFor(source)) is not { } sub) continue;
            if (source.IsBalance)
            {
                sub.HourlySpend = new DeepSeekLedger(Paths)
                    .TodayHourlySpend(sub.AccountLabel, DateTimeOffset.UtcNow);
            }
            subs.Add(sub);
        }
        return subs;
    }

    /// <summary>注册表条目 → 加载用的显示身份。</summary>
    private static SourceProfile ProfileFor(SourceDescriptor source) =>
        new(source.Key, source.RailName, source.MonthlyLabel);

    private static SubData? LoadOne(string file, SourceProfile profile)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(File.ReadAllText(file), JsonOptions);
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
                });
            }
            return new SubData
            {
                Key = profile.Key,
                Name = profile.Name,
                AccountLabel = snapshot.AccountLabel,
                PeriodTokens = snapshot.PeriodTokens,
                PeriodRequests = snapshot.PeriodRequests,
                PeriodCost = snapshot.PeriodCost,
                FetchedAt = snapshot.FetchedAt,
                Pools = pools,
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
