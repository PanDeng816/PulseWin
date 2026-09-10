using System.Threading;
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
    private SingleInstance? _instance;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulseWin";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 兜底:任何未处理异常都记进 diagnostics 并提示,而不是弹原始 .NET 崩溃框。
        DispatcherUnhandledException += (_, args) =>
        {
            Diagnostics.Note("未处理异常", args.Exception);
            System.Windows.MessageBox.Show(
                $"Pulse 遇到一个错误,已记录到 Data\\diagnostics.json:\n\n{args.Exception.Message}",
                "Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        bool openSettings = e.Args.Any(a =>
            string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        // 只允许一个实例:两个 rail 会让 API 请求翻倍并互相争抢快照文件。
        _instance = SingleInstance.Acquire();
        if (!_instance.IsOwner)
        {
            _instance.SignalExistingInstance(openSettings);
            Shutdown();
            return;
        }
        _instance.StartListening(
            onShowRail: () => Dispatcher.BeginInvoke(() => _main?.ShowRail()),
            onShowSettings: () => Dispatcher.BeginInvoke(OpenSettings));

        _main = new MainWindow();

        _engine = new UsageEngine();
        _engine.SnapshotsChanged += () =>
            Dispatcher.BeginInvoke(() => _main?.ReloadData(fromEngine: true));
        _engine.Start();

        // 点击圆环 = 立刻同步一次真实 API(不只是重读本地快照)
        _main.RefreshRequested += () => _engine?.RequestRefreshNow();

        _main.Show();
        SetupTray();
        if (openSettings) OpenSettings();
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
        try
        {
            _settings = new SettingsWindow(_engine);
            _settings.SettingsChanged += () => _main?.ApplySettings();
            _settings.Show();
        }
        catch (Exception ex)
        {
            Diagnostics.Note("打开设置窗口失败", ex);
            System.Windows.MessageBox.Show(
                $"无法打开设置窗口:\n\n{ex.Message}",
                "Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        _instance?.Dispose();
        Shutdown();
    }
}

/// <summary>
/// 单实例守卫:第二个实例不启动 rail,而是给已有实例发个信号让它把界面亮出来,
/// 然后自己退出。避免重复的 API 请求和并发写快照。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PulseWin.SingleInstance";
    private const string SignalName = @"Local\PulseWin.ShowRail";

    private readonly Mutex _mutex;
    private EventWaitHandle? _signal;
    private CancellationTokenSource? _listenCts;
    private Thread? _listener;

    public bool IsOwner { get; }

    private SingleInstance(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>已有实例存在时调用:通知它显示界面(或直接打开设置)。</summary>
    public void SignalExistingInstance(bool openSettings)
    {
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            if (openSettings)
            {
                var openName = SignalName + ".Settings";
                using var open = new EventWaitHandle(false, EventResetMode.AutoReset, openName);
                open.Set();
            }
            else
            {
                signal.Set();
            }
        }
        catch (Exception)
        {
            // 通知失败时静默退出即可
        }
    }

    /// <summary>主实例:后台等第二个实例的信号。</summary>
    public void StartListening(Action onShowRail, Action onShowSettings)
    {
        if (!IsOwner) return;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName + ".Settings");

        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _signal, settingsSignal };
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int idx = WaitHandle.WaitAny(handles, 500);
                    if (idx == 0) onShowRail();
                    else if (idx == 1) onShowSettings();
                }
                catch (ObjectDisposedException) { return; }
            }
        })
        { IsBackground = true, Name = "PulseWin.SingleInstanceListener" };
        _listener.Start();
    }

    public void Dispose()
    {
        _listenCts?.Cancel();
        _signal?.Dispose();
        _listenCts?.Dispose();
        if (IsOwner)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
