using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PulseWin;

/// <summary>
/// 托盘图标仪表化:把**最紧张的源**(百分比最大的那一池)画成托盘上的小环,
/// 颜色语义与 rail 一致(阈值内绿、超阈值红、封顶深红)。灵感来自 Syrtis 的
/// 托盘显示模式与 CodexBar 的"菜单栏图标即仪表"——托盘常驻而 rail 会隐藏,
/// 图标本身就是最后一条读数路径。
///
/// 没有任何可画百分比的池时(比如只装了 DeepSeek 余额型)回退品牌图标;
/// 托盘悬停文字永远给全部源的摘要。**句柄必须手工释放**:Icon.GetHicon
/// 的 HICON 不进 GC 终结队列,不 DestroyIcon 就是一路泄漏直到重启。
/// </summary>
public sealed class TrayIconMeter : IDisposable
{
    private IntPtr _currentHandle;
    private Icon? _currentWrapper;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// 根据当前快照生成(图标, 悬停摘要)。调用方把图标交给 NotifyIcon.Icon,
    /// 返回 null 表示"没有可画的百分比,用品牌图标"。
    /// 句柄管理:Icon.FromHandle 是**不拥有句柄的包装**,所以这里自留 HICON、
    /// 换图标时 DestroyIcon——交给 Icon.Dispose() 反而销毁不掉,这才是泄漏源。
    /// </summary>
    public (Icon? Icon, string Tooltip) Build(IReadOnlyList<SubData> subs, Icon brand)
    {
        PoolData? worst = null;
        string worstName = "";
        foreach (var sub in subs)
        foreach (var pool in sub.Pools)
        {
            if (!pool.HasPercent) continue;
            if (worst is null || pool.Fraction > worst.Fraction)
            {
                worst = pool;
                worstName = sub.Name;
            }
        }

        var tooltip = BuildTooltip(subs);
        DisposeMeter();
        if (worst is null) return (null, tooltip);

        _currentHandle = DrawMeter(worst.Fraction, UsageTintGdi(worst));
        _currentWrapper = Icon.FromHandle(_currentHandle);
        return (_currentWrapper, $"{worstName} 最紧张:{worst.DisplayText}   ·   " + tooltip);
    }

    /// <summary>释放上一次生成的仪表图标(品牌图标归调用方所有,这里不碰)。</summary>
    public void DisposeMeter()
    {
        if (_currentHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentHandle);
            _currentHandle = IntPtr.Zero;
        }
        _currentWrapper = null;
    }

    public void Dispose() => DisposeMeter();

    private static string BuildTooltip(IReadOnlyList<SubData> subs)
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

    private static System.Drawing.Color UsageTintGdi(PoolData pool)
    {
        if (pool.IsSpent || pool.Fraction >= 1) return System.Drawing.Color.FromArgb(0xD9, 0x17, 0x21);
        if (pool.Fraction >= UsageTint.AlarmThreshold) return System.Drawing.Color.FromArgb(0xFF, 0x45, 0x3A);
        return System.Drawing.Color.FromArgb(0x00, 0xE6, 0x8C);
    }

    private static IntPtr DrawMeter(double fraction, System.Drawing.Color progress)
    {
        // 32px = 16diu × 2(本机 200% 缩放);低分屏系统自己缩到 16,高分屏才不糊
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var rect = new RectangleF(3f, 3f, 26f, 26f);
            using var track = new Pen(System.Drawing.Color.FromArgb(90, 255, 255, 255), 3.6f);
            g.DrawArc(track, rect, 0f, 360f);
            using var value = new Pen(progress, 3.6f);
            value.StartCap = LineCap.Round;
            value.EndCap = LineCap.Round;
            float sweep = (float)(Math.Clamp(fraction, 0, 1) * 360.0);
            if (sweep >= 1f) g.DrawArc(value, rect, -90f, sweep);

            string label = ((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)).ToString();
            using var font = new Font("Segoe UI", label.Length >= 3 ? 10.5f : 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(0xF5, 0xF5, 0xF7));
            g.DrawString(label, font, textBrush, new RectangleF(0, 1, 32, 32), format);
        }
        return bitmap.GetHicon();
    }
}
