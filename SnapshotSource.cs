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
}

/// <summary>一个订阅(套餐)组。</summary>
public sealed class SubData
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string AccountLabel { get; init; }
    public List<PoolData> Pools { get; init; } = new();
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
                Pools = pools,
            };
        }
        catch { return null; }
    }
}
