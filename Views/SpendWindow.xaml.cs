using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PulseWin;

/// <summary>
/// 用量窗口:上半是**管理**(每个订阅还剩多少、什么时候重置),下半是**分析**
/// (这段时间的活干到哪儿去了、钱花在哪、点进去看某一天)。只读本地记录 + 悬浮环
/// 已同步好的额度快照,自己不发任何请求。
///
/// 数字口径见 <see cref="SpendSummary"/>;这里只负责画。
/// </summary>
public partial class SpendWindow : Window
{
    private SpendLedger? _ledger;
    private SpendSpan _span = SpendSpan.Week;
    private bool _loading;
    private bool _closed;

    // —— 视图状态 ——
    private List<ChannelUsage> _channels = [];
    private ChannelUsage? _selectedChannel;
    private bool _modelsByCost;
    /// <summary>非空 = 正在下钻看某一天(点柱图/热图进来)。</summary>
    private DateOnly? _dayFilter;
    /// <summary>"自定义"区间生效中(应用后为真;只点开面板不算)。</summary>
    private bool _customActive;
    private DateTime _customFrom, _customTo;

    /// <summary>额度条数据(悬浮环已同步好的快照)。</summary>
    private List<SubData> _subs = [];

    /// <summary>比例条的基准宽度(像素)。</summary>
    private const double BarBase = 180;

    /// <summary>GOAT 的峰值计费时段(本地小时;记忆口径:北京 09–12、14–18)。</summary>
    private static readonly int[] PeakHours = { 9, 10, 11, 14, 15, 16, 17 };

