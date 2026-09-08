using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 把 Pulse 的 SwiftUI Shape 移植成 WPF StreamGeometry。所有形状都按
/// "面向右"画一次,再经变换摆到左/上/浮动位置(与 mac 版同一思路)。
/// </summary>
public static class RailGeometry
{
    /// <summary>squircle 超椭圆指数(mac 用 4,接近 Apple continuous 圆角)。</summary>
    private const double SquircleExponent = 4;
    private const int CornerSamples = 48;

    /// <summary>画 1/4 超椭圆角:center 到角心,from/to 是中心到起/终点的单位方向。</summary>
    private static void AppendQuarter(StreamGeometryContext ctx, Point center, double radius,
        Vector from, Vector to)
    {
        if (radius <= 0.01) { ctx.LineTo(center + radius * from, true, false); return; }
        for (int step = 1; step <= CornerSamples; step++)
        {
            double t = step / (double)CornerSamples * (Math.PI / 2);
            double along = Math.Pow(Math.Cos(t), 2 / SquircleExponent);
            double across = Math.Pow(Math.Sin(t), 2 / SquircleExponent);
            ctx.LineTo(center + radius * (from * along + to * across), true, false);
        }
    }

    /// <summary>连续圆角矩形(squircle 角),用于卡身。</summary>
    public static StreamGeometry SquircleRoundRect(double x, double y, double w, double h, double r)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        r = Math.Clamp(r, 0, Math.Min(w, h) / 2);
        ctx.BeginFigure(new Point(x + r, y), true, true);
        ctx.LineTo(new Point(x + w - r, y), true, false);
        AppendQuarter(ctx, new Point(x + w - r, y + r), r, new Vector(1, 0), new Vector(0, 1)); // 右上
        ctx.LineTo(new Point(x + w, y + h - r), true, false);
        AppendQuarter(ctx, new Point(x + w - r, y + h - r), r, new Vector(0, 1), new Vector(-1, 0)); // 右下
        ctx.LineTo(new Point(x + r, y + h), true, false);
        AppendQuarter(ctx, new Point(x + r, y + h - r), r, new Vector(-1, 0), new Vector(0, -1)); // 左下
        ctx.LineTo(new Point(x, y + r), true, false);
        AppendQuarter(ctx, new Point(x + r, y + r), r, new Vector(0, -1), new Vector(1, 0)); // 左上
        return g;
    }

    /// <summary>
    /// berth 外形。canonical 是竖放(面向右)的 rail rect;edge 决定最后摆向。
    /// openness:1 全展开,0 折叠成贴边 sliver(仅 docked 有意义)。
    /// </summary>
    public static Geometry Berth(Rect rect, DockEdge edge, bool isDocked, double openness)
    {
        if (!isDocked || edge == DockEdge.Floating)
        {
            // 自由悬浮 = 真胶囊(圆端),不是切角。
            double rad = Math.Min(rect.Width, rect.Height) / 2;
            var c = new StreamGeometry();
            using var ctx = c.Open();
            ctx.BeginFigure(new Point(rect.X + rad, rect.Y), true, true);
            ctx.ArcTo(new Point(rect.X + rad, rect.Bottom), new Size(rad, rad), 0, true, SweepDirection.Clockwise, true, false);
            ctx.ArcTo(new Point(rect.X + rad, rect.Y), new Size(rad, rad), 0, true, SweepDirection.Clockwise, true, false);
            return c;
        }

        // 以 "面向右" 的姿态画在竖直 rect 上(宽 = rail 厚,高 = rail 长)。
        Rect v = edge == DockEdge.Top
            ? new Rect(0, 0, rect.Height, rect.Width)
            : new Rect(0, 0, rect.Width, rect.Height);

        var g = FacingRight(v, openness);
        switch (edge)
        {
            case DockEdge.Left:
                g.Transform = new MatrixTransform(new Matrix(-1, 0, 0, 1, rect.Width, 0));
                break;
            case DockEdge.Top:
                g.Transform = new MatrixTransform(new Matrix(0, -1, 1, 0, 0, rect.Height));
                break;
        }
        return g;
    }

    /// <summary>面向右的 rail:右缘直边贴屏幕,上/下端凹弧 flare 融入边缘。</summary>
    private static StreamGeometry FacingRight(Rect rect, double openness)
    {
        double w = rect.Width, h = rect.Height;
        double flareH = Dock.FlareHeight * openness;
        double flareW = Dock.FlareWidth * openness;
        double collapsedR = Dock.CollapsedWidth;
        double cornerR = collapsedR + (Dock.CornerRadius - collapsedR) * openness;

        double f = Math.Min(flareH, h / 2);
        double r = Math.Clamp(cornerR, 0, Math.Min(w, (h - f * 2) / 2));
        double fw = Math.Clamp(flareW, 0, w - r);
        double k = 0.55; // 贝塞尔凹弧控制点系数(mac 原值)

        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(r, f), true, true);
        ctx.LineTo(new Point(w - fw, f), true, false);
        // 上端凹弧,向上挑到屏幕边缘 (w,0)
        ctx.BezierTo(
            new Point(w - fw * (1 - k), f),
            new Point(w, f * k),
            new Point(w, 0), true, false);
        ctx.LineTo(new Point(w, h), true, false);
        // 下端镜像凹弧回到 rail 体
        ctx.BezierTo(
            new Point(w, h - f * k),
            new Point(w - fw * (1 - k), h - f),
            new Point(w - fw, h - f), true, false);
        ctx.LineTo(new Point(r, h - f), true, false);
        // 左下 squircle 角
        AppendQuarter(ctx, new Point(r, h - f - r), r, new Vector(0, 1), new Vector(-1, 0));
        ctx.LineTo(new Point(0, f + r), true, false);
        AppendQuarter(ctx, new Point(r, f + r), r, new Vector(-1, 0), new Vector(0, -1));
        return g;
    }

    /// <summary>整圆轨迹(环轨道/时钟轨道用 Pen 描边)。</summary>
    public static EllipseGeometry Circle(Point c, double radius) =>
        new(c, radius, radius);

    /// <summary>
    /// 从 12 点方向顺时针扫 sweepDeg 的中心线圆弧(配合 round-cap Pen 即得
    /// SwiftUI stroke 效果)。sweep 接近 360° 时退回整圆,避免 ArcSegment 退化。
    /// </summary>
    public static Geometry Arc(Point c, double radius, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.9) return Circle(c, radius);

        double DegToRad = Math.PI / 180;
        Point P(double deg)
        {
            double rad = deg * DegToRad;
            return new Point(c.X + radius * Math.Sin(rad), c.Y - radius * Math.Cos(rad));
        }

        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(P(startDeg), false, false);
        double endDeg = startDeg + sweepDeg;
        ctx.ArcTo(P(endDeg), new Size(radius, radius), 0, Math.Abs(sweepDeg) > 180,
            sweepDeg > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        return g;
    }

    /// <summary>圆角 Pen:环弧的两端是圆头(同 mac lineCap .round)。</summary>
    public static Pen RingPen(Brush brush, double width)
    {
        var p = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }
}
