using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private static readonly Brush BarModels = Frozen(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly Brush BarAgents = Frozen(Color.FromRgb(0x5E, 0x5C, 0xE6));
    private static readonly Brush BarProjects = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush BarSessions = Frozen(Color.FromRgb(0xBF, 0x5A, 0xF2));
    private static readonly Brush BarEmpty = Frozen(Color.FromRgb(0xC7, 0xC7, 0xCC));
    private static readonly Brush ChartBar = Frozen(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly Brush ChartZero = Frozen(Color.FromRgb(0xE5, 0xE5, 0xEA));

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

        // 牌价表过期就在后台拉一份新的(上游规矩:失败继续用旧的、五分钟后再试)。
        // **不阻塞这次渲染**——界面照旧用现有表出数,拉到新的再重算一遍。
        ModelPricesUpdater.RefreshIfStale(() =>
        {
            // 价目变了:账本里的金额是按旧价算的,必须作废重来,否则窗口与"模型"页
            // 会拿两份不同价的账本。
            SpendLedgerCache.Invalidate();
            // 这个回调在**后台线程**上跑,而且可能晚于窗口关闭(网络慢时尤甚),
            // 也可能晚于整个应用退出(诊断出图就是这种)。两者都让 BeginInvoke 抛
            // InvalidOperationException,所以先确认还活着再排队。
            if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(ReloadLedger); }
            catch (InvalidOperationException) { /* 刚好在退出:不再重算,窗口都要没了 */ }
        });

        bool hasData = _ledger is not null;
        PriceSourceText.Text = hasData ? "重新读取本地记录…" : "正在读取本地记录…";

        // **走共享账本**:与设置窗的"模型"页共用同一份(引用计数,谁都没用时才释放),
        // 不再各建各的——构建一次要扫两个 SQLite 库,两份就是白翻一倍内存与 CPU。
        SpendLedgerCache.AcquireAsync().ContinueWith(task =>
        {
            _loading = false;
            if (task.IsFaulted)
            {
                PriceSourceText.Text = "读取失败:" + task.Exception?.GetBaseException().Message;
                return;
            }
            var ledger = task.Result;

            // 等待期间窗口可能已经关了:那份引用必须立刻还掉,否则关窗后内存不回落。
            if (_closed)
            {
                SpendLedgerCache.Release();
                return;
            }

            if (_holdsLedgerRef) SpendLedgerCache.Release();   // 还上一次,避免计数虚高
            _holdsLedgerRef = true;
            _ledger = ledger;
            var prices = ModelPrices.Current;
            var summary = SpendSummary.Build(ledger, _span);
            PriceSourceText.Text = $"价目表:{prices.Source}({prices.ModelCount} 个模型)"
                + (prices.FetchedAt is { } at ? $",抓取于 {at:yyyy-MM-dd}" : "")
                + $"   ·   来源:{string.Join(" + ", ledger.PresentStores)}"
                + (ledger.MissingStores.Count > 0 ? $"(本机未装 {string.Join("、", ledger.MissingStores)})" : "");
            Render(summary);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool _holdsLedgerRef;
    private bool _closed;

    private void Render(SpendSummary summary)
    {
        TotalTokens.Text = SpendFormat.TokensExact(summary.TotalTokens);
        TotalCost.Text = SpendFormat.Amount(summary.TotalCost, summary.CostIsPartial);

        var notes = new List<string>();
        double rate = AppSettings.Current.UsdToCny;
        notes.Add($"按厂商公开 API 价估算{(summary.CostIsPartial ? "(只覆盖有价的部分)" : "")},不是账单。"
            + (rate > 0 ? $"金额按 $1 ≈ ¥{rate:0.00} 换算显示。" : "金额为美元原值。"));
        if (summary.UnpricedTokens > 0)
            notes.Add($"{summary.UnpricedModels} 个模型没有公开价,它们的 {SpendFormat.Tokens(summary.UnpricedTokens)} tokens 只计数、不计金额;"
                + "下面带 * 的金额是有价部分的小计,显示 — 的算不出金额。");
        if (summary.UnclassifiedTokens > 0)
            notes.Add($"其中 {SpendFormat.Tokens(summary.UnclassifiedTokens)} tokens 来源只给了总量、没分类,计入总数但不计价。");
        CostNote.Text = string.Join(" ", notes);

        // 缓存写恒为 0 容易被当成 bug(这是个显眼的数字)。本机 ZCode / OpenCode 的记录里
        // 就没有 cache-creation 这一项,不是算漏了——标注在数字旁边,别让用户去查。
        bool noCacheWrite = summary.Tally.CacheWrite == 0 && summary.Tally.CacheRead > 0;
        TallyLine.Text = $"输入 {SpendFormat.Tokens(summary.Tally.Input)}   ·   "
            + $"缓存写 {(noCacheWrite ? "0(本机源未提供)" : SpendFormat.Tokens(summary.Tally.CacheWrite))}   ·   "
            + $"缓存读 {SpendFormat.Tokens(summary.Tally.CacheRead)}   ·   "
            + $"输出 {SpendFormat.Tokens(summary.Tally.Output)}"
            + (summary.CacheHit is { } hit ? $"   ·   缓存命中 {hit:P1}" : "");

        string range = summary.Span == SpendSpan.All
            ? $"全部记录(自 {summary.FirstDay:yyyy-MM-dd} 起)"
            : $"{summary.FirstDay:yyyy-MM-dd} ~ {summary.To.AddDays(-1):yyyy-MM-dd}";
        ScopeLine.Text = $"{range}   ·   {summary.Requests:N0} 次调用   ·   {summary.Sessions} 个会话   ·   {summary.Projects} 个项目"
            + (summary.PeakHour is { } peak ? $"   ·   最忙 {peak}:00" : "");

        RenderChart(summary);
        RenderHeatmap();
        RenderModels(summary);
        RenderList(AgentList, summary.Agents.Select(a => new RowVm
        {
            Name = a.Agent,
            Tokens = SpendFormat.Tokens(a.Tokens),
            Amount = SpendFormat.Amount(a.Cost, a.HasUnpriced),
            AmountTip = SpendFormat.AmountTip(a.Cost, a.HasUnpriced),
            Detail = $"{a.Requests:N0} 次调用",
            BarWidth = Fraction(a.Tokens, summary.Agents.Max(x => x.Tokens)) * BarBase,
            BarBrush = BarAgents
        }).ToList());
        RenderList(ProjectList, summary.ProjectRows.Take(12).Select(p => new RowVm
        {
            Name = p.Project,
            Tokens = SpendFormat.Tokens(p.Tokens),
            Amount = SpendFormat.Amount(p.Cost, p.HasUnpriced),
            AmountTip = SpendFormat.AmountTip(p.Cost, p.HasUnpriced),
            Detail = p.Sessions > 0 || !p.HasArchivedDetail
                ? $"{p.Sessions} 个会话"
                : "明细已归档进本地仓库",
            BarWidth = Fraction(p.Tokens, summary.ProjectRows.FirstOrDefault()?.Tokens ?? 0) * BarBase,
            BarBrush = BarProjects
        }).ToList());
        RenderList(SessionList, summary.SessionRows.Take(12).Select(x => new RowVm
        {
            Name = string.IsNullOrWhiteSpace(x.Title) ? x.SessionId : x.Title!,
            Tokens = SpendFormat.Tokens(x.Tokens),
            Amount = SpendFormat.Amount(x.Cost, x.HasUnpriced),
            Detail = $"{x.Agent} · {x.Project ?? "无项目"} · {x.Last:MM-dd HH:mm}",
            BarWidth = Fraction(x.Tokens, summary.SessionRows.FirstOrDefault()?.Tokens ?? 0) * BarBase,
            BarBrush = BarSessions,
            AmountTip = SpendFormat.AmountTip(x.Cost, x.HasUnpriced),
            SessionId = x.SessionId
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
            Amount = SpendFormat.Amount(m.Amount ?? 0, m.UnpricedTokens > 0),
            AmountTip = SpendFormat.AmountTip(m.Amount ?? 0, m.UnpricedTokens > 0),
            Detail = $"输入 {SpendFormat.Tokens(m.Tally.Input)} · 缓存读 {SpendFormat.Tokens(m.Tally.CacheRead)} · 输出 {SpendFormat.Tokens(m.Tally.Output)}"
                + (m.CacheHit is { } hit ? $" · 命中 {hit:P1}" : "")
                + (m.Unclassified > 0 ? $" · 未分类 {SpendFormat.Tokens(m.Unclassified)}" : "")
                + (m.UnpricedTokens > 0 ? $" · 其中 {SpendFormat.Tokens(m.UnpricedTokens)} 无公开价" : ""),
            BarWidth = Fraction(m.Tokens, max) * BarBase,
            BarBrush = m.Priced ? BarModels : BarEmpty
        }).ToList();
        RenderList(ModelList, rows);

        var unpriced = summary.Models.Where(m => m.UnpricedTokens > 0).Select(m => m.Model).ToList();
        ModelHint.Text = summary.Models.Count == 0
            ? "这个区间里没有记录。"
            : (unpriced.Count > 0
                ? $"有算不出价的 token 的模型(金额只是有价部分):{string.Join("、", unpriced)}"
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
                Foreground = Frozen(Color.FromRgb(0x86, 0x86, 0x8B))
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

    /// <summary>
    /// 活动热图(GitHub 贡献格):列 = 周、行 = 周一~周日,颜色越亮当天 token 越多。
    /// **不受区间选择影响**——它回答的是"这段时间的作息/节奏",区间按钮切到"今天"
    /// 时只剩一格就没意思了,所以永远画全量记录(受窗口宽度限制取最近的周数)。
    /// </summary>
    private void RenderHeatmap()
    {
        HeatCanvas.Children.Clear();
        if (_ledger is null || _ledger.Entries.Count == 0)
        {
            HeatHint.Text = "还没有记录。";
            return;
        }

        var byDay = new Dictionary<DateOnly, (long Tokens, double Cost)>();
        foreach (var entry in _ledger.Entries)
        {
            if (entry.Kind == SpendAggKind.Hour) continue;
            var acc = byDay.TryGetValue(entry.Day, out var a) ? a : (0L, 0d);
            byDay[entry.Day] = (acc.Item1 + entry.TotalTokens, acc.Item2 + entry.Cost.Total);
        }
        if (byDay.Count == 0)
        {
            HeatHint.Text = "还没有记录。";
            return;
        }

        double cell = 12, gap = 3, step = cell + gap;
        int fitColumns = Math.Max(1, (int)(HeatCanvas.ActualWidth > 0 ? HeatCanvas.ActualWidth / step : 33));
        var today = DateTime.Today;
        // 本周周一为最后一列;往回数 weekColumns 列
        var lastMonday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        // 列数 = 数据实际跨度覆盖的周数,并以窗口宽度为上限。数据只有 5 周时不该硬画
        // 33 列、把左边 28 列全留成空格(本机实测就是这样:2~7 月一片空、记录全挤在右端)。
        var firstDataDay = byDay.Keys.Min();
        var firstDataMonday = firstDataDay.ToDateTime(TimeOnly.MinValue)
            .AddDays(-((int)firstDataDay.DayOfWeek + 6) % 7);
        int spanColumns = (int)((lastMonday - firstDataMonday).TotalDays / 7) + 1;
        int weekColumns = Math.Clamp(Math.Min(fitColumns, Math.Max(spanColumns, 1)), 1, 60);
        var firstMonday = lastMonday.AddDays(-7 * (weekColumns - 1));
        // 数据周数少于窗口宽度时,把整块水平居中——否则要么全挤在左(改前)、要么全挤在右,
        // 空的那一半看起来像"内容没画出来"。
        double usedW = weekColumns * step - gap;
        double offsetX = Math.Max(0, ((HeatCanvas.ActualWidth > 0 ? HeatCanvas.ActualWidth : usedW) - usedW) / 2);
        long max = byDay.Values.Max(v => v.Tokens);
        var monthLabels = new List<(int Column, string Label)>();

        for (int col = 0; col < weekColumns; col++)
        {
            for (int row = 0; row < 7; row++)
            {
                var day = firstMonday.AddDays(col * 7 + row);
                if (day > today) break;
                var key = DateOnly.FromDateTime(day);
                long tokens = byDay.TryGetValue(key, out var v) ? v.Tokens : 0;

                var rect = new Rectangle
                {
                    Width = cell,
                    Height = cell,
                    RadiusX = 2.5,
                    RadiusY = 2.5,
                    Fill = HeatBrush(tokens, max)
                };
                if (tokens > 0)
                {
                    rect.ToolTip = $"{day:yyyy-MM-dd}\n{SpendFormat.TokensExact(tokens)} tokens\n{SpendFormat.MoneyExact(byDay[key].Cost)}";
                }
                else
                {
                    rect.ToolTip = $"{day:yyyy-MM-dd}\n没有记录";
                    rect.Stroke = Frozen(Color.FromRgb(0xD8, 0xD8, 0xDC));
                    rect.StrokeThickness = 0.5;
                }
                Canvas.SetLeft(rect, offsetX + col * step);
                Canvas.SetTop(rect, row * step);
                HeatCanvas.Children.Add(rect);
            }

            // 列首恰逢月初(或第一列)时,在顶部标月份
            var colFirst = firstMonday.AddDays(col * 7);
            if (colFirst.Month != (col > 0 ? firstMonday.AddDays((col - 1) * 7).Month : 0) || col == 0)
                monthLabels.Add((col, colFirst.ToString("M月")));
        }

        foreach (var (column, label) in monthLabels)
        {
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Foreground = Frozen(Color.FromRgb(0x86, 0x86, 0x8B))
            };
            Canvas.SetLeft(labelBlock, offsetX + column * step);
            Canvas.SetTop(labelBlock, 7 * step - 2);
            HeatCanvas.Children.Add(labelBlock);
        }

        HeatHint.Text = $"共 {byDay.Count} 天有记录   ·   最忙 {byDay.Aggregate((a, b) => a.Value.Tokens > b.Value.Tokens ? a : b).Key:yyyy-MM-dd}"
            + $"   ·   (格色越亮当天用量越大)";
    }

    private static Brush HeatBrush(long tokens, long max)
    {
        Color color = max <= 0 || tokens <= 0
            ? Color.FromRgb(0xED, 0xED, 0xF0)
            : (tokens * 4d / max) switch
            {
                < 1 => Color.FromRgb(0xD2, 0xF0, 0xE1),
                < 2 => Color.FromRgb(0xA4, 0xE2, 0xC3),
                < 3 => Color.FromRgb(0x5F, 0xCD, 0x9E),
                _ => Color.FromRgb(0x00, 0xA8, 0x5C)
            };
        return Frozen(color);
    }

    /// <summary>点"最近会话"的行 → 弹该会话的下钻窗口。</summary>
    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_ledger is null) return;
        if (sender is not FrameworkElement { DataContext: RowVm { SessionId: { } sessionId } }) return;

        // 必须从 LiveEntries(源库原始明细)取:明细几乎全部进了用量仓库的聚合,
        // Entries 里只剩聚合行和老于灌入窗口的散行——从 Entries 找会话永远是空的。
        var entries = _ledger.LiveEntries
            .Where(en => en.Kind == SpendAggKind.Live && en.SessionId == sessionId)
            .ToList();
        if (entries.Count == 0) return;
        try
        {
            var window = new SessionWindow(entries);
            window.Owner = this;
            window.Show();
        }
        catch (Exception ex)
        {
            Diagnostics.Note("打开会话下钻失败", ex);
        }
    }

    private static double Fraction(long value, long max) =>
        max <= 0 ? 0 : Math.Clamp((double)value / max, 0, 1) * 1.0;

    /// <summary>关窗时还掉共享账本的引用(计数归零时账本释放,内存立刻回落)。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_holdsLedgerRef)
        {
            _holdsLedgerRef = false;
            SpendLedgerCache.Release();
        }
        _ledger = null;
        base.OnClosed(e);
    }

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

        /// <summary>非空 = 这行可以点开下钻(最近会话);null = 普通行,箭头光标。</summary>
        public string? SessionId { get; init; }

        public System.Windows.Input.Cursor RowCursor =>
            SessionId is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;
    }
}
