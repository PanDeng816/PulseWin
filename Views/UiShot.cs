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

        RenderTintPickers(Path.Combine(dir, "tint-picker.png"));
        RenderSpendWindow(Path.Combine(dir, "spend-window.png"));
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

    private static void RenderSpendWindow(string path)
    {
        var window = new SpendWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,          // 屏幕外:布局照算,人看不见
            Top = -32000,
            ShowInTaskbar = false,
        };
        window.Show();

        // 等数据加载完:统计窗读本机两个库 + 画图,异步完成后才有内容。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(22) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                window.UpdateLayout();
                Save(window, new Size(window.ActualWidth, window.ActualHeight), path);
            }
            catch (Exception ex) { Diagnostics.Note("统计窗出图失败", ex); }
            finally
            {
                window.Close();
                System.Windows.Application.Current.Shutdown();
            }
        };
        timer.Start();
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
