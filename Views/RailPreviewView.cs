using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 设置页的 rail 实时预览：用**与主窗同一套几何公式**（MainWindow.RingSizeTable 等）
/// 画一个缩小版的贴边 rail，尺寸/间距/读数开关/背景不透明度一改就重画。
///
/// 为什么不用真的 MainWindow 截图：那是另一个窗口、还要真实快照与指针状态，
/// 放进设置页既慢又不可控。这里用固定的演示数据（GOAT 正在使用、GO 空闲、
/// DeepSeek 余额型），只为看**样式**，不代表真实额度。
/// </summary>
internal sealed class RailPreviewView : FrameworkElement
{
    /// <summary>演示数据：三个源，覆盖"有内圈/无内圈/余额型"三种形态。</summary>
    private static readonly List<SubData> Demo =
    [
        new SubData
        {
            Key = "goat", Name = "GOAT", AccountLabel = "演示",
            Pools =
            {
                new PoolData { Label = "5小时", PoolKind = "FiveHour", Unit = "cr", Used = 4.5, Cap = 14, IsAvailable = true },
                new PoolData { Label = "本月", PoolKind = "Monthly", Unit = "cr", Used = 60.2, Cap = 70, IsAvailable = true },
            },
        },
        new SubData
        {
            Key = "opencode", Name = "GO", AccountLabel = "演示",
            Pools =
            {
                new PoolData { Label = "5小时", PoolKind = "FiveHour", Unit = "%", Used = 0, Cap = 100, IsAvailable = true },
                new PoolData { Label = "本月", PoolKind = "Monthly", Unit = "%", Used = 27, Cap = 100, IsAvailable = true },
            },
        },
        new SubData
        {
            Key = "deepseek", Name = "DeepSeek", AccountLabel = "演示",
            Pools =
            {
                new PoolData
                {
                    Label = "余额", PoolKind = "Balance", Unit = "$", Amount = 12.34,
                    Used = 7.66, Cap = 20.0, IsAvailable = true,
                },
            },
        },
    ];

