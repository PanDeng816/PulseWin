using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseWin;

/// <summary>
/// 设置窗 / 用量窗的**玻璃外壳**。
///
/// 为什么不是 Mica / Acrylic（2026-10-21 实机逐个试过，这是结论）：
///  · <c>DWMWA_SYSTEMBACKDROP_TYPE</c>：返回 S_OK，但 WPF 窗口上**什么也不画** ——
///    它要求窗口带 <c>WS_EX_NOREDIRECTIONBITMAP</c>（WinUI3 有，WPF 没有）；补上这个
///    标志后依旧不画，看到的只是"背后内容清晰透出、没有模糊"。
///  · <c>SetWindowCompositionAttribute</c>（老的 acrylic）：返回 TRUE，但会把 WPF 的
///    客户区**整块抹掉** —— 窗口还在、Responding，画面上什么都没有。
///  两者在这台 Win11 26H2 上都不能用，所以改成抓屏做背景。
///
/// 现在的做法：**打开窗口时抓一次屏幕 → 缩小 12 倍 → 放大铺满**。缩放在放大时被双线性
/// 插值糊开，成本接近零（没有高斯卷积），观感就是"背后的内容被磨砂了"。窗口不移动时
/// 背景不跟着变 —— 磨砂本来也看不清对应关系，这一点用户察觉不到，而代价是零。
///
/// rail 不走这里：它是分层窗口，玻璃是自绘的（见 <see cref="GlassPaint"/>）。
/// </summary>
public enum BackdropKind
{
    /// <summary>不透明（v1.24 及以前的样子）。</summary>
    Off = 0,
    /// <summary>磨砂：模糊轻一些，底色更实。</summary>
    Mica = 1,
    /// <summary>玻璃：模糊重一些，底色更透。</summary>
    Acrylic = 2,
}

internal static class Backdrop
{
    /// <summary>缩小倍数：越大越糊。16 倍在 200% DPI 下是干净的磨砂（12 倍还能看出字块）。</summary>
    private const int Downscale = 16;

    /// <summary>每个窗口一份原始缩略图（窗口关掉后自动回收）。合成按当前主题现做，所以切主题不用重抓屏。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, ImageSource> RawShots = new();

    /// <summary>背景图往主题色里压多少：浅色压白、深色压黑。越大越"淡"。</summary>
    private const byte LightVeil = 0x70;
    private const byte DarkVeil = 0x84;

    /// <summary>
    /// 把缩略图合成到主题色里（其实就是盖一层半透明色膜）。
    /// 目的不是把图变白，而是**压掉对比度**：否则背后一块深色区域透上来，
    /// 在半透明卡片上就是一个边界清楚的灰斑 —— 那正是"看不清"的来源。
    /// </summary>
    private static ImageSource Compose(ImageSource source, bool dark)
    {
        if (source is not BitmapSource bitmap) return source;

        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        if (w <= 0 || h <= 0) return source;

        Color tint = dark ? Color.FromRgb(0x13, 0x13, 0x16) : Color.FromRgb(0xF4, 0xF4, 0xF7);
        byte veil = dark ? DarkVeil : LightVeil;

        var veilBrush = new SolidColorBrush(Color.FromArgb(veil, tint.R, tint.G, tint.B));
        veilBrush.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(bitmap, new Rect(0, 0, w, h));
            dc.DrawRectangle(veilBrush, null, new Rect(0, 0, w, h));
        }

        var composed = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        composed.Freeze();
        return composed;
    }

    /// <summary>设置里的三档 → 实际材质。</summary>
    public static BackdropKind FromSetting(int mode) => (BackdropKind)Math.Clamp(mode, 0, 2);

    /// <summary>把所有已开着的窗口重套一遍（设置窗里改档位时，已打开的用量窗要跟着变）。</summary>
    public static void ApplyAll(BackdropKind kind, bool dark)
    {
        if (Application.Current is not { } app) return;
        foreach (Window w in app.Windows)
            Apply(w, kind, dark);
    }

    /// <summary>
    /// 套玻璃外壳。**必须在窗口首次显示之前调用**（那时屏幕上还没有它自己，
    /// 抓到的才是"背后的真实内容"）；重复调用复用已有快照。
    /// </summary>
    public static void Apply(Window w, BackdropKind kind, bool dark)
    {
        if (w is null) return;

        // 分层窗口（rail / 悬浮卡）不能改 Background：它们靠 per-pixel alpha 自绘，
        // 玻璃层由 GlassPaint 负责。这里只处理普通窗口。
        if (w.AllowsTransparency)
        {
            ApplyTitleBar(w, dark);
            return;
        }

        if (kind == BackdropKind.Off)
        {
            RawShots.Remove(w);
            // 恢复 XAML 里的 {DynamicResource ShellBackBrush}，回到不透明
            w.SetResourceReference(Window.BackgroundProperty, "ShellBackBrush");
        }
        else
        {
            var raw = RawShots.GetValue(w, _ => Capture()!);
            if (raw is not null)
            {
                // 先合成再当背景：原始抓屏对比度太高（DSH 的深色卡片会缩成一大块灰），
                // 页底那 12% 的透过率就足以让卡片上出现明显的灰斑。这里把图压到主题色里，
                // 只留"氛围"不留"形状"——真磨砂玻璃也会把背后的东西冲淡。
                var composed = Compose(raw, dark);
                var brush = new ImageBrush(composed)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                brush.Freeze();
                w.Background = brush;
            }
        }

        ApplyTitleBar(w, dark);
    }

    /// <summary>深色主题下标题栏也得是深的（这一条 DWMWA 是有效的，保留）。</summary>
    private static void ApplyTitleBar(Window w, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            void OnSource(object? s, EventArgs e)
            {
                w.SourceInitialized -= OnSource;
                SetDarkTitleBar(new WindowInteropHelper(w).Handle, dark);
            }

            w.SourceInitialized += OnSource;
            return;
        }

        SetDarkTitleBar(hwnd, dark);
    }

    private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>
    /// 抓整屏（CAPTUREBLT 连分层窗口一起抓），再缩到 1/12 存成一张很小的位图。
    /// 放大时由 WPF 的双线性插值糊开，效果等于一次便宜的降采样模糊。
    /// </summary>
    private static ImageSource? Capture()
    {
        int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, w, h);
        if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        IntPtr old = SelectObject(memDc, bitmap);
        try
        {
            // CAPTUREBLT(0x40000000) 不能省：否则分层窗口（rail、卡片）抓不到
            BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);

            var full = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            full.Freeze();

            var small = new TransformedBitmap(full,
                new ScaleTransform(1.0 / Downscale, 1.0 / Downscale));
            small.Freeze();
            return small;
        }
        catch
        {
            return null;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
