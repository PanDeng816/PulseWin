using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseWin;

/// <summary>
/// 用户可调项(落盘 Data\settings.json)。以前报警阈值/同步间隔都是编译期常量,
/// 设置窗口只能填 API Key——开放这几项后不用改代码重发就能调整观感。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly object Gate = new();

    /// <summary>用量报警阈值(0~1)。超过即转红。</summary>
    public double AlertThreshold { get; set; } = 0.80;

    /// <summary>同步成功后的等待间隔(秒)。</summary>
    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>rail 背景不透明度(0~1)。</summary>
    public double SurfaceOpacity { get; set; } = 0.80;

    /// <summary>DeepSeek 只有余额、没有额度,环的分母从哪来(见 BalanceBasis)。</summary>
    public BalanceBasis DeepSeekBasis { get; set; } = BalanceBasis.SinceTopUp;

    /// <summary>Budget 模式下的"满箱"金额;空/非正数视为未设置。</summary>
    public double? DeepSeekBudget { get; set; }

    /// <summary>账户同时持有多种币种时,环跟着哪一种;空 = 第一个有钱的。</summary>
    public string? DeepSeekCurrency { get; set; }

    /// <summary>rail 上要显示哪些数据源(关掉的既不显示也不占高度)。</summary>
    public bool ShowGoat { get; set; } = true;
    public bool ShowOpenCode { get; set; } = true;
    public bool ShowDeepSeek { get; set; } = true;

    /// <summary>环下是否显示读数(百分比/金额)。关掉后环间距与 rail 高度同步收窄。</summary>
    public bool ShowPercent { get; set; } = true;

    /// <summary>
    /// 全局快捷键,写法如 "Ctrl+Alt+P"。**空 = 不绑定**(默认)。
    /// 默认给一套组合键,等于替没提要求的人从别的程序手里拿走那几个键。
    /// </summary>
    public string? HotkeyToggleRail { get; set; }
    public string? HotkeyOpenSettings { get; set; }

    /// <summary>
    /// 用量到多少时发系统通知:0 = 关(默认),否则 75 / 80 / 90 / 95。
    /// 用固定档位而不是自由数值:这个设置的目的是"挑一个你想被告知的时刻",
    /// 四档就够;而默认关着,是因为一个装上就开始自己弹通知的程序,
    /// 在你装它的那天就改变了你同意的东西。
    /// </summary>
    public int AlertPercent { get; set; }

    /// <summary>额度重置(新一轮窗口开始)时是否通知。</summary>
    public bool NotifyOnReset { get; set; }

    /// <summary>
    /// 每个来源自定义环色(#RRGGBB)。空 = 按用量着色(绿→红)。
    /// 环色**默认按用量**,这是刻意的:环的颜色表示"离上限还有多远",
    /// 不是"这是哪个产品"(产品由环心图标表示)。想固定成品牌色是可选行为。
    /// </summary>
    public string? GoatTint { get; set; }
    public string? OpenCodeTint { get; set; }
    public string? DeepSeekTint { get; set; }

    /// <summary>按来源键取自定义环色(空 = 不自定义)。</summary>
    public string? TintFor(string sourceKey) => sourceKey switch
    {
        "goat" => GoatTint,
        "opencode" => OpenCodeTint,
        "deepseek" => DeepSeekTint,
        _ => null
    };

    private static AppSettings? _current;
    private static AppDataPaths? _paths;

    public static AppSettings Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (Gate) return _current ??= Load();
        }
    }

    public static void Attach(AppDataPaths paths) => _paths = paths;

    private static AppSettings Load()
    {
        try
        {
            if (_paths is { } p && File.Exists(p.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(p.SettingsFile), JsonOptions);
                if (loaded is not null) return loaded.Sanitized();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Note("读取设置失败,使用默认值", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        var clean = Sanitized();
        AlertThreshold = clean.AlertThreshold;
        SyncIntervalSeconds = clean.SyncIntervalSeconds;
        SurfaceOpacity = clean.SurfaceOpacity;
        DeepSeekBasis = clean.DeepSeekBasis;
        DeepSeekBudget = clean.DeepSeekBudget;
        DeepSeekCurrency = clean.DeepSeekCurrency;
        ShowGoat = clean.ShowGoat;
        ShowOpenCode = clean.ShowOpenCode;
        ShowDeepSeek = clean.ShowDeepSeek;
        ShowPercent = clean.ShowPercent;
        HotkeyToggleRail = clean.HotkeyToggleRail;
        HotkeyOpenSettings = clean.HotkeyOpenSettings;
        AlertPercent = clean.AlertPercent;
        NotifyOnReset = clean.NotifyOnReset;
        GoatTint = clean.GoatTint;
        OpenCodeTint = clean.OpenCodeTint;
        DeepSeekTint = clean.DeepSeekTint;
        try
        {
            if (_paths is { } p)
                AtomicFile.WriteText(p.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("保存设置失败", ex);
        }
    }

    private AppSettings Sanitized() => new()
    {
        AlertThreshold = Math.Clamp(AlertThreshold, 0.5, 0.99),
        SyncIntervalSeconds = Math.Clamp(SyncIntervalSeconds, 15, 600),
        SurfaceOpacity = Math.Clamp(SurfaceOpacity, 0.3, 1.0),
        DeepSeekBasis = Enum.IsDefined(DeepSeekBasis) ? DeepSeekBasis : BalanceBasis.SinceTopUp,
        // 有限的、正数才算数,否则当作"没设"
        DeepSeekBudget = DeepSeekBudget is { } budget && double.IsFinite(budget) && budget > 0
            ? budget
            : null,
        DeepSeekCurrency = string.IsNullOrWhiteSpace(DeepSeekCurrency)
            ? null
            : DeepSeekCurrency.Trim().ToUpperInvariant(),
        ShowGoat = ShowGoat,
        ShowOpenCode = ShowOpenCode,
        ShowDeepSeek = ShowDeepSeek,
        ShowPercent = ShowPercent,
        // 空的快捷键原样保留;能解析的归一成规范写法,解析不了的**丢掉而不是照存**
        // ——存一个用不了的组合键,界面上看不出它坏了。
        HotkeyToggleRail = NormalizeHotkey(HotkeyToggleRail),
        HotkeyOpenSettings = NormalizeHotkey(HotkeyOpenSettings),
        AlertPercent = AlertPercent is 0 or 75 or 80 or 90 or 95 ? AlertPercent : 0,
        NotifyOnReset = NotifyOnReset,
        GoatTint = NormalizeTint(GoatTint),
        OpenCodeTint = NormalizeTint(OpenCodeTint),
        DeepSeekTint = NormalizeTint(DeepSeekTint),
    };

    private static string? NormalizeHotkey(string? text)
    {
        var parsed = Native.ParseHotkey(text);
        return parsed is { } value ? Native.DescribeHotkey(value.Modifiers, value.Key) : null;
    }

    /// <summary>环色只接受 #RRGGBB / #AARRGGBB;别的写法当作"没设",不让它把界面画坏。</summary>
    private static string? NormalizeTint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;
        if (value.Length is not (7 or 9)) return null;
        for (int i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i])) return null;
        }
        return value.ToUpperInvariant();
    }

    /// <summary>把设置回写成当前值的副本(供 UI 显示)。</summary>
    public AppSettings Clone() => new()
    {
        AlertThreshold = AlertThreshold,
        SyncIntervalSeconds = SyncIntervalSeconds,
        SurfaceOpacity = SurfaceOpacity,
        DeepSeekBasis = DeepSeekBasis,
        DeepSeekBudget = DeepSeekBudget,
        DeepSeekCurrency = DeepSeekCurrency,
        ShowGoat = ShowGoat,
        ShowOpenCode = ShowOpenCode,
        ShowDeepSeek = ShowDeepSeek,
        ShowPercent = ShowPercent,
        HotkeyToggleRail = HotkeyToggleRail,
        HotkeyOpenSettings = HotkeyOpenSettings,
        AlertPercent = AlertPercent,
        NotifyOnReset = NotifyOnReset,
        GoatTint = GoatTint,
        OpenCodeTint = OpenCodeTint,
        DeepSeekTint = DeepSeekTint,
    };
}