    private static readonly Typeface Face =
        new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold,
            FontStretches.Normal);

    /// <summary>设置变了：重画一帧。</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 8 || h <= 8) return;

        // 桌面底 + 右侧一条"屏幕边缘"，rail 贴在它旁边
        dc.DrawRoundedRectangle(UiTheme.B(UiTheme.IsDark ? Color.FromRgb(0x22, 0x22, 0x28)
                                                        : Color.FromRgb(0xE7, 0xE7, 0xEC)),
            null, new Rect(0, 0, w, h), 10, 10);

        var s = AppSettings.Current;
        int size = s.RingSize;
        var table = MainWindow.RingSizeTable(size);
        bool showPercent = s.ShowPercent;

        double unitHpt = showPercent
            ? MainWindow.RingCenterInUnit + MainWindow.RingOuterRadius + MainWindow.RingToTextGap
              + MainWindow.TextLineHeight
            : MainWindow.RingCenterInUnit + MainWindow.RingOuterRadius - 2;
        double baseGap = s.RingSpacing switch { 0 => 2, 2 => 10, _ => 4 };
        double gapPt = showPercent ? baseGap : Math.Min(baseGap, 2);
        double padBottomPt = MainWindow.PadTopPt
                             + (MainWindow.RingCenterInUnit - MainWindow.RingOuterRadius)
                             - (showPercent ? 0 : 2);
        double railHpt = MainWindow.PadTopPt + Demo.Count * unitHpt + (Demo.Count - 1) * gapPt
                         + padBottomPt;
        double railWpt = table.RailW;

        double scale = Math.Min(1, (h - 20) / Pt.P(railHpt));
        double railW = Pt.P(railWpt) * scale;
        double railH = Pt.P(railHpt) * scale;
        double x = w - railW - 12;
        double y = (h - railH) / 2;
        double radius = Math.Min(Dock.CornerRadius * (railWpt / 56.0) * scale, railW / 2);

        // rail 本体：黑色玻璃，不透明度跟设置走
        byte alpha = (byte)Math.Clamp(s.SurfaceOpacity * 255, 0, 255);
        var body = new Rect(x, y, railW, railH);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(alpha, 0x0A, 0x0A, 0x0E)),
            null, body, radius, radius);
        // 与主窗同一套玻璃层，预览才不会跟真实 rail 长得不一样
        GlassPaint.Paint(dc, GlassPaint.RoundRect(body, radius), body, GlassPaint.Strength);

        double centerX = x + railW / 2;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < Demo.Count; i++)
        {
            double centerY = y + Pt.P(MainWindow.PadTopPt + i * (unitHpt + gapPt)
                                       + MainWindow.RingCenterInUnit) * scale;
            DrawRing(dc, Demo[i], new Point(centerX, centerY), table, scale, i == 0, showPercent, dpi);
        }
    }

    private static void DrawRing(DrawingContext dc, SubData sub, Point c,
        (double RailW, double OuterD, double OuterW, double InnerD, double InnerW) table,
        double scale, bool burning, bool showPercent, double dpi)
    {
        var five = sub.Pools.FirstOrDefault(p => p.PoolKind == "FiveHour");
        var month = sub.Pools.FirstOrDefault(p => p.PoolKind == "Monthly");
        var balance = sub.Pools.FirstOrDefault(p => p.PoolKind == "Balance");

        var track = new Pen(UiTheme.B(PanelPalette.Track), Pt.P(table.OuterW) * scale);
        double outerR = Pt.P(table.OuterD) / 2 * scale;

        // 外圈轨道 + 外圈用量
        dc.DrawEllipse(null, track, c, outerR, outerR);
        var headline = balance ?? month;
        if (headline is { IsAvailable: true })
            DrawArc(dc, c, outerR, TintFor(headline), headline.Fraction, Pt.P(table.OuterW) * scale);

        // 内圈：只在"正在使用"时出现（与主窗同一条规则）
        if (balance is null && (burning || !AppSettings.Current.HideIdleInnerRing))
        {
            double innerR = Pt.P(table.InnerD) / 2 * scale;
            var innerTrack = new Pen(UiTheme.B(PanelPalette.Track),
                Pt.P(table.InnerW) * scale);
            dc.DrawEllipse(null, innerTrack, c, innerR, innerR);
            if (five is { IsAvailable: true, HasPercent: true })
                DrawArc(dc, c, innerR, TintFor(five), five.Fraction, Pt.P(table.InnerW) * scale);
        }

        DrawIcon(dc, sub.Key, c, scale);

        if (!showPercent || headline is not { IsAvailable: true }) return;
        string label = headline.DisplayText;
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, Pt.P(headline.PoolKind == "Balance" ? 12 : 13) * scale,
            UiTheme.B(TintFor(headline)), dpi);
        dc.DrawText(text, new Point(c.X - text.Width / 2,
            c.Y + outerR + Pt.P(MainWindow.RingToTextGap) * scale));
    }

    private static Color TintFor(PoolData pool) =>
        UsageTint.For(Math.Clamp(pool.Fraction, 0, 1), pool.IsSpent);

    /// <summary>从 12 点方向顺时针画用量弧。</summary>
    private static void DrawArc(DrawingContext dc, Point c, double r, Color tint, double fraction, double width)
    {
        double sweep = Math.Clamp(fraction, 0, 1) * 360;
        var pen = new Pen(UiTheme.B(tint), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (sweep >= 359.5)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        if (sweep <= 0.3) return;

        // 与主窗同一条规则：起点 9 点方向（-90°），两端各留 1.2° 的缝，短弧不留
        const double gap = 1.2;
        if (sweep > 3)
        {
            sweep -= gap * 2;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var start = PointOn(c, r, -90 + gap);
                var end = PointOn(c, r, -90 + gap + sweep);
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            return;
        }

        var shortArc = new StreamGeometry();
        using (var ctx = shortArc.Open())
        {
            var start = PointOn(c, r, -90);
            var end = PointOn(c, r, -90 + sweep);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        shortArc.Freeze();
        dc.DrawGeometry(null, pen, shortArc);
    }

    /// <summary>0° = 12 点方向，顺时针为正（与 <see cref="RailGeometry.Arc"/> 同一套角度）。</summary>
    private static Point PointOn(Point c, double r, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(rad), c.Y - r * Math.Cos(rad));
    }

    private static void DrawIcon(DrawingContext dc, string key, Point c, double scale)
    {
        string baseKey = SourceKeys.BaseKey(key);
        double box = Pt.P(baseKey == "deepseek" ? 17 : 14) * scale;
        var white = UiTheme.B(PanelPalette.Primary);

        switch (baseKey)
        {
            case "goat" when Icons.GoatBitmap() is { } bitmap:
                dc.DrawImage(bitmap, new Rect(c.X - box / 2, c.Y - box / 2, box, box));
                break;
            case "deepseek":
                var whale = Icons.DeepSeekGeometry().Clone();
                double ws = box / 24.0;
                whale.Transform = new MatrixTransform(ws, 0, 0, ws, c.X - box / 2, c.Y - box / 2);
                dc.DrawGeometry(white, null, whale);
                break;
            default:
                var geometry = Icons.OpenCodeGeometry().Clone();
                double gs = box / 24.0;
                geometry.Transform = new MatrixTransform(gs, 0, 0, gs, c.X - box / 2, c.Y - box / 2);
                dc.DrawGeometry(white, null, geometry);
                break;
        }
    }
}
