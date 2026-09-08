using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>详情卡窗布局:规则圆角方框(无指针),贴 rail 一侧放置。</summary>
public static class CardLayout
{
    public const double Margin = 12;
    public static readonly double GapToRail = Card.HorizontalGap;

    // 与 CardWindow.DrawPoolRow 的行距保持一致(中文字体行高偏大,取足量防溢出)
    private static double RowHeightPt = 76;

    /// <summary>账户卡(三池明细)的窗/内容框。高度按池数动态计算,防止文字溢出。</summary>
    public static (Size win, Rect body) SubCard(int poolCount)
    {
        double w = Card.Width;
        // 头部区(图标 + 两行名 + 下移留白)→ 首行 y≈58
        double h = Pt.P(58) + RowHeightPt * poolCount + Pt.P(24);
        double m = Margin;
        return (new Size(w + m * 2, h + m * 2), new Rect(m, m, w, h));
    }
}

/// <summary>详情卡窗:黑色玻璃圆角方框。显示一个订阅的 5小时/本周/总额度三池明细。</summary>
public sealed class CardWindow : Window
{
    private SubData? _sub;
    private Rect _body;

    public CardWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Native.ApplyToolWindowStyle(this);
    }

    public void Configure(SubData sub, Size winSize, Rect body)
    {
        _sub = sub;
        _body = body;
        Width = winSize.Width; Height = winSize.Height;
        InvalidateVisual();
    }

    /// <summary>只移动位置不重绘(rail 滑入/拖动时让卡片跟着走,避免与 rail 重叠)。</summary>
    public void MoveTo(Point topLeft)
    {
        Left = topLeft.X;
        Top = topLeft.Y;
    }

    public void ShowCard() => Show();

    public void HideCard() { _sub = null; Hide(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_sub is not { } sub) return;
        double dpi = Native.Scale(this);

        double r = Card.CornerRadius;
        dc.DrawRoundedRectangle(Solid(PanelPalette.Surface), null, _body, r, r);

        double x = _body.X, y = _body.Y;
        double w = _body.Width;
        double pad = Card.Padding;
        double px = x + pad;
        double contentW = w - pad * 2;

        // 组头:图标 + 名称 + 账户标识
        double iconBox = Pt.P(18);
        double py = y + pad - 2;
        DrawLogo(dc, sub, new Rect(px, py, iconBox, iconBox));
        var nameFt = Text(sub.Name, Pt.P(15), FontWeights.SemiBold, Solid(PanelPalette.Primary), dpi);
        dc.DrawText(nameFt, new Point(px + iconBox + Pt.P(7), py + (iconBox - nameFt.Height) / 2));
        var acctFt = Text(sub.AccountLabel, Pt.P(11), FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
        dc.DrawText(acctFt, new Point(px + iconBox + Pt.P(7), py + (iconBox - nameFt.Height) / 2 + nameFt.Height + 1));

        // 头部右侧:总用量。有 token 数据的源(GOAT)显示"本月已用 X.X M token";
        // 无 token 的源(GO)回退显示月度用量百分比。
        if (sub.PeriodTokens is { } tokens)
        {
            var tokenFt = Text(FormatTokens(tokens), Pt.P(17), FontWeights.Bold,
                Solid(PanelPalette.Primary), dpi);
            dc.DrawText(tokenFt, new Point(px + contentW - tokenFt.Width, py - 1));
            var labelFt = Text("本月已用 Token", Pt.P(10.5), FontWeights.Normal,
                Solid(PanelPalette.Dim), dpi);
            dc.DrawText(labelFt, new Point(px + contentW - labelFt.Width, py - 1 + tokenFt.Height + Pt.P(1)));
        }
        else if (sub.Pools.FirstOrDefault(p => p.PoolKind == "Monthly") is { IsAvailable: true } monthPool)
        {
            Color mt = UsageTint.For(monthPool.Fraction, monthPool.IsSpent);
            var usedFt = Text(Amount(monthPool.Used, monthPool.Unit), Pt.P(17), FontWeights.Bold, Solid(mt), dpi);
            dc.DrawText(usedFt, new Point(px + contentW - usedFt.Width, py - 1));
            var capFt = Text("总 " + Amount(monthPool.Cap, monthPool.Unit), Pt.P(10.5),
                FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
            dc.DrawText(capFt, new Point(px + contentW - capFt.Width, py - 1 + usedFt.Height + Pt.P(1)));
        }

        // 三池行(整体下移,头部与池行之间留出更从容的空间)
        double rowY = py + iconBox + Pt.P(26);
        foreach (var pool in sub.Pools)
            rowY = DrawPoolRow(dc, pool, new Point(px, rowY), contentW, dpi);
    }

    private double DrawPoolRow(DrawingContext dc, PoolData pool, Point p, double wdt, double dpi)
    {
        bool avail = pool.IsAvailable;
        double frac = avail ? Math.Clamp(pool.Fraction, 0, 1) : 0;
        Color tint = avail ? UsageTint.For(frac, pool.IsSpent) : PanelPalette.Track;

        // 行1:池名 + 百分比(>80% 红)
        var labelFt = Text(pool.Label, Pt.P(12.5), FontWeights.Medium, Solid(PanelPalette.Primary), dpi);
        dc.DrawText(labelFt, new Point(p.X, p.Y));
        string pct = avail ? pool.PercentText : "--";
        var pctFt = Text(pct, Pt.P(12.5), FontWeights.SemiBold, Solid(tint), dpi);
        dc.DrawText(pctFt, new Point(p.X + wdt - pctFt.Width, p.Y));

        // 行2:进度条
        double barY = p.Y + labelFt.Height + Pt.P(5);
        double barH = Card.ProgressBarHeight;
        var track = new Rect(p.X, barY, wdt, barH);
        dc.DrawRoundedRectangle(Solid(WithAlpha(PanelPalette.Primary, 0.12)), null, track, barH / 2, barH / 2);
        if (frac > 0.004)
        {
            var fill = new Rect(p.X, barY, Math.Max(wdt * frac, barH), barH);
            dc.DrawRoundedRectangle(Solid(tint), null, fill, barH / 2, barH / 2);
        }

        // 行3:用量 / 重置
        string detail = !avail ? "暂无读数"
            : $"{Amount(pool.Used, pool.Unit)} / {Amount(pool.Cap, pool.Unit)} · {pool.ResetText}";
        var detailFt = Text(detail, Pt.P(10.5), FontWeights.Normal, Solid(PanelPalette.Dim), dpi);
        double detailY = barY + barH + Pt.P(5);
        dc.DrawText(detailFt, new Point(p.X, detailY));

        return detailY + detailFt.Height + Pt.P(12);
    }

    private static string Amount(double? v, string unit) =>
        v is { } n ? (unit == "$" ? "$" + n.ToString("0.00") : n.ToString("0") + "%") : "--";

    /// <summary>Token 总量:≥1M 保留 1 位小数,<1M 保留 2 位小数,单位 M。</summary>
    private static string FormatTokens(long tokens)
    {
        double m = tokens / 1_000_000d;
        return m >= 1 ? m.ToString("0.0") + "M" : m.ToString("0.00") + "M";
    }

    private void DrawLogo(DrawingContext dc, SubData sub, Rect box)
    {
        if (sub.Key == "goat")
        {
            var bmp = Icons.GoatBitmap();
            if (bmp is not null) dc.DrawImage(bmp, box);
            return;
        }
        var g = Icons.OpenCodeGeometry().Clone();
        double s = box.Width / 24.0;
        g.Transform = new MatrixTransform(s, 0, 0, s, box.X, box.Y);
        dc.DrawGeometry(Solid(PanelPalette.Primary), null, g);
    }

    private static FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dpi) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, dpi);

    private static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);
}
