using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>卡窗里指针凸出的朝向。</summary>
public enum PointerSide { TowardRight, TowardLeft, TowardUp }

/// <summary>
/// 卡片 = 圆角体 + 指向 rail 的尖角,单条连续轮廓(把凸出插进 body 边缘,
/// 天然同向绕行,不会出现双 subpath 反向抵消出的洞)。
/// </summary>
public static class CardGeometry
{
    private const int CornerSamples = 24;
    private const int BezierSamples = 10;

    /// <summary>
    /// 生成卡身 + 指针的连续轮廓点(顺时针)。bodyRect 是卡身圆角矩形;
    /// reach 是尖端伸出卡身侧边的距离;centre 沿凸出侧测量(Right/Left 为 y,
    /// Up 为 x);half = 指针基部半宽。
    /// </summary>
    public static Point[] Build(Rect bodyRect, double cornerR, PointerSide side,
        double reach, double half, double centre)
    {
        double x = bodyRect.X, y = bodyRect.Y, w = bodyRect.Width, h = bodyRect.Height;
        double r = Math.Clamp(cornerR, 0, Math.Min(w, h) / 2);

        // —— body 顺时针轮廓(右缘直段留待替换) ——
        var pts = new List<Point> { new(x + r, y) };
        void Line(double px, double py) => pts.Add(new Point(px, py));
        void Corner(double cx, double cy, Vector from, Vector to)
        {
            for (int i = 1; i <= CornerSamples; i++)
            {
                double t = i / (double)CornerSamples * (Math.PI / 2);
                double a = Math.Pow(Math.Cos(t), 0.5);   // ^(2/4)
                double b = Math.Pow(Math.Sin(t), 0.5);
                pts.Add(new Point(cx + r * (from.X * a + to.X * b),
                                  cy + r * (from.Y * a + to.Y * b)));
            }
        }
        Line(x + w - r, y);
        Corner(x + w - r, y + r, new Vector(1, 0), new Vector(0, 1));  // 到 (x+w, y+r)
        Line(x + w, y + h - r);
        Corner(x + w - r, y + h - r, new Vector(0, 1), new Vector(-1, 0)); // 到 (x+w-r, y+h)
        Line(x + r, y + h);
        Corner(x + r, y + h - r, new Vector(-1, 0), new Vector(0, -1)); // 到 (x, y+h-r)
        Line(x, y + r);
        Corner(x + r, y + r, new Vector(0, -1), new Vector(1, 0));     // 回到 (x+r, y)

        // —— 在目标直缘段上把 [centre-half, centre+half] 替换为指针凸出 ——
        // 直缘端点(排除圆角)
        double low, high; Point outBase;
        Func<double, Point> pointOnEdge;
        switch (side)
        {
            case PointerSide.TowardRight:
                low = y + r + 0.5; high = y + h - r - 0.5; outBase = new Point(x + w, 0);
                pointOnEdge = t => new Point(x + w, t);
                break;
            case PointerSide.TowardLeft:
                low = y + r + 0.5; high = y + h - r - 0.5; outBase = new Point(x, 0);
                pointOnEdge = t => new Point(x, t);
                break;
            default: // TowardUp
                low = x + r + 0.5; high = x + w - r - 0.5; outBase = new Point(0, y);
                pointOnEdge = t => new Point(t, y);
                break;
        }
        centre = Math.Clamp(centre, low + half, high - half);
        if (high - low < half * 2 + 2) { centre = (low + high) / 2; half = Math.Min(half, (high - low) / 2 - 1); }

        // 在轮廓里定位旧直线上 [start, end] 的点区间,整段换掉
        double start = centre - half, end = centre + half;
        bool Along(Point p)
        {
            switch (side)
            {
                case PointerSide.TowardRight:
                case PointerSide.TowardLeft:
                    return Math.Abs(p.X - (side == PointerSide.TowardRight ? x + w : x)) < 0.01 && p.Y >= low && p.Y <= high;
                default:
                    return Math.Abs(p.Y - y) < 0.01 && p.X >= low && p.X <= high;
            }
        }
        double EdgeVal(Point p) => side is PointerSide.TowardUp ? p.X : p.Y;

        Point[] body = pts.ToArray();
        int iStart = -1, iEnd = -1;
        for (int i = 0; i < body.Length; i++)
        {
            if (!Along(body[i])) continue;
            double v = EdgeVal(body[i]);
            if (iStart < 0 && v >= start - 0.01) iStart = i;
            if (v <= end + 0.01) iEnd = i;
        }

        var result = new List<Point>();
        if (iStart <= 0 || iEnd < iStart)
        {
            // 异常兜底:不加凸出,直接返回 body
            return body;
        }
        for (int i = 0; i <= iStart; i++) result.Add(body[i]);

        // 贝塞尔凸出(从 body 边缘出发,探到 tip,回到边缘)
        var (a0, a1, a2, a3, b0, b1, b2, b3) = PointerBezier(side, pointOnEdge, centre, half, reach, outBase);
        foreach (var p in Sample(a0, a1, a2, a3)) result.Add(p);
        foreach (var p in Sample(b0, b1, b2, b3)) result.Add(p);

        for (int i = iEnd + 1; i < body.Length; i++) result.Add(body[i]);
        return result.ToArray();
    }

