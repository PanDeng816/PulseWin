using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PulseWin;

/// <summary>
/// 工具窗（设置 / 用量 / 会话）的**主题与密度**：XAML 资源键与代码画笔共用这一组值。
///
/// 分工：
///  · XAML 侧用 <c>{DynamicResource XxxBrush}</c>，深色由 <c>Themes\Dark.xaml</c> 覆盖同名键；
///  · 代码生成的内容（图表、列表、卡片）不能走资源，改从这里取色；
///  · 密度（舒适/紧凑）只影响卡片内边距与行距，键在 <c>App.xaml</c> 顶层，切换时整体改写。
/// </summary>
public static class UiTheme
{
    /// <summary>当前是否深色。切换入口只有 <see cref="Apply"/>。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>主题或密度变了：代码生成的内容要重建（图表/列表/自绘卡片）。</summary>
    public static event Action? Changed;

    // ————————————————— 颜色 —————————————————

    public static Color PageBack => P(0x13, 0x13, 0x16, 0xF4, 0xF4, 0xF7);
    /// <summary>侧栏底（深色下比页面底更暗一点）。</summary>
    public static Color SidebarBack => P(0x16, 0x16, 0x1A, 0xF4, 0xF4, 0xF7);
    public static Color WindowBack => P(0x1B, 0x1B, 0x20, 0xFF, 0xFF, 0xFF);
    public static Color CardBack => P(0x1E, 0x1E, 0x23, 0xFF, 0xFF, 0xFF);
    public static Color CardBorder => P(0x2C, 0x2C, 0x33, 0xE8, 0xE8, 0xEC);
    public static Color Separator => P(0x2A, 0x2A, 0x30, 0xEC, 0xEC, 0xEF);
    public static Color TextMain => P(0xED, 0xED, 0xF1, 0x1D, 0x1D, 0x1F);
    public static Color TextSub => P(0x9A, 0x9A, 0xA2, 0x86, 0x86, 0x8B);
    public static Color FieldBack => P(0x2A, 0x2A, 0x31, 0xF0, 0xF0, 0xF2);
    /// <summary>分段开关轨道（未选中）。</summary>
    public static Color SegBack => P(0x2C, 0x2C, 0x33, 0xEB, 0xEB, 0xEF);
    public static Color SegHover => P(0x35, 0x35, 0x3D, 0xE0, 0xE0, 0xE4);
    /// <summary>拖拽把手 / 弱化图形。</summary>
    public static Color Handle => P(0x6A, 0x6A, 0x72, 0xB0, 0xB0, 0xB6);
    public static Color Accent => P(0x0A, 0x84, 0xFF, 0x00, 0x7A, 0xFF);
    public static Color AccentSoft => P(0x3F, 0x6F, 0xA8, 0x8C, 0xBF, 0xFF);
    public static Color TileBack => P(0x26, 0x26, 0x2C, 0xF7, 0xF8, 0xFA);
    /// <summary>浅蓝 chip 底（「网页控制台」那类口径标注）。</summary>
    public static Color ChipBack => P(0x17, 0x32, 0x4E, 0xEA, 0xF2, 0xFF);
    public static Color Warning => P(0xFF, 0x8A, 0x65, 0xC7, 0x50, 0x2E);
    public static Color Good => P(0x2E, 0xD4, 0x7A, 0x00, 0xA8, 0x5C);
    public static Color Warn => P(0xFF, 0xB3, 0x40, 0xFF, 0x9F, 0x0A);
    public static Color Bad => P(0xFF, 0x6B, 0x60, 0xE0, 0x48, 0x3E);
    public static Color ChartBar => P(0x0A, 0x84, 0xFF, 0x00, 0x7A, 0xFF);
    public static Color ChartZero => P(0x2C, 0x2C, 0x33, 0xE5, 0xE5, 0xEA);
    public static Color ChartTrack => P(0x2A, 0x2A, 0x30, 0xF0, 0xF0, 0xF3);
    public static Color PeakBar => P(0xFF, 0xB3, 0x40, 0xFF, 0x9F, 0x0A);
    public static Color BarModels => P(0x0A, 0x84, 0xFF, 0x00, 0x7A, 0xFF);
    public static Color BarAgents => P(0x7D, 0x7B, 0xFF, 0x5E, 0x5C, 0xE6);
    public static Color BarProjects => P(0xFF, 0xB3, 0x40, 0xFF, 0x9F, 0x0A);
    public static Color BarSessions => P(0xD5, 0x83, 0xFF, 0xBF, 0x5A, 0xF2);
    public static Color BarEmpty => P(0x4A, 0x4A, 0x52, 0xC7, 0xC7, 0xCC);

