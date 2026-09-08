using System.Windows;
using System.Windows.Threading;

namespace PulseWin;

public partial class SettingsWindow : Window
{
    private readonly UsageEngine _engine;
    private readonly DispatcherTimer _statusTimer;

    public SettingsWindow(UsageEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
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
        base.OnClosed(e);
    }
}
