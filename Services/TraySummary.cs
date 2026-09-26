namespace PulseWin;

/// <summary>
/// 托盘**悬停摘要**的生成器。历史上这里还把"最紧张的额度池"画成托盘小环,
/// v1.12.2 起**托盘常驻品牌图标**——用户明确要求托盘/任务栏图标保持产品图标
/// (小环在任务栏里根本认不出是什么程序)。额度读数仍可从 rail、悬停摘要与
/// 阈值告警气泡拿到,信息没有丢。
///
/// 悬停文字仍然有用:托盘常驻而 rail 会隐藏,摘要就是最后一层轻量读数。
/// </summary>
internal static class TraySummary
{
    public static string BuildTooltip(IReadOnlyList<SubData> subs)
    {
        if (subs.Count == 0) return "Pulse — 没有已启用的数据源(双击显示/隐藏)";
        // NotifyIcon.Text 上限 63 字符,超了截断——三个源都装上时摘要要紧凑
        var parts = subs.Select(sub =>
        {
            var pool = sub.Pools.Where(p => p.HasReading).OrderByDescending(p => p.Fraction).FirstOrDefault();
            if (pool is null) return sub.Name;
            return pool.HasPercent
                ? $"{sub.Name} {pool.DisplayText}"
                : $"{sub.Name} {Money.Short(pool.Amount ?? 0, pool.Unit)}";
        });
        return string.Join(" · ", parts) + "(双击显示/隐藏)";
    }
}
