using System.Windows;
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

    public SettingsWindow(UsageEngine engine)
    {
        InitializeComponent();
        _engine = engine;
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
        _loading = false;
        UpdateLabels();
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
        _saveTimer.Stop();
        _saveTimer.Start();
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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