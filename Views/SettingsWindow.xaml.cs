using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace PulseWin;

public partial class SettingsWindow : Window
{
    private readonly UsageEngine _engine;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _saveTimer;

    /// <summary>
    /// 初始为 true:InitializeComponent 解析 XAML 时,Slider 因为设了 Minimum 会
    /// 把自己的 Value 从默认 0 强制修正到 Minimum,这个 coercion **当场触发
    /// ValueChanged**——而那一刻排在 Slider 后面的 AlertValue 等控件还没被创建,
    /// 处理器里去碰它们就是 NullReferenceException。用这个标志把初始化期间的事件
    /// 全部挡掉,加载完成后再手动刷新一次标签。
    /// </summary>
    private bool _loading = true;

    /// <summary>设置改动后通知主窗重绘(阈值/不透明度都属于渲染参数)。</summary>
    public event Action? SettingsChanged;

    /// <summary>只影响绘制、不影响数据的设置(透明度/告警阈值)走这条:主窗立刻重画,
    /// 但不重读快照。拖动滑块每帧都会触发,不能让它带动磁盘 IO。</summary>
    public event Action? AppearanceChanged;

    /// <summary>源凭据卡:每个数据源一张,按 <see cref="SourceCatalog"/> 生成。</summary>
    private readonly Dictionary<string, SourceCard> _sourceCards = new(StringComparer.Ordinal);

    /// <summary>环色拾取器:每个数据源一个,同样按注册表生成。</summary>
    private readonly Dictionary<string, TintPicker> _tintPickers = new(StringComparer.Ordinal);

    /// <summary>正在把最后一个勾选框按回去(见 OnSourceVisibilityToggled),用来挡掉回弹引发的事件。</summary>
    private bool _revertingSource;

    public SettingsWindow(UsageEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        // 高度固定为工作区的 2/3,其余靠滚动条看。设置项只会越加越多,
        // 让窗口跟着内容长高迟早顶出屏幕。
        var workArea = SystemParameters.WorkArea;
        Height = Math.Round(workArea.Height * 2 / 3);
        MaxHeight = Math.Max(workArea.Height - 40, 240);

        LoadSettings();

        // 圆环顺序列表:拖动/箭头改序后写设置并让主窗重排
        RingOrder.OrderChanged += RingOrder_OrderChanged;

        // 状态是分钟级变化，2 秒足够；每秒重写所有卡片只会白白触发布局
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        // 拖动滑块会连续触发改动:界面立即生效,落盘延迟到停手之后
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            AppSettings.Current.Save();
        };

        RefreshStatus();

