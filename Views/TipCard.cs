using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 图表悬停卡片的内容（外壳——圆角、阴影、描边、配色——由 <c>Themes\Light.xaml</c> 的
/// 全局 <c>ToolTip</c> 样式提供，这里只负责排版）。
///
/// 版式借鉴 mac/VS Code 的见解卡：一条**很小的标题**、一个**放大的主数值**、几行弱化明细。
/// 原来的字符串提示一旦超过两行就糊成一片，看不出哪个数字是主角。
/// </summary>
internal static class TipCard
{
    internal enum Tag { None, Peak, OffPeak }

    /// <summary>一张卡片：标题（日期/时刻）、主数值、明细行、可选的峰/谷小标。</summary>
    internal sealed record Data(string Title, string Value, IReadOnlyList<string> Lines, Tag Tag = Tag.None);

    private static FrameworkElement BuildChip(bool peak, Color accent) => new Border
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1, 5, 1),
        Margin = new Thickness(8, 0, 0, 0),
        // 直接走 UiTheme.B：它按色值缓存，深浅主题切换后自然取新画刷（自己再缓一层会钉死旧主题）
        Background = UiTheme.B(Color.FromArgb(peak ? (byte)0x22 : (byte)0x1A, accent.R, accent.G, accent.B)),
        Child = new TextBlock
        {
            Text = peak ? "峰时" : "谷时",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.B(accent),
        },
    };

    public static FrameworkElement Build(Data data)
    {
        var stack = new StackPanel { MaxWidth = 236, MinWidth = 116 };

        // 标题行：左标题、右小标（峰/谷），标题过长省略而不是换行
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = data.Title,
            FontSize = 11,
            Foreground = UiTheme.B(UiTheme.TextSub),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(title);

        if (data.Tag != Tag.None)
        {
            var chip = BuildChip(data.Tag == Tag.Peak,
                data.Tag == Tag.Peak ? UiTheme.PeakBar : UiTheme.Good);
            Grid.SetColumn(chip, 1);
            head.Children.Add(chip);
        }

        stack.Children.Add(head);

        var value = new TextBlock
        {
            Text = data.Value,
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.B(UiTheme.TextMain),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        stack.Children.Add(value);

        for (int i = 0; i < data.Lines.Count; i++)
        {
            var line = new TextBlock
            {
                Text = data.Lines[i],
                FontSize = 11,
                LineHeight = 16,
                Foreground = UiTheme.B(UiTheme.TextSub),
                Margin = new Thickness(0, i == 0 ? 4 : 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Typography.SetNumeralAlignment(line, FontNumeralAlignment.Tabular);
            stack.Children.Add(line);
        }

        return stack;
    }

    /// <summary>把旧的字符串提示包成一张单行卡片，避免图表工具在两种内容类型之间分叉。</summary>
    public static Data Plain(string title, string value, params string[] lines) => new(title, value, lines);
}