    // 热图色阶（空 / 四档绿）
    public static Color HeatEmpty => P(0x26, 0x26, 0x2C, 0xED, 0xED, 0xF0);
    public static Color HeatL1 => P(0x12, 0x3A, 0x2A, 0xD2, 0xF0, 0xE1);
    public static Color HeatL2 => P(0x1B, 0x5C, 0x3F, 0xA4, 0xE2, 0xC3);
    public static Color HeatL3 => P(0x23, 0x87, 0x5A, 0x5F, 0xCD, 0x9E);
    public static Color HeatL4 => P(0x2E, 0xD4, 0x7A, 0x00, 0xA8, 0x5C);

    private static Color P(byte dr, byte dg, byte db, byte lr, byte lg, byte lb) =>
        IsDark ? Color.FromRgb(dr, dg, db) : Color.FromRgb(lr, lg, lb);

    /// <summary>按值缓存冻结画刷：绘制路径里每帧都要取色，不能每次 new。</summary>
    private static readonly Dictionary<Color, Brush> BrushCache = [];

    public static Brush B(Color color)
    {
        if (BrushCache.TryGetValue(color, out var cached)) return cached;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        BrushCache[color] = brush;
        return brush;
    }

    // ————————————————— 密度 —————————————————

    /// <summary>紧凑档（设置项 UiDensity=1）。</summary>
    public static bool Compact => AppSettings.Current.UiDensity == 1;

    /// <summary>设置窗分组卡片的内边距（上下要留得下 1px 分隔线）。</summary>
    public static Thickness SettingsCardPad => Compact ? new(12, 3, 12, 3) : new(16, 5, 16, 5);
    public static Thickness SettingsCardGap => Compact ? new(0, 0, 0, 10) : new(0, 0, 0, 16);
    /// <summary>设置窗一行的高度（上下内边距 11 → 7）。</summary>
    public static Thickness RowMargin => Compact ? new(0, 7, 0, 7) : new(0, 11, 0, 11);
    public static Thickness SettingsSectionMargin => Compact ? new(1, 0, 0, 7) : new(1, 0, 0, 10);

    /// <summary>用量窗卡片内边距 / 间距。</summary>
    public static Thickness SpendCardPad => Compact ? new(11, 9, 11, 9) : new(14, 12, 14, 12);
    public static Thickness SpendCardGap => Compact ? new(0, 0, 0, 8) : new(0, 0, 0, 12);
    public static Thickness SpendSectionMargin => Compact ? new(1, 2, 0, 6) : new(1, 4, 0, 8);
    /// <summary>列表行上下间距。</summary>
    public static Thickness RowGap => Compact ? new(0, 2, 0, 2) : new(0, 3, 0, 3);
    /// <summary>KPI 瓦片内边距。</summary>
    public static Thickness TilePad => Compact ? new(9, 7, 9, 6) : new(11, 9, 11, 8);
    public static Thickness TileGap => Compact ? new(0, 0, 6, 6) : new(0, 0, 8, 8);

    // ————————————————— 应用 ——————————————————

    /// <summary>启动时（创建任何窗口之前）按设置应用主题与密度。</summary>
    public static void Load()
    {
        bool dark = AppSettings.Current.UiThemeMode switch
        {
            1 => true,
            2 => SystemPrefersDark(),
            _ => false,
        };
        Apply(dark, notify: false);
    }