        // 玻璃外壳:句柄还没建,Backdrop 会挂到 SourceInitialized 上滞后应用。
        Backdrop.Apply(this, UiTheme.GlassKind, UiTheme.IsDark);
    }

    private void LoadSettings()
    {
        var s = AppSettings.Current;
        AlertSlider.Value = Math.Round(s.AlertThreshold * 100);
        OpacitySlider.Value = Math.Round(s.SurfaceOpacity * 100);
        IntervalSlider.Value = s.SyncIntervalSeconds;
        UsdRateBox.Text = s.UsdToCny.ToString("0.##", CultureInfo.InvariantCulture);
        ProxyCombo.SelectedIndex = Math.Clamp(s.ProxyMode, 0, 2);
        ProxyBox.Text = s.ProxyAddress ?? "";
        ShowPercentBox.IsChecked = s.ShowPercent;
        IdleInnerRingBox.IsChecked = s.HideIdleInnerRing;
        SizeSmall.IsChecked = s.RingSize == 0;
        SizeStandard.IsChecked = s.RingSize == 1;
        SizeLarge.IsChecked = s.RingSize == 2;
        SpacingTight.IsChecked = s.RingSpacing == 0;
        SpacingStandard.IsChecked = s.RingSpacing == 1;
        SpacingLoose.IsChecked = s.RingSpacing == 2;
        ThemeLight.IsChecked = s.UiThemeMode == 0;
        ThemeDark.IsChecked = s.UiThemeMode == 1;
        ThemeSystem.IsChecked = s.UiThemeMode == 2;
        DensityComfort.IsChecked = s.UiDensity == 0;
        DensityCompact.IsChecked = s.UiDensity == 1;
        GlassOff.IsChecked = s.GlassMode == 0;
        GlassFrost.IsChecked = s.GlassMode == 1;
        GlassLiquid.IsChecked = s.GlassMode == 2;
        SliverBox.IsChecked = s.HideToSliver;
        FullScreenBox.IsChecked = s.HideInFullScreen;
        BrowserSessionBox.IsChecked = s.UseBrowserSessionForModelDetail;
        BigModelBudgetBox.Text = s.BigModelMonthlyBudgetCny is { } b
            ? b.ToString("0.##", CultureInfo.InvariantCulture)
            : "";

        // 数据源凭据卡 + 圆环顺序:都从注册表生成,加源不用改这里
        BuildSourceCards();
        BuildTintPickers();
        ReloadRingOrder();
        BuildManualCookieSites();
        UpdateManualCookieHint();
        _loading = false;
        UpdateLabels();
        RailPreview?.Refresh();
    }

    // ————————————————— 数据源 / 圆环顺序(都按注册表生成) —————————————————

    /// <summary>按 <see cref="SourceCatalog"/> 为每个源建一张凭据卡,加源不用改这里。</summary>
    private void BuildSourceCards()
    {
        SourceCardsPanel.Children.Clear();
        _sourceCards.Clear();
        foreach (var source in SourceCatalog.All)
        {
            var card = new SourceCard(source, _engine);
            card.VisibilityToggled += () => OnSourceVisibilityToggled(card);
            card.AccountsChanged += OnAccountsChanged;
            card.BasisChanged += () =>
            {
                SettingsChanged?.Invoke();
                SaveSoon();
                _engine.RequestRefreshNow();
            };
            _sourceCards[source.Key] = card;
            SourceCardsPanel.Children.Add(card);
        }
    }

    /// <summary>OpenCode Go 账号增删/改名/显示变了:环顺序与 rail 都要重载。</summary>
    private void OnAccountsChanged()
    {
        SaveSoon();
        ReloadRingOrder();
        SettingsChanged?.Invoke();
    }

    /// <summary>环色拾取器:每个源一个,按注册表生成(从前是三个命名控件 + 三段 if)。</summary>
    private void BuildTintPickers()
    {
        TintPanel.Children.Clear();
        _tintPickers.Clear();
        foreach (var source in SourceCatalog.All)
        {
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = source.DisplayName,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = UiTheme.B(UiTheme.TextMain),
            });
            var picker = new TintPicker { Value = AppSettings.Current.TintFor(source.Key) };
            picker.ValueChanged += (_, _) =>
            {
                if (_loading) return;
                var s = AppSettings.Current;
                if (picker.Value is { } tint) s.SourceTints[source.Key] = tint;
                else s.SourceTints.Remove(source.Key);
                s.Save();
                SettingsChanged?.Invoke();
            };
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            _tintPickers[source.Key] = picker;
            TintPanel.Children.Add(row);
        }
    }

    /// <summary>重建圆环顺序列表(显示哪些源变了 / 顺序变了都走这里)。</summary>
    private void ReloadRingOrder() => RingOrder.Load(AppSettings.Current.VisibleSources);

    /// <summary>圆环顺序被拖动或箭头改了。</summary>
    private void RingOrder_OrderChanged(List<string> order)
    {
        AppSettings.Current.VisibleSources = order;
        AppSettings.Current.Save();
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    /// <summary>
    /// 某个源的"显示"勾选变了。写入 <see cref="AppSettings.VisibleSources"/> 并保证
    /// **至少留一个源**(全关之后 rail 上就没有内容了);源集合变了才让引擎重新同步。
    ///
    /// 过滤与兜底都要认**实例键**(opencode:2):附加账号的键也住在这个列表里,
    /// 用源键过滤会把它们删掉;实例是否显示跟随它所属源的勾选(关掉整个源 =
    /// 该源的附加账号一起下线)。
    /// </summary>
    private void OnSourceVisibilityToggled(SourceCard card)
    {
        if (_loading || _revertingSource) return;
        var s = AppSettings.Current;

        // 按当前顺序重建可见列表:先保序,再按勾选增减
        var ordered = s.VisibleSources.Where(SourceKeys.IsKnown).ToList();
        foreach (var source in SourceCatalog.All)
            if (!ordered.Contains(source.Key)) ordered.Add(source.Key);

        // 主源键按卡勾选;实例键跟随所属源卡的勾选
        var visible = ordered
            .Where(k => _sourceCards.TryGetValue(SourceKeys.BaseKey(k), out var c) && c.IsChecked
                || !_sourceCards.ContainsKey(SourceKeys.BaseKey(k)))
            .ToList();

        if (visible.Count == 0)
        {
            // 回弹:把刚取消的那个按回去(直接改控件状态,不走事件)
            _revertingSource = true;
            card.SetCheckedQuiet(true);
            _revertingSource = false;
            visible.Add(card.Key);
            SourceHint.Text = "至少要保留一个数据源——全部关掉之后浮窗上就没有内容了。";
            SourceHint.Visibility = Visibility.Visible;
        }
        else
        {
            SourceHint.Visibility = Visibility.Collapsed;
        }

        // 可见集合不变(只是回弹)就不用惊动引擎
        bool changed = !visible.ToHashSet().SetEquals(s.VisibleSources.ToHashSet());
        s.VisibleSources = visible;
        s.Save();
        ReloadRingOrder();

        if (changed) _engine.RequestRefreshNow();

        // 主窗会按新的勾选重新加载数据并重算单元高度,rail 长度随即跟着变
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void UpdateLabels()
    {
        AlertValue.Text = $"{AlertSlider.Value:0}%";
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        IntervalValue.Text = $"{IntervalSlider.Value:0}s";
    }

    private void SettingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        UpdateLabels();
        Apply();
    }

    private void Apply()
    {
        var s = AppSettings.Current;
        s.AlertThreshold = AlertSlider.Value / 100d;
        s.SurfaceOpacity = OpacitySlider.Value / 100d;
        s.SyncIntervalSeconds = (int)IntervalSlider.Value;
        AppearanceChanged?.Invoke();   // 外观类设置:让浮窗立刻重绘,但不重读数据
        RailPreview?.Refresh();        // 背景不透明度也在预览里
        SaveSoon();
    }

    /// <summary>拖动滑块会连续触发:界面立即生效,落盘延迟到停手之后。</summary>
    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// 汇率(美元→人民币),只影响用量统计金额的显示。0 = 显示美元原值。
    /// 非法输入回弹为当前值,不让坏数字进设置。
    /// </summary>
    private void ProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.ProxyMode = Math.Max(ProxyCombo.SelectedIndex, 0);
        ProxyRow.Visibility = AppSettings.Current.ProxyMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        SaveSoon();   // 代理要重启才生效,不触发 SettingsChanged
    }

    private void ProxyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string text = ProxyBox.Text?.Trim() ?? "";
        // 简单校验:能解析成带主机的 URI 才收,否则回弹
        string value = Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host) ? uri.ToString() : AppSettings.Current.ProxyAddress ?? "";
        AppSettings.Current.ProxyAddress = string.IsNullOrWhiteSpace(value) ? null : value;
        ProxyBox.Text = AppSettings.Current.ProxyAddress ?? "";
        SaveSoon();
    }

    private void UsdRateBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string text = UsdRateBox.Text?.Trim() ?? "";
        double value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && parsed >= 0
            ? Math.Clamp(parsed, 0, 100)
            : AppSettings.Current.UsdToCny;

        AppSettings.Current.UsdToCny = value;
        UsdRateBox.Text = value.ToString("0.##", CultureInfo.InvariantCulture);
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void RefreshStatus()
    {
        foreach (var (key, card) in _sourceCards)
            card.RefreshStatus(_engine.StatusForSource(key));
        DataDirText.Text = SnapshotSource.DataDirectory;
    }

    // ————————————————— 主界面(显示哪些环 / 是否显示读数) —————————————————

    /// <summary>"在圆环下显示读数"开关(与"显示哪些源"无关,各管各的)。</summary>
    private void ShowPercent_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.ShowPercent = ShowPercentBox.IsChecked == true;
        RailPreview?.Refresh();
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    /// <summary>"只在正在使用时显示 5 小时圈"开关。只影响绘制,不改 rail 尺寸(环心不动)。</summary>
    private void IdleInnerRing_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.HideIdleInnerRing = IdleInnerRingBox.IsChecked == true;
        RailPreview?.Refresh();
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    /// <summary>
    /// 侧栏切页。XAML 解析期间 NavAppearance 的 IsChecked=True 也会触发一次,
    /// 那时 PageTitle 还没创建——初始可见性已在 XAML 里写对,这里只在加载后生效。
    /// </summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        (string title, StackPanel page) = sender switch
        {
            _ when sender == NavRings => ("圆环与数字", PageRings),
            _ when sender == NavBehavior => ("行为", PageBehavior),
            _ when sender == NavSources => ("数据源", PageSources),
            _ when sender == NavRefresh => ("刷新与网络", PageRefresh),
            _ when sender == NavAbout => ("关于", PageAbout),
            _ => ("外观", PageAppearance),
        };
        PageTitle.Text = title;
        PageAppearance.Visibility = page == PageAppearance ? Visibility.Visible : Visibility.Collapsed;
        PageRings.Visibility = page == PageRings ? Visibility.Visible : Visibility.Collapsed;
        PageBehavior.Visibility = page == PageBehavior ? Visibility.Visible : Visibility.Collapsed;
        PageSources.Visibility = page == PageSources ? Visibility.Visible : Visibility.Collapsed;
        PageRefresh.Visibility = page == PageRefresh ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == PageAbout ? Visibility.Visible : Visibility.Collapsed;

        if (page == PageAbout) RefreshUpdateHint();
    }

    /// <summary>
    /// 套餐/渠道的用量展示在独立的「用量」窗口(原"套餐用量"页已整体搬走,
    /// 剩下的都和悬浮环额度同步有关)。这里只把请求转出去,由 App 打开窗口。
    /// </summary>
    public event Action? OpenUsageRequested;

    private void OpenUsage_Click(object sender, RoutedEventArgs e) => OpenUsageRequested?.Invoke();

    /// <summary>BigModel 包月预算(元/月):正数生效,清空 = 不设进度只看估算。</summary>
    private void BigModelBudget_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.BigModelMonthlyBudgetCny =
            double.TryParse(BigModelBudgetBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && v > 0 ? v : null;
        SaveSoon();
    }

    /// <summary>开关"浏览器会话明细"(数据源页):存盘并重取一次(开了就立即去读 cookie)。</summary>
    private void BrowserSession_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.UseBrowserSessionForModelDetail = BrowserSessionBox.IsChecked == true;
        AppSettings.Current.Save();
    }

    // ————————————————— 浏览器登录会话(内嵌登录窗 + 手动粘贴兜底) —————————————————
    // 「自动读本机浏览器 cookie 库」这条路已被 Edge 的 v20(App-Bound Encryption)堵死
    // (Edge 154 起所有 cookie 都是 App-Bound 加密,第三方进程解不了),所以首选改成开一个
    // 内嵌 WebView2 让用户登录一次:浏览器引擎把**已解密**的会话 cookie 直接给出来,既不碰
    // 浏览器文件、也不碰 ABE。详见 BrowserLoginWindow / BrowserLoginTargets。

    /// <summary>手动粘贴那行的站点下拉项。</summary>
    private sealed record CookieSite(string Key, string Label);

    private void BuildManualCookieSites()
    {
        ManualCookieSite.ItemsSource = new List<CookieSite>
        {
            new("opencode", "OpenCode Go"),
            new("commandcode", "Command Code GOAT"),
        };
        ManualCookieSite.DisplayMemberPath = nameof(CookieSite.Label);
        ManualCookieSite.SelectedIndex = 0;
    }

    private string SelectedCookieSite =>
        (ManualCookieSite.SelectedItem as CookieSite)?.Key ?? "opencode";

    private void ManualCookieSite_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateManualCookieHint();
    }

    /// <summary>两行的「登录…」按钮共用;站点取 Tag。</summary>
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || BrowserLoginTargets.ByKey(key) is not { } target)
        {
            return;
        }

        bool captured = new BrowserLoginWindow(
            target, Path.Combine(_engine.Paths.RootDirectory, "WebView2"))
        {
            Owner = this,
        }.ShowDialog() == true;

        // 顺手把手动粘贴那行切到这个站,便于看着状态核对
        if (ManualCookieSite.ItemsSource is List<CookieSite> sites)
        {
            ManualCookieSite.SelectedItem = sites.FirstOrDefault(s => s.Key == key);
        }
        UpdateManualCookieHint();
        if (captured) _engine.RequestRefreshNow();   // 立刻用新 cookie 试一次,不用等下一个同步周期
    }

    private void ManualCookieSave_Click(object sender, RoutedEventArgs e)
    {
        string site = SelectedCookieSite;
        string text = ManualCookieBox.Text.Trim();
        if (text.Length == 0)
        {
            ManualCookieState.Text = "先粘贴再保存";
            return;
        }

        BrowserCookieStore.SaveManual(site, text);
        ManualCookieBox.Text = "";
        UpdateManualCookieHint();
        _engine.RequestRefreshNow();   // 立刻用新 cookie 试一次,不用等下一个同步周期
    }

    private void ManualCookieClear_Click(object sender, RoutedEventArgs e)
    {
        BrowserCookieStore.ClearManual(SelectedCookieSite);
        UpdateManualCookieHint();
        _engine.RequestRefreshNow();
    }

    private void UpdateManualCookieHint()
    {
        SetLoginState(OpenCodeLoginState, OpenCodeLoginButton, "opencode");
        SetLoginState(CommandCodeLoginState, CommandCodeLoginButton, "commandcode");

        string site = SelectedCookieSite;
        bool hasSession = BrowserCookieStore.HasManual(site);
        ManualCookieState.Text = hasSession ? "已保存" : "";
        ManualCookieHint.Text = (site, hasSession) switch
        {
            ("commandcode", true) => "已保存 commandcode.ai 会话。",
            ("commandcode", false) =>
                "登录窗用不了时的兜底:在 commandcode.ai 按 F12 → Application → Cookies,"
                + "复制 __Secure-commandcode_prod_.session_token 的值贴到右边。",
            (_, true) => "已保存 opencode.ai 会话。",
            (_, false) =>
                "登录窗用不了时的兜底:在 opencode.ai 控制台按 F12 → Application → Cookies,"
                + "复制 __Host-console_session 的值贴到右边。",
        };
    }

    /// <summary>登录行的状态:有会话就显示「已保存登录会话」并把按钮改成「重新登录…」。</summary>
    private static void SetLoginState(System.Windows.Controls.TextBlock state,
        System.Windows.Controls.Button button, string siteKey)
    {
        bool has = BrowserCookieStore.HasManual(siteKey);
        state.Text = has ? "已保存登录会话" : "";
        button.Content = has ? "重新登录…" : "登录…";
    }

    // ————————————————— 行为/外观的批 1 设置 —————————————————

    /// <summary>主题三档:浅色 / 深色 / 跟随系统。切换即时生效(不再要求重启)。</summary>
    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.UiThemeMode = ThemeDark.IsChecked == true ? 1 : ThemeSystem.IsChecked == true ? 2 : 0;
        s.Save();
        bool dark = s.UiThemeMode == 1 || (s.UiThemeMode == 2 && UiTheme.SystemPrefersDark());
        UiTheme.Apply(dark);
        // 深色要连标题栏一起改,否则深色窗顶着一片白标题栏
        Backdrop.ApplyAll(UiTheme.GlassKind, UiTheme.IsDark);
        RefreshCodeGenerated();
    }

    /// <summary>密度两档。XAML 侧靠 DynamicResource 自动换,Padding 写在代码里的控件要重建。</summary>
    private void Density_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.UiDensity = DensityCompact.IsChecked == true ? 1 : 0;
        AppSettings.Current.Save();
        UiTheme.Apply(UiTheme.IsDark);   // 重新写密度资源
        RefreshCodeGenerated();
    }

    /// <summary>
    /// 玻璃三档:0 关 / 1 磨砂 / 2 玻璃。
    /// 一处开关管两层 —— 设置/用量窗换系统材质,rail 换自绘玻璃层(见 GlassPaint)。
    /// </summary>
    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.GlassMode = GlassLiquid.IsChecked == true ? 2 : GlassFrost.IsChecked == true ? 1 : 0;
        s.Save();
        UiTheme.Apply(UiTheme.IsDark);              // 重写背景键的 alpha
        Backdrop.ApplyAll(UiTheme.GlassKind, UiTheme.IsDark);
        AppearanceChanged?.Invoke();                // 主窗重画(rail 的自绘玻璃层)
    }

    /// <summary>颜色/内边距写死在构造里的这些控件,主题或密度一变就得重建。</summary>
    private void RefreshCodeGenerated()
    {
        BuildSourceCards();
        BuildTintPickers();
        ReloadRingOrder();
        RailPreview?.Refresh();
    }

    private void RingSize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.RingSize = SizeSmall.IsChecked == true ? 0 : SizeLarge.IsChecked == true ? 2 : 1;
        RailPreview?.Refresh();
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void RingSpacing_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.RingSpacing = SpacingTight.IsChecked == true ? 0 : SpacingLoose.IsChecked == true ? 2 : 1;
        RailPreview?.Refresh();
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void Sliver_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.HideToSliver = SliverBox.IsChecked == true;
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void FullScreen_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.HideInFullScreen = FullScreenBox.IsChecked == true;
        SaveSoon();
    }

    // ————————————————— 关于页 —————————————————

    private void RefreshUpdateHint()
    {
        AboutVersion.Text = $"版本 {UpdateChecker.CurrentVersion.ToString(3)}";
        UpdateHint.Text = UpdateChecker.Available is { } rel
            ? $"有新版本:{rel.Title}。去 GitHub Releases 页面下载。"
            : UpdateChecker.LastError is { } err ? $"上次检查失败:{err}"
            : UpdateChecker.LastCheckedAt is { } at ? $"已是最新版本。上次检查:{at:HH:mm}。"
            : "还没有检查过。";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateHint.Text = "正在检查…";
        await UpdateChecker.CheckAsync();
        RefreshUpdateHint();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        // 关窗时如果还有没落盘的改动(刚拖完滑块就关),立刻写掉
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            AppSettings.Current.Save();
        }
        base.OnClosed(e);
    }
}
