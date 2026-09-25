namespace PulseWin;

/// <summary>
/// 消耗预测:按本程序自己观测到的"已用比例随时间上涨"估算"照当前速度几小时后用完"。
///
/// 证据链是内存里的采样点(引擎每次成功同步留一笔,约 60s 一个),进程重启清零——
/// 所以刚启动的几分钟里没有预测,这是诚实而不是缺陷。采样被打断(充值/重置导致
/// 读数大幅回落)时清空重计。上游的 BurnRate 同一原则:证据不足就什么都不说。
/// </summary>
public static class BurnRate
{
    private sealed class Track
    {
        public readonly List<(double T, double Used)> Points = new();
    }

    private static readonly Dictionary<string, Track> Tracks = new();
    private static readonly object Gate = new();

    /// <summary>一次成功同步后的采样。used 0~1。</summary>
    public static void Observe(string poolKey, double usedFraction, double nowSeconds)
    {
        lock (Gate)
        {
            if (!Tracks.TryGetValue(poolKey, out var t)) { t = new Track(); Tracks[poolKey] = t; }
            var pts = t.Points;

            // 读数大幅回落 = 重置或换池,历史作废
            if (pts.Count > 0 && usedFraction < pts[^1].Used - 0.02) pts.Clear();
            pts.Add((nowSeconds, Math.Clamp(usedFraction, 0, 1)));
            // 只留最近 30 分钟
            pts.RemoveAll(p => nowSeconds - p.T > 1800);
        }
    }

    /// <summary>
    /// 估算。返回 (到耗尽的小时数, 是否在 <paramref name="hoursLeft"/> 内耗尽)。
    /// 证据不足(样本少/时间跨度短/速率非正)返回 null——调用方就不画这一行。
    /// </summary>
    public static (double Hours, bool Exhausts)? Estimate(string poolKey, double? hoursLeft)
    {
        lock (Gate)
        {
            if (!Tracks.TryGetValue(poolKey, out var t) || t.Points.Count < 3) return null;
            var pts = t.Points;
            double span = pts[^1].T - pts[0].T;
            if (span < 180) return null;   // 至少观测 3 分钟
            double du = pts[^1].Used - pts[0].Used;
            if (du <= 0.0005) return null; // 没在动,不猜
            double perHour = du / (span / 3600);
            double remaining = Math.Clamp(1 - pts[^1].Used, 0, 1);
            double hours = remaining / perHour;
            if (hours > 24 * 30) return null; // 一个月开外,没意义
            return hoursLeft is { } left ? (hours, hours < left) : (hours, false);
        }
    }

    /// <summary>人类读法:2.4h → "2.4 小时",50h → "2 天"。</summary>
    public static string Describe(double hours) => hours < 48
        ? $"{hours:0.#} 小时"
        : $"{hours / 24:0.#} 天";
}
