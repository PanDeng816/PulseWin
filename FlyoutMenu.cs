using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PulseWin;

/// <summary>
/// rail / 托盘共用的弹出菜单,样式对齐 ZCode 的右键菜单:白色圆角卡片、
/// 柔和投影、分组留白,悬停浅灰圆角高亮。
///
/// 用无边框透明置顶窗承载(与悬浮卡 CardWindow 同一模式),而不是系统
/// ContextMenu / WinForms ContextMenuStrip(样式突兀、圆角投影做不干净),
/// 也不是 WPF Popup——后台进程弹 Popup 时渲染管线不画内容,窗口"可见"但
/// 全透明。菜单窗激活自己(像系统菜单一样),失焦即关,鼠标/键盘行为因此
/// 与普通菜单完全一致。
/// 进程是 SystemAware DPI,屏幕坐标只有一套,弹出位置直接用 DIU 给。
/// </summary>
internal static class FlyoutMenu
{
    /// <summary>一条菜单项。Checked 非 null 时显示勾选标记(如"开机自动启动")。</summary>
    public sealed record Item(string Text, Action? Click = null, bool? Checked = null);

    /// <summary>分组边界:渲染为一段留白加一条极浅的分割线。</summary>
    public sealed record Gap();

    private static readonly Brush CardBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));
    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)));
    private static readonly Brush HairlineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private const double ItemHeight = 34;
    private const double RowTextIndent = 14;   // 文本相对行左缘的起点
    private const double CardPadding = 6;
    private const double CardMargin = 14;      // 投影的呼吸空间,卡片本身不可见
    private const double MinCardWidth = 176;
    private static readonly Typeface TextTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // 每屏一个打开中的菜单:再弹第二个时把旧的关掉,和系统菜单行为一致。
    private static FlyoutMenuWindow? _open;

    /// <summary>
    /// 在屏幕坐标(DIU)处弹出菜单。锚点落在屏幕边缘时菜单会被夹回工作区
    /// (rail 贴着屏幕右缘,不夹的话菜单直接画到屏幕外)。
    /// </summary>
    public static void Show(Window owner, Point anchorDiu, IReadOnlyList<object> entries, Action? closed = null)
    {
        _open?.CloseSelf();
        _open = null;

        var stack = new StackPanel();
        double widest = 0;

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case Gap:
                    stack.Children.Add(new Border
                    {
                        Height = 1,
                        Background = HairlineBrush,
                        Margin = new Thickness(10, 7, 10, 7),
                    });
                    break;
                case Item item:
                    stack.Children.Add(MenuItemRow(item, ref widest));
                    break;
            }
        }

        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(CardPadding),
            Margin = new Thickness(CardMargin),
            MinWidth = MinCardWidth,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.22,
            },
            Child = stack,
        };

        var window = new FlyoutMenuWindow(card, closed);

        // 弹出前按估算尺寸夹住锚点,Loaded 后再用真实尺寸校正——
        // 否则第一帧会从屏幕外跳进来。
        double estimatedW = Math.Max(MinCardWidth, widest + RowTextIndent + CardPadding * 2) + CardMargin * 2;
        double estimatedH = entries.OfType<Item>().Count() * ItemHeight
            + entries.OfType<Gap>().Count() * 15
            + CardPadding * 2 + CardMargin * 2;
        Place(window, anchorDiu, estimatedW, estimatedH, owner);

        window.Loaded += (_, _) =>
        {
            double w = card.ActualWidth + CardMargin * 2;
            double h = card.ActualHeight + CardMargin * 2;
            Place(window, anchorDiu, w, h, owner);
        };

        _open = window;
        window.Show();
        window.Activate();   // 失焦即关的前提:菜单窗在前台,和系统菜单一致
    }

    private static FrameworkElement MenuItemRow(Item item, ref double widest)
    {
        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = 14,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(RowTextIndent, 0, 12, 0),
        };

        Grid grid;
        if (item.Checked is null)
        {
            grid = new Grid { Children = { text } };
        }
        else
        {
            var mark = new TextBlock
            {
                Text = item.Checked == true ? "✓" : "",
                FontSize = 13,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 20,
            };
            grid = new Grid { Children = { mark, text } };
            Grid.SetColumn(text, 1);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        var row = new Border
        {
            Height = ItemHeight,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,   // 透明也接鼠标,否则 hover 触发不了
            Cursor = Cursors.Hand,
            Child = grid,
        };
        var hover = new Style(typeof(Border));
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
        hover.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(Border.BackgroundProperty, HoverBrush) },
        });
        row.Style = hover;

        widest = Math.Max(widest, MeasureText(item.Text) + (item.Checked is null ? 0 : 24));

        row.MouseLeftButtonUp += (_, _) =>
        {
            // **先关菜单再执行动作**:动作可能开新窗抢激活(反过来触发 Deactivated→Close)
            // 或直接 Shutdown——后关的话就是对"正在关闭中的窗口"重复 Close,
            // WPF 会抛 InvalidOperationException(App 的异常兜底再弹个错误框)。
            var open = _open;
            _open = null;
            open?.CloseSelf();
            item.Click?.Invoke();
        };
        return row;
    }

    private static double MeasureText(string text)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, TextTypeface, 14, TextBrush, 1.0);
        return ft.Width;
    }

    private static void Place(Window window, Point anchor, double width, double height, Window owner)
    {
        var wa = Native.WorkingAreaUnderPointer(Native.Scale(owner));
        // 锚点是卡片左上角;窗口含投影边距,定位时减回去
        double x = Math.Clamp(anchor.X - CardMargin, wa.Left + 2, Math.Max(wa.Left + 2, wa.Right - width - 2));
        double y = Math.Clamp(anchor.Y - CardMargin, wa.Top + 2, Math.Max(wa.Top + 2, wa.Bottom - height - 2));
        window.Left = x;
        window.Top = y;
    }
}

/// <summary>
/// 菜单的承载窗:无边框透明置顶,行为对齐系统菜单(激活自己、失焦即关、Esc 关)。
/// 所有关闭都走 <see cref="CloseSelf"/>:菜单项的动作可能 Shutdown、或开新窗口
/// 抢走激活(再触发 Deactivated),这些路径会对已在关闭中的窗口再调一次 Close——
/// WPF 对 closing 中的窗口重复 Close 会抛 InvalidOperationException。
/// </summary>
internal sealed class FlyoutMenuWindow : Window
{
    private bool _closing;
    private readonly Action? _closed;

    public FlyoutMenuWindow(FrameworkElement card, Action? closed)
    {
        _closed = closed;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = card;
        Deactivated += (_, _) => CloseSelf();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseSelf();
        };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => _closed?.Invoke();
    }

    /// <summary>幂等关闭:已经在关闭流程中就静默跳过。</summary>
    public void CloseSelf()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
