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
            _ when sender == NavModels => ("模型", PageModels),
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

    // ————————————————— 模型页 —————————————————

    private SpendSpan _modelSpan = SpendSpan.Week;
    private List<ModelUsage> _modelRows = [];
    private string? _selectedModel;
    private bool _modelsBusy;
    private bool _modelsLoaded;

    /// <summary>
    /// 本窗口此刻**是否还需要**账本。用于堵住一个竞态：账本构建是异步的，
    /// 若用户在构建完成前就离开模型页/关窗，同步的 <see cref="ReleaseModelsLedger"/>
    /// 会因为"还没加载完"什么都不做，而构建完成后引用就永久挂住了。
    /// 所以离开时置 false，构建完成后发现已不需要就自己还掉。
    /// </summary>
    private bool _wantLedger;

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
        _selectedModel = null;
        ModelDetailPanel.Visibility = Visibility.Collapsed;
        RefreshModelRows();
    }

    /// <summary>开关"浏览器会话明细":存盘并重取一次(开了就立即去读 cookie)。</summary>
    private void BrowserSession_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.UseBrowserSessionForModelDetail = BrowserSessionBox.IsChecked == true;
        AppSettings.Current.Save();
        _ = RefreshWebSessionNoteAsync();
    }

    /// <summary>打开模型页:取共享账本(引用计数 +1),按当前区间渲染列表。</summary>
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
            RefreshModelRows();
            _ = RefreshWebSessionNoteAsync();
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = "读取失败:" + ex.Message;
            Diagnostics.Note("模型页读取账本失败", ex);
        }
        finally
        {
            _modelsBusy = false;
        }
    }

    private SpendLedger? _modelsLedger;

    /// <summary>离开模型页:放掉账本引用(计数归零时内存立刻归还)。</summary>
    private void ReleaseModelsLedger()
    {
        _wantLedger = false;
        if (!_modelsLoaded) return;
        _modelsLoaded = false;
        _modelsLedger = null;
        _modelRows = [];
        ModelListPanel.Children.Clear();
        ModelDetailPanel.Visibility = Visibility.Collapsed;
        SpendLedgerCache.Release();
    }

    private void RefreshModelRows()
    {
        if (_modelsLedger is not { } ledger) return;
        _modelRows = ModelUsageIndex.Build(ledger, _modelSpan);
        RenderModelList();

        var (from, _) = SpendSummary.Range(_modelSpan, DateTime.Now);
        string range = _modelSpan == SpendSpan.All
            ? $"自 {(ledger.Earliest ?? DateTime.Now):yyyy-MM-dd} 起"
            : $"{from:yyyy-MM-dd} 起";
        long total = _modelRows.Sum(m => m.TotalTokens);
        double cost = _modelRows.Sum(m => m.Cost);
        bool partial = _modelRows.Any(m => m.HasUnpriced);
        ModelsStatus.Text = _modelRows.Count == 0
            ? $"{range}:这个区间没有记录。"
            : $"{range}:{_modelRows.Count} 个模型 · {SpendFormat.TokensExact(total)} tokens · 估算 {SpendFormat.Amount(cost, partial)}";
    }

    /// <summary>逐模型明细是可选通道(要浏览器登录态),异步补一句说明,不阻塞列表。</summary>
    private async Task RefreshWebSessionNoteAsync()
    {
        var buckets = await CommandCodeWebSession.TryFetchModelCacheAsync(
            DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now);
        if (buckets is { Count: > 0 })
        {
            ModelsIntro.Text = "本机用过的 AI 模型。数据来自各客户端记录库(ZCode / OpenCode),"
                + $"并已从浏览器会话读到 {buckets.Count} 条 Command Code 逐模型明细。"
                + "金额按 models.dev 公开 API 价估算——不是账单。点一行看详情。";
        }
        else if (CommandCodeWebSession.LastStatus is { Length: > 0 } note)
        {
            ModelsIntro.Text = "本机用过的 AI 模型。数据来自各客户端记录库(ZCode / OpenCode),"
                + "按 models.dev 公开 API 价估算金额——不是账单。"
                + $"(Command Code 逐模型明细:{note}。)"
                + "点一行看详情。";
        }
    }

    private void RenderModelList()
    {
        ModelListPanel.Children.Clear();
        if (_modelRows.Count == 0) return;

        long max = _modelRows.Max(m => m.TotalTokens);
        bool first = true;
        foreach (var m in _modelRows)
        {
            if (!first)
                ModelListPanel.Children.Add(new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("SeparatorBrush"),
                    Height = 1,
                });
            first = false;

            var row = BuildModelRow(m, max);
            ModelListPanel.Children.Add(row);
        }
    }

    private FrameworkElement BuildModelRow(ModelUsage m, long max)
    {
        var grid = new Grid { Margin = new Thickness(16, 10, 16, 10), Background = System.Windows.Media.Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = m.Model,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // 进度条:相对最大用量
        double frac = max <= 0 ? 0 : (double)m.TotalTokens / max;
        var bar = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = (System.Windows.Media.Brush)FindResource("FieldBackBrush"),
            Margin = new Thickness(0, 6, 0, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var barGrid = new Grid();
        barGrid.Children.Add(bar);
        var fill = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        fill.Width = Math.Max(6, frac * 220);
        barGrid.Children.Add(fill);
        barGrid.HorizontalAlignment = HorizontalAlignment.Left;
        left.Children.Add(barGrid);

        string cache = m.CacheHit is { } hit ? $" · 缓存命中 {hit:P0}" : "";
        left.Children.Add(new TextBlock
        {
            Text = $"{SpendFormat.Tokens(m.TotalTokens)} tokens · {m.Requests:N0} 次调用{cache}",
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        });
        grid.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.Add(new TextBlock
        {
            Text = SpendFormat.Amount(m.Cost, m.HasUnpriced),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
        });
        var vendor = new TextBlock
        {
            Text = m.FullyUnpriced ? "无公开价" : (m.VendorName ?? "已归档"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        };
        right.Children.Add(vendor);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var host = new Border { Child = grid, Cursor = System.Windows.Input.Cursors.Hand };
        var hover = new Style(typeof(Border));
        hover.Setters.Add(new Setter(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        hover.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(Border.BackgroundProperty, (System.Windows.Media.Brush)FindResource("NavHoverBrush")) },
        });
        host.Style = hover;
        host.MouseLeftButtonUp += (_, _) => ShowModelDetail(m.Model);
        return host;
    }

    private void ShowModelDetail(string model)
    {
        var m = _modelRows.FirstOrDefault(x => x.Model == model);
        if (m is null) return;
        _selectedModel = model;

        ModelDetailPanel.Visibility = Visibility.Visible;
        ModelDetailTitle.Text = m.Model + " · 详情";

        // KPI 四宫格
        ModelKpiGrid.Children.Clear();
        AddKpi("总 token", SpendFormat.Tokens(m.TotalTokens));
        AddKpi("估算金额", SpendFormat.Amount(m.Cost, m.HasUnpriced));
        AddKpi("调用次数", m.Requests.ToString("N0"));
        AddKpi("会话数", m.Sessions.ToString("N0"));

        // token 构成
        var t = m.Tally;
        ModelTokenLine.Text = $"输入 {SpendFormat.Tokens(t.Input)} · 缓存写 {SpendFormat.Tokens(t.CacheWrite)}"
            + $" · 缓存读 {SpendFormat.Tokens(t.CacheRead)} · 输出 {SpendFormat.Tokens(t.Output)}"
            + (m.UnclassifiedTokens > 0 ? $" · 未分类 {SpendFormat.Tokens(m.UnclassifiedTokens)}" : "")
            + (m.CacheHit is { } hit ? $" · 缓存命中 {hit:P1}" : "");

        RenderModelTrend(m);
        RenderModelProjects(m);

        // 牌价
        if (m.Price is { } p)
        {
            string cacheRead = p.CacheRead is { } cr ? $"${cr:0.###}/M" : "按输入价";
            ModelPriceLine.Text = $"输入 ${p.Input:0.###}/M · 输出 ${p.Output:0.###}/M · 缓存读 {cacheRead}"
                + $" (来源:{p.Vendor})";
        }
        else if (m.IsPriced)
        {
            // 金额算得出、但单价没留档:记录来自本地用量仓库的天级聚合行,
            // 金额是建仓当天按牌价算好的,单价本身没存。说清楚,不要误导成"没有价"。
            ModelPriceLine.Text = "单价未留档:这里的历史记录来自本地用量仓库的汇总行,"
                + "金额是入库当天按当时牌价算好的,单价本身没有保存。";
        }
        else
        {
            ModelPriceLine.Text = "这个模型没有公开牌价:只统计 token,金额算不出来。";
        }

        // 用到的来源与时间范围
        string agents = m.Agents.Count > 0 ? string.Join("、", m.Agents) : "—";
        string firstLast = m.FirstUsed is { } f && m.LastUsed is { } l
            ? $" · {f:MM-dd HH:mm} ~ {l:MM-dd HH:mm}" : "";
        ModelDetailTitle.Text = $"{m.Model} · 详情({agents}{firstLast})";
    }

    private void AddKpi(string label, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        });
        ModelKpiGrid.Children.Add(panel);
    }

    /// <summary>该模型的逐日用量柱状图(自绘,不引控件库)。</summary>
    private void RenderModelTrend(ModelUsage m)
    {
        ModelTrendBox.Child = null;
        if (m.Daily.Count == 0) return;

        var canvas = new Canvas { ClipToBounds = true };
        long max = m.Daily.Max(d => d.Tokens);
        double gap = 2;
        // 宽度在渲染后才知道,用一个代理在尺寸就绪时再画
        ModelTrendBox.SizeChanged += (_, _) => DrawTrend(canvas, m, max, gap);
        ModelTrendBox.Child = canvas;
        DrawTrend(canvas, m, max, gap);
    }

    private void DrawTrend(Canvas canvas, ModelUsage m, long max, double gap)
    {
        canvas.Children.Clear();
        double w = ModelTrendBox.ActualWidth;
        double h = ModelTrendBox.ActualHeight;
        if (w <= 0 || h <= 0 || m.Daily.Count == 0) return;

        int n = m.Daily.Count;
        double slot = w / n;
        double barW = Math.Max(1.5, Math.Min(22, slot * 0.7));
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var empty = (System.Windows.Media.Brush)FindResource("FieldBackBrush");

        for (int i = 0; i < n; i++)
        {
            var (day, tokens, _) = m.Daily[i];
            double x = i * slot + (slot - barW) / 2;
            if (tokens <= 0)
            {
                var dot = new System.Windows.Shapes.Rectangle
                {
                    Width = barW, Height = 1.5, Fill = empty,
                    ToolTip = $"{day:yyyy-MM-dd}:没有记录",
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, h - 1.5);
                canvas.Children.Add(dot);
                continue;
            }
            double bh = max <= 0 ? 0 : Math.Max(2, (double)tokens / max * (h - 16));
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = barW, Height = bh, Fill = accent,
                RadiusX = 2, RadiusY = 2,
                ToolTip = $"{day:yyyy-MM-dd}\n{SpendFormat.TokensExact(tokens)} tokens",
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, h - 16 - bh);
            canvas.Children.Add(bar);
        }

        var first = new TextBlock
        {
            Text = m.Daily[0].Day.ToString("MM-dd"), FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
        };
        Canvas.SetLeft(first, 0); Canvas.SetTop(first, h - 13);
        canvas.Children.Add(first);
        if (n > 1)
        {
            var last = new TextBlock
            {
                Text = m.Daily[^1].Day.ToString("MM-dd"), FontSize = 10,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            };
            last.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(last, Math.Max(0, w - last.DesiredSize.Width));
            Canvas.SetTop(last, h - 13);
            canvas.Children.Add(last);
        }
    }

    private void RenderModelProjects(ModelUsage m)
    {
        ModelProjectsPanel.Children.Clear();
        if (m.TopProjects.Count == 0)
        {
            ModelProjectsPanel.Children.Add(new TextBlock
            {
                Text = "没有可归属的项目(记录来自已归档的汇总行)。",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            });
            return;
        }
        long max = m.TopProjects.Max(p => p.Tokens);
        foreach (var p in m.TopProjects)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(p.Project.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : p.Project,
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = p.Project,
            });
            var right = new TextBlock
            {
                Text = SpendFormat.Tokens(p.Tokens),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSubBrush"),
            };
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            ModelProjectsPanel.Children.Add(row);
        }
    }

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
