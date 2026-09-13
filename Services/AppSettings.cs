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
    };

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
    };
}
