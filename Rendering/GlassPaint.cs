using System.Windows;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// rail 与悬浮卡的**自绘玻璃层**。
///
/// 背景：rail 是分层窗口（per-pixel alpha + 自绘圆弧），系统 blur 对它有两个硬伤
/// （模糊区只能是矩形、跟不了圆弧；调一次就不可逆打坏 alpha），所以"液态玻璃"
/// 在这里只能靠画。
///
/// **只画边缘，不画面**：最早试过"顶部大镜面 + 底部大阴影"，实测把面板弄得发花 ——
/// 面板本身只有 80% 不透明，背后是白底网页时文字会透上来，再叠一层大面积渐变就完全
/// 读不清了。现在的做法是沿轮廓内侧给一道上亮下暗的窄光，再加一条底缘暗边，
/// 看起来像"有厚度的玻璃板"，但不会盖住内容。
/// </summary>
internal static class GlassPaint
{
    /// <summary>
    /// 设置里的档位 → 玻璃强度。0 关、1 磨砂（轻）、2 玻璃（重）。
    /// 只看设置，不看系统是否支持系统材质 —— 自绘不依赖系统。
    /// </summary>
    public static double Strength => AppSettings.Current.GlassMode switch
    {
        >= 2 => 1.0,
        1 => 0.55,
        _ => 0.0,
    };

    /// <summary>
    /// 在已经画好底色的形状上叠玻璃层。<paramref name="shape"/> 必须是闭合几何，
    /// 所有绘制都会被裁进它里面，所以圆弧边缘不会溢出。
    /// </summary>
    public static void Paint(DrawingContext dc, Geometry shape, Rect bounds, double strength)
    {
        if (strength <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        dc.PushClip(shape);
        try
        {
            // ① 内缘亮边：粗笔沿轮廓画、被裁掉外侧一半，正好只剩内侧的一道光。
            dc.DrawGeometry(null, new Pen(Frozen(new LinearGradientBrush(
                Alpha(0xFF, 0xFF, 0xFF, (byte)(104 * strength)),
                Alpha(0xFF, 0xFF, 0xFF, (byte)(14 * strength)),
                new Point(0.2, 0), new Point(0.8, 1))), 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }, shape);

            // ② 底缘暗边：只压最下面一成多，给面板一点厚度，别让它像一张贴纸。
            dc.DrawRectangle(Frozen(new LinearGradientBrush(
                Alpha(0, 0, 0, 0),
                Alpha(0, 0, 0, (byte)(64 * strength)),
                new Point(0.5, 0.9), new Point(0.5, 1))), null, bounds);
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>圆角矩形几何：必须和 <c>DrawRoundedRectangle</c> 画出来的一致，
    /// 否则玻璃层的边缘会和实体形状错开（早期用 squircle 就错出四个角上的缺口）。</summary>
    public static Geometry RoundRect(Rect rect, double radius)
    {
        var geometry = new RectangleGeometry(rect, radius, radius);
        geometry.Freeze();
        return geometry;
    }

    private static Color Alpha(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
