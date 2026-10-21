using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PulseWin;

/// <summary>
/// 工具窗的离屏出图(`--ui-shot`)。不 Show 真实窗口、不抢焦点、不受"已有实例"守卫影响,
/// 产物写到 <c>Data\ui-shot\</c>。
///
/// 为什么要有它:色块按钮装不装得下文字、热图会不会大片留空,这类事只能看像素,
/// 而设置窗/统计窗我起不了第二份(单实例),也不该去动用户正开着的那个进程。
/// 设置窗要一个 UsageEngine 才能构造,而出图不该顺带打 API,所以环色选择器单独渲染控件。
/// </summary>
internal static class UiShot
{
    public static void Run()
    {
        string dir = Path.Combine(SnapshotSource.DataDirectory, "ui-shot");
        Directory.CreateDirectory(dir);

        // --ui-shot 没有主窗口,默认的 OnLastWindowClose 会在第一张图后就把应用关掉
        // (第二张窗只来得及存一张空图,还会抛"应用程序对象正在关闭")。改成显式退出。
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        RenderTintPickers(Path.Combine(dir, "tint-picker.png"));
        try
        {
            using var engine = new UsageEngine();   // 不 Start:引擎只有 Start 后才发网络请求
            RenderSettingsPage(engine, Path.Combine(dir, "settings-appearance.png"), "外观", "NavAppearance");
            RenderSettingsPage(engine, Path.Combine(dir, "settings-behavior.png"), "行为", "NavBehavior");
            RenderSettingsPage(engine, Path.Combine(dir, "settings-rings.png"), "圆环与数字", "NavRings");
            RenderSettingsPage(engine, Path.Combine(dir, "settings-sources.png"), "数据源", "NavSources");
            RenderSettingsPage(engine, Path.Combine(dir, "settings-refresh.png"), "刷新与网络", "NavRefresh");
            RenderSettingsPage(engine, Path.Combine(dir, "settings-about.png"), "关于", "NavAbout");
        }
        catch (Exception ex)
        {
            Diagnostics.Note("设置页出图失败", ex);
        }
        RenderSpendWindow(dir);
    }

