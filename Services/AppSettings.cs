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

    /// <summary>
    /// rail 上显示哪些数据源,以及它们的顺序。**顺序即数组顺序**——从前这里是
    /// "三个布尔 + 一个 6 种排列的枚举字符串",源一多就是阶乘爆炸,而且加源要同时改
    /// 六处。现在统一成一个列表:内容 = 可见的源(按用户排的顺序),不在列表里的源
    /// 就是不显示。见 <see cref="VisibleSources"/> 与 <see cref="SourceCatalog"/>。
    ///
    /// **兼容旧设置**:老版本的 ShowGoat/ShowOpenCode/ShowDeepSeek/SourceOrder 仍能被读入
    /// (见 <see cref="Sanitized"/> 里的迁移),存量用户升级不丢配置。
    ///
    /// **默认留空、由 <see cref="NormalizeVisibleSources"/> 兜底**,而不是直接写死三个源:
    /// 若非空默认值不动,反序列化一份没有该字段的旧配置时它仍是默认值,
    /// "旧字段迁移"分支就永远走不到(本机实测踩过这个坑,见 --check-migration)。
    /// </summary>
    public List<string> VisibleSources { get; set; } = new();

    /// <summary>
    /// 只在**用了 5 小时额度**的源上画出内圈(5 小时环);没用的源只画外圈(总额度)。
    ///
    /// 默认开:rail 上一排"双环"里,大部分源的这个 5 小时窗口其实是 0%——那一圈空轨道
    /// 只增加视觉噪音,不带来信息。显示成单环之后,"哪个源正在被用"一眼就能看出来
    /// (内圈出现本身就是信号)。
    /// </summary>
    public bool HideIdleInnerRing { get; set; } = true;

    /// <summary>
    /// 旧版字段(仅用于读入迁移,写出时不再产生)。留着是因为 settings.json 里可能还有它们,
    /// 删掉字段会让老配置的勾选状态丢掉。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowGoat { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowOpenCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowDeepSeek { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceOrder { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GoatTint { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpenCodeTint { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeepSeekTint { get; set; }

    /// <summary>环下是否显示读数(百分比/金额)。关掉后环间距与 rail 高度同步收窄。</summary>
    public bool ShowPercent { get; set; } = true;

    /// <summary>
    /// 圆环尺寸三档:0=Small(环 36pt/线 3.2)、1=Standard(40/3.6,默认)、2=Large(44/4,v1.5 原样)。
    /// rail 宽、环心距等几何全部随档联动(见 MainWindow.RingSizeTable)。
    /// </summary>
    public int RingSize { get; set; } = 2;

    /// <summary>环与环的间距三档:0=Tight(2pt)、1=Standard(4pt)、2=Loose(10pt)。</summary>
    public int RingSpacing { get; set; } = 1;

    /// <summary>
    /// 自动隐藏时收缩成贴在屏幕边缘的 6pt 细条(上游 hide until pointed at),
    /// 而不是滑出屏外。细条上带最紧张源的警报色;指针靠近热区即展开。
    /// </summary>
    public bool HideToSliver { get; set; } = false;

    /// <summary>前台窗口是全屏应用时自动把 rail 藏起来(看视频/演示不被打扰)。</summary>
    public bool HideInFullScreen { get; set; } = false;

    /// <summary>
    /// 是否启用 **Command Code 浏览器会话通道**:读本机浏览器(Edge/Chrome)的登录 cookie,
    /// 去调只有登录态才开放的逐模型明细接口(缓存读/写 token 等)。
    ///
    /// **默认关**,有两个硬理由:
    /// ① 有真实前车之鉴——同类工具 CodexBar-Win 因"解密浏览器 cookie 读配额"被
    ///    杀毒软件全家误判成 infostealer,最后撤掉全部二进制。读 cookie 这件事
    ///    本身就会触发安全软件的启发式规则,哪怕用途正当。
    /// ② 浏览器运行时 cookie 数据库是**排他锁定**的(本机实测连只读共享打开都
    ///    WinError 32),所以这条通道基本只在浏览器完全关闭时才有机会读到——
    ///    日常开着浏览器时它多半是"读不到",体验并不稳定。
    ///
    /// 本地来源(ZCode / OpenCode 的记录库)不给缓存读/写以外的任何东西缺项,
    /// 逐模型页在它关着的时候也完整可用。
    /// </summary>
    public bool UseBrowserSessionForModelDetail { get; set; } = false;

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

    /// <summary>本机某个源当前是否显示。</summary>
    public bool IsSourceVisible(string key) => VisibleSources.Contains(key);

    /// <summary>某个源的环色(#RRGGBB;null = 按用量自动着色)。</summary>
    public string? TintFor(string sourceKey) =>
        SourceTints.TryGetValue(sourceKey, out var tint) ? tint : null;

    /// <summary>
    /// 每个来源自定义环色。**用字典而不是三个字段**:加源不用再加属性,
    /// 而且 JSON 里就是 { "goat": "#..", "deepseek": "#.." } 一目了然。
    /// 空值不写(见 <see cref="Sanitized"/>)。
    /// </summary>
    public Dictionary<string, string> SourceTints { get; set; } = new();

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

    /// <summary>
    /// 从一段 JSON(可能是旧格式)解析出归一后的设置。**独立于单例与磁盘**——
    /// 升级迁移的正确性靠 <c>--check-migration</c> 这样在临时文件上核对,
    /// 而不是去动用户真实的 settings.json。
    /// </summary>
    public static AppSettings? ParseForTest(string json)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return loaded?.Sanitized();
        }
        catch
        {
            return null;
        }
    }

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
        VisibleSources = clean.VisibleSources;
        ShowGoat = null;            // 旧字段:迁移完成后不再写出
        ShowOpenCode = null;
        ShowDeepSeek = null;
        SourceOrder = null;
        GoatTint = null;
        OpenCodeTint = null;
        DeepSeekTint = null;
        ShowPercent = clean.ShowPercent;
        HideIdleInnerRing = clean.HideIdleInnerRing;
        RingSize = clean.RingSize;
        RingSpacing = clean.RingSpacing;
        HideToSliver = clean.HideToSliver;
        HideInFullScreen = clean.HideInFullScreen;
        UseBrowserSessionForModelDetail = clean.UseBrowserSessionForModelDetail;
        ProxyMode = clean.ProxyMode;
        ProxyAddress = clean.ProxyAddress;
        SourceTints = clean.SourceTints;
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
        VisibleSources = NormalizeVisibleSources(),
        ShowPercent = ShowPercent,
        HideIdleInnerRing = HideIdleInnerRing,
        RingSize = Math.Clamp(RingSize, 0, 2),
        RingSpacing = Math.Clamp(RingSpacing, 0, 2),
        HideToSliver = HideToSliver,
        HideInFullScreen = HideInFullScreen,
        UseBrowserSessionForModelDetail = UseBrowserSessionForModelDetail,
        ProxyMode = Math.Clamp(ProxyMode, 0, 2),
        ProxyAddress = string.IsNullOrWhiteSpace(ProxyAddress) ? null : ProxyAddress.Trim(),
        SourceTints = NormalizeTints(),
    };

    /// <summary>
    /// 归一可见源列表:丢掉未知键、去重、保证至少一个(全空时退回默认全显示)。
    /// **先看新字段,再回落到旧字段**——存量用户的 settings.json 里只有
    /// ShowGoat/ShowOpenCode/ShowDeepSeek/SourceOrder,直接读新字段会得到空列表,
    /// 表现为"升级后所有环都不见了"。
    /// </summary>
    private List<string> NormalizeVisibleSources()
    {
        var keys = SourceCatalog.Keys;
        var result = (VisibleSources ?? new())
            .Where(keys.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 迁移:新字段为空(老配置或首次运行)时,从旧字段重建
        if (result.Count == 0 && MigratedFromLegacySources() is { Count: > 0 } legacy)
            result = legacy;

        return result.Count > 0 ? result : keys.ToList();
    }

    /// <summary>旧设置(三个勾选 + 排列字符串)还原成"可见且有序"的源列表。</summary>
    private List<string>? MigratedFromLegacySources()
    {
        // 三个旧布尔全为 null = settings.json 里根本没有旧字段(全新安装),
        // 交给上层用默认值,不要在这里凭空造一个"只显示 goat"的配置。
        if (ShowGoat is null && ShowOpenCode is null && ShowDeepSeek is null) return null;

        var visible = new List<string>();
        if (ShowGoat != false) visible.Add("goat");
        if (ShowOpenCode != false) visible.Add("opencode");
        if (ShowDeepSeek != false) visible.Add("deepseek");

        // 旧 SourceOrder 里出现过的键按它排前,其余保持默认顺序
        if (!string.IsNullOrWhiteSpace(SourceOrder))
        {
            var order = SourceOrder.Split(',').Select(k => k.Trim()).ToList();
            var known = SourceCatalog.Keys;
            visible = visible.OrderBy(k => order.IndexOf(k) is var i && i >= 0 ? i : 99)
                .ThenBy(k => known.ToList().IndexOf(k))
                .ToList();
        }
        return visible.Count > 0 ? visible : null;
    }

    /// <summary>环色字典:只留已知源、合法色值;空色值不写(免得 JSON 里一堆 null)。
    /// 顺手把旧版三个独立环色字段迁进来。</summary>
    private Dictionary<string, string> NormalizeTints()
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in SourceTints ?? new())
        {
            if (!SourceCatalog.IsKnownKey(key)) continue;
            if (NormalizeTint(value) is { } tint) result[key] = tint;
        }
        // 迁移旧字段(仅当字典里还没有这个键时,新值优先)
        foreach (var (key, legacy) in new[]
                 {
                     ("goat", GoatTint), ("opencode", OpenCodeTint), ("deepseek", DeepSeekTint),
                 })
        {
            if (result.ContainsKey(key)) continue;
            if (NormalizeTint(legacy) is { } tint) result[key] = tint;
        }
        return result;
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
        VisibleSources = new List<string>(VisibleSources),
        ShowPercent = ShowPercent,
        HideIdleInnerRing = HideIdleInnerRing,
        RingSize = RingSize,
        RingSpacing = RingSpacing,
        HideToSliver = HideToSliver,
        HideInFullScreen = HideInFullScreen,
        UseBrowserSessionForModelDetail = UseBrowserSessionForModelDetail,
        ProxyMode = ProxyMode,
        ProxyAddress = ProxyAddress,
        SourceTints = new Dictionary<string, string>(SourceTints),
    };
}
