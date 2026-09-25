using System.IO;
using System.Linq;
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

    /// <summary>
    /// 美元兑人民币汇率,用量统计的估算金额按它显示。价目表(models.dev)本身是美元,
    /// 这里只影响显示。0 = 显示美元原值,不做换算。
    /// </summary>
    public double UsdToCny { get; set; } = 7.2;

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
    /// 圆环尺寸三档:0=Small(环 36pt/线 3.2)、1=Standard(40/3.6,默认)、2=Large(44/4,v1.5 原样)。
    /// rail 宽、环心距等几何全部随档联动(见 MainWindow.RingSizeTable)。
    /// </summary>
    public int RingSize { get; set; } = 1;

    /// <summary>环与环的间距三档:0=Tight(2pt)、1=Standard(4pt)、2=Loose(10pt)。</summary>
    public int RingSpacing { get; set; } = 1;

    /// <summary>
    /// 液态玻璃:用系统的 acrylic 背景模糊代替半透明纯黑(SetWindowCompositionAttribute)。
    /// 开启后面板自身不填底色,底色由模糊层的 tint 提供;失败或不支持时自动回退纯透明黑。
    /// </summary>
    public bool GlassBackdrop { get; set; } = true;

    /// <summary>
    /// 自动隐藏时收缩成贴在屏幕边缘的 6pt 细条(上游 hide until pointed at),
    /// 而不是滑出屏外。细条上带最紧张源的警报色;指针靠近热区即展开。
    /// </summary>
    public bool HideToSliver { get; set; } = true;

    /// <summary>前台窗口是全屏应用时自动把 rail 藏起来(看视频/演示不被打扰)。</summary>
    public bool HideInFullScreen { get; set; } = false;

    /// <summary>
    /// rail 上数据源的显示顺序,逗号分隔的来源键(如 "goat,opencode,deepseek")。
    /// 没列出的源排最后;设置窗给六个预设组合。
    /// </summary>
    public string SourceOrder { get; set; } = "goat,opencode,deepseek";

    /// <summary>
    /// 网络代理:0=跟随系统(默认)、1=直连、2=手动 HTTP 代理(地址见 ProxyAddress)。
    /// 修改后重启程序生效(HttpClient 是启动时创建的长生命周期对象)。
    /// </summary>
    public int ProxyMode { get; set; } = 0;

    /// <summary>手动代理地址,如 http://127.0.0.1:7890。SOCKS5 客户端请填它的 HTTP 端口。</summary>
    public string? ProxyAddress { get; set; }

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
        UsdToCny = clean.UsdToCny;
        SurfaceOpacity = clean.SurfaceOpacity;
        DeepSeekBasis = clean.DeepSeekBasis;
        DeepSeekBudget = clean.DeepSeekBudget;
        DeepSeekCurrency = clean.DeepSeekCurrency;
        ShowGoat = clean.ShowGoat;
        ShowOpenCode = clean.ShowOpenCode;
        ShowDeepSeek = clean.ShowDeepSeek;
        ShowPercent = clean.ShowPercent;
        RingSize = clean.RingSize;
        RingSpacing = clean.RingSpacing;
        GlassBackdrop = clean.GlassBackdrop;
        HideToSliver = clean.HideToSliver;
        HideInFullScreen = clean.HideInFullScreen;
        SourceOrder = clean.SourceOrder;
        ProxyMode = clean.ProxyMode;
        ProxyAddress = clean.ProxyAddress;
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
        UsdToCny = Math.Clamp(UsdToCny, 0, 100),
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
        RingSize = Math.Clamp(RingSize, 0, 2),
        RingSpacing = Math.Clamp(RingSpacing, 0, 2),
        GlassBackdrop = GlassBackdrop,
        HideToSliver = HideToSliver,
        HideInFullScreen = HideInFullScreen,
        SourceOrder = NormalizeOrder(SourceOrder),
        ProxyMode = Math.Clamp(ProxyMode, 0, 2),
        ProxyAddress = string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim(),
        GoatTint = NormalizeTint(GoatTint),
        OpenCodeTint = NormalizeTint(OpenCodeTint),
        DeepSeekTint = NormalizeTint(DeepSeekTint),
    };

    /// <summary>来源顺序只认三个已知键,别的字符一律丢掉;全空则回默认。归一成 "a,b,c"。</summary>
    internal static string NormalizeOrder(string? text)
    {
        var known = new[] { "goat", "opencode", "deepseek" };
        var keys = (text ?? "").Split(',').Select(k => k.Trim().ToLowerInvariant()).Where(known.Contains).Distinct().ToList();
        // 补上没出现的键,保持已知顺序,保证三个源都有位置
        keys.AddRange(known.Where(k => !keys.Contains(k)));
        return string.Join(",", keys);
    }

    /// <summary>环色只接受 #RRGGBB / #AARRGGBB;别的写法当作"没设",不让它把界面画坏。
    /// (TintPicker 的手输框也走这一条规则,所以校验只此一份。)</summary>
    internal static string? NormalizeTint(string? text)
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
        UsdToCny = UsdToCny,
        SurfaceOpacity = SurfaceOpacity,
        DeepSeekBasis = DeepSeekBasis,
        DeepSeekBudget = DeepSeekBudget,
        DeepSeekCurrency = DeepSeekCurrency,
        ShowGoat = ShowGoat,
        ShowOpenCode = ShowOpenCode,
        ShowDeepSeek = ShowDeepSeek,
        ShowPercent = ShowPercent,
        RingSize = RingSize,
        RingSpacing = RingSpacing,
        GlassBackdrop = GlassBackdrop,
        HideToSliver = HideToSliver,
        HideInFullScreen = HideInFullScreen,
        SourceOrder = SourceOrder,
        ProxyMode = ProxyMode,
        ProxyAddress = ProxyAddress,
        GoatTint = GoatTint,
        OpenCodeTint = OpenCodeTint,
        DeepSeekTint = DeepSeekTint,
    };
}
