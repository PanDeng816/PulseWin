using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PulseWin;

/// <summary>
/// 单个会话的下钻窗口(双击"最近会话"列表的一行打开)。回答的是"这个会话框
/// 里到底发生了什么":模型怎么分的、时间怎么花的。**只读内存里已有的账本,
/// 不回源库重扫**——打开它就该是瞬时的。
///
/// 数据是 <see cref="SpendAggKind.Live"/> 的明细行;本地仓库的聚合行没有会话号,
/// 到不了这里,所以这里的数字可能与统计页(含归档)有微小出入,是口径不是错误。
/// </summary>
public sealed class SessionWindow : Window
{
    private const double BarBase = 180;

    public SessionWindow(IReadOnlyList<SpendEntry> entries)
    {
        Title = "会话详情";
        Width = 520;
        Height = 600;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));

        var ordered = entries.OrderBy(e => e.Timestamp).ToList();
        string title = ordered.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Title))?.Title
            ?? ordered.First().SessionId
            ?? "无标题会话";
        long tokens = ordered.Sum(e => e.TotalTokens);
        double cost = ordered.Sum(e => e.Cost.Total);
        long requests = ordered.Sum(e => e.Requests);

        // —— 头部:会话身份 + 总量 ——
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        head.Children.Add(DimLine($"{ordered.First().Agent} · {ordered.First().Project ?? "无项目"}"));
        head.Children.Add(DimLine(
            $"{ordered.First().Timestamp:MM-dd HH:mm} ~ {ordered.Last().Timestamp:MM-dd HH:mm}   ·   "
            + $"{requests:N0} 次调用   ·   {SpendFormat.TokensExact(tokens)} tokens   ·   {SpendFormat.Money(cost)}"));

        // —— 模型分布 ——
        var modelGroups = ordered.GroupBy(e => ModelPrices.DisplayName(e.Model))
            .Select(g => (Model: g.Key, Tokens: g.Sum(e => e.TotalTokens), Cost: g.Sum(e => e.Cost.Total),
                Requests: g.Sum(e => e.Requests),
                Tally: g.Aggregate(TokenTally.Zero, (a, e) => a + e.Tally)))
            .OrderByDescending(g => g.Tokens)
            .ToList();
        long maxModelTokens = modelGroups.Count > 0 ? modelGroups[0].Tokens : 0;
        var modelHost = new StackPanel();
        foreach (var group in modelGroups)
        {
            modelHost.Children.Add(ModelRow(group.Model, group.Tokens, group.Cost, group.Requests, group.Tally, maxModelTokens));
        }

        // —— 按小时的时间线 ——
        var timeline = new Canvas { Height = 96, ClipToBounds = true };
        var hours = ordered.GroupBy(e => new DateTime(e.Timestamp.Year, e.Timestamp.Month, e.Timestamp.Day, e.Timestamp.Hour, 0, 0))
            .Select(g => (Hour: g.Key, Tokens: g.Sum(e => e.TotalTokens)))
            .OrderBy(g => g.Hour)
            .ToList();
        if (hours.Count > 0)
        {
            long max = hours.Max(h => h.Tokens);
            // 会话可能跨天:每根柱是一小时,相邻但隔天的柱之间留一道竖分隔
            double slot = Math.Max(8, 460d / hours.Count);
            double barWidth = Math.Max(3, Math.Min(30, slot * 0.7));
            for (int i = 0; i < hours.Count; i++)
            {
                double x = i * slot;
                long tokensAt = hours[i].Tokens;
                double barHeight = tokensAt == 0 ? 0 : Math.Max(2, (double)tokensAt / max * 74);
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = Frozen(Color.FromRgb(0x9A, 0x8A, 0xFF)),
                    ToolTip = $"{hours[i].Hour:MM-dd HH:00}\n{SpendFormat.TokensExact(tokensAt)} tokens"
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, 74 - barHeight);
                timeline.Children.Add(bar);
                if (i > 0 && hours[i].Hour.Date != hours[i - 1].Hour.Date)
                {
                    var divider = new Rectangle { Width = 1, Height = 74, Fill = Frozen(Color.FromArgb(0x30, 0xF5, 0xF5, 0xF7)) };
                    Canvas.SetLeft(divider, x - slot * 0.15);
                    Canvas.SetTop(divider, 0);
                    timeline.Children.Add(divider);
                }
            }
            var first = new TextBlock { Text = hours[0].Hour.ToString("MM-dd HH:00"), FontSize = 10.5, Foreground = Dim() };
            Canvas.SetLeft(first, 0);
            Canvas.SetTop(first, 78);
            timeline.Children.Add(first);
            var last = new TextBlock { Text = hours[^1].Hour.ToString("MM-dd HH:00"), FontSize = 10.5, Foreground = Dim() };
            last.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(last, Math.Max(0, hours.Count * slot - last.DesiredSize.Width));
            Canvas.SetTop(last, 78);
            timeline.Children.Add(last);
        }

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildLayout(head, modelHost, timeline) };
        Content = scroll;
    }

    private static UIElement BuildLayout(UIElement head, UIElement models, UIElement timeline) =>
        new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 20),
            Children =
            {
                head,
                SectionTitle("模型分布"),
                Card(models),
                SectionTitle("按小时"),
                Card(timeline)
            }
        };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 8)
    };

    private static Border Card(UIElement content) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E)),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14),
        Margin = new Thickness(0, 0, 0, 14),
        Child = content
    };

    private static TextBlock DimLine(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Dim(),
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };

    private static Brush Dim() => Frozen(Color.FromArgb(0x8C, 0xF5, 0xF5, 0xF7));

    private static UIElement ModelRow(string model, long tokens, double cost, long requests, TokenTally tally, long maxTokens)
    {
        double fraction = maxTokens <= 0 ? 0 : Math.Clamp((double)tokens / maxTokens, 0, 1);
        var name = new TextBlock
        {
            Text = model,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var detail = new TextBlock
        {
            Text = $"输入 {SpendFormat.Tokens(tally.Input)} · 缓存读 {SpendFormat.Tokens(tally.CacheRead)} · 输出 {SpendFormat.Tokens(tally.Output)}"
                + (tally.Input + tally.CacheWrite + tally.CacheRead > 0
                    ? $" · 命中 {(double)tally.CacheRead / (tally.Input + tally.CacheWrite + tally.CacheRead):P1}" : "")
                + $" · {requests:N0} 次",
            FontSize = 11,
            Foreground = Dim(),
            Margin = new Thickness(8, 3, 0, 0)
        };
        var bar = new Border
        {
            Background = Frozen(Color.FromRgb(0x1A, 0x1A, 0x20)),
            CornerRadius = new CornerRadius(2),
            Height = 4,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Border
            {
                Background = Frozen(Color.FromRgb(0x00, 0xE6, 0x8C)),
                CornerRadius = new CornerRadius(2),
                Height = 4,
                Width = fraction * BarBase,
                HorizontalAlignment = HorizontalAlignment.Left
            }
        };
        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(name);
        var barRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        barRow.Children.Add(bar);
        barRow.Children.Add(detail);
        left.Children.Add(barRow);

        var tokensText = new TextBlock
        {
            Text = SpendFormat.Tokens(tokens),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var costText = new TextBlock
        {
            Text = SpendFormat.Money(cost),
            FontSize = 12.5,
            MinWidth = 62,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(tokensText, 1);
        Grid.SetColumn(costText, 2);
        grid.Children.Add(left);
        grid.Children.Add(tokensText);
        grid.Children.Add(costText);
        return grid;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
