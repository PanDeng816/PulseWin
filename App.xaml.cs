using System.Windows;
using Microsoft.Win32;

namespace PulseWin;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private MainWindow? _main;
    private UsageEngine? _engine;
    private SettingsWindow? _settings;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulseWin";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _main = new MainWindow();

        _engine = new UsageEngine();
        _engine.SnapshotsChanged += () =>
            Dispatcher.BeginInvoke(() => _main?.ReloadData());
        _engine.Start();

        _main.Show();
        SetupTray();
    }

    private void SetupTray()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = stream is { } s ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Pulse — AI 额度浮窗(双击显示/隐藏)",
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var toggle = new System.Windows.Forms.ToolStripMenuItem("显示 / 隐藏浮窗");
        toggle.Click += (_, _) => _main?.ToggleRailVisible();
        var refresh = new System.Windows.Forms.ToolStripMenuItem("立即刷新");
        refresh.Click += (_, _) => _engine?.RequestRefreshNow();
        var settings = new System.Windows.Forms.ToolStripMenuItem("设置…");
        settings.Click += (_, _) => OpenSettings();
        var startup = new System.Windows.Forms.ToolStripMenuItem("开机自动启动");
        startup.CheckOnClick = true;
        startup.Checked = IsStartupEnabled();
        startup.CheckedChanged += (_, _) => SetStartupEnabled(startup.Checked);
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出 Pulse");
        exit.Click += (_, _) => ExitApp();
        menu.Items.AddRange([toggle, refresh, settings,
            new System.Windows.Forms.ToolStripSeparator(), startup,
            new System.Windows.Forms.ToolStripSeparator(), exit]);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _main?.ToggleRailVisible();
    }

    private void OpenSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        if (_engine is null) return;
        _settings = new SettingsWindow(_engine);
        _settings.Show();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string path && path.Contains("PulseWin");
    }

    private static void SetStartupEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写时静默跳过
        }
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _icon?.Dispose();
        _settings?.Close();
        _engine?.Dispose();
        _main?.RequestExit();
        Shutdown();
    }
}
