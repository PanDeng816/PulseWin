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

    /// <summary>把窗口提升到置顶层最上,防止被其他置顶/画中画窗口压住。</summary>
    public static void BringToTopmost(Window w)
    {
        if (!w.IsVisible) return;
        IntPtr h = new WindowInteropHelper(w).Handle;
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

    /// <summary>光标所在显示器的工作区(物理像素)。</summary>
    public static System.Windows.Rect WorkingAreaUnderPointer(double dpiScale)
    {
        var p = CursorPosition() ?? new Point(0, 0);
        // WinForms Screen 坐标是物理像素;转 DIU 与 WPF 窗口坐标一致。
        var s = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)p.X, (int)p.Y));
        return new System.Windows.Rect(s.WorkingArea.X / dpiScale, s.WorkingArea.Y / dpiScale,
            s.WorkingArea.Width / dpiScale, s.WorkingArea.Height / dpiScale);
    }

    /// <summary>全虚拟屏边界(DIU),用于把停靠位换算成屏幕坐标。</summary>
    public static System.Windows.Rect VirtualScreen(double dpiScale)
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        return new System.Windows.Rect(vs.X / dpiScale, vs.Y / dpiScale,
            vs.Width / dpiScale, vs.Height / dpiScale);
    }
}