    /// <summary>设置窗的某一页(用同一个不 Start 的引擎,避免重复初始化)。</summary>
    private static void RenderSettingsPage(UsageEngine engine, string path, string title, string navName)
    {
        try
        {
            var window = new SettingsWindow(engine)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
            };
            window.Show();
            // 切到目标页(侧栏是 RadioButton,直接置 IsChecked 会触发 Nav_Checked)。
            // **必须在 Show 之后**:Show 之前可视树还没建,切页后内层容器没有实际尺寸。
            if (window.FindName(navName) is System.Windows.Controls.RadioButton nav)
                nav.IsChecked = true;
            // 切页会换掉可见的 StackPanel,布局要让它跑完一遍再截图;
            // 用 UpdateLayout 还不够——外层窗口尺寸没变,内层是懒布局。
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            double w = window.ActualWidth, h = window.ActualHeight;
            if (w < 1 || h < 1) w = window.Width;
            if (h < 1) h = window.Height;
            Save(window, new Size(w, h), path);
            // 整窗图受窗口高度限制,卡片下半可能被裁(数据源页的 OpenCode Go 卡
            // 就很长);另出一张内容长图(滚动区全高)兜底。
            SaveFullContent(window, Path.ChangeExtension(path, ".full.png"));
            window.Close();
        }
        catch (Exception ex)
        {
            Diagnostics.Note($"设置页出图失败({title})", ex);
        }
    }

    /// <summary>环色选择器:自动档(最长文案)+ 一个固定色档,验证文字不被右缘切。</summary>
    private static void RenderTintPickers(string path)
    {
        var panel = new StackPanel
        {
            Background = Brushes.White,
            Margin = new Thickness(20),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "环色选择器(设置 › 圆环与数字 › 圆环颜色)",
            FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        foreach (var (label, value) in new (string, string?)[]
                 {
                     ("Command Code GOAT", null),        // 自动 · 按用量 —— 最长文案
                     ("OpenCode Go", "#4D6BFE"),          // 固定色,显示 HEX
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 13, Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            });
            row.Children.Add(new TintPicker { Value = value });
            panel.Children.Add(row);
        }

        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            Child = panel,
        };
        // 单独测量:控件没挂到窗口上,得自己 Arrange 才有尺寸
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Save(host, host.DesiredSize, path);
    }

    /// <summary>
    /// 出图用的合成官方读数。真实数据要浏览器登录态(读 cookie),而出图环境里浏览器
    /// cookie 库是锁着的——用它把「官方 vs 本机」对照格与逐模型榜渲染出来看。
    /// </summary>
    private static OpenCodeUsageStats SampleOfficialUsage()
    {
        var now = DateTimeOffset.Now;
        var models = new List<OpenCodeModelStat>
        {
            new("deepseek-v4.1-flash", "opencode-go", 5100, 22_000_000, 3_900_000, 498_000_000, 0, 8.82),
            new("glm-5.3-flash", "opencode-go", 420, 1_500_000, 210_000, 24_000_000, 0, 0.61),
            new("mimo-v2.5", "opencode-go", 250, 600_000, 90_000, 1_200_000, 0, 0.22),
        };
        var days = Enumerable.Range(0, 7).Select(i => new OpenCodeDayStat(
            now.AddDays(-i).ToString("yyyy-MM-dd"),
            800 - i * 60, 82_000_000 - i * 5_000_000, 1.38 - i * 0.08)).ToList();
        return new OpenCodeUsageStats(
            "30d", 5770, 23_556_932, 4_104_268, 523_265_190, 0, 9.65, models, days, now);
    }

    private static void RenderSpendWindow(string dir)
    {
        var window = new SpendWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,          // 屏幕外:布局照算,人看不见
            Top = -32000,
            ShowInTaskbar = false,
            // 宽窗口出图是给人看排版的(默认 640 会压掉对照格右缘文字);窄窗口的布局
            // 另用 PULSEWIN_UISHOT_WIDTH=640 再跑一遍——两种宽度都有卡片会被裁的风险,
            // 只验一种会漏(实测 640 下「网页控制台」chip 被顶出卡片)。
            Width = int.TryParse(Environment.GetEnvironmentVariable("PULSEWIN_UISHOT_WIDTH"), out var shotWidth)
                && shotWidth >= 480 ? shotWidth : 1400,
        };
        window.Show();

        // 等数据加载完:用量窗读本机两个库 + 画图,异步完成后才有内容。
        // 出图直接渲染 ScrollViewer 的内容(StackPanel 全高)——窗口高度会被
        // Win32 压回屏幕,但内容元素的完整高度不受限,一张长图收下所有卡片。
        // 先出总览,再驱动真实 tab 按钮**逐个**切到每个套餐页各出一张(与手点同一条路径)。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(22) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                SaveFullContent(window, Path.Combine(dir, "usage-overview.png"));
                // 另存一张"窗口本身"(含顶部工具条:tab/区间/状态行)——头部不在滚动区里,
                // 长图收不到它,而状态行文案("正在读取…"有没有被改写成结果)只有它能看到。
                window.UpdateLayout();
                Save(window, new Size(window.ActualWidth, window.ActualHeight), Path.Combine(dir, "usage-window-chrome.png"));
            }
            catch (Exception ex)
            {
                Diagnostics.Note("用量窗出图失败", ex);
                window.Close();
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // **每个套餐页都要出图**:一页里"少了哪张卡"只有在图上才看得出来,
            // 而窗口一次只显示一个 tab(手点既慢、也没法保证每页都看全)。
            int index = 1;
            int count = window.UiShotTabCount;
            var step = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            step.Tick += (_, _) =>
            {
                if (index >= count)
                {
                    step.Stop();
                    window.Close();
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
                try
                {
                    window.UiShotOpenTab(index);
                    string label = window.UiShotTabLabel(index);
                    // OpenCode Go 页的官方读数要浏览器登录态才拿得到,出图环境读不到 cookie——
                    // 注入一份合成读数,好让「官方 vs 本机」对照格与逐模型榜真的被渲染出来看一眼
                    //(项目里 --rail-shot 也是这么干的)。
                    if (label.Contains("OpenCode", StringComparison.OrdinalIgnoreCase))
                        window.UiShotInjectOfficialUsage(SampleOfficialUsage());
                    string file = $"usage-channel-{index}.png";
                    SaveFullContent(window, Path.Combine(dir, file));
                    Diagnostics.Note($"套餐页已出图 {file} — {label}");
                }
                catch (Exception ex)
                {
                    Diagnostics.Note($"用量窗套餐页出图失败(index={index})", ex);
                }
                index++;
            };
            step.Start();
        };
        timer.Start();
    }

    /// <summary>把滚动区的内容(StackPanel 全高)渲染成一张长图;没有滚动容器则退回整窗。</summary>
    private static void SaveFullContent(FrameworkElement host, string path)
    {
        host.UpdateLayout();
        var sv = FindScrollViewer(host);
        if (sv?.Content is FrameworkElement content && content.ActualHeight > 0)
        {
            content.UpdateLayout();
            Save(content, new Size(content.ActualWidth, content.ActualHeight), path);
        }
        else
        {
            Save(host, new Size(host.ActualWidth, host.ActualHeight), path);
        }
    }

    /// <summary>窗口里**最宽**的滚动容器:设置窗有侧栏+内容区两个 ScrollViewer,
    /// 抓第一个会抓到侧栏(出图就是一条黑边栏);内容区永远比侧栏宽。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var all = new List<ScrollViewer>();
        CollectScrollViewers(root, all);
        return all.OrderByDescending(sv => sv.ActualWidth).FirstOrDefault();
    }

    private static void CollectScrollViewers(DependencyObject root, List<ScrollViewer> found)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) found.Add(sv);
            CollectScrollViewers(child, found);
        }
    }

    private static void Save(FrameworkElement element, Size size, string path)
    {
        int w = (int)Math.Ceiling(size.Width), h = (int)Math.Ceiling(size.Height);
        if (w < 1 || h < 1) { Diagnostics.Note($"界面出图跳过(尺寸为 0):{path}"); return; }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        // **不要直接 bmp.Render(element)**:那样会带上元素在父级里的 margin 偏移,
        // 导致整幅右移(滚动区内容有 20px 左边距,于是右缘被裁掉同样宽的一条)。
        // 用 VisualBrush 把元素内容画到 (0,0) 自己控制原点。
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(
                new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null,
                new Rect(0, 0, w, h));
        }
        bmp.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Diagnostics.Note($"界面已出图 {path} ({size.Width:0}x{size.Height:0})");
    }
}