    /// <summary>切换主题：深色把 Dark.xaml 合并到 Light.xaml 之后（后合并者优先）。</summary>
    public static void Apply(bool dark, bool notify = true)
    {
        IsDark = dark;
        if (Application.Current is { } app)
        {
            var merged = app.Resources.MergedDictionaries;
            var existing = merged.FirstOrDefault(d =>
                d.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
            if (existing is not null) merged.Remove(existing);
            if (dark)
            {
                merged.Add(new ResourceDictionary
                {
                    Source = new Uri("Themes/Dark.xaml", UriKind.Relative),
                });
            }
            ApplyDensity();
            ApplyGlass();
        }
        if (notify) Changed?.Invoke();
    }

    /// <summary>密度写进应用资源顶层：XAML 的 DynamicResource 会当场刷新。</summary>
    public static void ApplyDensity()
    {
        if (Application.Current is not { } app) return;
        var r = app.Resources;
        r["SettingsCardPad"] = SettingsCardPad;
        r["SettingsCardGap"] = SettingsCardGap;
        r["RowMargin"] = RowMargin;
        r["SettingsSectionMargin"] = SettingsSectionMargin;
        r["SpendCardPad"] = SpendCardPad;
        r["SpendCardGap"] = SpendCardGap;
        r["SpendSectionMargin"] = SpendSectionMargin;
        r["RowGap"] = RowGap;
        r["TilePad"] = TilePad;
        r["TileGap"] = TileGap;
    }

    // ————————————————— 玻璃 —————————————————

    /// <summary>玻璃档位 → 系统材质（设置/用量窗用）。系统低于 Win11 22621 时返回 Off。</summary>
    public static BackdropKind GlassKind => Backdrop.FromSetting(AppSettings.Current.GlassMode);

    /// <summary>
    /// 按玻璃开关改写一组**背景类资源键**：开 = 带 alpha（DWM 材质透得出来），
    /// 关 = 不透明（v1.24 及以前的样子）。
    ///
    /// 只改 A 通道，RGB 永远取当前主题的色值，所以深/浅两套自动跟着走，不用维护两份表。
    /// 层次是三层叠加：窗口底（全透）→ 侧栏/内容页底（半透）→ 卡片（近实）。
    /// **三层都半透会互相盖死**（0.5 叠三层 ≈ 0.88），所以窗口底必须是 Transparent。
    /// </summary>
    public static void ApplyGlass()
    {
        if (Application.Current is not { } app) return;
        var r = app.Resources;
        bool on = AppSettings.Current.GlassMode > 0;

        r["ShellBackBrush"] = on ? Brushes.Transparent : B(PageBack);

        byte sidebar = IsDark ? (byte)0xB0 : (byte)0xC0;
        byte page = IsDark ? (byte)0xD8 : (byte)0xDC;
        byte card = IsDark ? (byte)0xE2 : (byte)0xE8;
        byte tile = IsDark ? (byte)0xD6 : (byte)0xE0;
        byte field = IsDark ? (byte)0xF2 : (byte)0xF4;
        byte seg = IsDark ? (byte)0xD8 : (byte)0xDD;

        SetGlass(r, "SidebarBackBrush", SidebarBack, on, sidebar);
        SetGlass(r, "PageBackBrush", PageBack, on, page);
        SetGlass(r, "CardBackBrush", CardBack, on, card);
        SetGlass(r, "TileBackBrush", TileBack, on, tile);
        SetGlass(r, "FieldBackBrush", FieldBack, on, field);
        SetGlass(r, "SegBackBrush", SegBack, on, seg);
    }

    /// <summary>玻璃关时写回不透明（A=FF），开时给指定 alpha。</summary>
    private static void SetGlass(ResourceDictionary r, string key, Color baseColor, bool on, byte alpha) =>
        r[key] = B(Color.FromArgb(on ? alpha : (byte)0xFF, baseColor.R, baseColor.G, baseColor.B));

    /// <summary>Windows 应用主题（设置 → 个性化 → 颜色 → 选择默认应用模式）。</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
