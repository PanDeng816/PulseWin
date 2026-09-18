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
    public const int WM_HOTKEY = 0x0312;

    // 热键修饰键。**要求至少带一个**:只按 Shift 或只按一个字母的组合,
    // 等于替没提要求的人从别的程序手里拿走一个键(见 RegisterHotkey 的说明)。
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// 注册一个全局快捷键。返回 false 表示注册失败(组合被别的程序占了,或和自己
    /// 另一个快捷键撞了)——**必须把这个说出来**,一个悄悄不生效的快捷键比没有更糟,
    /// 因为用户会去怪功能本身。
    /// </summary>
    public static bool RegisterHotkey(Window w, int id, uint modifiers, uint virtualKey)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        return h != IntPtr.Zero && RegisterHotKey(h, id, modifiers | MOD_NOREPEAT, virtualKey);
    }

    public static void UnregisterHotkey(Window w, int id)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) UnregisterHotKey(h, id);
    }

    /// <summary>接 <c>WM_HOTKEY</c>,把注册号交回调用方。</summary>
    public static void HookHotkey(Window w, Action<int> pressed)
    {
        w.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(h)?.AddHook(
                (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg != WM_HOTKEY) return IntPtr.Zero;
                    pressed(wParam.ToInt32());
                    handled = true;
                    return IntPtr.Zero;
                });
        };
    }

    /// <summary>把 "Ctrl+Alt+P" 这样的写法解析成 (修饰键, 虚拟键码)。空/非法返回 null。</summary>
    public static (uint Modifiers, uint Key)? ParseHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint modifiers = 0;
        uint key = 0;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; continue;
                case "alt": modifiers |= MOD_ALT; continue;
                case "shift": modifiers |= MOD_SHIFT; continue;
                case "win" or "windows": modifiers |= MOD_WIN; continue;
            }
            var parsed = ParseKey(part);
            if (parsed is null) return null;
            key = parsed.Value;
        }
        // 必须含 Ctrl/Alt/Win 之一;只带 Shift 或什么都不带的组合会被拒——
        // 那种键等于从所有程序的输入框里拿走一个字母。
        if (key == 0) return null;
        if ((modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0) return null;
        return (modifiers, key);
    }

    private static uint? ParseKey(string part)
    {
        if (part.Length == 1 && part[0] is >= 'a' and <= 'z')
            return (uint)char.ToUpperInvariant(part[0]);
        if (part.Length == 1 && part[0] is >= '0' and <= '9')
            return (uint)part[0];
        if (part.StartsWith('f') && int.TryParse(part[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);   // VK_F1
        return part switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            _ => null
        };
    }

    /// <summary>把解析过的组合倒回可显示的写法(设置界面回显)。</summary>
    public static string DescribeHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(key switch
        {
            >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x30 and <= 0x39 => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => "F" + (key - 0x70 + 1),
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            _ => "?"
        });
        return string.Join("+", parts);
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
