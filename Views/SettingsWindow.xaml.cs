using System.Globalization;
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

    /// <summary>
    /// 上一次的"显示哪几个源"组合。源开关变了才需要让引擎重新同步——
    /// 只改"是否显示读数"不该白打一次 API。
    /// </summary>
    private (bool Goat, bool Go, bool DeepSeek) _lastSourceFlags;

    /// <summary>正在把最后一个勾选框按回去(见 Visibility_Changed),用来挡掉回弹引发的事件。</summary>
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

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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
    }

    private void LoadSettings()
    {
        var s = AppSettings.Current;
        AlertSlider.Value = Math.Round(s.AlertThreshold * 100);
        OpacitySlider.Value = Math.Round(s.SurfaceOpacity * 100);
        IntervalSlider.Value = s.SyncIntervalSeconds;
        BasisCombo.SelectedIndex = s.DeepSeekBasis switch
        {
            BalanceBasis.BalanceOnly => 1,
            BalanceBasis.Budget => 2,
            _ => 0,
        };
        BudgetBox.Text = s.DeepSeekBudget?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        UsdRateBox.Text = s.UsdToCny.ToString("0.##", CultureInfo.InvariantCulture);
        ProxyCombo.SelectedIndex = Math.Clamp(s.ProxyMode, 0, 2);
        ProxyBox.Text = s.ProxyAddress ?? "";
        ShowGoatBox.IsChecked = s.ShowGoat;
        ShowGoBox.IsChecked = s.ShowOpenCode;
        ShowDeepSeekBox.IsChecked = s.ShowDeepSeek;
        ShowPercentBox.IsChecked = s.ShowPercent;
        SizeSmall.IsChecked = s.RingSize == 0;
        SizeStandard.IsChecked = s.RingSize == 1;
        SizeLarge.IsChecked = s.RingSize == 2;
        SpacingTight.IsChecked = s.RingSpacing == 0;
        SpacingStandard.IsChecked = s.RingSpacing == 1;
        SpacingLoose.IsChecked = s.RingSpacing == 2;
        SliverBox.IsChecked = s.HideToSliver;
        FullScreenBox.IsChecked = s.HideInFullScreen;
        BrowserSessionBox.IsChecked = s.UseBrowserSessionForModelDetail;
        OrderCombo.SelectedIndex = s.SourceOrder switch
        {
            "goat,deepseek,opencode" => 1,
            "opencode,goat,deepseek" => 2,
            "opencode,deepseek,goat" => 3,
            "deepseek,goat,opencode" => 4,
            "deepseek,opencode,goat" => 5,
            _ => 0,
        };
        _lastSourceFlags = (s.ShowGoat, s.ShowOpenCode, s.ShowDeepSeek);
        GoatTintPick.Value = s.GoatTint;
        GoTintPick.Value = s.OpenCodeTint;
        DeepSeekTintPick.Value = s.DeepSeekTint;
        UpdateBasisHint();
        UpdatePercentHint();
        _loading = false;
        UpdateLabels();
    }

    // ————————————————— 环色 —————————————————

    /// <summary>色板里选了颜色(或切回自动):只更新变化的那一项,存盘并让浮窗立刻重绘。</summary>
    private void Tint_ValueChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        if (sender == GoatTintPick) s.GoatTint = GoatTintPick.Value;
        else if (sender == GoTintPick) s.OpenCodeTint = GoTintPick.Value;
        else s.DeepSeekTint = DeepSeekTintPick.Value;
        s.Save();
        SettingsChanged?.Invoke();
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
        SettingsChanged?.Invoke();   // 让浮窗立刻按新值重绘
        SaveSoon();
    }

    /// <summary>拖动滑块会连续触发:界面立即生效,落盘延迟到停手之后。</summary>
    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ————————————————— DeepSeek —————————————————

    private BalanceBasis SelectedBasis() => BasisCombo.SelectedIndex switch
    {
        1 => BalanceBasis.BalanceOnly,
        2 => BalanceBasis.Budget,
        _ => BalanceBasis.SinceTopUp,
    };

    private void UpdateBasisHint()
    {
        var basis = SelectedBasis();
        BudgetRow.Visibility = basis == BalanceBasis.Budget ? Visibility.Visible : Visibility.Collapsed;
        BasisHint.Text = basis switch
        {
            BalanceBasis.SinceTopUp =>
                "以本程序观察到的最高余额为满分:余额上涨只可能是充值,所以一涨就重置回满。"
                + "首次运行没有历史峰值,环会从 0% 开始,直到真的花了钱。",
            BalanceBasis.BalanceOnly =>
                "不画百分比,环上直接显示余额金额(短写法,精确值在悬停卡里)。",
            _ => "以你填的金额为满分:(预算 − 余额) / 预算。留空或填 0 则退回只看余额。",
        };
    }

    private void BasisCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.DeepSeekBasis = SelectedBasis();
        UpdateBasisHint();
        SettingsChanged?.Invoke();
        SaveSoon();
        _engine.RequestRefreshNow();   // 分母换了,立刻按新基准重算一次
    }

    private void BudgetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string text = BudgetBox.Text?.Trim() ?? "";
        double? value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && parsed > 0
            ? parsed
            : null;

        AppSettings.Current.DeepSeekBudget = value;
        BudgetBox.Text = value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        SettingsChanged?.Invoke();
        SaveSoon();
        _engine.RequestRefreshNow();
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
        var g = _engine.Goat;
        GoatSource.Text = string.IsNullOrEmpty(g.CredentialSource) ? "" : $"凭据来源:{g.CredentialSource}";
        GoatStatus.Text = g.StatusText;
        GoatHint.Text = g.State switch
        {
            SourceState.Connected => "",
            SourceState.AuthenticationRequired => "未找到可用凭据:可输入 Command Code API Key(或登录过 Command Code CLI 后重启本程序自动读取)。",
            _ => "正在使用最后一次成功数据;恢复后自动更新。",
        };

        var go = _engine.Go;
        GoSource.Text = string.IsNullOrEmpty(go.CredentialSource) ? "" : $"凭据来源:{go.CredentialSource}";
        GoStatus.Text = go.StatusText;
        GoHint.Text = go.State switch
        {
            SourceState.Connected => "",
            SourceState.AuthenticationRequired => "未找到可用凭据:可输入 OpenCode Go API Key(或本机 OpenCode auth.json 里有 opencode-go 登录态时自动读取)。",
            _ => "正在使用最后一次成功数据;恢复后自动更新。",
        };

        var ds = _engine.DeepSeek;
        DeepSeekSource.Text = string.IsNullOrEmpty(ds.CredentialSource) ? "" : $"凭据来源:{ds.CredentialSource}";
        DeepSeekStatus.Text = ds.StatusText;
        DeepSeekHint.Text = ds.State switch
        {
            SourceState.Connected => "",
            SourceState.AuthenticationRequired => "未找到凭据:输入 DeepSeek API Key(在 platform.deepseek.com 控制台创建)。",
            _ => "正在使用最后一次成功数据;恢复后自动更新。",
        };

        DataDirText.Text = $"数据目录(便携):{SnapshotSource.DataDirectory}";
    }

    private async void SaveGoat_Click(object sender, RoutedEventArgs e)
    {
        var key = GoatKey.Text?.Trim();
        if (string.IsNullOrEmpty(key)) return;
        GoatHint.Text = "正在验证 Key…";
        bool ok = await _engine.SaveManualKeyAsync(MonitorSource.CommandCodeGoat, key);
        GoatHint.Text = ok ? "已保存并连接成功。" : "验证失败:Key 无效或网络异常。";
        if (ok) GoatKey.Clear();
    }

    private async void SaveGo_Click(object sender, RoutedEventArgs e)
    {
        var key = GoKey.Text?.Trim();
        if (string.IsNullOrEmpty(key)) return;
        GoHint.Text = "正在验证 Key…";
        bool ok = await _engine.SaveManualKeyAsync(MonitorSource.OpenCodeGo, key);
        GoHint.Text = ok ? "已保存并连接成功。" : "验证失败:Key 无效或网络异常。";
        if (ok) GoKey.Clear();
    }

    private async void SaveDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        var key = DeepSeekKey.Text?.Trim();
        if (string.IsNullOrEmpty(key)) return;
        DeepSeekHint.Text = "正在验证 Key…";
        bool ok = await _engine.SaveManualKeyAsync(MonitorSource.DeepSeek, key);
        DeepSeekHint.Text = ok ? "已保存并连接成功。" : "验证失败:Key 无效或网络异常。";
        if (ok) DeepSeekKey.Clear();
    }

    // ————————————————— 主界面(显示哪些环 / 是否显示读数) —————————————————

    private void Visibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _revertingSource) return;
        var s = AppSettings.Current;
        s.ShowGoat = ShowGoatBox.IsChecked == true;
        s.ShowOpenCode = ShowGoBox.IsChecked == true;
        s.ShowDeepSeek = ShowDeepSeekBox.IsChecked == true;
        s.ShowPercent = ShowPercentBox.IsChecked == true;

        // **至少要保留一个数据源**(上游规格:设置必须保证始终留有一个可用入口)。
        // 三个全关之后主窗虽然还有占位兜底、不至于崩,但 rail 上已经没有任何内容,
        // 这个状态没有意义。把刚刚取消的那一个按回去,并说明原因——比默默允许存下
        // 一个空配置要好。
        if (sender is CheckBox { IsChecked: false } box
            && !s.ShowGoat && !s.ShowOpenCode && !s.ShowDeepSeek)
        {
            _revertingSource = true;
            box.IsChecked = true;          // 回弹会触发一次新事件,用标志挡掉
            _revertingSource = false;
            s.ShowGoat = ShowGoatBox.IsChecked == true;
            s.ShowOpenCode = ShowGoBox.IsChecked == true;
            s.ShowDeepSeek = ShowDeepSeekBox.IsChecked == true;
            SourceHint.Text = "至少要保留一个数据源——三个全关之后浮窗上就没有内容了。";
            SourceHint.Visibility = Visibility.Visible;
        }
        else
        {
            SourceHint.Visibility = Visibility.Collapsed;
        }

        UpdatePercentHint();

        // 源开关变了要顺带让引擎重新同步:刚勾上的源不该等到下一轮(最长 60 秒)
        // 才有数据,刚取消的源也不必再等一个周期才停。
        var flags = (s.ShowGoat, s.ShowOpenCode, s.ShowDeepSeek);
        if (flags != _lastSourceFlags)
        {
            _lastSourceFlags = flags;
            _engine.RequestRefreshNow();
        }

        // 主窗会按新的勾选重新加载数据并重算单元高度,rail 长度随即跟着变
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void UpdatePercentHint()
    {
        PercentHint.Text = ShowPercentBox.IsChecked == true
            ? "读数显示在圆环下方,每个单元更高。"
            : "已隐藏读数:rail 上只有圆环,间距与整体高度都会收窄。";
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
            _ when sender == NavModels => ("套餐用量", PageModels),
            _ when sender == NavRefresh => ("刷新与网络", PageRefresh),
            _ when sender == NavAbout => ("关于", PageAbout),
            _ => ("外观", PageAppearance),
        };
        PageTitle.Text = title;
        PageAppearance.Visibility = page == PageAppearance ? Visibility.Visible : Visibility.Collapsed;
        PageRings.Visibility = page == PageRings ? Visibility.Visible : Visibility.Collapsed;
        PageBehavior.Visibility = page == PageBehavior ? Visibility.Visible : Visibility.Collapsed;
        PageSources.Visibility = page == PageSources ? Visibility.Visible : Visibility.Collapsed;
        PageModels.Visibility = page == PageModels ? Visibility.Visible : Visibility.Collapsed;
        PageRefresh.Visibility = page == PageRefresh ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == PageAbout ? Visibility.Visible : Visibility.Collapsed;

        // 模型页要读本机用量库(几万条明细),**只在真的打开这一页时才构建**,
        // 离开就释放(见 ReleaseModelsLedger)。这是"新增功能不涨常驻内存"的关键。
        if (page == PageModels) EnsureModelsLoaded();
        else ReleaseModelsLedger();

        if (page == PageAbout) RefreshUpdateHint();
    }

    // ————————————————— 套餐用量页 —————————————————

    private SpendSpan _modelSpan = SpendSpan.Week;
    private List<ChannelUsage> _channels = [];
    private ChannelUsage? _selectedChannel;
    private bool _modelsBusy;
    private bool _modelsLoaded;

    /// <summary>
    /// 本窗口此刻**是否还需要**账本。用于堵住一个竞态：账本构建是异步的，
    /// 若用户在构建完成前就离开套餐页/关窗，同步的 <see cref="ReleaseModelsLedger"/>
    /// 会因为"还没加载完"什么都不做，而构建完成后引用就永久挂住了。
    /// </summary>
    private bool _wantLedger;

    private SpendLedger? _modelsLedger;

    private void ModelSpan_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _modelSpan = sender switch
        {
            _ when sender == ModelSpanToday => SpendSpan.Today,
            _ when sender == ModelSpanMonth => SpendSpan.Month,
            _ when sender == ModelSpanAll => SpendSpan.All,
            _ => SpendSpan.Week,
        };
        string? keep = _selectedChannel?.Name;
        RefreshChannelRows();
        // 区间变了尽量停在同一个套餐上(它还在就选中它)
        if (keep is not null)
            SelectChannel(_channels.FirstOrDefault(c => c.Name == keep));
    }

    /// <summary>开关"浏览器会话明细":存盘并重取一次(开了就立即去读 cookie)。</summary>
    private void BrowserSession_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.UseBrowserSessionForModelDetail = BrowserSessionBox.IsChecked == true;
        AppSettings.Current.Save();
        _ = RefreshWebSessionNoteAsync();
    }

    /// <summary>打开套餐页:取共享账本(引用计数 +1),按当前区间渲染套餐列表。</summary>
    private async void EnsureModelsLoaded()
    {
        _wantLedger = true;
        if (_modelsBusy || _modelsLoaded) return;
        _modelsBusy = true;
        ModelsStatus.Text = "正在读取本机用量记录…";
        try
        {
            var ledger = await SpendLedgerCache.AcquireAsync();

            // 等待期间用户可能已经离开这一页或关了窗:那样这份引用必须立刻还掉,
            // 不能因为"构建完时才发现没人要"而泄漏(那会让关闭后内存一直不回落)。
            if (!_wantLedger)
            {
                SpendLedgerCache.Release();
                return;
            }

            _modelsLoaded = true;
            _modelsLedger = ledger;
            RefreshChannelRows();
            _ = RefreshWebSessionNoteAsync();
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = "读取失败:" + ex.Message;
            Diagnostics.Note("套餐页读取账本失败", ex);
        }
        finally
        {
            _modelsBusy = false;
        }
    }

    /// <summary>离开套餐页:放掉账本引用(计数归零时内存立刻归还)。</summary>
    private void ReleaseModelsLedger()
    {
        _wantLedger = false;
        if (!_modelsLoaded) return;
        _modelsLoaded = false;
        _modelsLedger = null;
        _channels = [];
        _selectedChannel = null;
        ChannelTabsPanel.Children.Clear();
        ChannelDetailPanel.Visibility = Visibility.Collapsed;
        SpendLedgerCache.Release();
    }

    /// <summary>逐模型明细是可选通道(要浏览器登录态),异步补一句说明,不阻塞列表。</summary>
    private async Task RefreshWebSessionNoteAsync()
    {
        var buckets = await CommandCodeWebSession.TryFetchModelCacheAsync(
            DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now);
        if (buckets is { Count: > 0 })
        {
            ModelsIntro.Text = "按套餐/渠道看用量(数据来自 ZCode / OpenCode 记录库)。"
                + $"已从浏览器会话读到 {buckets.Count} 条 Command Code 逐模型明细。"
                + "金额按 models.dev 公开 API 价估算——不是账单。";
        }
        else if (CommandCodeWebSession.LastStatus is { Length: > 0 } note)
        {
            ModelsIntro.Text = "按套餐/渠道看用量(数据来自 ZCode / OpenCode 记录库),"
                + "金额按 models.dev 公开 API 价估算——不是账单。"
                + $"(Command Code 逐模型明细:{note})";
        }
    }

    private void RefreshChannelRows()
    {
        if (_modelsLedger is not { } ledger) return;
        _channels = ChannelUsageIndex.Build(ledger, _modelSpan);
        RenderChannelTabs();

        var (from, _) = SpendSummary.Range(_modelSpan, DateTime.Now);
        string range = _modelSpan == SpendSpan.All
            ? $"自 {(ledger.Earliest ?? DateTime.Now):yyyy-MM-dd} 起"
            : $"{from:yyyy-MM-dd} 起";
        long total = _channels.Sum(c => c.TotalTokens);
        double cost = _channels.Sum(c => c.Cost);
        bool partial = _channels.Any(c => c.HasUnpriced);
        ModelsStatus.Text = _channels.Count == 0
            ? $"{range}:这个区间没有记录。"
            : $"{range}:{_channels.Count} 个套餐 · {SpendFormat.TokensExact(total)} tokens · 估算 {SpendFormat.Amount(cost, partial)}";

        // 默认选中用量最大的套餐
        if (_channels.Count > 0) SelectChannel(_channels[0]);
    }

    /// <summary>套餐切换按钮:每个套餐一个,选中即下面显示它的数据。</summary>
    private void RenderChannelTabs()
    {
        ChannelTabsPanel.Children.Clear();
        foreach (var ch in _channels)
        {
            bool active = _selectedChannel is { } sel && sel.Key == ch.Key && sel.Agent == ch.Agent;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = ch.Name,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = active ? System.Windows.Media.Brushes.White
                    : (System.Windows.Media.Brush)FindResource("TextBrush"),
            });
            var tok = new TextBlock
            {
                Text = "  " + SpendFormat.Tokens(ch.TotalTokens),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = active ? System.Windows.Media.Brushes.White
                    : (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            };
            content.Children.Add(tok);

            var button = new Button
            {
                Style = (Style)FindResource("LightButton"),
                Content = content,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            if (active)
            {
                button.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
                button.Foreground = System.Windows.Media.Brushes.White;
            }
            var captured = ch;
            button.Click += (_, _) => SelectChannel(captured);
            ChannelTabsPanel.Children.Add(button);
        }
    }

    private void SelectChannel(ChannelUsage? ch)
    {
        if (ch is null) return;
        _selectedChannel = ch;
        RenderChannelTabs();   // 重画选中态
        ChannelDetailPanel.Visibility = Visibility.Visible;

        ChannelTitle.Text = ch.Name;
        ChannelSubtitle.Text = $"{ch.Agent} 渠道"
            + (ch.FirstUsed is { } f && ch.LastUsed is { } l ? $" · {f:MM-dd HH:mm} ~ {l:MM-dd HH:mm}" : "");

        // —— KPI 六格:总费用 / 总请求 / 总 TOKEN / 缓存命中率 / 缓存读写 / 会话数 ——
        ChannelKpiGrid.Children.Clear();
        AddCell(ChannelKpiGrid, "总费用", SpendFormat.Amount(ch.Cost, ch.HasUnpriced),
            ch.Requests > 0 ? $"均 {SpendFormat.Amount(ch.Cost / ch.Requests, false)}/次" : null,
            "Money");
        AddCell(ChannelKpiGrid, "总请求", ch.Requests.ToString("N0"), "当前区间", "Requests");
        AddCell(ChannelKpiGrid, "总 TOKEN", SpendFormat.Tokens(ch.TotalTokens),
            $"输入 {SpendFormat.Tokens(ch.Tally.Input)} · 输出 {SpendFormat.Tokens(ch.Tally.Output)}", "Tokens");
        AddCell(ChannelKpiGrid, "缓存命中率",
            ch.CacheHit is { } hit ? $"{hit:P1}" : "—",
            $"命中 {SpendFormat.Tokens(ch.Tally.CacheRead)}", "Cache");
        AddCell(ChannelKpiGrid, "缓存读 / 写",
            $"{SpendFormat.Tokens(ch.Tally.CacheRead)} / {SpendFormat.Tokens(ch.Tally.CacheWrite)}",
            null, "Cache");
        AddCell(ChannelKpiGrid, "会话数", ch.Sessions.ToString("N0"), "去重 sessionId", "Sessions");

        // —— Token 构成六格 ——
        ChannelTokenGrid.Children.Clear();
        var t = ch.Tally;
        AddCell(ChannelTokenGrid, "输入", SpendFormat.Tokens(t.Input), null, "Tokens");
        AddCell(ChannelTokenGrid, "输出", SpendFormat.Tokens(t.Output), null, "Tokens");
        AddCell(ChannelTokenGrid, "缓存读", SpendFormat.Tokens(t.CacheRead),
            ch.CacheHit is { } ch2 ? $"命中率 {ch2:P1}" : null, "Cache");
        AddCell(ChannelTokenGrid, "缓存写", SpendFormat.Tokens(t.CacheWrite), null, "Cache");
        AddCell(ChannelTokenGrid, "未分类", SpendFormat.Tokens(ch.UnclassifiedTokens),
            ch.UnclassifiedTokens > 0 ? "来源只报总量、不参与计价" : "无", "Muted");
        AddCell(ChannelTokenGrid, "合计", SpendFormat.TokensExact(ch.TotalTokens), null, "Tokens");

        RenderHourlyChart(ch);
        RenderDailyChart(ch);
        RenderChannelModels(ch);
        RenderChannelMeta(ch);
    }

    /// <summary>KPI 小格:大数字 + 小标签 + 可选脚注。金额/缓存类用强调色。</summary>
    private void AddCell(Panel host, string label, string value, string? note, string kind)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        System.Windows.Media.Brush accent = kind switch
        {
            "Money" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A)),
            "Tokens" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xFF)),
            "Cache" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xA8, 0x5C)),
            "Requests" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5E, 0x5C, 0xE6)),
            "Sessions" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBF, 0x5A, 0xF2)),
            _ => (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = accent,
        });
        if (!string.IsNullOrEmpty(note))
        {
            panel.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            });
        }
        host.Children.Add(panel);
    }

    /// <summary>今日 24 小时柱状图(自绘)。参考 GoGauge 的"今日趋势 24 小时"。</summary>
    private void RenderHourlyChart(ChannelUsage ch)
    {
        ChannelHourBox.Child = null;
        var canvas = new Canvas { ClipToBounds = true };
        ChannelHourBox.Child = canvas;
        long total = ch.TodayHourly.Sum(h => h.Tokens);
        ChannelTodayHint.Text = total == 0
            ? "今天还没有记录(程序未运行的小时不会有采样)。"
            : $"{SpendFormat.Tokens(total)} tokens · 最忙 {(ch.TodayHourly.OrderByDescending(h => h.Tokens).First().Hour)}:00";
        canvas.SizeChanged += (_, _) => DrawHourly(canvas, ch.TodayHourly);
        DrawHourly(canvas, ch.TodayHourly);
    }

    private void DrawHourly(Canvas canvas, IReadOnlyList<HourBucket> hours)
    {
        canvas.Children.Clear();
        double w = ChannelHourBox.ActualWidth, h = ChannelHourBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double slot = w / 24;
        double barW = Math.Max(3, Math.Min(20, slot * 0.62));
        long max = hours.Count > 0 ? hours.Max(x => x.Tokens) : 0;
        var fillIn = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var fillOut = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xA8, 0x5C));
        var empty = (System.Windows.Media.Brush)FindResource("FieldBackBrush");
        double plotH = h - 16;

        for (int i = 0; i < 24; i++)
        {
            var hr = hours[i];
            double x = i * slot + (slot - barW) / 2;
            if (hr.Tokens <= 0)
            {
                var dot = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = 1.5, Fill = empty, ToolTip = $"{i:00}:00 没有记录",
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 1.5);
                canvas.Children.Add(dot);
                continue;
            }
            // 输入(含缓存)/输出 两段堆叠,便于看构成
            long inputPart = hr.Input + hr.CacheRead + hr.CacheWrite;
            long outputPart = hr.Output;
            double totalH = max <= 0 ? 0 : Math.Max(2, (double)hr.Tokens / max * plotH);
            double outH = hr.Tokens == 0 ? 0 : totalH * outputPart / hr.Tokens;
            double inH = totalH - outH;

            if (inH > 0)
            {
                var inBar = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = inH, Fill = fillIn, RadiusX = 1.5, RadiusY = 1.5,
                    ToolTip = $"{i:00}:00\n{SpendFormat.TokensExact(hr.Tokens)} tokens\n{hr.Requests} 次调用",
                };
                Canvas.SetLeft(inBar, x);
                Canvas.SetTop(inBar, plotH - totalH);
                canvas.Children.Add(inBar);
            }
            if (outH > 0)
            {
                var outBar = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = outH, Fill = fillOut, RadiusX = 1.5, RadiusY = 1.5,
                    ToolTip = $"{i:00}:00\n输出 {SpendFormat.TokensExact(outputPart)} tokens",
                };
                Canvas.SetLeft(outBar, x);
                Canvas.SetTop(outBar, plotH - outH);
                canvas.Children.Add(outBar);
            }
        }

        // 只标 0/6/12/18 四个刻度,免得挤
        foreach (int hh in new[] { 0, 6, 12, 18 })
        {
            var label = new TextBlock
            {
                Text = $"{hh:00}:00", FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = hh * slot + (slot - label.DesiredSize.Width) / 2;
            Canvas.SetLeft(label, Math.Max(0, Math.Min(w - label.DesiredSize.Width, x)));
            Canvas.SetTop(label, h - 13);
            canvas.Children.Add(label);
        }
    }

    /// <summary>每日用量柱状图(自绘)。</summary>
    private void RenderDailyChart(ChannelUsage ch)
    {
        ChannelDailyBox.Child = null;
        var canvas = new Canvas { ClipToBounds = true };
        ChannelDailyBox.Child = canvas;
        canvas.SizeChanged += (_, _) => DrawDaily(canvas, ch.Daily);
        DrawDaily(canvas, ch.Daily);
    }

    private void DrawDaily(Canvas canvas,
        IReadOnlyList<(DateOnly Day, long Tokens, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Requests)> daily)
    {
        canvas.Children.Clear();
        double w = ChannelDailyBox.ActualWidth, h = ChannelDailyBox.ActualHeight;
        if (w <= 0 || h <= 0 || daily.Count == 0) return;

        int n = daily.Count;
        double slot = w / n;
        double barW = Math.Max(2, Math.Min(22, slot * 0.7));
        long max = daily.Max(d => d.Tokens);
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var empty = (System.Windows.Media.Brush)FindResource("FieldBackBrush");
        double plotH = h - 16;

        for (int i = 0; i < n; i++)
        {
            var d = daily[i];
            double x = i * slot + (slot - barW) / 2;
            if (d.Tokens <= 0)
            {
                var dot = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = 1.5, Fill = empty, ToolTip = $"{d.Day:yyyy-MM-dd}:没有记录",
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 1.5);
                canvas.Children.Add(dot);
                continue;
            }
            double bh = max <= 0 ? 0 : Math.Max(2, (double)d.Tokens / max * plotH);
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = barW, Height = bh, Fill = accent, RadiusX = 2, RadiusY = 2,
                ToolTip = $"{d.Day:yyyy-MM-dd}\n{SpendFormat.TokensExact(d.Tokens)} tokens\n"
                    + $"{SpendFormat.MoneyExact(d.Cost)} · {d.Requests} 次调用",
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotH - bh);
            canvas.Children.Add(bar);
        }

        var first = new TextBlock
        {
            Text = daily[0].Day.ToString("MM-dd"), FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        };
        Canvas.SetLeft(first, 0); Canvas.SetTop(first, h - 13);
        canvas.Children.Add(first);
        if (n > 1)
        {
            var last = new TextBlock
            {
                Text = daily[^1].Day.ToString("MM-dd"), FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            };
            last.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(last, Math.Max(0, w - last.DesiredSize.Width));
            Canvas.SetTop(last, h - 13);
            canvas.Children.Add(last);
        }
    }

    /// <summary>该套餐里各模型的用量行(带占比条)。</summary>
    private void RenderChannelModels(ChannelUsage ch)
    {
        ChannelModelPanel.Children.Clear();
        if (ch.Models.Count == 0)
        {
            ChannelModelPanel.Children.Add(new TextBlock
            {
                Text = "这个区间没有模型记录。",
                FontSize = 12, Margin = new Thickness(16, 4, 16, 14),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            });
            return;
        }

        long max = ch.Models.Max(m => m.Tokens);
        bool first = true;
        foreach (var m in ch.Models)
        {
            if (!first)
                ChannelModelPanel.Children.Add(new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("SeparatorBrush"),
                    Height = 1,
                });
            first = false;

            var grid = new Grid { Margin = new Thickness(16, 9, 16, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = m.Model, FontSize = 12.5,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var barRow = new Grid { Margin = new Thickness(0, 4, 0, 3) };
            barRow.Children.Add(new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = (System.Windows.Media.Brush)FindResource("FieldBackBrush"),
                HorizontalAlignment = HorizontalAlignment.Left, Width = 200,
            });
            barRow.Children.Add(new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(5, (max <= 0 ? 0 : (double)m.Tokens / max) * 200),
            });
            left.Children.Add(barRow);
            string cache = m.CacheHit is { } hit ? $" · 缓存命中 {hit:P0}" : "";
            left.Children.Add(new TextBlock
            {
                Text = $"{SpendFormat.Tokens(m.Tokens)} tokens · {m.Requests:N0} 次{cache}",
                FontSize = 10.5,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            });
            grid.Children.Add(left);

            var amount = new TextBlock
            {
                Text = SpendFormat.Amount(m.Cost, m.HasUnpriced),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            };
            Grid.SetColumn(amount, 1);
            grid.Children.Add(amount);

            ChannelModelPanel.Children.Add(grid);
        }
    }

    /// <summary>归属信息:实际用到的 provider id 与来源库,便于对账。</summary>
    private void RenderChannelMeta(ChannelUsage ch)
    {
        string ids = ch.ProviderIds.Count > 0 ? string.Join("、", ch.ProviderIds) : "(未标注)";
        string sourceKey = ch.SourceKey is { } sk ? $" · 对应数据源 {sk}" : "";
        ChannelMetaLine.Text = $"来源客户端 {ch.Agent}{sourceKey}\n"
            + $"provider id:{ids}\n"
            + $"统计口径与「用量统计」窗口一致:四类 token 之和对不上来源总量时差额记未分类(计入总数、不计价);"
            + $"金额按 models.dev 公开牌价估算。";
    }

    // ————————————————— 行为/外观的批 1 设置 —————————————————

    // ————————————————— 行为/外观的批 1 设置 —————————————————

    private void RingSize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.RingSize = SizeSmall.IsChecked == true ? 0 : SizeLarge.IsChecked == true ? 2 : 1;
        SettingsChanged?.Invoke();
        SaveSoon();
    }

    private void RingSpacing_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.RingSpacing = SpacingTight.IsChecked == true ? 0 : SpacingLoose.IsChecked == true ? 2 : 1;
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

    private static readonly string[] OrderKeys =
    {
        "goat,opencode,deepseek", "goat,deepseek,opencode",
        "opencode,goat,deepseek", "opencode,deepseek,goat",
        "deepseek,goat,opencode", "deepseek,opencode,goat",
    };

    private void OrderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (OrderCombo.SelectedIndex is { } i && (uint)i < OrderKeys.Length)
        {
            AppSettings.Current.SourceOrder = OrderKeys[i];
            SettingsChanged?.Invoke();   // 主窗按新顺序重载
            SaveSoon();
        }
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
        // 模型页占着共享账本的引用:关窗必须还,否则那份账本(含两个库聚合成千上万个
        // 对象)会一直挂着,关掉设置窗内存也不回落。
        ReleaseModelsLedger();
        base.OnClosed(e);
    }
}
