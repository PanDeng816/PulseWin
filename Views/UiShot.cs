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

    private static void RenderSpendWindow(string dir)
    {
        var window = new SpendWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,          // 屏幕外:布局照算,人看不见
            Top = -32000,
            ShowInTaskbar = false,
        };
        window.Show();

        // 等数据加载完:用量窗读本机两个库 + 画图,异步完成后才有内容。
        // 出图直接渲染 ScrollViewer 的内容(StackPanel 全高)——窗口高度会被
        // Win32 压回屏幕,但内容元素的完整高度不受限,一张长图收下所有卡片。
        // 先出总览,再驱动真实 tab 按钮切到套餐页出第二张(与手点同一条路径)。
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
                window.UiShotOpenFirstChannel();
                var second = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                second.Tick += (_, _) =>
                {
                    second.Stop();
                    try
                    {
                        SaveFullContent(window, Path.Combine(dir, "usage-channel.png"));
                    }
                    catch (Exception ex) { Diagnostics.Note("用量窗套餐页出图失败", ex); }
                    finally
                    {
                        window.Close();
                        System.Windows.Application.Current.Shutdown();
                    }
                };
                second.Start();
            }
            catch (Exception ex)
            {
                Diagnostics.Note("用量窗出图失败", ex);
                window.Close();
                System.Windows.Application.Current.Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>把用量窗滚动区的内容(StackPanel 全高)渲染成一张长图。</summary>
    private static void SaveFullContent(SpendWindow window, string path)
    {
        window.UpdateLayout();
        var sv = FindScrollViewer(window);
        if (sv?.Content is FrameworkElement content && content.ActualHeight > 0)
        {
            content.UpdateLayout();
            Save(content, new Size(content.ActualWidth, content.ActualHeight), path);
        }
        else
        {
            Save(window, new Size(window.ActualWidth, window.ActualHeight), path);
        }
    }

    /// <summary>窗口里的滚动容器(用量窗的内容区)。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static void Save(FrameworkElement element, Size size, string path)
    {
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Diagnostics.Note($"界面已出图 {path} ({size.Width:0}x{size.Height:0})");
    }
}