    private static readonly Brush BarModels = Frozen(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly Brush BarAgents = Frozen(Color.FromRgb(0x5E, 0x5C, 0xE6));
    private static readonly Brush BarProjects = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush BarSessions = Frozen(Color.FromRgb(0xBF, 0x5A, 0xF2));
    private static readonly Brush BarEmpty = Frozen(Color.FromRgb(0xC7, 0xC7, 0xCC));
    private static readonly Brush ChartBar = Frozen(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly Brush ChartZero = Frozen(Color.FromRgb(0xE5, 0xE5, 0xEA));
    private static readonly Brush PeakBar = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    // 套餐页从设置窗搬来,那里用主题资源,这里用同色值的固定画刷
    private static readonly Brush TextMain = Frozen(Color.FromRgb(0x1D, 0x1D, 0x1F));
    private static readonly Brush TextSub = Frozen(Color.FromRgb(0x86, 0x86, 0x8B));
    private static readonly Brush FieldBack = Frozen(Color.FromRgb(0xE8, 0xE8, 0xEC));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly Brush Separator = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEF));
    private static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x00, 0xA8, 0x5C));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush BadBrush = Frozen(Color.FromRgb(0xE0, 0x48, 0x3E));

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public SpendWindow()
    {
        InitializeComponent();
        // 本机 200% DPI:窗口高度压进工作区,内容靠滚动看。
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(320, work.Height - 40);
        MaxWidth = Math.Max(480, work.Width - 40);
        if (Height > MaxHeight) Height = MaxHeight;
        if (Width > MaxWidth) Width = MaxWidth;

        SpanWeek.IsChecked = true;
        ModelSortTokens.IsChecked = true;
        foreach (var button in new[] { SpanToday, SpanWeek, SpanMonth, SpanCustom, SpanAll })
            button.Checked += Span_Checked;
        Loaded += (_, _) => ReloadLedger();
    }

    // ————————————————— 数据加载 —————————————————

    private void Span_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (tag == "Custom") return;   // "自定义"只展开日期面板,等"应用"才生效
        if (!Enum.TryParse<SpendSpan>(tag, out var span)) return;
        _span = span;
        _customActive = false;
        _dayFilter = null;
        // 换区间只重算加法,不重读库(读一次要扫两万多行)。
        RenderAll();
    }

    private void SpanCustom_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        CustomRangePanel.Visibility = Visibility.Visible;
    }

    private void SpanCustom_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        CustomRangePanel.Visibility = Visibility.Collapsed;
    }

    private void CustomApply_Click(object sender, RoutedEventArgs e)
    {
        if (CustomFrom.SelectedDate is not { } from || CustomTo.SelectedDate is not { } to) return;
        if (to < from) (from, to) = (to, from);
        _customFrom = from.Date;
        _customTo = to.Date;
        _customActive = true;
        _dayFilter = null;
        RenderAll();
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
            // 价目变了:账本里的金额是按旧价算的,必须作废重来,否则窗口与套餐页
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

        // **走共享账本**:与设置窗的数据源页共用同一份(引用计数,谁都没用时才释放),
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
            if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(RenderAll); }
            catch (InvalidOperationException) { }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool _holdsLedgerRef;

    /// <summary>当前生效的区间(自定义优先;下钻不受它管)。</summary>
    private (DateTime From, DateTime To) EffectiveRange() =>
        _customActive
            ? (_customFrom, _customTo.AddDays(1))
            : SpendSummary.Range(_span, DateTime.Now);

    // ————————————————— 渲染总流程 —————————————————

    private void RenderAll()
    {
        if (_ledger is null) return;

        // 顶部状态行("正在读取…")必须在这里改写成结果——v1.12.0 重写时漏了这一步,
        // 表现成"一直在重新读取、一直没成功"(其实数据早加载好了,只是文案没更新)。
        var prices = ModelPrices.Current;
        PriceSourceText.Text = $"价目表:{prices.Source}({prices.ModelCount} 个模型)"
            + (prices.FetchedAt is { } at ? $",抓取于 {at:yyyy-MM-dd}" : "")
            + $"   ·   来源:{string.Join(" + ", _ledger.PresentStores)}"
            + (_ledger.MissingStores.Count > 0 ? $"(本机未装 {string.Join("、", _ledger.MissingStores)})" : "");

        // 渠道列表(tab 要显示每个套餐的用量徽标;区间变了要跟着重算)
        _channels = ChannelUsageIndex.Build(_ledger, _span);
        // 区间变了尽量停在同一个套餐上
        if (_selectedChannel is { } keep)
            _selectedChannel = _channels.FirstOrDefault(c => c.Key == keep.Key);

        // 额度快照(磁盘小文件,每次刷新顺手重读,悬浮环那边每分钟落一次盘)
        _subs = SnapshotSource.LoadAllSources();

        RenderViewTabs();
        RenderQuotaBar();

        if (_selectedChannel is { } ch)
            SelectChannel(ch);
        else
            Render(_dayFilter is { } day
                ? SpendSummary.Build(_ledger, day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(TimeOnly.MinValue).AddDays(1))
                : SpendSummary.Build(_ledger, EffectiveRange().From, EffectiveRange().To));
    }

    // ————————————————— 视图切换 —————————————————

    /// <summary>正在重建 tab 行:此时设置 IsChecked 触发的 Checked 不能走点击路径(会递归重建)。</summary>
    private bool _syncingTabs;

    private void RenderViewTabs()
    {
        _syncingTabs = true;
        TabRow.Children.Clear();
        AddTab("总览", isActive: _selectedChannel is null, () =>
        {
            _selectedChannel = null;
            _dayFilter = null;
            SwitchView();
        });
        foreach (var ch in _channels)
        {
            // 本区间没有用量的套餐:按钮上直接写"未启用/无用量",而不是显示 0——
            // 订阅过、以后还会订阅的套餐不该看起来像"数据丢了"。
            string badge = ch.HasData
                ? SpendFormat.Tokens(ch.TotalTokens)
                : (ch.Enabled == false ? "未启用" : "无用量");
            var captured = ch;
            AddTab($"{ch.Name}  {badge}", isActive: _selectedChannel?.Key == ch.Key, () =>
            {
                _selectedChannel = captured;
                _dayFilter = null;
                SwitchView();
            });
        }
        _syncingTabs = false;
    }

    private void AddTab(string text, bool isActive, Action onClick)
    {
        var tab = new RadioButton
        {
            Style = (Style)FindResource("SpanToggle"),
            Content = text,
            GroupName = "ViewTab",
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 12.5,
        };
        tab.Checked += (_, _) =>
        {
            if (_syncingTabs) return;
            onClick();
        };
        tab.Unchecked += (_, _) => { };
        TabRow.Children.Add(tab);
        if (isActive)
        {
            _syncingTabs = true;
            try { tab.IsChecked = true; }
            finally { _syncingTabs = false; }
        }
    }

    private void SwitchView()
    {
        bool overview = _selectedChannel is null;
        OverviewPanel.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        ChannelPanel.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        DayFilterRow.Visibility = Visibility.Collapsed;   // 下钻只属于总览
        RenderViewTabs();
        RenderAll();
        // 切页回到顶部,别让用户停在上一个视图的滚动位置上
        if (TemplatedControlScroll() is { } sv) sv.ScrollToTop();
    }

    /// <summary>窗口的滚动容器(XAML 里是匿名 ScrollViewer,按名字找不到,这里扫可视树)。</summary>
    private ScrollViewer? TemplatedControlScroll()
    {
        return FindDescendants(this).OfType<ScrollViewer>().FirstOrDefault();
    }

    private static IEnumerable<DependencyObject> FindDescendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var sub in FindDescendants(child))
                yield return sub;
        }
    }

    // ————————————————— 额度条 —————————————————

    private void RenderQuotaBar()
    {
        QuotaPanel.Children.Clear();
        bool bigModelRow = RenderBigModelRow(QuotaPanel);

        if (_subs.Count == 0 && !bigModelRow)
        {
            QuotaHint.Text = "还没有额度读数:悬浮环第一次同步成功后,这里会列出每个订阅的剩余与重置时间。";
            return;
        }

        foreach (var sub in _subs)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = sub.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = TextMain,
            });

            var pools = new StackPanel();
            // 有读数的池在前;没读数(服务端没返回)的池不占行
            foreach (var pool in sub.Pools.Where(p => p.HasReading))
                pools.Children.Add(BuildPoolLine(pool));
            if (pools.Children.Count == 0)
                pools.Children.Add(new TextBlock
                {
                    Text = sub.FetchedAt is { } at
                        ? $"快照 {at.LocalDateTime:HH:mm},但接口没有返回可用额度。"
                        : "还没有快照。",
                    FontSize = 11.5, Foreground = TextSub,
                });
            Grid.SetColumn(pools, 1);
            row.Children.Add(pools);
            QuotaPanel.Children.Add(row);
        }

        QuotaHint.Text = "额度读数来自悬浮环的同步(约每分钟一次),是官方接口的数字,不是估算;"
            + "BigModel 包月一行是按公开牌价估算的金额,与包月费无关。";
    }

    /// <summary>一条 pool 读数:标签 + 进度条 + 百分比/金额 + 重置倒计时。</summary>
    private FrameworkElement BuildPoolLine(PoolData pool)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock
        {
            Text = pool.Label,
            FontSize = 11.5,
            Width = 46,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSub,
        });

        if (pool.HasPercent)
        {
            line.Children.Add(BuildBar(pool.Fraction, 132));
            line.Children.Add(new TextBlock
            {
                Text = $" {pool.PercentText} · 已用 {Money.Short(pool.Used ?? 0, pool.Unit)}"
                    + (pool.Cap is { } cap ? $" / {Money.Short(cap, pool.Unit)}" : ""),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMain,
            });
        }
        else if (pool.Amount is { } amount)
        {
            // 余额型(DeepSeek):没有"用了多少",钱本身就是读数
            line.Children.Add(new TextBlock
            {
                Text = Money.Exact(amount, pool.Unit)
                    + (pool.GrantedAmount > 0 ? $"(含赠送 {Money.Short(pool.GrantedAmount.Value, pool.Unit)})" : ""),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMain,
            });
        }

        string remain = pool.PoolKind == "Balance" ? "" : " · " + pool.RemainingText();
        line.Children.Add(new TextBlock
        {
            Text = remain,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSub,
        });
        return line;
    }

    /// <summary>比例条:底轨 + 填充,颜色按用量分档(&lt;60% 绿、&lt;85% 橙、其余红)。</summary>
    private FrameworkElement BuildBar(double fraction, double width)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = FieldBack,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = fraction >= 0.85 ? BadBrush : fraction >= 0.6 ? WarnBrush : GoodBrush,
                Width = Math.Max(2, width * fraction),
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        return track;
    }

    /// <summary>
    /// BigModel 包月行:没有配额接口,只有"按牌价估的金额 vs 自己填的包月预算"。
    /// 返回这行有没有画出来(没记录也没预算时什么都不画)。
    /// </summary>
    private bool RenderBigModelRow(Panel host)
    {
        if (_ledger is null) return false;
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var month = SpendSummary.Build(_ledger, monthStart, monthStart.AddMonths(1));
        double rate = AppSettings.Current.UsdToCny;
        if (rate <= 0) rate = 1;
        double spentCny = month.TotalCost * rate;
        if (spentCny <= 0 && AppSettings.Current.BigModelMonthlyBudgetCny is null) return false;

        double? budget = AppSettings.Current.BigModelMonthlyBudgetCny;
        int dayOfMonth = (today - monthStart).Days + 1;
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        double projected = spentCny / dayOfMonth * daysInMonth;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = "BigModel 包月",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = TextMain,
        });

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock
        {
            Text = "本月估算",
            FontSize = 11.5,
            Width = 46,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSub,
            ToolTip = $"本月(自 {monthStart:yyyy-MM-dd})按公开牌价估算 {SpendFormat.TokensExact(month.TotalTokens)} tokens,\n"
                + "BigModel 不提供用量接口,这个数来自本机记录,不是账单。",
        });
        string value;
        if (budget is { } b)
        {
            line.Children.Add(BuildBar(b > 0 ? spentCny / b : 0, 132));
            value = $" ¥{spentCny:N0} / 预算 ¥{b:N0}";
        }
        else
        {
            value = $" ¥{spentCny:N0}";
        }
        line.Children.Add(new TextBlock
        {
            Text = value + $" · 照当前速度月底约 ¥{projected:N0}",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextMain,
        });
        Grid.SetColumn(line, 1);
        row.Children.Add(line);
        host.Children.Add(row);
        return true;
    }

    // ————————————————— 总览渲染 —————————————————

    private void Render(SpendSummary summary)
    {
        TotalTokens.Text = SpendFormat.Tokens(summary.TotalTokens);
        // 中文单位是给眼睛的,精确值留在悬停里
        TotalTokens.ToolTip = $"{SpendFormat.TokensExact(summary.TotalTokens)} tokens";
        TotalCost.Text = SpendFormat.Amount(summary.TotalCost, summary.CostIsPartial);
        TotalCost.ToolTip = SpendFormat.AmountTip(summary.TotalCost, summary.CostIsPartial);

        RenderDelta(summary);

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

        if (_dayFilter is { } day)
        {
            ScopeLine.Text = $"{day:yyyy-MM-dd} 单日   ·   {summary.Requests:N0} 次调用   ·   {summary.Sessions} 个会话   ·   {summary.Projects} 个项目"
                + (summary.PeakHour is { } peak ? $"   ·   最忙 {peak}:00" : "");
        }
        else
        {
            var (from, to) = EffectiveRange();
            // 注意:任意区间重载走的是 SpendSpan.All 元数据,这里必须看 _span 而不是 summary.Span,
            // 否则"近 7 天"会被显示成"全部记录"。
            string range = _customActive
                ? $"{from:yyyy-MM-dd} ~ {to.AddDays(-1):yyyy-MM-dd}(自定义)"
                : _span == SpendSpan.All
                    ? $"全部记录(自 {summary.FirstDay:yyyy-MM-dd} 起)"
                    : $"{summary.FirstDay:yyyy-MM-dd} ~ {to.AddDays(-1):yyyy-MM-dd}";
            ScopeLine.Text = $"{range}   ·   {summary.Requests:N0} 次调用   ·   {summary.Sessions} 个会话   ·   {summary.Projects} 个项目"
                + (summary.PeakHour is { } peak ? $"   ·   最忙 {peak}:00" : "");
        }

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

    /// <summary>环比:本期 vs 等长的上一周期。回答"这周烧得是不是更凶了"。</summary>
    private void RenderDelta(SpendSummary summary)
    {
        if (_ledger is null) { DeltaLine.Text = ""; return; }

        (DateTime From, DateTime To, string Label)? prev;
        if (_dayFilter is { } day)
            prev = (day.ToDateTime(TimeOnly.MinValue).AddDays(-1), day.ToDateTime(TimeOnly.MinValue), $"前一天({day.AddDays(-1):MM-dd})");
        else if (_customActive)
        {
            var (f, t) = EffectiveRange();
            prev = (f - (t - f), f, "上一周期");
        }
        else if (SpendSummary.PreviousRange(_span, DateTime.Now) is { } r)
            prev = (r.From, r.To, "上一周期");
        else
            prev = null;

        if (prev is not { } p) { DeltaLine.Text = ""; return; }

        var before = SpendSummary.Build(_ledger, p.From, p.To);
        DeltaLine.Text = $"较{p.Label}({p.From:MM-dd}~{p.To.AddDays(-1):MM-dd}):"
            + $" tokens {Delta(before.TotalTokens, summary.TotalTokens)}"
            + $" · 金额 {Delta(before.TotalCost, summary.TotalCost)}"
            + (before.TotalTokens == 0 && summary.TotalTokens > 0 ? "(上期没有记录)" : "");
    }

    private static string Delta(double before, double now)
    {
        if (before <= 0) return now <= 0 ? "持平" : "新增";
        double pct = (now - before) / before * 100;
        return (pct >= 0 ? "+" : "") + pct.ToString("0.0") + "%";
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

    private static void RenderList(ItemsControl target, List<RowVm> rows)
    {
        target.ItemsSource = rows.Count > 0 ? rows : null;
    }

    // ————————————————— 每日柱图(自动降采样 + 点击下钻) —————————————————

    private void RenderChart(SpendSummary summary)
    {
        ChartCanvas.Children.Clear();
        var days = summary.Days.ToList();
        if (days.Count == 0 || days.All(d => d.Tokens == 0))
        {
            ChartHint.Text = "这个区间里没有记录。";
            return;
        }

        // 天数一多逐日柱就是一堵墙:62 天以上按周聚合(周一为界)、730 天以上按月。
        // 聚合桶不可点(下钻只对"一天"有意义),悬停里给全范围。
        List<(DateOnly Start, DateOnly End, long Tokens, double Cost, bool HasUnpriced)> bars;
        string unit;
        if (days.Count > 730)
        {
            bars = Bucket(days, d => new DateOnly(d.Year, d.Month, 1), b => b.AddMonths(1).AddDays(-1));
            unit = "月";
        }
        else if (days.Count > 62)
        {
            bars = Bucket(days, d => d.AddDays(-((int)d.DayOfWeek + 6) % 7), b => b.AddDays(6));
            unit = "周";
        }
        else
        {
            bars = days.Select(d => (d.Day, d.Day, d.Tokens, d.Cost, d.HasUnpriced)).ToList();
            unit = "天";
        }
        ChartHint.Text = unit == "天"
            ? "点柱子可以看那一天的明细。"
            : $"天数较多,已按{unit}聚合(峰值 {SpendFormat.Tokens(bars.Max(b => b.Tokens))})。";

        double width = ChartCanvas.ActualWidth > 0 ? ChartCanvas.ActualWidth : 500;
        double height = 132;
        double labelBand = 16;
        double plotHeight = height - labelBand;
        int count = bars.Count;
        double slot = width / count;
        double barWidth = Math.Max(2, Math.Min(26, slot * 0.68));
        long max = bars.Max(b => b.Tokens);

        for (int i = 0; i < count; i++)
        {
            var b = bars[i];
            double x = i * slot + (slot - barWidth) / 2;
            bool singleDay = b.Start == b.End;
            string tip = singleDay
                ? $"{b.Start:yyyy-MM-dd}\n{SpendFormat.TokensExact(b.Tokens)} tokens\n{SpendFormat.MoneyExact(b.Cost)}"
                : $"{b.Start:MM-dd} ~ {b.End:MM-dd}\n{SpendFormat.TokensExact(b.Tokens)} tokens\n{SpendFormat.MoneyExact(b.Cost)}"
                    + (b.HasUnpriced ? "\n(含无公开价的模型)" : "");

            if (b.Tokens == 0)
            {
                // 整段没有调用:画一条贴底的淡线,而不是与"有量但极小"混在一起
                var zero = new Rectangle
                {
                    Width = barWidth,
                    Height = 1.5,
                    Fill = ChartZero,
                    ToolTip = tip + (singleDay ? "" : "\n没有记录"),
                };
                Canvas.SetLeft(zero, x);
                Canvas.SetTop(zero, plotHeight - 1.5);
                ChartCanvas.Children.Add(zero);
                continue;
            }

            double ratio = max == 0 ? 0 : (double)b.Tokens / max;
            double barHeight = Math.Max(2, ratio * plotHeight);
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                Fill = ChartBar,
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = tip,
                Cursor = Cursors.Hand,
            };
            if (singleDay)
            {
                // 点击下钻只对"一天"的柱生效
                var captured = b.Start;
                bar.MouseLeftButtonDown += (_, e) => { e.Handled = true; EnterDayFocus(captured); };
            }
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotHeight - barHeight);
            ChartCanvas.Children.Add(bar);
        }

        // 底部只标首/中/尾三个日期,免得挤成一团
        foreach (var index in new[] { 0, count / 2, count - 1 }.Distinct())
        {
            var label = new TextBlock
            {
                Text = bars[index].Start.ToString("MM-dd"),
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
    }

    /// <summary>把逐日序列按 keySelector 分桶(桶界 = key 本身),桶末 = endSelector(key)。</summary>
    private static List<(DateOnly, DateOnly, long, double, bool)> Bucket(
        List<SpendSummary.DayRow> days,
        Func<DateOnly, DateOnly> keySelector,
        Func<DateOnly, DateOnly> endSelector)
    {
        var acc = new SortedDictionary<DateOnly, (long Tokens, double Cost, bool HasUnpriced)>();
        foreach (var d in days)
        {
            var key = keySelector(d.Day);
            var cur = acc.TryGetValue(key, out var a) ? a : (Tokens: 0L, Cost: 0d, HasUnpriced: false);
            acc[key] = (cur.Tokens + d.Tokens, cur.Cost + d.Cost, cur.HasUnpriced || d.HasUnpriced);
        }
        return acc.Select(kv => (kv.Key, endSelector(kv.Key), kv.Value.Tokens, kv.Value.Cost, kv.Value.HasUnpriced)).ToList();
    }

    // ————————————————— 活动热图(铺满宽 + 点击下钻) —————————————————

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
        // **永远铺满可用宽**(锚定今天往回数):数据短时格子里自然有空周(画淡框),
        // 数据长时以上限 60 周(约 14 个月)截断。居中/右贴的做法都试过,看起来都
        // 像"内容没画出来"——铺满最不像 bug。
        int weekColumns = Math.Clamp(fitColumns, 1, 60);
        var firstMonday = lastMonday.AddDays(-7 * (weekColumns - 1));
        double usedW = weekColumns * step - gap;

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
                    Fill = HeatBrush(tokens, max),
                    Cursor = Cursors.Hand,
                };
                rect.ToolTip = tokens > 0
                    ? $"{day:yyyy-MM-dd}\n{SpendFormat.TokensExact(tokens)} tokens\n{SpendFormat.MoneyExact(byDay[key].Cost)}\n(点击看这一天)"
                    : $"{day:yyyy-MM-dd}\n没有记录";
                var captured = key;
                rect.MouseLeftButtonDown += (_, e) => { e.Handled = true; EnterDayFocus(captured); };
                Canvas.SetLeft(rect, col * step);
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
            Canvas.SetLeft(labelBlock, column * step);
            Canvas.SetTop(labelBlock, 7 * step - 2);
            HeatCanvas.Children.Add(labelBlock);
        }

        var earliest = byDay.Keys.Min();
        var firstVisibleMonday = DateOnly.FromDateTime(firstMonday);
        string truncated = earliest < firstVisibleMonday
            ? $"   ·   {firstVisibleMonday.AddDays(-1):yyyy-MM-dd} 之前的 {byDay.Count(d => d.Key < firstVisibleMonday)} 天没画下(宽不够),数字仍在统计里"
            : "";
        HeatHint.Text = $"共 {byDay.Count} 天有记录   ·   最忙 {byDay.Aggregate((a, b) => a.Value.Tokens > b.Value.Tokens ? a : b).Key:yyyy-MM-dd}"
            + $"   ·   (格色越亮当天用量越大,点格子看那一天){truncated}";
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

    // ————————————————— 下钻某天 —————————————————

    private void EnterDayFocus(DateOnly day)
    {
        if (_ledger is null) return;
        _dayFilter = day;
        _selectedChannel = null;
        DayFilterRow.Visibility = Visibility.Visible;
        OverviewPanel.Visibility = Visibility.Visible;
        ChannelPanel.Visibility = Visibility.Collapsed;
        RenderViewTabs();
        Render(SpendSummary.Build(_ledger, day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(TimeOnly.MinValue).AddDays(1)));
    }

    private void DayFilterBack_Click(object sender, RoutedEventArgs e)
    {
        _dayFilter = null;
        DayFilterRow.Visibility = Visibility.Collapsed;
        RenderAll();
    }

    // ————————————————— 套餐页 —————————————————

    private void ModelSort_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _modelsByCost = sender == ModelSortCost;
        if (_selectedChannel is { } ch) RenderChannelModels(ch);
    }

    private void SelectChannel(ChannelUsage ch)
    {
        _selectedChannel = ch;
        ChannelPanel.Visibility = Visibility.Visible;
        OverviewPanel.Visibility = Visibility.Collapsed;

        ChannelTitle.Text = ch.Name;
        // 副标题:来源客户端 + 订阅状态 + 本页统计的时间范围(或"本区间无用量")
        string? state = ch.Enabled switch
        {
            true => "订阅中",
            false => "未启用",
            _ => null,
        };
        string range = ch.HasData
            ? (ch.FirstUsed is { } f && ch.LastUsed is { } l ? $"{f:MM-dd HH:mm} ~ {l:MM-dd HH:mm}" : "")
            : ch.LifetimeLast is { } ll ? $"本区间无用量(最后一次 {ll:MM-dd})" : "本区间无用量";
        ChannelSubtitle.Text = $"{ch.Agent} 渠道"
            + (state is not null ? $" · {state}" : "")
            + (range.Length > 0 ? $" · {range}" : "");

        // —— KPI 六格:总费用 / 总请求 / 总 TOKEN / 缓存命中率 / 缓存读写 / 会话数 ——
        ChannelKpiGrid.Children.Clear();
        AddCell(ChannelKpiGrid, "总费用", ch.HasData ? SpendFormat.Amount(ch.Cost, ch.HasUnpriced) : "—",
            ch.HasData && ch.Requests > 0 ? $"均 {SpendFormat.Amount(ch.Cost / ch.Requests, false)}/次" : null,
            "Money");
        AddCell(ChannelKpiGrid, "总请求", ch.Requests.ToString("N0"), "当前区间", "Requests");
        AddCell(ChannelKpiGrid, "总 TOKEN", SpendFormat.Tokens(ch.TotalTokens),
            $"输入 {SpendFormat.Tokens(ch.Tally.Input)} · 输出 {SpendFormat.Tokens(ch.Tally.Output)}", "Tokens");
        AddCell(ChannelKpiGrid, "缓存命中率",
            ch.CacheHit is { } hit ? $"{hit:P1}" : "—",
            $"命中 {SpendFormat.Tokens(ch.Tally.CacheRead)}", "Cache");
        AddCell(ChannelKpiGrid, "缓存读 / 写",
            $"{SpendFormat.Tokens(ch.Tally.CacheRead)} / {SpendFormat.Tokens(ch.Tally.CacheWrite)}",
            null, "Cache");
        AddCell(ChannelKpiGrid, "会话数", ch.Sessions.ToString("N0"), "去重 sessionId", "Sessions");

        // —— Token 构成六格 ——
        ChannelTokenGrid.Children.Clear();
        var t = ch.Tally;
        AddCell(ChannelTokenGrid, "输入", SpendFormat.Tokens(t.Input), null, "Tokens");
        AddCell(ChannelTokenGrid, "输出", SpendFormat.Tokens(t.Output), null, "Tokens");
        AddCell(ChannelTokenGrid, "缓存读", SpendFormat.Tokens(t.CacheRead),
            ch.CacheHit is { } ch2 ? $"命中率 {ch2:P1}" : null, "Cache");
        AddCell(ChannelTokenGrid, "缓存写", SpendFormat.Tokens(t.CacheWrite), null, "Cache");
        AddCell(ChannelTokenGrid, "未分类", SpendFormat.Tokens(ch.UnclassifiedTokens),
            ch.UnclassifiedTokens > 0 ? "来源只报总量、不参与计价" : "无", "Muted");
        AddCell(ChannelTokenGrid, "合计", SpendFormat.TokensExact(ch.TotalTokens), null, "Tokens");

        // 本区间无用量:给一条说明,免得看着像"没统计到"
        ChannelStatusLine.Text = ch.HasData ? "" : ch.LifetimeTokens > 0
            ? $"这个区间没有用量。历史上用过 {SpendFormat.Tokens(ch.LifetimeTokens)} tokens / {ch.LifetimeRequests:N0} 次"
                + (ch.LifetimeFirst is { } lf ? $",从 {lf:yyyy-MM-dd} 起。" : "。")
            : "这个区间没有用量,也没有历史记录(配置里有这个套餐)。";

        RenderChannelQuota(ch);
        RenderAccountMetering(ch);
        RenderHourlyChart(ch);
        RenderChannelPeak(ch);
        RenderPerf(ch);
        RenderDailyChart(ch);
        RenderChannelModels(ch);
        RenderDailyTable(ch);
        RenderChannelMeta(ch);
    }

    /// <summary>这个套餐的订阅额度(渠道 SourceKey 对得上快照源时显示;GOAT 的 5h/周/月窗口)。</summary>
    private void RenderChannelQuota(ChannelUsage ch)
    {
        var sub = ch.SourceKey is { } key ? _subs.FirstOrDefault(s => s.Key == key) : null;
        if (sub is null)
        {
            ChannelQuotaCard.Visibility = Visibility.Collapsed;
            return;
        }
        ChannelQuotaCard.Visibility = Visibility.Visible;
        ChannelQuotaPanel.Children.Clear();
        foreach (var pool in sub.Pools.Where(p => p.HasReading))
            ChannelQuotaPanel.Children.Add(BuildPoolLine(pool));
        if (ChannelQuotaPanel.Children.Count == 0)
            ChannelQuotaPanel.Children.Add(new TextBlock
            {
                Text = "快照里没有这个套餐的可用读数。",
                FontSize = 11.5, Foreground = TextSub,
            });
        ChannelQuotaHint.Text = "官方接口读数(悬浮环同步),不是估算。";
    }

    // ————————————————— 账号计量对照(官方服务端 vs 本机记录) —————————————————

    /// <summary>
    /// 有官方计量接口的渠道(当前即 GOAT):官方账期读数覆盖**所有设备、所有 key**,
    /// 本机记录只有这台电脑的客户端——差值就是其他设备/命令行直接使用的量。
    /// 上一版用户发现"官方 37.4 亿 vs 本机 34.2 亿"对不上,根因就是这个,这里把差值摆到明面上。
    /// </summary>
    private void RenderAccountMetering(ChannelUsage ch)
    {
        var sub = ch.SourceKey is { } key ? _subs.FirstOrDefault(s => s.Key == key) : null;
        long? officialRead = sub?.PeriodTokens;
        bool show = ch.SourceKey == "goat" && officialRead is { };
        AccountMeterCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        long officialTokens = officialRead!.Value;

        // 账期起点 = 月池重置时间往回一个自然周期(约 30 天)。ResetAt 缺失时退回本机最早记录。
        var monthPool = sub!.Pools.FirstOrDefault(p => p.PoolKind == "Monthly");
        var periodStart = monthPool?.ResetAt is { } reset
            ? reset.ToLocalTime().DateTime - TimeSpan.FromDays(30)
            : _ledger?.Earliest ?? DateTime.Now.AddDays(-30);

        // 本机同账期、同渠道(按这个套餐的 provider id 过滤)。小时行(agg_hour)与
        // 天级行/明细是同一批数据的两种聚合,这里必须跳过——否则总量翻倍(实测 68.4=2×34.2)。
        long localTokens = 0, localRequests = 0;
        if (_ledger is { } ledger)
        {
            foreach (var e in ledger.Entries)
            {
                if (e.Timestamp < periodStart) continue;
                if (e.Kind == SpendAggKind.Hour) continue;
                if (e.ProviderId is not { } pid || !ch.ProviderIds.Contains(pid)) continue;
                localTokens += e.TotalTokens;
                localRequests += e.Requests;
            }
        }

        long diff = officialTokens - localTokens;
        double? diffPct = localTokens > 0 ? diff * 100.0 / localTokens : null;
        double? diffPctOfOfficial = officialTokens > 0 ? diff * 100.0 / officialTokens : null;

        AccountMeterGrid.Children.Clear();
        AddCell(AccountMeterGrid, "官方账期已用",
            SpendFormat.Tokens(officialTokens),
            (sub.PeriodRequests is { } req ? $"{req:N0} 次请求" : null)
            + (sub.PeriodCost is { } cost ? $" · 约 {cost:0.#} cr" : ""),
            "Money");
        AddCell(AccountMeterGrid, $"本机记录(自 {periodStart:MM-dd HH:mm})",
            SpendFormat.Tokens(localTokens),
            localRequests > 0 ? $"{localRequests:N0} 次请求" : "这台电脑的 ZCode/OpenCode 记录",
            "Tokens");
        AddCell(AccountMeterGrid, "差值(其他设备/CLI)",
            SpendFormat.Tokens(diff),
            diff > 0 && diffPctOfOfficial is { } p ? $"占官方 {p:0.0}%" : (diff <= 0 ? "本机没有多出的记录" : null),
            "Cache");

        AccountMeterHint.Text = "官方 = Command Code 服务端的账期计量,覆盖这台电脑之外的所有设备与 key;"
            + "本机 = 只来自这台电脑的客户端记录库。差值是本机看不到明细的用量"
            + "(其他电脑、命令行直接使用等)——下面的官方逐模型明细就是补这块的。";
        if (diff < 0)
            AccountMeterHint.Text += " 差值为负:本机记录比官方多,通常是区间口径的偏差(以官方为准)。";

        _ = LoadAccountModelsAsync(ch, periodStart);
    }

    /// <summary>官方逐模型明细(/internal/usage/charts,浏览器登录态):按模型聚合整个账期。</summary>
    private async Task LoadAccountModelsAsync(ChannelUsage ch, DateTime periodStart)
    {
        AccountModelHint.Text = "正在读取官方逐模型明细…";
        AccountModelList.ItemsSource = null;
        var from = new DateTimeOffset(periodStart, TimeZoneInfo.Local.GetUtcOffset(periodStart));
        var buckets = await CommandCodeWebSession.TryFetchModelCacheAsync(from, DateTimeOffset.Now);
        if (_closed) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        if (buckets is null)
        {
            AccountModelList.ItemsSource = null;
            AccountModelHint.Text = CommandCodeWebSession.LastStatus is { } status
                && status.Contains("未启用", StringComparison.Ordinal)
                ? "想看全部设备/各 key 的按模型用量:在 设置 → 数据源 打开「浏览器会话明细」,"
                    + "并保持浏览器登录 commandcode.ai(浏览器开着时 cookie 库常被锁,关掉浏览器再点「重新读取」多半能读到)。"
                : "官方逐模型明细当前读不到:" + (CommandCodeWebSession.LastStatus ?? "未知原因")
                    + "。对照行的差值不受影响,那来自官方聚合接口。";
            return;
        }

        // 时间桶 × 模型 → 按模型聚合(charts 不给牌价,金额一栏不显示)
        var ordered = buckets.GroupBy(b => b.Model)
            .Select(g => (Model: g.Key, Total: g.Sum(x => x.Total),
                Input: g.Sum(x => x.Input), Cw: g.Sum(x => x.CacheWrite),
                Cr: g.Sum(x => x.CacheRead), Out: g.Sum(x => x.Output)))
            .OrderByDescending(x => x.Total)
            .ToList();
        long max = ordered.FirstOrDefault().Total;
        AccountModelList.ItemsSource = ordered.Select(x => new RowVm
        {
            Name = x.Model,
            Tokens = SpendFormat.Tokens(x.Total),
            Amount = "",
            AmountTip = "官方明细不含牌价,这里不显示金额",
            Detail = $"输入 {SpendFormat.Tokens(x.Input)} · 缓存写 {SpendFormat.Tokens(x.Cw)} · 缓存读 {SpendFormat.Tokens(x.Cr)} · 输出 {SpendFormat.Tokens(x.Out)}",
            BarWidth = Fraction(x.Total, max) * BarBase,
            BarBrush = BarModels,
        }).ToList();

        long officialTotal = buckets.Sum(x => x.Total);
        AccountModelHint.Text = $"已读到官方明细(共 {SpendFormat.Tokens(officialTotal)} tokens,{buckets.Count} 个时间桶,"
            + $"自 {periodStart:MM-dd} 起)。这是账号在所有设备上的合计,没有设备/项目维度。"
            + $"注意:官方明细的 token 口径与服务端聚合可能略有出入,以对照行的大数为准。";
    }

    private void RenderHourlyChart(ChannelUsage ch)
    {        ChannelHourBox.Child = null;
        var canvas = new Canvas { ClipToBounds = true };
        ChannelHourBox.Child = canvas;
        long total = ch.TodayHourly.Sum(h => h.Tokens);
        ChannelTodayHint.Text = total == 0
            ? "今天还没有记录(程序未运行的小时不会有采样)。"
            : $"{SpendFormat.Tokens(total)} tokens · 最忙 {ch.TodayHourly.OrderByDescending(h => h.Tokens).First().Hour}:00";
        canvas.SizeChanged += (_, _) => DrawHourly(canvas, ch.TodayHourly);
        DrawHourly(canvas, ch.TodayHourly);
    }

    private void DrawHourly(Canvas canvas, IReadOnlyList<HourBucket> hours)
    {
        canvas.Children.Clear();
        double w = ChannelHourBox.ActualWidth, h = ChannelHourBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double slot = w / 24;
        double barW = Math.Max(3, Math.Min(20, slot * 0.62));
        long max = hours.Count > 0 ? hours.Max(x => x.Tokens) : 0;
        var empty = FieldBack;
        double plotH = h - 16;

        for (int i = 0; i < 24; i++)
        {
            var hr = hours[i];
            double x = i * slot + (slot - barW) / 2;
            if (hr.Tokens <= 0)
            {
                var dot = new Rectangle
                {
                    Width = barW, Height = 1.5, Fill = empty, ToolTip = $"{i:00}:00 没有记录",
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 1.5);
                canvas.Children.Add(dot);
                continue;
            }
            // 输入(含缓存)/输出 两段堆叠,便于看构成;峰时段柱画成橙色,一眼看出"贵的时段"
            long inputPart = hr.Input + hr.CacheRead + hr.CacheWrite;
            long outputPart = hr.Output;
            double totalH = max <= 0 ? 0 : Math.Max(2, (double)hr.Tokens / max * plotH);
            double outH = hr.Tokens == 0 ? 0 : totalH * outputPart / hr.Tokens;
            double inH = totalH - outH;
            var inFill = PeakHours.Contains(i) ? PeakBar : Accent;

            if (inH > 0)
            {
                var inBar = new Rectangle
                {
                    Width = barW, Height = inH, Fill = inFill, RadiusX = 1.5, RadiusY = 1.5,
                    ToolTip = $"{i:00}:00\n{SpendFormat.TokensExact(hr.Tokens)} tokens\n{hr.Requests} 次调用"
                        + (PeakHours.Contains(i) ? "\n(峰时段)" : ""),
                };
                Canvas.SetLeft(inBar, x);
                Canvas.SetTop(inBar, plotH - totalH);
                canvas.Children.Add(inBar);
            }
            if (outH > 0)
            {
                var outBar = new Rectangle
                {
                    Width = barW, Height = outH, Fill = GoodBrush, RadiusX = 1.5, RadiusY = 1.5,
                    ToolTip = $"{i:00}:00\n输出 {SpendFormat.TokensExact(outputPart)} tokens",
                };
                Canvas.SetLeft(outBar, x);
                Canvas.SetTop(outBar, plotH - outH);
                canvas.Children.Add(outBar);
            }
        }

        // 只标 0/6/12/18 四个刻度,免得挤
        foreach (int hh in new[] { 0, 6, 12, 18 })
        {
            var label = new TextBlock
            {
                Text = $"{hh:00}:00", FontSize = 10,
                Foreground = TextSub,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = hh * slot + (slot - label.DesiredSize.Width) / 2;
            Canvas.SetLeft(label, Math.Max(0, Math.Min(w - label.DesiredSize.Width, x)));
            Canvas.SetTop(label, h - 13);
            canvas.Children.Add(label);
        }
    }

    /// <summary>
    /// 时段分布(仅"峰值加价"型套餐,当前即 GOAT):整个区间里峰时段占了多少用量。
    /// 峰时段的柱画橙色,和上面"今日逐小时"同一套配色。
    /// </summary>
    private void RenderChannelPeak(ChannelUsage ch)
    {
        bool show = ch.SourceKey == "goat";
        ChannelPeakCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        ChannelRangeHourBox.Child = null;
        var canvas = new Canvas { ClipToBounds = true };
        ChannelRangeHourBox.Child = canvas;
        canvas.SizeChanged += (_, _) => DrawRangeHourly(canvas, ch.RangeHourly);
        DrawRangeHourly(canvas, ch.RangeHourly);

        long total = ch.RangeHourly.Sum(h => h.Tokens);
        double totalCost = ch.RangeHourly.Sum(h => h.Cost);
        long peak = ch.RangeHourly.Where(h => PeakHours.Contains(h.Hour)).Sum(h => h.Tokens);
        double peakCost = ch.RangeHourly.Where(h => PeakHours.Contains(h.Hour)).Sum(h => h.Cost);

        ChannelPeakGrid.Children.Clear();
        if (total == 0)
        {
            ChannelPeakHint.Text = "这个区间没有逐小时记录(仓库里只有天级聚合的老数据)。";
            return;
        }
        AddCell(ChannelPeakGrid, "峰时段占比", $"{(double)peak / total:P1}",
            $"09–12 / 14–18 点 · {SpendFormat.Tokens(peak)} tokens", "Money");
        AddCell(ChannelPeakGrid, "其余时段", $"{(double)(total - peak) / total:P1}",
            $"{SpendFormat.Tokens(total - peak)} tokens", "Tokens");
        AddCell(ChannelPeakGrid, "峰时段金额", SpendFormat.Amount(peakCost, ch.HasUnpriced),
            totalCost > 0 ? $"占本期金额 {(double)peakCost / totalCost:P1}" : null, "Cache");
        ChannelPeakHint.Text = "橙色柱 = 峰时段(本地 09:00–12:00、14:00–18:00),GOAT 在这些时段按更高的时段价计费。"
            + "上面的金额按公开牌价估算、未含时段系数——想压 GOAT 的 credits 消耗,把大任务挪到峰时段之外最有效。";
    }

    private void DrawRangeHourly(Canvas canvas, IReadOnlyList<RangeHourBucket> hours)
    {
        canvas.Children.Clear();
        double w = ChannelRangeHourBox.ActualWidth, h = ChannelRangeHourBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double slot = w / 24;
        double barW = Math.Max(3, Math.Min(20, slot * 0.62));
        long max = hours.Count > 0 ? hours.Max(x => x.Tokens) : 0;
        double plotH = h - 16;

        for (int i = 0; i < 24; i++)
        {
            var hr = hours[i];
            double x = i * slot + (slot - barW) / 2;
            if (hr.Tokens <= 0)
            {
                var dot = new Rectangle { Width = barW, Height = 1.5, Fill = FieldBack, ToolTip = $"{i:00}:00 没有记录" };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 1.5);
                canvas.Children.Add(dot);
                continue;
            }
            double bh = max <= 0 ? 0 : Math.Max(2, (double)hr.Tokens / max * plotH);
            var bar = new Rectangle
            {
                Width = barW, Height = bh, RadiusX = 1.5, RadiusY = 1.5,
                Fill = PeakHours.Contains(i) ? PeakBar : Accent,
                ToolTip = $"{i:00}:00(整个区间累计)\n{SpendFormat.TokensExact(hr.Tokens)} tokens\n"
                    + $"{SpendFormat.MoneyExact(hr.Cost)} · {hr.Requests:N0} 次调用"
                    + (PeakHours.Contains(i) ? "\n(峰时段)" : ""),
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotH - bh);
            canvas.Children.Add(bar);
        }

        foreach (int hh in new[] { 0, 6, 12, 18 })
        {
            var label = new TextBlock { Text = $"{hh:00}:00", FontSize = 10, Foreground = TextSub };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = hh * slot + (slot - label.DesiredSize.Width) / 2;
            Canvas.SetLeft(label, Math.Max(0, Math.Min(w - label.DesiredSize.Width, x)));
            Canvas.SetTop(label, h - 13);
            canvas.Children.Add(label);
        }
    }

    /// <summary>
    /// 性能与行为:来自客户端记录的遥测字段(推理 token/耗时/首字延迟/工具调用/重试/失败)。
    /// 这些是"这次调用花了多久、有没有卡、推理占多少",比单纯的 token 数更能说明体验。
    /// </summary>
    private void RenderPerf(ChannelUsage ch)
    {
        ChannelPerfGrid.Children.Clear();
        bool any = ch.ReasoningTokens > 0 || ch.DurationSamples > 0 || ch.ToolCalls > 0;
        ChannelPerfHint.Text = any
            ? "来自客户端记录的每次调用遥测(平均耗时的分母是「记了耗时的请求数」)"
            : "客户端没有为这些请求记录遥测(推理/耗时/工具调用),这一块为空。";
        if (!any) return;

        AddCell(ChannelPerfGrid, "推理占输出",
            ch.ReasoningShare is { } rs ? $"{rs:P1}" : "—",
            ch.ReasoningTokens > 0 ? $"推理 {SpendFormat.Tokens(ch.ReasoningTokens)}" : null, "Sessions");
        AddCell(ChannelPerfGrid, "平均耗时",
            ch.AvgSeconds is { } sec ? $"{sec:0.0}s" : "—",
            ch.DurationSamples > 0 ? $"{ch.DurationSamples:N0} 次有记录" : null, "Tokens");
        AddCell(ChannelPerfGrid, "平均首字延迟",
            ch.AvgTtftSeconds is { } t ? $"{t:0.00}s" : "—",
            ch.TtftSamples > 0 ? $"{ch.TtftSamples:N0} 次有记录" : null, "Cache");
        AddCell(ChannelPerfGrid, "工具调用", ch.ToolCalls.ToString("N0"),
            ch.Requests > 0 ? $"均 {(double)ch.ToolCalls / ch.Requests:0.0} 次/请求" : null, "Requests");
        AddCell(ChannelPerfGrid, "重试", ch.Retries.ToString("N0"),
            ch.Requests > 0 ? $"占请求 {ch.Retries * 100.0 / ch.Requests:0.0}%" : null, "Money");
        AddCell(ChannelPerfGrid, "平均每请求",
            ch.AvgTokensPerRequest is { } avg ? SpendFormat.Tokens((long)avg) : "—",
            "tokens / 次", "Tokens");
    }

    private void RenderDailyChart(ChannelUsage ch)
    {
        ChannelDailyBox.Child = null;
        var canvas = new Canvas { ClipToBounds = true };
        ChannelDailyBox.Child = canvas;
        canvas.SizeChanged += (_, _) => DrawDaily(canvas, ch.Daily);
        DrawDaily(canvas, ch.Daily);
    }

    private void DrawDaily(Canvas canvas,
        IReadOnlyList<(DateOnly Day, long Tokens, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Requests)> daily)
    {
        canvas.Children.Clear();
        double w = ChannelDailyBox.ActualWidth, h = ChannelDailyBox.ActualHeight;
        if (w <= 0 || h <= 0 || daily.Count == 0) return;

        int n = daily.Count;
        double slot = w / n;
        double barW = Math.Max(2, Math.Min(22, slot * 0.7));
        long max = daily.Max(d => d.Tokens);
        double plotH = h - 16;

        for (int i = 0; i < n; i++)
        {
            var d = daily[i];
            double x = i * slot + (slot - barW) / 2;
            if (d.Tokens <= 0)
            {
                var dot = new Rectangle
                {
                    Width = barW, Height = 1.5, Fill = FieldBack, ToolTip = $"{d.Day:yyyy-MM-dd}:没有记录",
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 1.5);
                canvas.Children.Add(dot);
                continue;
            }
            double bh = max <= 0 ? 0 : Math.Max(2, (double)d.Tokens / max * plotH);
            var bar = new Rectangle
            {
                Width = barW, Height = bh, Fill = Accent, RadiusX = 2, RadiusY = 2,
                ToolTip = $"{d.Day:yyyy-MM-dd}\n{SpendFormat.TokensExact(d.Tokens)} tokens\n"
                    + $"{SpendFormat.MoneyExact(d.Cost)} · {d.Requests} 次调用",
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotH - bh);
            canvas.Children.Add(bar);
        }

        var first = new TextBlock { Text = daily[0].Day.ToString("MM-dd"), FontSize = 10, Foreground = TextSub };
        Canvas.SetLeft(first, 0); Canvas.SetTop(first, h - 13);
        canvas.Children.Add(first);
        if (n > 1)
        {
            var last = new TextBlock { Text = daily[^1].Day.ToString("MM-dd"), FontSize = 10, Foreground = TextSub };
            last.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(last, Math.Max(0, w - last.DesiredSize.Width));
            Canvas.SetTop(last, h - 13);
            canvas.Children.Add(last);
        }
    }

    /// <summary>该套餐里各模型的用量行(带占比条;可按 token 或金额排序)。</summary>
    private void RenderChannelModels(ChannelUsage ch)
    {
        ChannelModelPanel.Children.Clear();
        var models = _modelsByCost
            ? ch.Models.OrderByDescending(m => m.Cost).ToList()
            : ch.Models.ToList();
        if (models.Count == 0)
        {
            ChannelModelPanel.Children.Add(new TextBlock
            {
                Text = "这个区间没有模型记录。",
                FontSize = 12, Margin = new Thickness(1, 4, 1, 4),
                Foreground = TextSub,
            });
            return;
        }

        long max = models.Max(m => m.Tokens);
        double maxCost = models.Max(m => m.Cost);
        bool first = true;
        foreach (var m in models)
        {
            if (!first)
                ChannelModelPanel.Children.Add(new Border { Background = Separator, Height = 1 });
            first = false;

            var grid = new Grid { Margin = new Thickness(1, 9, 1, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = m.Model, FontSize = 12.5,
                Foreground = TextMain,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var barRow = new Grid { Margin = new Thickness(0, 4, 0, 3) };
            barRow.Children.Add(new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = FieldBack,
                HorizontalAlignment = HorizontalAlignment.Left, Width = 200,
            });
            // 排序键决定比例条画什么:按金额时条长比金额,条色不变(条只是排序键的可视化)
            double ratio = _modelsByCost
                ? maxCost <= 0 ? 0 : m.Cost / maxCost
                : max <= 0 ? 0 : (double)m.Tokens / max;
            barRow.Children.Add(new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = Accent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(5, ratio * 200),
            });
            left.Children.Add(barRow);
            string cache = m.CacheHit is { } hit ? $" · 缓存命中 {hit:P0}" : "";
            left.Children.Add(new TextBlock
            {
                Text = $"{SpendFormat.Tokens(m.Tokens)} tokens · {m.Requests:N0} 次{cache}",
                FontSize = 10.5,
                Foreground = TextSub,
            });
            grid.Children.Add(left);

            var amount = new TextBlock
            {
                Text = SpendFormat.Amount(m.Cost, m.HasUnpriced),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = TextMain,
                ToolTip = SpendFormat.AmountTip(m.Cost, m.HasUnpriced),
            };
            Grid.SetColumn(amount, 1);
            grid.Children.Add(amount);

            ChannelModelPanel.Children.Add(grid);
        }
    }

    /// <summary>逐日明细表:每行一个模型,列出区间内逐天的 token / 次数 / 金额。</summary>
    private void RenderDailyTable(ChannelUsage ch)
    {
        ChannelDailyTablePanel.Children.Clear();

        // 取区间里有量的天(升序)
        var days = ch.Daily.Where(d => d.Tokens > 0).Select(d => d.Day).ToList();
        if (days.Count == 0)
        {
            ChannelDailyTableHint.Text = "这个区间没有用量。";
            return;
        }
        // 只显示最近 6 天:内容区约 560diu,模型名列最小 150,一格"6995.7万·459"约 78,
        // 6 列 + 边距刚好;7 列就会把最右一列推出窗口右缘。
        var shown = days.TakeLast(6).ToList();
        ChannelDailyTableHint.Text = shown.Count < days.Count
            ? $"每行一个模型,列为最近 {shown.Count} 天(区间共 {days.Count} 天有用量)。"
            : $"每行一个模型,列为 {shown.Count} 天。";

        var grid = new Grid { Margin = new Thickness(1, 2, 1, 6) };
        // 第一列(模型名)给一个下限宽度,否则会被后面的日期列挤到只剩 "d…"
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        foreach (var _ in shown)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;
        // 表头:模型名 + 各天(MM-dd)
        AddTableRow(grid, row++, "模型", shown.Select(d => d.ToString("MM-dd")).ToArray(), bold: true, isHeader: true);
        foreach (var m in ch.Models.Take(10))
        {
            if (m.Daily is not { Count: > 0 }) continue;
            var byDay = m.Daily.ToDictionary(x => x.Day, x => x);
            // 单元格**只放 token**(次数与金额进 tooltip)
            var cells = shown.Select(d =>
                byDay.TryGetValue(d, out var v) && v.Tokens > 0
                    ? SpendFormat.Tokens(v.Tokens)
                    : "·").ToArray();
            var tips = shown.Select(d =>
                byDay.TryGetValue(d, out var v) && v.Tokens > 0
                    ? $"{d:yyyy-MM-dd}\n{SpendFormat.TokensExact(v.Tokens)} tokens\n{v.Requests} 次调用\n{SpendFormat.MoneyExact(v.Cost)}"
                    : $"{d:yyyy-MM-dd}\n没有记录").ToArray();
            AddTableRow(grid, row++, m.Model, cells, bold: false, isHeader: false, tips);
        }

        ChannelDailyTablePanel.Children.Add(grid);
    }

    private void AddTableRow(Grid grid, int row, string label, string[] cells, bool bold, bool isHeader,
        string[]? tips = null)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock
        {
            Text = label,
            FontSize = isHeader ? 11 : 12,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 5, 10, 5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = isHeader ? TextSub : TextMain,
        };
        Grid.SetRow(name, row);
        grid.Children.Add(name);

        for (int i = 0; i < cells.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = cells[i],
                FontSize = 11,
                Margin = new Thickness(6, 5, 2, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                ToolTip = tips is not null && i < tips.Length ? tips[i] : null,
                Foreground = isHeader ? TextSub : TextMain,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, i + 1);
            grid.Children.Add(cell);
        }
    }

    /// <summary>归属信息:实际用到的 provider id 与来源库,便于对账。</summary>
    private void RenderChannelMeta(ChannelUsage ch)
    {
        string ids = ch.ProviderIds.Count > 0 ? string.Join("、", ch.ProviderIds) : "(未标注)";
        string sourceKey = ch.SourceKey is { } sk ? $" · 对应数据源 {sk}" : "";
        string state = ch.Enabled switch
        {
            true => "订阅中",
            false => "未启用(客户端配置里已关闭,历史用量仍照实统计)",
            _ => "状态未知",
        };
        string life = ch.LifetimeTokens > 0
            ? $"全时段累计 {SpendFormat.Tokens(ch.LifetimeTokens)} tokens / {ch.LifetimeRequests:N0} 次"
                + (ch.LifetimeFirst is { } lf && ch.LifetimeLast is { } ll ? $",{lf:yyyy-MM-dd} ~ {ll:yyyy-MM-dd}" : "")
            : "全时段没有记录(本机从未用过这个套餐)";
        ChannelMetaLine.Text = $"来源客户端 {ch.Agent}{sourceKey} · {state}\n"
            + $"provider id:{ids}\n"
            + life + "\n"
            + "统计口径与总览一致:四类 token 之和对不上来源总量时差额记未分类(计入总数、不计价);"
            + "金额按 models.dev 公开牌价估算。";
    }

    /// <summary>KPI 小格:大数字 + 小标签 + 可选脚注。金额/缓存类用强调色。</summary>
    private void AddCell(Panel host, string label, string value, string? note, string kind)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        Brush accent = kind switch
        {
            "Money" => Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A)),
            "Tokens" => Frozen(Color.FromRgb(0x00, 0x7A, 0xFF)),
            "Cache" => Frozen(Color.FromRgb(0x00, 0xA8, 0x5C)),
            "Requests" => Frozen(Color.FromRgb(0x5E, 0x5C, 0xE6)),
            "Sessions" => Frozen(Color.FromRgb(0xBF, 0x5A, 0xF2)),
            _ => TextSub,
        };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = TextSub,
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = accent,
        });
        if (!string.IsNullOrEmpty(note))
        {
            panel.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSub,
            });
        }
        host.Children.Add(panel);
    }

    // ————————————————— 导出 —————————————————

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var csv = new MenuItem { Header = "导出 CSV(Excel 可直接打开)" };
        csv.Click += (_, _) => ExportCurrent(exportJson: false);
        var json = new MenuItem { Header = "导出 JSON(原始数值)" };
        json.Click += (_, _) => ExportCurrent(exportJson: true);
        menu.Items.Add(csv);
        menu.Items.Add(json);
        menu.PlacementTarget = ExportButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ExportCurrent(bool exportJson)
    {
        if (_ledger is null) return;
        var (from, to) = _dayFilter is { } day
            ? (day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(TimeOnly.MinValue).AddDays(1))
            : EffectiveRange();
        var summary = SpendSummary.Build(_ledger, from, to);
        double rate = AppSettings.Current.UsdToCny;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Pulse用量_{from:yyyyMMdd}-{to.AddDays(-1):yyyyMMdd}",
            DefaultExt = exportJson ? ".json" : ".csv",
            Filter = exportJson ? "JSON (*.json)|*.json" : "CSV (Excel 兼容) (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (exportJson)
            {
                var payload = new
                {
                    from = from.ToString("yyyy-MM-dd HH:mm"),
                    to = to.ToString("yyyy-MM-dd HH:mm"),
                    note = "金额按 models.dev 公开 API 牌价估算,不是账单;cost_cny 按本地汇率设置换算。",
                    total = new
                    {
                        tokens = summary.TotalTokens,
                        cost_usd = summary.TotalCost,
                        cost_cny = summary.TotalCost * rate,
                        requests = summary.Requests,
                        sessions = summary.Sessions,
                        projects = summary.Projects,
                        tally = summary.Tally,
                    },
                    days = summary.Days.Select(d => new
                    {
                        day = d.Day.ToString("yyyy-MM-dd"), d.Tokens,
                        cost_usd = Math.Round(d.Cost, 4), cost_cny = Math.Round(d.Cost * rate, 4), d.HasUnpriced,
                    }),
                    models = summary.Models.Select(m => new
                    {
                        model = m.Model, m.Tokens, cost_usd = m.Amount, m.Tally,
                        m.Requests, m.CacheHit, m.UnpricedTokens,
                    }),
                    projects = summary.ProjectRows.Select(p => new
                    {
                        project = p.Project, p.Tokens, cost_usd = p.Cost, cost_cny = Math.Round(p.Cost * rate, 4),
                        p.Sessions, p.HasArchivedDetail,
                    }),
                    sessions = summary.SessionRows.Select(s => new
                    {
                        s.SessionId, s.Title, s.Agent, s.Project, s.Tokens,
                        cost_usd = s.Cost, cost_cny = Math.Round(s.Cost * rate, 4), s.Last,
                    }),
                };
                File.WriteAllText(dialog.FileName,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# Pulse 用量导出(按 models.dev 公开 API 牌价估算,不是账单)");
                sb.AppendLine($"# 区间,{from:yyyy-MM-dd HH:mm},至,{to:yyyy-MM-dd HH:mm}");
                sb.AppendLine($"# 合计 tokens,{summary.TotalTokens},金额 USD,{summary.TotalCost:F4},人民币 {summary.TotalCost * rate:F2},调用,{summary.Requests}");
                sb.AppendLine();
                sb.AppendLine("== 逐日 ==");
                sb.AppendLine("日期,tokens,金额USD,金额CNY,调用");
                foreach (var d in summary.Days)
                    sb.AppendLine($"{d.Day:yyyy-MM-dd},{d.Tokens},{d.Cost:F4},{d.Cost * rate:F4},");
                sb.AppendLine();
                sb.AppendLine("== 模型 ==");
                sb.AppendLine("模型,tokens,金额USD,金额CNY,调用,缓存命中,无价token");
                foreach (var m in summary.Models)
                    sb.AppendLine($"{Csv(m.Model)},{m.Tokens},{m.Amount ?? 0:F4},{(m.Amount ?? 0) * rate:F4},{m.Requests},"
                        + (m.CacheHit is { } h ? h.ToString("0.0%") : "") + $",{m.UnpricedTokens}");
                sb.AppendLine();
                sb.AppendLine("== 项目 ==");
                sb.AppendLine("项目,tokens,金额USD,金额CNY,会话");
                foreach (var p in summary.ProjectRows)
                    sb.AppendLine($"{Csv(p.Project)},{p.Tokens},{p.Cost:F4},{p.Cost * rate:F4},{p.Sessions}");
                sb.AppendLine();
                sb.AppendLine("== 会话 ==");
                sb.AppendLine("会话ID,标题,来源,项目,tokens,金额USD,金额CNY,最后活动");
                foreach (var s in summary.SessionRows)
                    sb.AppendLine($"{Csv(s.SessionId)},{Csv(s.Title ?? "")},{Csv(s.Agent)},{Csv(s.Project ?? "")},"
                        + $"{s.Tokens},{s.Cost:F4},{s.Cost * rate:F4},{s.Last:yyyy-MM-dd HH:mm:ss}");
                // BOM:Excel 双击打开不乱码
                File.WriteAllText(dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            }
            PriceSourceText.Text = $"已导出 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            PriceSourceText.Text = "导出失败:" + ex.Message;
            Diagnostics.Note("导出用量失败", ex);
        }
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

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

    /// <summary>UiShot 用:切到第一个套餐页(直接驱动真实 tab 按钮,与手点同一条路径)。</summary>
    internal void UiShotOpenFirstChannel()
    {
        if (TabRow.Children.OfType<RadioButton>().Skip(1).FirstOrDefault() is { } tab)
            tab.IsChecked = true;
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
