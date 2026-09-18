using System.Windows;
using System.Windows.Interop;

namespace PulseWin;

/// <summary>
/// 全局快捷键。用 <c>RegisterHotKey</c> 而不是低级键盘钩子:钩子要看别的程序的
/// 每一次按键,那需要系统级的输入监听,为了"打开一个设置窗口"不值得(上游同样的取舍)。
///
/// 两条刻意的规矩:
/// ① **默认不绑定任何组合**——默认给一套键,等于替没提要求的人从别的程序手里拿走那几个键;
/// ② **注册失败要能看见**——被别的程序占了、或和本程序另一个快捷键撞了,都要在设置里
///    说清楚。一个悄悄不生效的快捷键比没有更糟,因为用户会去怪功能本身。
/// </summary>
public static class GlobalHotkeys
{
    private const int ToggleRailId = 0xA001;
    private const int OpenSettingsId = 0xA002;

    private static Window? _window;
    private static Action? _onToggleRail;
    private static Action? _onSettings;

    /// <summary>各快捷键当前是否注册成功(设置界面显示用)。</summary>
    public static bool ToggleRailRegistered { get; private set; }
    public static bool OpenSettingsRegistered { get; private set; }
    public static string? ToggleRailError { get; private set; }
    public static string? OpenSettingsError { get; private set; }

    public static event Action? StatusChanged;

    public static void Attach(Window window, Action onToggleRail, Action onSettings)
    {
        _window = window;
        _onToggleRail = onToggleRail;
        _onSettings = onSettings;
        Native.HookHotkey(window, id =>
        {
            if (id == ToggleRailId) _onToggleRail?.Invoke();
            else if (id == OpenSettingsId) _onSettings?.Invoke();
        });
        // **句柄要等 SourceInitialized 才存在**,RegisterHotKey 传 0 必然失败。
        // 所以窗口一就绪就再注册一次,而不是只在启动流程里注册一次。
        window.SourceInitialized += (_, _) => Apply(AppSettings.Current);
    }

    /// <summary>按设置(重新)注册。设置里改完组合键调用。</summary>
    public static void Apply(AppSettings settings)
    {
        if (_window is null) return;
        // 窗口还没拿到句柄:什么都不做,也不报错——Attach 挂的 SourceInitialized
        // 会再调一次;在这里就报"注册失败"会把一个时机问题说成配置问题。
        if (new WindowInteropHelper(_window).Handle == IntPtr.Zero) return;

        Unregister();
        ToggleRailRegistered = TryRegister(settings.HotkeyToggleRail, ToggleRailId, out var railError);
        ToggleRailError = railError;
        OpenSettingsRegistered = TryRegister(settings.HotkeyOpenSettings, OpenSettingsId, out var settingsError);
        OpenSettingsError = settingsError;
        // 记一条持久诊断:快捷键"没反应"时,先看这里是被占用了还是压根没绑
        Diagnostics.Note($"全局快捷键: 显示/隐藏[{(string.IsNullOrWhiteSpace(settings.HotkeyToggleRail) ? "未绑定" : ToggleRailRegistered ? "已注册" : "失败:" + ToggleRailError)}] "
            + $"打开设置[{(string.IsNullOrWhiteSpace(settings.HotkeyOpenSettings) ? "未绑定" : OpenSettingsRegistered ? "已注册" : "失败:" + OpenSettingsError)}]");
        StatusChanged?.Invoke();
    }

    private static bool TryRegister(string? text, int id, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return false;      // 没设 = 不注册(不是失败)
        var parsed = Native.ParseHotkey(text);
        if (parsed is null)
        {
            error = "写法无法识别";
            return false;
        }
        if (Native.RegisterHotkey(_window!, id, parsed.Value.Modifiers, parsed.Value.Key)) return true;
        error = "已被其它程序占用";
        return false;
    }

    private static void Unregister()
    {
        if (_window is null) return;
        Native.UnregisterHotkey(_window, ToggleRailId);
        Native.UnregisterHotkey(_window, OpenSettingsId);
    }

    /// <summary>设置里给用户看的提示文案。</summary>
    public static string Describe(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.HotkeyToggleRail)
            && string.IsNullOrWhiteSpace(settings.HotkeyOpenSettings))
        {
            return "默认不绑定按键。组合必须含 Ctrl、Alt 或 Win 中的一个。";
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.HotkeyToggleRail))
            parts.Add(ToggleRailRegistered ? "显示/隐藏:已生效" : $"显示/隐藏:注册失败({ToggleRailError})");
        if (!string.IsNullOrWhiteSpace(settings.HotkeyOpenSettings))
            parts.Add(OpenSettingsRegistered ? "打开设置:已生效" : $"打开设置:注册失败({OpenSettingsError})");
        return string.Join("  ·  ", parts);
    }
}
