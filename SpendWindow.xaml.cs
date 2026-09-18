using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PulseWin;

/// <summary>
/// 用量统计窗口。**只读本地记录,不发任何请求**——它回答的是"这段时间的活干到哪儿去了",
/// 是个坐下来看的问题,不是瞥一眼的问题(所以它不在 rail 上)。
///
/// 数字口径见 <see cref="SpendSummary"/>;这里只负责画。
/// </summary>
public partial class SpendWindow : Window
{
    private SpendLedger? _ledger;
    private SpendSpan _span = SpendSpan.Week;
    private bool _loading;

    /// <summary>比例条的基准宽度(像素)。</summary>
    private const double BarBase = 180;

    private static readonly Brush BarModels = Frozen(Color.FromRgb(0x00, 0xE6, 0x8C));
    private static readonly Brush BarAgents = Frozen(Color.FromRgb(0x4D, 0x6B, 0xFE));
    private static readonly Brush BarProjects = Frozen(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush BarSessions = Frozen(Color.FromRgb(0x9A, 0x8A, 0xFF));
    private static readonly Brush BarEmpty = Frozen(Color.FromRgb(0x55, 0x55, 0x60));
    private static readonly Brush ChartBar = Frozen(Color.FromRgb(0x00, 0xE6, 0x8C));
    private static readonly Brush ChartZero = Frozen(Color.FromRgb(0x44, 0x44, 0x4E));

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public SpendWindow()
    {
        InitializeComponent();
        // 本机 200% DPI:760diu 的高度会顶出屏幕底部。压进工作区,内容靠滚动看。
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(320, work.Height - 40);
        MaxWidth = Math.Max(460, work.Width - 40);
        if (Height > MaxHeight) Height = MaxHeight;
        if (Width > MaxWidth) Width = MaxWidth;

        SpanWeek.IsChecked = true;
        foreach (var button in new[] { SpanToday, SpanWeek, SpanMonth, SpanAll })
            button.Checked += Span_Checked;
        Loaded += (_, _) => ReloadLedger();
    }

    private void Span_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<SpendSpan>(tag, out var span)) return;
        _span = span;
        // 换区间只重算加法,不重读库(读一次要扫两万多行)。
        if (_ledger is not null) Render(SpendSummary.Build(_ledger, _span));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ModelPrices.Invalidate();
        ReloadLedger();
    }

    private void ReloadLedger()
    {
        if (_loading) return;
        _loading = true;
        bool hasData = _ledger is not null;
        PriceSourceText.Text = hasData ? "重新读取本地记录…" : "正在读取本地记录…";

        Task.Run(() =>
        {
            var prices = ModelPrices.Current;
            var ledger = SpendLedger.Build(prices);
            return (prices, ledger);
        }).ContinueWith(task =>
        {
            _loading = false;
            if (task.IsFaulted)
            {
                PriceSourceText.Text = "读取失败:" + task.Exception?.GetBaseException().Message;
                return;
            }
            var (prices, ledger) = task.Result;
            _ledger = ledger;
            var summary = SpendSummary.Build(ledger, _span);
            PriceSourceText.Text = $"价目表:{prices.Source}({prices.ModelCount} 个模型)"
                + (prices.FetchedAt is { } at ? $",抓取于 {at:yyyy-MM-dd}" : "")
                + $"   ·   来源:{string.Join(" + ", ledger.PresentStores)}"
                + (ledger.MissingStores.Count > 0 ? $"(本机未装 {string.Join("、", ledger.MissingStores)})" : "");
            Render(summary);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Render(SpendSummary summary)
    {
        TotalTokens.Text = SpendFormat.TokensExact(summary.TotalTokens);
        TotalCost.Text = SpendFormat.Money(summary.TotalCost);

        var notes = new List<string>();
        notes.Add($"按厂商公开 API 价估算{(summary.CostIsPartial ? "(只覆盖有价的部分)" : "")},不是账单。");
        if (summary.UnpricedTokens > 0)
            notes.Add($"{summary.UnpricedModels} 个模型没有公开价,它们的 {SpendFormat.Tokens(summary.UnpricedTokens)} tokens 只计数、不计金额。");
        if (summary.UnclassifiedTokens > 0)
            notes.Add($"其中 {SpendFormat.Tokens(summary.UnclassifiedTokens)} tokens 来源只给了总量、没分类,计入总数但不计价。");
        CostNote.Text = string.Join(" ", notes);

        TallyLine.Text = $"输入 {SpendFormat.Tokens(summary.Tally.Input)}   ·   "
            + $"缓存写 {SpendFormat.Tokens(summary.Tally.CacheWrite)}   ·   "
            + $"缓存读 {SpendFormat.Tokens(summary.Tally.CacheRead)}   ·   "
            + $"输出 {SpendFormat.Tokens(summary.Tally.Output)}";

        string range = summary.Span == SpendSpan.All
            ? $"全部记录(自 {summary.FirstDay:yyyy-MM-dd} 起)"
            : $"{summary.FirstDay:yyyy-MM-dd} ~ {summary.To.AddDays(-1):yyyy-MM-dd}";
        ScopeLine.Text = $"{range}   ·   {summary.Requests:N0} 次调用   ·   {summary.Sessions} 个会话   ·   {summary.Projects} 个项目"
            + (summary.PeakHour is { } peak ? $"   ·   最忙 {peak}:00" : "");

        RenderChart(summary);
        RenderModels(summary);
        RenderList(AgentList, summary.Agents.Select(a => new RowVm
        {
            Name = a.Agent,
            Tokens = SpendFormat.Tokens(a.Tokens),
            Amount = SpendFormat.Money(a.Cost),
            Detail = $"{a.Requests:N0} 次调用",
            BarWidth = Fraction(a.Tokens, summary.Agents.Max(x => x.Tokens)) * BarBase,
            BarBrush = BarAgents
        }).ToList());
        RenderList(ProjectList, summary.ProjectRows.Take(12).Select(p => new RowVm
        {
            Name = p.Project,
            Tokens = SpendFormat.Tokens(p.Tokens),
            Amount = SpendFormat.Money(p.Cost),
            Detail = $"{p.Sessions} 个会话",
            BarWidth = Fraction(p.Tokens, summary.ProjectRows.FirstOrDefault()?.Tokens ?? 0) * BarBase,
            BarBrush = BarProjects
        }).ToList());
        RenderList(SessionList, summary.SessionRows.Take(12).Select(x => new RowVm
        {
            Name = string.IsNullOrWhiteSpace(x.Title) ? x.SessionId : x.Title!,
            Tokens = SpendFormat.Tokens(x.Tokens),
            Amount = SpendFormat.Money(x.Cost),
            Detail = $"{x.Agent} · {x.Project ?? "无项目"} · {x.Last:MM-dd HH:mm}",
            BarWidth = Fraction(x.Tokens, summary.SessionRows.FirstOrDefault()?.Tokens ?? 0) * BarBase,
            BarBrush = BarSessions,
            AmountTip = SpendFormat.MoneyExact(x.Cost)
        }).ToList());

        Footnote.Text = "数据来自本机各客户端的记录库(ZCode 的 model_usage、OpenCode 的会话消息),"
            + "按 models.dev 上厂商公开的 API 牌价折算,仅供参考;金额不是实际账单。"
            + (_ledger is { } l && l.Notes.Count > 0 ? "  读取提示:" + string.Join(";", l.Notes) : "");
    }

    private void RenderModels(SpendSummary summary)
    {
        long max = summary.Models.FirstOrDefault()?.Tokens ?? 0;
        var rows = summary.Models.Take(14).Select(m => new RowVm
        {
            Name = m.Model,
            Tokens = SpendFormat.Tokens(m.Tokens),
            Amount = SpendFormat.Money(m.Amount),
            AmountTip = m.Priced ? SpendFormat.MoneyExact(m.Amount) : "该模型没有公开牌价,只统计 token",
            Detail = $"输入 {SpendFormat.Tokens(m.Tally.Input)} · 缓存读 {SpendFormat.Tokens(m.Tally.CacheRead)} · 输出 {SpendFormat.Tokens(m.Tally.Output)}"
                + (m.Unclassified > 0 ? $" · 未分类 {SpendFormat.Tokens(m.Unclassified)}" : "")
                + (m.Priced ? "" : " · 无公开价"),
            BarWidth = Fraction(m.Tokens, max) * BarBase,
            BarBrush = m.Priced ? BarModels : BarEmpty
        }).ToList();
        RenderList(ModelList, rows);

        var unpriced = summary.Models.Where(m => !m.Priced).Select(m => m.Model).ToList();
        ModelHint.Text = summary.Models.Count == 0
            ? "这个区间里没有记录。"
            : (unpriced.Count > 0
                ? $"无公开牌价、只计 token 的模型:{string.Join("、", unpriced)}"
                : "");
    }

    private void RenderChart(SpendSummary summary)
    {
        ChartCanvas.Children.Clear();
        var days = summary.Days.ToList();
        if (days.Count == 0 || days.All(d => d.Tokens == 0))
        {
            ChartHint.Text = "这个区间里没有记录。";
            return;
        }

        double width = ChartCanvas.ActualWidth > 0 ? ChartCanvas.ActualWidth : 500;
        double height = 132;
        double labelBand = 16;
        double plotHeight = height - labelBand;
        int count = days.Count;
        double slot = width / count;
        double barWidth = Math.Max(2, Math.Min(26, slot * 0.68));
        long max = days.Max(d => d.Tokens);

        for (int i = 0; i < count; i++)
        {
            var day = days[i];
            double x = i * slot + (slot - barWidth) / 2;
            double ratio = max == 0 ? 0 : (double)day.Tokens / max;
            double barHeight = day.Tokens == 0 ? 0 : Math.Max(2, ratio * plotHeight);

            if (day.Tokens == 0)
            {
                // 整天没有调用:画一条贴底的淡线,而不是与"有量但极小"混在一起
                var zero = new Rectangle
                {
                    Width = barWidth,
                    Height = 1.5,
                    Fill = ChartZero,
                    ToolTip = $"{day.Day:yyyy-MM-dd}\n没有记录"
                };
                Canvas.SetLeft(zero, x);
                Canvas.SetTop(zero, plotHeight - 1.5);
                ChartCanvas.Children.Add(zero);
                continue;
            }

            var bar = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = ChartBar,
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = $"{day.Day:yyyy-MM-dd}\n{SpendFormat.TokensExact(day.Tokens)} tokens\n{SpendFormat.MoneyExact(day.Cost)}"
                    + (day.HasUnpriced ? "\n(含无公开价的模型)" : "")
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotHeight - barHeight);
            ChartCanvas.Children.Add(bar);
        }

        // 底部只标首/中/尾三个日期,免得挤成一团
        foreach (var index in new[] { 0, count / 2, count - 1 }.Distinct())
        {
            var label = new TextBlock
            {
                Text = days[index].Day.ToString("MM-dd"),
                FontSize = 10.5,
                Foreground = Frozen(Color.FromArgb(0x8C, 0xF5, 0xF5, 0xF7))
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = index * slot + (slot - label.DesiredSize.Width) / 2;
            x = Math.Max(0, Math.Min(width - label.DesiredSize.Width, x));
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, plotHeight + 2);
            ChartCanvas.Children.Add(label);
        }

        long total = days.Sum(d => d.Tokens);
        var peak = days.OrderByDescending(d => d.Tokens).First();
        ChartHint.Text = $"峰值 {peak.Day:MM-dd}({SpendFormat.Tokens(peak.Tokens)})"
            + $"   ·   区间合计 {SpendFormat.Tokens(total)}"
            + $"   ·   日均 {SpendFormat.Tokens(total / Math.Max(1, days.Count))}";
    }

    private static void RenderList(ItemsControl target, List<RowVm> rows)
    {
        target.ItemsSource = rows;
        if (rows.Count == 0)
        {
            target.ItemsSource = new List<RowVm>
            {
                new() { Name = "—", Tokens = "", Amount = "", Detail = "没有记录", BarWidth = 0 }
            };
        }
    }

    private static double Fraction(long value, long max) =>
        max <= 0 ? 0 : Math.Clamp((double)value / max, 0, 1) * 1.0;

    /// <summary>列表行。<see cref="BarWidth"/> 已算成像素,绑定直接用。</summary>
    public sealed class RowVm
    {
        public string Name { get; init; } = "";
        public string Tokens { get; init; } = "";
        public string Amount { get; init; } = "";
        public string AmountTip { get; init; } = "";
        public string Detail { get; init; } = "";
        public double BarWidth { get; init; }
        public Brush BarBrush { get; init; } = Brushes.Gray;
    }
}
