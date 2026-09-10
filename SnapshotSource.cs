using System.IO;
using System.Text.Json;

namespace PulseWin;

/// <summary>一个额度池(5小时/本周/总额度)。</summary>
public sealed class PoolData
{
    public required string Label { get; init; }
    public required string PoolKind { get; init; }
    public double? Used { get; set; }
    public double? Cap { get; set; }
    public string Unit { get; init; } = "$";
    public DateTimeOffset? ResetAt { get; set; }
    public bool IsAvailable { get; set; }

    public double Fraction =>
        Used is { } u && Cap is > 0 ? Math.Clamp(u / Cap.Value, 0, 1.02) : 0;

    public bool IsSpent => IsAvailable && Used is { } u && Cap is { } c && u >= c;

    public string PercentText => !IsAvailable ? "--" : $"{Fraction * 100:0}%";

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
    public long? PeriodTokens { get; init; }   // 本月已用 token(仅 GOAT 有)
    public DateTimeOffset? FetchedAt { get; init; }  // 引擎最后一次成功同步的时刻
    public List<PoolData> Pools { get; init; } = new();

    /// <summary>数据是否已过期(超过 3 个同步周期没有更新)。</summary>
    public bool IsStale(DateTimeOffset now) =>
        FetchedAt is { } t && now - t > TimeSpan.FromMinutes(3);
}

/// <summary>
/// 把内置引擎(移植自 GOAT-Go-Usage-Monitor)落盘的快照映射到 UI 模型。
/// 数据目录是便携的 exe\Data\(引擎构造时已自动迁移旧目录数据)。
/// </summary>
public static class SnapshotSource
{
    private static readonly AppDataPaths Paths = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string DataDirectory => Paths.RootDirectory;

    /// <summary>
    /// 两个快照文件的修改时间戳之和。引擎每 60s 才落盘一次,主循环每 15s 轮询时
    /// 先用它短路,可以省掉无谓的读文件+反序列化+重建对象。
    /// </summary>
    public static long Stamp()
    {
        long stamp = 0;
        foreach (var file in new[] { Paths.SnapshotFile, Paths.OpenCodeSnapshotFile })
        {
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

    public static List<SubData> LoadAll()
    {
        var subs = new List<SubData>(2);
        var goat = LoadOne(Paths.SnapshotFile, isGoat: true);
        if (goat is not null) subs.Add(goat);
        var go = LoadOne(Paths.OpenCodeSnapshotFile, isGoat: false);
        if (go is not null) subs.Add(go);
        return subs;
    }

    private static SubData? LoadOne(string file, bool isGoat)
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
                    _ => "Monthly",
                };
                string label = kind switch
                {
                    "FiveHour" => "5小时",
                    "Weekly" => "本周",
                    _ => isGoat ? "总额度" : "本月",
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
                });
            }
            return new SubData
            {
                Key = isGoat ? "goat" : "opencode",
                Name = isGoat ? "GOAT" : "GO",
                AccountLabel = snapshot.AccountLabel,
                PeriodTokens = snapshot.PeriodTokens,
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
