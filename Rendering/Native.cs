using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PulseWin;

/// <summary>悬浮窗需要的少量 Win32 互操作。</summary>
public static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;


    // ————— 液态玻璃已移除(v1.7.3) —————
    // 这里原来有 ApplyAcrylic/ClearAcrylic(SetWindowCompositionAttribute),已整体删除。
    // 两个硬原因:①acrylic 模糊区只能是窗口矩形,跟着圆角不变——一开圆角全变直角;
    // ②在 WPF 分层窗口上调用它会把 per-pixel alpha **不可逆**打坏(rail 与卡片变成
    // 不透明黑矩形)。面板本来就是自绘黑色玻璃,不需要系统模糊。

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECTMonitor rcMonitor;
        public RECTMonitor rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTMonitor { public int L, T, R, B; }

    /// <summary>
    /// 前台窗口是否盖满了它所在的显示器(±8px 容差)——全屏视频/游戏/演示的判定。
    /// PulseWin 自身永不激活,前台永远不是它。
    /// </summary>
    public static bool ForegroundIsFullScreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (!GetWindowRectWin(fg, out var wr)) return false;
        IntPtr mon = MonitorFromWindow(fg, 2);  // MONITOR_DEFAULTTONEAREST
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        bool covers = wr.L <= mi.rcMonitor.L + 8 && wr.T <= mi.rcMonitor.T + 8
                   && wr.R >= mi.rcMonitor.R - 8 && wr.B >= mi.rcMonitor.B - 8;
        return covers;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRectWin(IntPtr h, out RECTMonitor r);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO info);

    /// <summary>把窗口提升到置顶层最上,防止被其他置顶/画中画窗口压住。</summary>
    public static void BringToTopmost(Window w)
    {
        if (!w.IsVisible) return;
        IntPtr h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 把任意 HWND 提到置顶层最上(不激活)。托盘/后台弹出的菜单是普通层级的
    /// 独立 HWND,会被前台窗口整个盖住——弹出时必须提一次才看得见。
    /// </summary>
    public static void BringHwndTopmost(IntPtr h)
    {
        if (h != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public const int VK_LBUTTON = 0x01;
    public static bool LeftButtonDown => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    public static Point? CursorPosition()
    {
        if (!GetCursorPos(out var p)) return null;
        return new Point(p.X, p.Y);
    }

    /// <summary>悬浮窗永不激活、不进 Alt-Tab、不占任务栏。</summary>
    public static void ApplyToolWindowStyle(Window w)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE);
            SetWindowLongPtr(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>
    /// 命中测试穿透:透明窗默认整矩形拦鼠标。hit 返回 true 的点(形状内)
    /// 交给窗口,其余返回 HTTRANSPARENT 放行给下层。
    /// </summary>
    public static void HookHitTest(Window w, Func<Point, bool> hit)
    {
        w.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            var src = HwndSource.FromHwnd(h);
            src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_NCHITTEST) return IntPtr.Zero;
                long lp = lParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                double scale = Native.Scale(w);
                Point rel = new(x / scale - w.Left, y / scale - w.Top);
                handled = true;
                return new IntPtr(hit(rel) ? HTCLIENT : HTTRANSPARENT);
            });
        };
    }

    public static double Scale(Window w) =>
        VisualTreeHelper.GetDpi(w).DpiScaleX; // SystemAware:全系统一致

    /// <summary>
    /// 悬浮窗永不激活、不进 Alt-Tab、不占任务栏。
    /// </summary>
    /// <summary>右键 → 弹菜单。**几何与拖动一致**:能拖的地方就能右键。
    ///
    /// 在窗口过程里接 <c>WM_RBUTTONUP</c>,不用 WPF 的右键事件:这个窗口
    /// 永不激活(WS_EX_NOACTIVATE),WPF 的输入路由在它上面不可靠——拖动和点击
    /// 本来就是靠轮询 + 命中测试做的,右键走同一条路才不会有"这块地方有时管用"。
    /// </summary>
    public static void HookContextMenu(Window w, Func<Point, bool> hit, Action<Point> show)
    {
        w.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(h)?.AddHook(
                (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg != WM_RBUTTONUP) return IntPtr.Zero;
                    long lp = lParam.ToInt64();
                    int x = (short)(lp & 0xFFFF);
                    int y = (short)((lp >> 16) & 0xFFFF);
                    double scale = Scale(w);
                    // WM_RBUTTONUP 的 lParam 是**客户区**坐标(WP_NCHITTEST 那个才是屏幕坐标),
                    // 所以命中判断不用减窗口位置;而弹菜单要的是屏幕坐标,得加回去。
                    Point rel = new(x / scale, y / scale);
                    if (!hit(rel)) return IntPtr.Zero;   // 形状外:放行(等于没点)
                    show(new Point(x / scale + w.Left, y / scale + w.Top));
                    handled = true;
                    return IntPtr.Zero;
                });
        };
    }

    // 光标屏幕工作区缓存:Screen.FromPoint 每次都会枚举显示器并分配对象,
    // 主循环每帧要问 1~2 次,不缓存就是每秒上百次纯浪费。
    private static System.Windows.Forms.Screen? _screenCache;
    private static System.Windows.Rect _workAreaCache;
    private static double _workAreaScale;

    /// <summary>显示器或任务栏尺寸变化后作废缓存(由 HookScreenChanges 自动调用)。</summary>
    public static void InvalidateScreenCache() => _screenCache = null;

    /// <summary>订阅显示器/系统度量变化,及时作废工作区缓存。</summary>
    public static void HookScreenChanges(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(h)?.AddHook(
                (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE) InvalidateScreenCache();
                    return IntPtr.Zero;
                });
        };
    }

    /// <summary>光标所在显示器的工作区(DIU)。光标仍在缓存的那块屏内就直接复用。</summary>
    public static System.Windows.Rect WorkingAreaUnderPointer(double dpiScale)
    {
        var p = CursorPosition() ?? new Point(0, 0);
        // WinForms Screen 坐标是物理像素;转 DIU 与 WPF 窗口坐标一致。
        var phys = new System.Drawing.Point((int)p.X, (int)p.Y);
        var s = _screenCache;
        if (s is null || _workAreaScale != dpiScale || !s.Bounds.Contains(phys))
        {
            s = System.Windows.Forms.Screen.FromPoint(phys);
            var wa = s.WorkingArea;
            _workAreaCache = new System.Windows.Rect(wa.X / dpiScale, wa.Y / dpiScale,
                wa.Width / dpiScale, wa.Height / dpiScale);
            _screenCache = s;
            _workAreaScale = dpiScale;
        }
        return _workAreaCache;
    }

    /// <summary>全虚拟屏边界(DIU),用于把停靠位换算成屏幕坐标。</summary>
    public static System.Windows.Rect VirtualScreen(double dpiScale)
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new System.Windows.Rect(vs.X / dpiScale, vs.Y / dpiScale,
            vs.Width / dpiScale, vs.Height / dpiScale);
    }
}
