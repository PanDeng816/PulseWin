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

    /// <summary>已经提示过的未处理异常(去重,防止每帧抛的异常连环弹框)。</summary>
    private readonly HashSet<string> _reportedExceptions = new(StringComparer.Ordinal);

    /// <summary>
    /// 无界面/诊断运行时(<c>--spend</c>/<c>--ui-shot</c>/…)。此时**绝不弹模态框**:
    /// 没有人在看一个正在出图的进程,弹框只会卡在那儿等一个不会来的点击。
    /// </summary>
    private bool _headless;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PulseWin";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _headless = e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal));

        // 兜底:任何未处理异常都记进 diagnostics 并提示,而不是弹原始 .NET 崩溃框。
        //
        // **同一个异常只提示一次**。这个兜底是给"偶尔出的意外"用的;但若异常发生在
        // 每帧的 Tick 里(如 P/Invoke 找不到入口点),每次重绘都会抛一次,而模态框会
        // 开一个嵌套消息循环、放行下一个 tick —— 于是弹框一个叠一个,程序看起来"卡死"。
        // 现在按异常类型+消息去重:第一次提示并记日志,后续只记日志不再打扰。
        DispatcherUnhandledException += (_, args) =>
        {
            Diagnostics.Note("未处理异常", args.Exception);
            args.Handled = true;

            // 退出/诊断期间的异常**不打扰用户**:
            //  · 诊断模式(自动出图、dump)没有人在看;
            //  · "应用程序对象正在关闭"是收尾期必然出现的收尾噪音——此时后台回调
            //    (牌价表更新、单实例信号)可能还挂在 Dispatcher 队列上,Shutdown 之后
            //    它们执行就会抛这一条。它不代表任何功能坏了,弹框纯粹是打扰
            //    (本机实测:跑一次 --ui-shot 就在桌面上留一个模态框等点击)。
            if (_headless) return;
            if (args.Exception is InvalidOperationException
                && args.Exception.Message.Contains("正在关闭", StringComparison.Ordinal))
                return;

            string key = args.Exception.GetType().Name + "|" + args.Exception.Message;
            if (_reportedExceptions.Add(key))
            {
                System.Windows.MessageBox.Show(
                    $"Pulse 遇到一个错误,已记录到 Data\\diagnostics.json:\n\n{args.Exception.Message}"
                    + "\n\n(同类错误之后不再重复提示。)",
                    "Pulse", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

        // 诊断用:读 ZCode 的活动日志,把判定打到 Data\activity.txt 后退出(不开界面)。
        // 可以跟一个日志目录参数,用来对合成日志做验证:--activity D:\some\dir
        int activityIndex = Array.FindIndex(e.Args,
            a => string.Equals(a, "--activity", StringComparison.OrdinalIgnoreCase));
        if (activityIndex >= 0)
        {
            string? directory = activityIndex + 1 < e.Args.Length
                && !e.Args[activityIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? e.Args[activityIndex + 1]
                : null;
            RunActivityDump(directory);
            Shutdown();
            return;
        }

        // 诊断用:把"模型"页的每模型聚合与 Command Code 浏览器会话通道的状态打到
        // Data\models-dump.txt 后退出(不开界面)。口径对不对要在数字上核对,不是截图。
        if (e.Args.Any(a => string.Equals(a, "--models", StringComparison.OrdinalIgnoreCase)))
        {
            RunModelsDump();
            Shutdown();
            return;
        }

        // 诊断用:把 DSH 的会话用量解析结果、文件级缓存命中情况,以及**与 DSH 自己的
        // 会话投影逐会话对账**打到 Data\dsh-dump.txt 后退出(不开界面)。
        // 口径对不对必须与"另一条独立路径算出来的数"比,而不是看界面能不能显示。
        if (e.Args.Any(a => string.Equals(a, "--dsh", StringComparison.OrdinalIgnoreCase)))
        {
            RunDshDump();
            Shutdown();
            return;
        }

        // 诊断用:把详情卡用合成数据离屏渲染成 PNG 后退出(不开界面、不联网)。
        // 卡片排版(文字会不会被右缘切、底部会不会溢出)必须在像素上核对——
        // 靠真人 hover 复现既慢又不可靠。产物在 Data\card-shot\ 下。
        if (e.Args.Any(a => string.Equals(a, "--card-shot", StringComparison.OrdinalIgnoreCase)))
        {
            CardShot.Run();
            Shutdown();
            return;
        }

        // 诊断用:核对"旧设置(三个勾选 + 排列字符串)能正确迁到新字段"。这是升级路径上
        // 最容易出错也最不该出错的一环——迁错的表现是"升级后某些环不见了",而用户
        // 不会想到去看 settings.json。结果打到 Data\migration-check.txt 后退出。
        if (e.Args.Any(a => string.Equals(a, "--check-migration", StringComparison.OrdinalIgnoreCase)))
        {
            RunMigrationCheck();
            Shutdown();
            return;
        }

        // 诊断用:用合成数据离屏渲染 rail 主界面成 PNG 后退出。环是自绘的,
        // "空闲源只画外圈"这类改动只能看图核对(靠等真实数据既不快也不可复现)。
        if (e.Args.Any(a => string.Equals(a, "--rail-shot", StringComparison.OrdinalIgnoreCase)))
        {
            RailShot.Run();
            Shutdown();
            return;
        }

        // 诊断用:把工具窗(设置/统计)离屏渲染成 PNG 后退出。用真实窗口控件(会读本机
        // 库出真数据),但不 Show、不抢焦点、不受"已有实例"守卫影响,产物在 Data\ui-shot\ 下。
        // **不在这里 Shutdown**:出图是异步的(要等窗口把数据加载完),由 UiShot 自己收尾。
        if (e.Args.Any(a => string.Equals(a, "--ui-shot", StringComparison.OrdinalIgnoreCase)))
        {
            UiShot.Run();
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
    /// 核对旧设置能正确迁到 <see cref="AppSettings.VisibleSources"/>。
    ///
    /// **为什么值得单独验**:迁错的表现是"升级后某个环不见了",而用户不会想到去翻
    /// settings.json;这条路径平时根本走不到(只有存量用户的旧配置才含旧字段)。
    /// 这里在**临时目录**上写一份旧格式设置、读回来、逐条对照,不碰用户的真实配置。
    /// </summary>
    private static void RunMigrationCheck()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            // 一组典型旧配置:关掉 OpenCode、顺序把 deepseek 排前、两个自定义环色
            var legacy = """
                {
                  "ShowGoat": true,
                  "ShowOpenCode": false,
                  "ShowDeepSeek": true,
                  "SourceOrder": "deepseek,goat,opencode",
                  "GoatTint": "#4D6BFE",
                  "DeepSeekTint": "#22D3EE"
                }
                """;
            var s = AppSettings.ParseForTest(legacy);
            if (s is null) { text.AppendLine("解析失败"); }
            else
            {
                text.AppendLine("=== 从旧设置迁移(输入: ShowGoat=true ShowOpenCode=false ShowDeepSeek=true) ===");
                text.AppendLine($"            (SourceOrder=deepseek,goat,opencode; 环色 goat=#4D6BFE deepseek=#22D3EE)");
                text.AppendLine();
                text.AppendLine($"可见源 = [{string.Join(", ", s.VisibleSources)}]   (期望 deepseek, goat)");
                text.AppendLine($"goat 可见     = {s.IsSourceVisible("goat")}   (期望 True)");
                text.AppendLine($"opencode 可见 = {s.IsSourceVisible("opencode")}   (期望 False)");
                text.AppendLine($"deepseek 可见 = {s.IsSourceVisible("deepseek")}   (期望 True)");
                text.AppendLine($"goat 环色     = {s.TintFor("goat") ?? "(无)"}   (期望 #4D6BFE)");
                text.AppendLine($"deepseek 环色 = {s.TintFor("deepseek") ?? "(无)"}   (期望 #22D3EE)");
                text.AppendLine($"opencode 环色 = {s.TintFor("opencode") ?? "(无)"}   (期望 (无))");

                // 全默认的新安装(没有任何字段)应当得到"三个源都显示"
                var fresh = AppSettings.ParseForTest("{}");
                text.AppendLine();
                text.AppendLine($"全新安装可见源 = [{string.Join(", ", fresh?.VisibleSources ?? new())}]   (期望 goat, opencode, deepseek)");

                // 旧配置里三个全关(边界):新逻辑必须至少留一个
                var allOff = AppSettings.ParseForTest("""{"ShowGoat":false,"ShowOpenCode":false,"ShowDeepSeek":false}""");
                text.AppendLine($"三个全关时可见源 = [{string.Join(", ", allOff?.VisibleSources ?? new())}]   (期望回默认三个,不留空)");

                // **用本机真实的 settings.json 再验一遍**——合成用例覆盖不了真实文件的每个字段组合,
                // 而"升级后我的环不见了"恰恰只会发生在真实文件上。
                string realFile = Path.Combine(SnapshotSource.DataPaths.RootDirectory, "settings.json");
                if (File.Exists(realFile))
                {
                    var real = AppSettings.ParseForTest(File.ReadAllText(realFile));
                    text.AppendLine();
                    text.AppendLine($"=== 本机真实设置({realFile}) ===");
                    text.AppendLine($"迁移后可见源 = [{string.Join(", ", real?.VisibleSources ?? new())}]");
                    foreach (var key in SourceCatalog.Keys)
                        text.AppendLine($"  {key,-9} 可见 = {real?.IsSourceVisible(key)}  环色 = {real?.TintFor(key) ?? "(无)"}");
                }
            }
        }
        catch (Exception ex)
        {
            text.AppendLine("迁移自检失败: " + ex);
        }

        try
        {
            string outPath = Path.Combine(SnapshotSource.DataDirectory, "migration-check.txt");
            File.WriteAllText(outPath, text.ToString());
            Diagnostics.Note($"设置迁移自检已输出到 {outPath}");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("写出设置迁移自检失败", ex);
        }
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
    /// 把"模型"页的每模型聚合与 Command Code 浏览器会话通道的状态写进
    /// Data\models-dump.txt 后退出。与 --spend 同类:口径要在数字上核对。
    /// </summary>
    private static void RunModelsDump()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            var prices = ModelPrices.Current;
            text.AppendLine($"价目表来源: {prices.Source}   模型 {prices.ModelCount} 条");
            var ledger = SpendLedger.Build(prices);
            text.AppendLine($"账本: {ledger.Entries.Count} 条聚合/明细 + {ledger.LiveEntries.Count} 条源库明细");
            text.AppendLine();

            foreach (var span in new[] { SpendSpan.Today, SpendSpan.Week, SpendSpan.All })
            {
                var rows = ModelUsageIndex.Build(ledger, span);
                text.AppendLine($"===== {span}  模型 {rows.Count} 个 =====");
                foreach (var m in rows)
                {
                    string cache = m.CacheHit is { } hit ? $"{hit * 100:0.0}%" : "—";
                    string price = m.Price is { } p
                        ? $"${p.Input:0.###}/${p.Output:0.###}"
                        : m.IsPriced ? "(单价未留档)" : "(无公开价)";
                    text.AppendLine($"  {m.Model,-38} {SpendFormat.TokensExact(m.TotalTokens),15}  {SpendFormat.Amount(m.Cost, m.HasUnpriced),10}"
                        + $"  请求 {m.Requests,6}  会话 {m.Sessions,4}  项目 {m.Projects,3}  缓存命中 {cache,7}  {price}  [{string.Join("/", m.Agents)}]"
                        + $"  {m.FirstUsed:MM-dd}~{m.LastUsed:MM-dd}");
                }
                text.AppendLine();
            }

            // 逐模型明细通道(需要浏览器登录态)
            text.AppendLine("===== Command Code 浏览器会话通道 =====");
            var buckets = CommandCodeWebSession.TryFetchModelCacheAsync(
                DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now).GetAwaiter().GetResult();
            text.AppendLine($"状态: {CommandCodeWebSession.LastStatus ?? "(未返回)"}");
            if (buckets is { Count: > 0 })
            {
                text.AppendLine($"逐模型明细 {buckets.Count} 条:");
                foreach (var g in buckets.GroupBy(b => b.Model).Take(30))
                {
                    long input = g.Sum(x => x.Input + x.CacheWrite + x.CacheRead);
                    long cacheRead = g.Sum(x => x.CacheRead);
                    text.AppendLine($"  {g.Key,-38} input={input,14:N0}  cacheRead={cacheRead,14:N0}");
                }
            }

            // 渠道(套餐)维度:每个套餐一页的数据源
            text.AppendLine();
            text.AppendLine("===== 套餐/渠道 =====");
            var registry = ChannelRegistry.Current;
            text.AppendLine($"本机配置的渠道: {string.Join("、", registry.KnownChannels.Select(c => c.Name + "(" + c.Agent + ")"))}");
            var channels = ChannelUsageIndex.Build(ledger, SpendSpan.All);
            text.AppendLine($"有记录 {channels.Count} 个:");
            foreach (var ch in channels)
            {
                string cache = ch.CacheHit is { } hit ? $"{hit * 100:0.0}%" : "—";
                text.AppendLine($"  [{ch.Name} / {ch.Agent}]");
                text.AppendLine($"    tokens={ch.TotalTokens:N0}  估算 {SpendFormat.Amount(ch.Cost, ch.HasUnpriced)}"
                    + $"  请求={ch.Requests}  会话={ch.Sessions}  缓存命中={cache}"
                    + $"  {ch.FirstUsed:MM-dd}~{ch.LastUsed:MM-dd}  providerIds=[{string.Join(",", ch.ProviderIds)}]");
                // 详细遥测（本机库里有、以前没用起来的字段）
                string reason = ch.ReasoningShare is { } rs ? $"{rs * 100:0.0}%" : "—";
                string avgSec = ch.AvgSeconds is { } sec ? $"{sec:0.0}s" : "—";
                string ttft = ch.AvgTtftSeconds is { } t ? $"{t:0.00}s" : "—";
                text.AppendLine($"    推理={ch.ReasoningTokens:N0}(占输出 {reason})  平均耗时={avgSec}  平均首字={ttft}"
                    + $"  工具调用={ch.ToolCalls:N0}  重试={ch.Retries}  失败={ch.Failures}"
                    + $"  平均每请求={ch.AvgTokensPerRequest:N0} tokens");
                text.AppendLine($"    模型({ch.Models.Count}): "
                    + string.Join(" / ", ch.Models.Take(6).Select(m =>
                        $"{m.Model} {SpendFormat.Tokens(m.Tokens)}"
                        + (m.ReasoningTokens > 0 ? $" 推理{SpendFormat.Tokens(m.ReasoningTokens)}" : "")
                        + (m.AvgSeconds is { } a ? $" {a:0.0}s" : ""))));
                // 模型的逐日明细（前两个模型，各取最近 5 天）——核对"每模型每天"矩阵
                foreach (var m in ch.Models.Take(2))
                {
                    if (m.Daily is not { Count: > 0 } dd) continue;
                    text.AppendLine($"    {m.Model} 逐日: " + string.Join(" ",
                        dd.TakeLast(5).Select(d => $"{d.Day:MM-dd}={SpendFormat.Tokens(d.Tokens)}({d.Requests}次)")));
                }
                var today = ch.TodayHourly.Where(h => h.Tokens > 0).ToList();
                text.AppendLine("    今日逐小时: " + (today.Count == 0 ? "(今天没有记录)"
                    : string.Join(" ", today.Select(h => $"{h.Hour}:{SpendFormat.Tokens(h.Tokens)}"))));
            }
        }
        catch (Exception ex)
        {
            text.AppendLine("失败: " + ex);
        }

        try
        {
            string path = Path.Combine(SnapshotSource.DataDirectory, "models-dump.txt");
            File.WriteAllText(path, text.ToString());
            Diagnostics.Note($"模型页数据已输出到 {path}");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("写模型页数据失败", ex);
        }
    }

    /// <summary>
    /// 读 ZCode 的活动日志并输出判定(诊断入口,与 --spend 同类)。它回答的是"现在会不会
    /// 显示在跑",以及连续几次 poll 的状态变化——排查"为什么环心没呼吸"就靠它。
    /// 传目录就用那个目录(拿合成日志验证边界),不给则用 ZCode 的真实日志目录。
    /// </summary>
    private static void RunActivityDump(string? logDirectory)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            var activity = new ZCodeActivity(logDirectory);
            string today = activity.TodayLogPath;
            text.AppendLine($"日志目录: {activity.LogDirectory}");
            text.AppendLine($"今日日志: {today}");
            text.AppendLine(File.Exists(today)
                ? $"  存在,{new FileInfo(today).Length:N0} 字节"
                : "  不存在(ZCode 今天没跑过 → 按空闲处理)");
            text.AppendLine();

            for (int i = 0; i < 6; i++)
            {
                bool changed = activity.Poll();
                text.AppendLine($"  t+{i}s  IsWorking={activity.IsWorking}  未闭合回合={activity.OpenTurns}"
                    + $"  本次变化={changed}  错误={activity.LastError ?? "无"}");
                if (i < 5) Thread.Sleep(1000);
            }

            text.AppendLine();
            text.AppendLine(activity.IsWorking
                ? "结论: ZCode 正在跑 → GOAT 环心图标会呼吸"
                : "结论: ZCode 空闲 → 环心图标静止");

            // DSH 的活动判定同样在这里核对:它读的是会话投影,不是日志增量。
            text.AppendLine();
            text.AppendLine("—— DSH ——");
            var dsh = new DshActivity();
            text.AppendLine($"投影目录: {dsh.ProjectionDirectory}");
            text.AppendLine(Directory.Exists(dsh.ProjectionDirectory)
                ? "  存在"
                : "  不存在(DSH 没跑过 → 按空闲处理)");
            for (int i = 0; i < 3; i++)
            {
                bool dshChanged = dsh.Poll();
                text.AppendLine($"  t+{i}s  IsWorking={dsh.IsWorking}  开着回合={dsh.OpenTurns}"
                    + $"  会话={dsh.ActiveSession ?? "-"}  信号时刻={dsh.LastSignalAt?.ToLocalTime():HH:mm:ss}"
                    + $"  本次变化={dshChanged}  错误={dsh.LastError ?? "无"}");
                if (i < 2) Thread.Sleep(1000);
            }
            text.AppendLine(dsh.IsWorking
                ? "结论: DSH 正在跑 → GOAT 环心图标会呼吸"
                : "结论: DSH 空闲");
        }
        catch (Exception ex)
        {
            text.AppendLine("失败: " + ex);
        }

        try
        {
            string path = Path.Combine(SnapshotSource.DataDirectory, "activity.txt");
            File.WriteAllText(path, text.ToString());
            Diagnostics.Note($"ZCode 活动判定已输出到 {path}");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("写活动判定失败", ex);
        }
    }

    /// <summary>
    /// DSH 用量读取器的诊断:解析结果、冷/热耗时、文件级缓存命中,以及**与 DSH 自己的
    /// 会话投影逐会话对账**(结果写 Data\dsh-dump.txt)。
    ///
    /// 对账是这里最有价值的一步:投影里的 <c>tokenUsage.totals</c> 是 DSH 自己算的累计值,
    /// 与我们从事件流逐步累加的结果不一致,就说明有事件没读到(最常见的原因是活动会话
    /// 正在被追加、zstd 尾部被截断)。差异**允许存在**——投影只覆盖"DSH 现在还认得的会话",
    /// 而用量是一笔长期的账(会话删了、DSH 重装过,投影就没了)——所以这里只报差异,不判谁对。
    /// </summary>
    private static void RunDshDump()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            var store = new DshUsageStore();
            text.AppendLine($"会话根目录: {store.Location}");
            text.AppendLine($"存在: {store.IsPresent}");
            text.AppendLine();

            var watch = Stopwatch.StartNew();
            var records = store.Read();
            long cold = watch.ElapsedMilliseconds;
            watch.Restart();
            store.Read();
            long warm = watch.ElapsedMilliseconds;
            var (hits, misses, files, cachedRecords, truncations) = DshUsageStore.CacheStats;

            text.AppendLine($"记录 {records.Count} 条   会话 {records.Select(r => r.SessionId).Distinct().Count()} 个"
                + $"   项目 {records.Select(r => r.Project).Distinct().Count()} 个");
            text.AppendLine($"耗时: 首次 {cold} ms(全量解压+解析) / 第二次 {warm} ms(命中缓存)");
            text.AppendLine($"缓存: 文件 {files} / 记录 {cachedRecords} / 命中 {hits} / 未命中 {misses} / 截断 {truncations}");
            text.AppendLine();

            if (records.Count > 0)
            {
                text.AppendLine($"时间: {records.Min(r => r.Timestamp):yyyy-MM-dd HH:mm:ss} ~ {records.Max(r => r.Timestamp):yyyy-MM-dd HH:mm:ss}");
                text.AppendLine($"四类: input={records.Sum(r => r.Tally.Input):N0}"
                    + $" cacheWrite={records.Sum(r => r.Tally.CacheWrite):N0}"
                    + $" cacheRead={records.Sum(r => r.Tally.CacheRead):N0}"
                    + $" output={records.Sum(r => r.Tally.Output):N0}");
                text.AppendLine($"总量 {records.Sum(r => r.TotalTokens):N0}"
                    + $"   未分类 {records.Sum(r => r.UnclassifiedTokens):N0}"
                    + $"   工具调用 {records.Sum(r => r.ToolCalls):N0}"
                    + $"   重试 {records.Sum(r => r.Retries)}   失败 {records.Sum(r => r.Failures)}");
                text.AppendLine();
                text.AppendLine("—— provider / model ——");
                foreach (var group in records.GroupBy(r => $"{r.ProviderId}|{r.Model}").OrderByDescending(g => g.Count()))
                    text.AppendLine($"  {group.Key,-52} x{group.Count()}");
                text.AppendLine("—— 项目 ——");
                foreach (var group in records.GroupBy(r => r.Project ?? "(无)").OrderByDescending(g => g.Sum(r => r.TotalTokens)).Take(6))
                    text.AppendLine($"  {group.Key,-46} {group.Sum(r => r.TotalTokens),15:N0}  ({group.Select(r => r.SessionId).Distinct().Count()} 会话)");
            }

            text.AppendLine();
            text.AppendLine("—— 与 DSH 会话投影对账(本机逐步累加 vs 投影 totals) ——");
            var bySession = records.GroupBy(r => r.SessionId ?? "")
                .ToDictionary(g => g.Key, g => g.Sum(r => r.TotalTokens), StringComparer.OrdinalIgnoreCase);
            string projectionDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "storages", "session_projcache", "sessions");
            int compared = 0, mismatched = 0;
            long difference = 0;
            if (Directory.Exists(projectionDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(projectionDirectory, "*.json"))
                {
                    long projected;
                    try { projected = ReadProjectedTokens(file); }
                    catch (Exception) { continue; }
                    if (projected <= 0) continue;

                    string id = Path.GetFileNameWithoutExtension(file);
                    long mine = bySession.GetValueOrDefault(id);
                    compared++;
                    if (projected == mine) continue;
                    mismatched++;
                    difference += projected - mine;
                    if (mismatched <= 10)
                        text.AppendLine($"  {id,-44} 投影 {projected,14:N0}   本机 {mine,14:N0}   差 {projected - mine,12:N0}");
                }
            }
            text.AppendLine($"  对比 {compared} 个会话,不一致 {mismatched} 个,合计差 {difference:N0} tokens");
            text.AppendLine("  (差异的常见来源:活动会话尾部截断、会话被删、投影比事件流落后一步)");
        }
        catch (Exception ex)
        {
            text.AppendLine("失败: " + ex);
        }

        try
        {
            string path = Path.Combine(SnapshotSource.DataDirectory, "dsh-dump.txt");
            File.WriteAllText(path, text.ToString());
            Diagnostics.Note($"DSH 用量诊断已输出到 {path}");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("写 DSH 用量诊断失败", ex);
        }
    }

    /// <summary>读一份 DSH 会话投影里自报的累计 token 数(四类之和)。读不出来返回 0。</summary>
    private static long ReadProjectedTokens(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("record", out var record)) return 0;
        if (!record.TryGetProperty("rows", out var rows)) return 0;
        if (!rows.TryGetProperty("tokenUsage", out var usage)) return 0;
        if (!usage.TryGetProperty("val", out var value)) return 0;
        if (!value.TryGetProperty("totals", out var totals)) return 0;

        long sum = 0;
        foreach (string name in new[] { "uncachedInputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens" })
        {
            if (totals.TryGetProperty(name, out var item) && item.ValueKind == System.Text.Json.JsonValueKind.Number
                && item.TryGetInt64(out long parsed))
                sum += parsed;
        }
        return sum;
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
            new FlyoutMenu.Item("用量…", OpenSpend),
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
    ///
    /// 优先复用 MainWindow 已经加载好的数据(它对同一份快照做了缓存与短路径),
    /// 不再每轮同步都从磁盘把三个 JSON 重读反序列化一遍。窗口还没准备好时
    /// (启动瞬间)才退回去自己读一次。
    /// </summary>
    /// <summary>
    /// 托盘:常驻**品牌图标**,悬停文字给额度摘要。
    ///
    /// v1.12.2 之前这里会把"最紧张的池"画成小环当图标(仪表化),现在改成固定品牌图标——
    /// 用户明确表示任务栏里的状态小环认不出是什么程序。挂载点保持不动:摘要在每次
    /// 同步后照旧刷新,阈值告警气泡也仍在这里触发。
    /// </summary>
    private void UpdateTrayMeter()
    {
        if (_tray is null || _icon is null) return;
        try
        {
            var subs = _main is { } main && main.Subs.Count > 0
                ? main.Subs
                : SnapshotSource.LoadAll();
            string tooltip = TraySummary.BuildTooltip(subs);
            if (tooltip.Length > 63) tooltip = tooltip[..63];
            _tray.Icon = _icon;
            _tray.Text = tooltip;
            CheckQuotaAlerts(subs);
        }
        catch (Exception ex)
        {
            _tray.Icon = _icon;
            Diagnostics.Note("托盘摘要更新失败,保持品牌图标", ex);
        }
    }

    /// <summary>每个额度池上一次的用量百分比(告警"从下穿上"检测;键 = 源/池)。</summary>
    private readonly Dictionary<string, double> _alertPercent = new(StringComparer.Ordinal);

    /// <summary>
    /// 额度告警:某个池的用量**从阈值下方穿到上方**时弹一次托盘气泡(只恢复/降下去不报,
    /// 也不在每次同步都重复报)。阈值就是 rail 转红的那一档(设置里的报警阈值),两边一个口径。
    /// 余额型池没有"用掉就没了"的重置语义,不参与。
    /// </summary>
    private void CheckQuotaAlerts(IReadOnlyList<SubData> subs)
    {
        if (_headless || _tray is null) return;
        double threshold = Math.Clamp(AppSettings.Current.AlertThreshold, 0.5, 0.99) * 100;
        foreach (var sub in subs)
        {
            foreach (var pool in sub.Pools)
            {
                if (pool.PoolKind == "Balance" || !pool.HasPercent) continue;
                double pct = pool.Fraction * 100;
                string key = sub.Key + "/" + pool.PoolKind;
                double last = _alertPercent.TryGetValue(key, out var v) ? v : pct;
                _alertPercent[key] = pct;
                if (last < threshold && pct >= threshold)
                {
                    string detail = pool.Cap is { } cap
                        ? $"已用 {Money.Short(pool.Used ?? 0, pool.Unit)} / {Money.Short(cap, pool.Unit)}"
                        : $"已用 {pool.PercentText}";
                    try
                    {
                        _tray.ShowBalloonTip(8000, "Pulse 额度提醒",
                            $"{sub.Name} {pool.Label}{detail},{pool.RemainingText()}。",
                            System.Windows.Forms.ToolTipIcon.Warning);
                    }
                    catch (Exception ex)
                    {
                        // 气泡在部分系统配置下会失败:提示是锦上添花,不因它报错
                        Diagnostics.Note("额度告警气泡失败", ex);
                    }
                    Diagnostics.Note($"额度告警:{key} {pct:0.0}% ≥ {threshold:0}%");
                }
            }
        }
    }

    /// <summary>用量窗口(单例:再点就把它亮出来)。</summary>
    private void OpenSpend()
    {
        if (_spend is { IsLoaded: true })
        {
            _spend.Activate();
            return;
        }
        try
        {
            var spend = new SpendWindow();
            // 关掉就放引用:统计窗的账本(几万条明细 + 图表/热图的可视树)很占内存,
            // 而 WinForms/WPF 关窗后如果还有字段指着它,这块内存就永远不还。
            // 用完即弃比"留着下次秒开"划算——重开一次只是再读一遍库。
            spend.Closed += (_, _) => _spend = null;
            _spend = spend;
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
            var settings = new SettingsWindow(_engine);
            // 同上:设置窗有多页 XAML(含三套色板弹层),关掉后不再留引用。
            settings.Closed += (_, _) => _settings = null;
            settings.SettingsChanged += () => _main?.ApplySettings();
            settings.OpenUsageRequested += OpenSpend;
            _settings = settings;
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