    private static IEnumerable<Point> Sample(Point p0, Point p1, Point p2, Point p3)
    {
        for (int i = 1; i <= BezierSamples; i++)
        {
            double t = i / (double)BezierSamples;
            double u = 1 - t;
            yield return new Point(
                u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
        }
    }

    /// <summary>两段三次贝塞尔:边缘→尖→边缘(mac 的控制点比例 0.24/0.55)。</summary>
    private static (Point, Point, Point, Point, Point, Point, Point, Point) PointerBezier(
        PointerSide side, Func<double, Point> edge, double centre, double half, double reach, Point basePt)
    {
        Point OnEdge(double off) => edge(centre + off);
        switch (side)
        {
            case PointerSide.TowardRight:
            {
                var p0 = OnEdge(-half); var p3 = OnEdge(half);
                var tip = basePt with { X = basePt.X + reach, Y = centre };
                var c1 = new Point(p0.X + reach * 0.24, centre - half * 0.44);
                var c2 = new Point(p0.X + reach * 0.55, centre - half * 0.24);
                var d1 = new Point(p0.X + reach * 0.55, centre + half * 0.24);
                var d2 = new Point(p0.X + reach * 0.24, centre + half * 0.44);
                return (p0, c1, c2, tip, tip, d1, d2, p3);
            }
            case PointerSide.TowardLeft:
            {
                var p0 = OnEdge(-half); var p3 = OnEdge(half);
                var tip = basePt with { X = basePt.X - reach, Y = centre };
                var c1 = new Point(p0.X - reach * 0.24, centre - half * 0.44);
                var c2 = new Point(p0.X - reach * 0.55, centre - half * 0.24);
                var d1 = new Point(p0.X - reach * 0.55, centre + half * 0.24);
                var d2 = new Point(p0.X - reach * 0.24, centre + half * 0.44);
                return (p0, c1, c2, tip, tip, d1, d2, p3);
            }
            default: // TowardUp:尖端朝上(负 y)
            {
                var p0 = OnEdge(-half); var p3 = OnEdge(half);
                var tip = basePt with { X = centre, Y = basePt.Y - reach };
                var c1 = new Point(centre - half * 0.44, p0.Y - reach * 0.24);
                var c2 = new Point(centre - half * 0.24, p0.Y - reach * 0.55);
                var d1 = new Point(centre + half * 0.24, p0.Y - reach * 0.55);
                var d2 = new Point(centre + half * 0.44, p0.Y - reach * 0.24);
                return (p0, c1, c2, tip, tip, d1, d2, p3);
            }
        }
    }

    /// <summary>把轮廓点连成封闭 StreamGeometry。</summary>
    public static StreamGeometry ToGeometry(Point[] contour)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(contour[0], true, true);
        for (int i = 1; i < contour.Length; i++) ctx.LineTo(contour[i], true, false);
        return g;
    }
}
