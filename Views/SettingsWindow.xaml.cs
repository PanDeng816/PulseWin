using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
        ShowGoatBox.IsChecked = s.ShowGoat;
        ShowGoBox.IsChecked = s.ShowOpenCode;
        ShowDeepSeekBox.IsChecked = s.ShowDeepSeek;
        ShowPercentBox.IsChecked = s.ShowPercent;
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
            _ when sender == NavSources => ("数据源", PageSources),
            _ when sender == NavRefresh => ("刷新", PageRefresh),
            _ => ("外观", PageAppearance),
        };
        PageTitle.Text = title;
        PageAppearance.Visibility = page == PageAppearance ? Visibility.Visible : Visibility.Collapsed;
        PageRings.Visibility = page == PageRings ? Visibility.Visible : Visibility.Collapsed;
        PageSources.Visibility = page == PageSources ? Visibility.Visible : Visibility.Collapsed;
        PageRefresh.Visibility = page == PageRefresh ? Visibility.Visible : Visibility.Collapsed;
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
