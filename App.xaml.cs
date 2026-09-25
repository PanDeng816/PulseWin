using System.Diagnostics;
using System.IO;
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
    private SpendWindow? _spend;
    private SingleInstance? _instance;
    private readonly TrayIconMeter _trayMeter = new();

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

        // 验证用:把用量统计的原始数字打到 Data\spend-dump.txt 后退出(不开界面)。
        if (e.Args.Any(a => string.Equals(a, "--spend", StringComparison.OrdinalIgnoreCase)))
        {
            RunSpendDump();
            Shutdown();
            return;
        }

        // 诊断用:强制更新一次模型牌价表,结果打到 Data\price-update.txt 后退出(不开界面)。
        if (e.Args.Any(a => string.Equals(a, "--update-prices", StringComparison.OrdinalIgnoreCase)))
        {
            RunPriceUpdate();
            Shutdown();
            return;
        }

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
        _main.RailMenuRequested += ShowRailMenu;

        _engine = new UsageEngine();
        _engine.SnapshotsChanged += () =>
            Dispatcher.BeginInvoke(() =>
            {
                _main?.ReloadData(fromEngine: true);
                UpdateTrayMeter();
            });
        _engine.Start();

        // 点击圆环 = 立刻同步一次真实 API(不只是重读本地快照)
        _main.RefreshRequested += () => _engine?.RequestRefreshNow();

        UpdateChecker.Start();

        _main.Show();
        SetupTray();
        if (openSettings) OpenSettings();
        if (e.Args.Any(a => string.Equals(a, "--spend-window", StringComparison.OrdinalIgnoreCase)))
            OpenSpend();
    }

    /// <summary>
    /// 把用量统计的原始数字写进 Data\spend-dump.txt 后退出。不开界面——
    /// 口径对不对要在数字上核对(与数据库直接算出来的对照),不是在截图上看。
    /// </summary>
    private static void RunSpendDump()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            var prices = ModelPrices.Current;
            var ledger = SpendLedger.Build(prices);
            text.AppendLine($"价目表来源: {prices.Source}   模型 {prices.ModelCount} 条   抓取 {prices.FetchedAt:yyyy-MM-dd HH:mm}");
            text.AppendLine($"来源存在: [{string.Join(", ", ledger.PresentStores)}]   缺失: [{string.Join(", ", ledger.MissingStores)}]");
            foreach (var note in ledger.Notes) text.AppendLine($"注意: {note}");
            text.AppendLine($"记录 {ledger.Entries.Count} 条,最早 {ledger.Earliest:yyyy-MM-dd HH:mm},最新 {ledger.Latest:yyyy-MM-dd HH:mm}");
            text.AppendLine();

            foreach (var span in new[] { SpendSpan.Today, SpendSpan.Week, SpendSpan.Month, SpendSpan.All })
            {
                var s = SpendSummary.Build(ledger, span);
                text.AppendLine($"===== {span}  ({s.From:yyyy-MM-dd} ~ {s.To.AddDays(-1):yyyy-MM-dd}) =====");
                text.AppendLine($"  总量 {SpendFormat.TokensExact(s.TotalTokens)} tokens   估算 {SpendFormat.Amount(s.TotalCost, s.CostIsPartial)}   请求 {s.Requests}   会话 {s.Sessions}   项目 {s.Projects}");
                text.AppendLine($"  四类: input={s.Tally.Input:N0} cacheWrite={s.Tally.CacheWrite:N0} cacheRead={s.Tally.CacheRead:N0} output={s.Tally.Output:N0}  未分类={s.UnclassifiedTokens:N0}");
                text.AppendLine($"  金额四类: {s.Cost.Input:F4} / {s.Cost.CacheWrite:F4} / {s.Cost.CacheRead:F4} / {s.Cost.Output:F4}");
                text.AppendLine($"  无公开价: token={s.UnpricedTokens:N0}  模型={s.UnpricedModels}  部分计价={s.CostIsPartial}");
                if (span == SpendSpan.All)
                {
                    text.AppendLine("  小时分布: " + string.Join(" ", s.HourlyTokens.Select((v, i) => $"{i}:{v / 1000}k")));
                }
                text.AppendLine("  --- 模型 ---");
                foreach (var m in s.Models.Take(15))
                {
                    text.AppendLine($"    {m.Model,-44} {SpendFormat.TokensExact(m.Tokens),15}  {SpendFormat.Amount(m.Amount ?? 0, m.UnpricedTokens > 0),10}  [{(m.Priced ? m.VendorName ?? "已归档" : "无公开价")}]  x{m.Requests}");
                }
                text.AppendLine("  --- 来源 ---");
                foreach (var a in s.Agents)
                    text.AppendLine($"    {a.Agent,-10} {SpendFormat.TokensExact(a.Tokens),15}  {SpendFormat.Amount(a.Cost, a.HasUnpriced),10}  请求 {a.Requests}");
                text.AppendLine("  --- 项目 ---");
                foreach (var p in s.ProjectRows.Take(8))
                    text.AppendLine($"    {p.Project,-40} {SpendFormat.TokensExact(p.Tokens),15}  {SpendFormat.Amount(p.Cost, p.HasUnpriced),10}  会话 {p.Sessions}");
                text.AppendLine();
            }
        }
        catch (Exception ex)
        {
            text.AppendLine("统计失败: " + ex);
        }

        try
        {
            string path = Path.Combine(SnapshotSource.DataDirectory, "spend-dump.txt");
            File.WriteAllText(path, text.ToString());
            Diagnostics.Note($"用量统计已输出到 {path}");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("写出用量统计失败", ex);
        }
    }

    /// <summary>
    /// 强制更新一次模型牌价表并等它做完,结果写进 Data\price-update.txt(诊断入口,
    /// 与 --spend 同类)。走的是统计窗口完全相同的更新路径,只是这里同步等结果。
    /// </summary>
    private static void RunPriceUpdate()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            var before = ModelPrices.Current;
            text.AppendLine($"更新前: {before.Source} / {before.ModelCount} 条 / 抓取 {before.FetchedAt:yyyy-MM-dd}");

            using var done = new ManualResetEventSlim(false);
            ModelPricesUpdater.ForceRefresh(() => done.Set());
            if (!done.Wait(TimeSpan.FromMinutes(3)))
                text.AppendLine("警告: 等待超时(下载可能仍在后台进行)");

            var after = ModelPrices.Current;
            text.AppendLine($"更新后: {after.Source} / {after.ModelCount} 条 / 抓取 {after.FetchedAt:yyyy-MM-dd}");
            text.AppendLine($"结果: {ModelPricesUpdater.LastResult ?? "(未发起)"}");
            text.AppendLine($"分组厂商: {string.Join(", ", after.VendorNames)}");
        }
        catch (Exception ex)
        {
            text.AppendLine("失败: " + ex);
        }

        try
        {
            string path = Path.Combine(SnapshotSource.DataDirectory, "price-update.txt");
            File.WriteAllText(path, text.ToString());
            Diagnostics.Note($"价目更新结果已输出到 {path}");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("写价目更新结果失败", ex);
        }
    }

    /// <summary>
    /// rail 上的右键菜单(几何与拖动一致:能拖的地方就能右键,细条状态也一样)。
    ///
    /// **每次点击现构建**,所以"刚发现的新版本"就在菜单里——不用等下一次刷新菜单。
    /// 这也顺带解释了这个菜单为什么存在:托盘的图标会被系统折叠进溢出区,
    /// 而 rail 就在眼前(上游 issue #24 就是嫌设置非得点菜单栏才能开)。
    /// </summary>
    private void ShowRailMenu(Point anchorDiu)
    {
        if (_main is null) return;
        Diagnostics.Note($"rail 右键菜单(锚点 {anchorDiu.X:0},{anchorDiu.Y:0})");
        _main.SetMenuOpen(true);
        try
        {
            FlyoutMenu.Show(_main, anchorDiu, MenuEntries(includeStartup: false),
                closed: () => _main.SetMenuOpen(false));
        }
        catch (Exception ex)
        {
            Diagnostics.Note("弹出 rail 菜单失败", ex);
            _main.SetMenuOpen(false);
        }
    }

    /// <summary>
    /// 托盘右键菜单。不再用 NotifyIcon.ContextMenuStrip(样式与 rail 菜单不一致),
    /// 改为右键抬起时在光标处弹同一个 FlyoutMenu。
    /// </summary>
    private void TrayMenuRequested()
    {
        if (_main is null) return;
        Diagnostics.Note("托盘右键菜单");
        var cursor = Native.CursorPosition() ?? new Point(0, 0);
        double scale = Native.Scale(_main);
        FlyoutMenu.Show(_main, new Point(cursor.X / scale, cursor.Y / scale),
            MenuEntries(includeStartup: true));
    }

    /// <summary>
    /// 两个菜单共用一份条目。托盘多一项"开机自动启动";更新条目只在真有新版本时出现。
    /// </summary>
    private List<object> MenuEntries(bool includeStartup)
    {
        var entries = new List<object>
        {
            new FlyoutMenu.Item(
                _main!.IsRailVisible ? "隐藏浮窗" : "显示浮窗", () => _main.ToggleRailVisible()),
            new FlyoutMenu.Item("立即刷新", () => _engine?.RequestRefreshNow()),
            new FlyoutMenu.Gap(),
            new FlyoutMenu.Item("用量统计…", OpenSpend),
            new FlyoutMenu.Item("设置…", OpenSettings),
        };

        if (includeStartup)
        {
            entries.Add(new FlyoutMenu.Item("开机自动启动",
                () => SetStartupEnabled(!IsStartupEnabled()), IsStartupEnabled()));
        }

        if (UpdateChecker.Available is { } release)
        {
            entries.Add(new FlyoutMenu.Gap());
            entries.Add(new FlyoutMenu.Item($"有可用更新 {release.Tag}", () => OpenReleasePage(release.Url)));
        }

        entries.Add(new FlyoutMenu.Gap());
        entries.Add(new FlyoutMenu.Item("退出 Pulse", ExitApp));
        return entries;
    }

    private static void OpenReleasePage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diagnostics.Note("打开下载页失败", ex);
        }
    }

    private void SetupTray()    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = stream is { } s ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Pulse — AI 额度浮窗(双击显示/隐藏)",
            Visible = true,
        };

        // 右键菜单不走 NotifyIcon.ContextMenuStrip(那是 WinForms 系统样式),
        // 在 MouseUp 里手动弹 FlyoutMenu,和 rail 菜单共用一份条目与样式。
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right) TrayMenuRequested();
        };
        _tray.DoubleClick += (_, _) => _main?.ToggleRailVisible();

        // 托盘图标本身就是仪表:最紧张的源画成小环。快照到达时更新,
        // 启动时也立即试一次(引擎还没同步完时快照文件已有上一轮的数据)。
        UpdateTrayMeter();
    }

    /// <summary>
    /// 把托盘图标换成最紧张源的小环。任何失败都退回品牌图标——仪表只是锦上添花,
    /// 不能因为它托盘没图标。
    /// </summary>
    private void UpdateTrayMeter()
    {
        if (_tray is null || _icon is null) return;
        try
        {
            var subs = SnapshotSource.LoadAll();
            var (icon, tooltip) = _trayMeter.Build(subs, _icon);
            _tray.Icon = icon ?? _icon;
            if (tooltip.Length > 63) tooltip = tooltip[..63];
            _tray.Text = tooltip;
        }
        catch (Exception ex)
        {
            _tray.Icon = _icon;
            Diagnostics.Note("托盘仪表更新失败,回退品牌图标", ex);
        }
    }

    /// <summary>用量统计窗口(单例:再点就把它亮出来)。</summary>
    private void OpenSpend()
    {
        if (_spend is { IsLoaded: true })
        {
            _spend.Activate();
            return;
        }
        try
        {
            _spend = new SpendWindow();
            _spend.Show();
        }
        catch (Exception ex)
        {
            Diagnostics.Note("打开用量统计窗口失败", ex);
            System.Windows.MessageBox.Show(
                $"无法打开用量统计:\n\n{ex.Message}",
                "Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSettings()    {
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
        UpdateChecker.Stop();
        _tray?.Dispose();
        _trayMeter.Dispose();
        _icon?.Dispose();
        _settings?.Close();
        _spend?.Close();
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
