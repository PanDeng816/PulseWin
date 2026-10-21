using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

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
    private SpendSpan _span = SpendSpan.Today;
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

    /// <summary>计费峰时段(本地小时;口径:北京 09–12、14–18)。**GOAT 与 OpenCode Go 用同一套时段**
    /// ——OpenCode Go 也是这两个时段加价(用户实测确认)。</summary>
    private static readonly int[] PeakHours = { 9, 10, 11, 14, 15, 16, 17 };

    /// <summary>该渠道的**计费峰时段**;null = 不按时段加价,柱子统一配色,也不拆峰/谷。</summary>
    private static int[]? PeakHoursFor(string? sourceKey) => sourceKey switch
    {
        "goat" or "opencode" => PeakHours,
        _ => null,
    };

    private const string PeakWindowText = "09–12 / 14–18 点";

    // 颜色全部走 UiTheme:深浅主题切换后,下一帧/下一次渲染就是新色值
    // (这里**不能**用 static readonly 缓存:那样会把启动时的主题永久钉死)
    private static Brush BarModels => UiTheme.B(UiTheme.BarModels);
    private static Brush BarAgents => UiTheme.B(UiTheme.BarAgents);
    private static Brush BarProjects => UiTheme.B(UiTheme.BarProjects);
    private static Brush BarSessions => UiTheme.B(UiTheme.BarSessions);
    private static Brush BarEmpty => UiTheme.B(UiTheme.BarEmpty);
    private static Brush ChartBar => UiTheme.B(UiTheme.ChartBar);
    private static Brush ChartZero => UiTheme.B(UiTheme.ChartZero);
    private static Brush PeakBar => UiTheme.B(UiTheme.PeakBar);

    // 曲线图用的原始颜色(渐变填充/峰时段底纹要按 Color 建画刷,不能直接拿 Brush)
    private static Color AccentColor => UiTheme.Accent;
    private static Color GoodColor => UiTheme.Good;
    private static Color PeakColor => UiTheme.PeakBar;

    // 套餐页从设置窗搬来,那里用主题资源,这里用同色值
    private static Brush TextMain => UiTheme.B(UiTheme.TextMain);
    private static Brush TextSub => UiTheme.B(UiTheme.TextSub);
    private static Brush FieldBack => UiTheme.B(UiTheme.FieldBack);
    private static Brush Accent => UiTheme.B(UiTheme.Accent);
    private static Brush Separator => UiTheme.B(UiTheme.Separator);
    private static Brush GoodBrush => UiTheme.B(UiTheme.Good);
    private static Brush WarnBrush => UiTheme.B(UiTheme.Warn);
    private static Brush BadBrush => UiTheme.B(UiTheme.Bad);

    /// <summary>浅一档的蓝(强调色的 45% 混白):铺满整行的占比条用它,免得盖过数字。</summary>
    private static Brush BarSoft => UiTheme.B(UiTheme.AccentSoft);

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    // ————————————————— 面积曲线(全窗口统一:所有"随时间变化"的图都走这里) —————————————————

    /// <summary>曲线下方的渐变填充(顶部有色、到底部完全透明),按颜色缓存,免得每次重绘都新建画刷。</summary>
    private static readonly Dictionary<Color, Brush> AreaFills = [];

    private static Brush AreaFill(Color c)
    {
        if (AreaFills.TryGetValue(c, out var cached)) return cached;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x3C, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x10, c.R, c.G, c.B), 0.7),
                new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
            },
        };
        brush.Freeze();
        AreaFills[c] = brush;
        return brush;
    }

    /// <summary>峰时段的浅色底纹(叠在曲线后面的竖条),颜色也缓存。</summary>
    private static readonly Dictionary<Color, Brush> BandFills = [];

    private static Brush BandFill(Color c)
    {
        if (BandFills.TryGetValue(c, out var cached)) return cached;
        var brush = Frozen(Color.FromArgb(0x14, c.R, c.G, c.B));
        BandFills[c] = brush;
        return brush;
    }

    /// <summary>
    /// 把一串数值画成**平滑面积曲线**(替代柱图)。用 Catmull-Rom 转三次贝塞尔,
    /// 控制点 Y 夹在相邻两点之间,保证曲线不会冲到 0 轴以下。
    ///
    /// 命中区是垫在曲线下面的不可见矩形(每点一格),所以悬停提示与点击下钻的行为
    /// 跟原来的柱子一模一样——换的只是长相。
    /// </summary>
    /// <param name="values">纵轴数值(0 = 贴底)。</param>
    /// <param name="tips">每个点的悬停文本,null = 该点不可悬停。</param>
    /// <param name="band">需要打底纹的点(峰时段),null = 不打。</param>
    /// <param name="onClick">点的点击回调(索引),null = 不可点。</param>
    /// <param name="xLabels">横轴刻度(索引 + 文本),一般只给少数几个。</param>
    private static void DrawAreaChart(
        Canvas canvas,
        double width,
        double height,
        IReadOnlyList<double> values,
        IReadOnlyList<string?> tips,
        Color lineColor,
        Color bandColor,
        Func<int, bool>? band,
        Action<int>? onClick,
        IReadOnlyList<(int Index, string Text)>? xLabels,
        double labelBand = 16,
        IReadOnlyList<double>? overlay = null,
        Color overlayColor = default)
    {
        canvas.Children.Clear();
        int n = values.Count;
        if (n == 0 || width <= 1 || height <= 1) return;

        double plotH = Math.Max(8, height - labelBand);
        const double padX = 7;
        double spanX = Math.Max(1, width - padX * 2);
        double step = n > 1 ? spanX / (n - 1) : 0;
        double X(int i) => n > 1 ? padX + i * step : width / 2;

        long max = 0;
        for (int i = 0; i < n; i++) max = Math.Max(max, (long)values[i]);

        // 基线:无论有没有数据都画,免得图看着"悬空"
        var baseline = new Rectangle { Width = width, Height = 1, Fill = UiTheme.B(UiTheme.ChartTrack) };
        Canvas.SetLeft(baseline, 0);
        Canvas.SetTop(baseline, plotH);
        canvas.Children.Add(baseline);

        if (max <= 0)
        {
            AddXLabels(canvas, width, plotH, xLabels, X);
            var empty = new TextBlock
            {
                Text = "这个区间没有记录", FontSize = 11, Foreground = TextSub,
            };
            Canvas.SetLeft(empty, padX);
            Canvas.SetTop(empty, plotH / 2 - 8);
            canvas.Children.Add(empty);
            return;
        }

        // 峰值留一点顶部余量,曲线不会顶到画布边缘
        double topRoom = 8;
        double usable = Math.Max(4, plotH - topRoom);
        double Y(int i) => plotH - Math.Max(0, values[i]) / max * usable;

        // 峰时段底纹(先画,压在曲线下面)
        if (band is not null)
        {
            int i = 0;
            while (i < n)
            {
                if (!band(i)) { i++; continue; }
                int j = i;
                while (j + 1 < n && band(j + 1)) j++;
                double x1 = Math.Max(0, X(i) - (n > 1 ? step / 2 : 20));
                double x2 = Math.Min(width, X(j) + (n > 1 ? step / 2 : 20));
                var rect = new Rectangle
                {
                    Width = Math.Max(2, x2 - x1),
                    Height = Math.Max(4, plotH - 5),
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = BandFill(bandColor),
                };
                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, 4);
                canvas.Children.Add(rect);
                i = j + 1;
            }
        }

        var pts = new List<Point>(n);
        for (int i = 0; i < n; i++) pts.Add(new Point(X(i), Y(i)));

        // 面积(曲线 + 收到基线,闭合填充渐变)
        var areaFig = new PathFigure { StartPoint = new Point(pts[0].X, plotH), IsClosed = true, IsFilled = true };
        foreach (var seg in SmoothSegments(pts)) areaFig.Segments.Add(seg);
        areaFig.Segments.Add(new LineSegment(new Point(pts[^1].X, plotH), false));
        var areaGeo = new PathGeometry();
        areaGeo.Figures.Add(areaFig);
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = areaGeo, Fill = AreaFill(lineColor) });

        // 曲线本体
        var lineFig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        foreach (var seg in SmoothSegments(pts)) lineFig.Segments.Add(seg);
        var lineGeo = new PathGeometry();
        lineGeo.Figures.Add(lineFig);
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = lineGeo,
            Stroke = Frozen(lineColor),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        // 叠加线(可选):同一套纵轴上的第二条序列,比如"输出"占"合计"多少
        if (overlay is { Count: > 1 })
        {
            var opts = new List<Point>(n);
            for (int i = 0; i < n; i++)
                opts.Add(new Point(X(i), plotH - Math.Max(0, i < overlay.Count ? overlay[i] : 0) / max * usable));
            var overFig = new PathFigure { StartPoint = opts[0], IsClosed = false, IsFilled = false };
            foreach (var seg in SmoothSegments(opts)) overFig.Segments.Add(seg);
            var overGeo = new PathGeometry();
            overGeo.Figures.Add(overFig);
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = overGeo,
                Stroke = Frozen(overlayColor),
                StrokeThickness = 1.4,
                StrokeDashArray = [3, 2],
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        // 数据点:点少时逐个画,点多时只画最大值那一个(避免糊成一条线)
        int maxIndex = 0;
        for (int i = 1; i < n; i++) if (values[i] > values[maxIndex]) maxIndex = i;
        double dotR = n <= 40 ? 1.6 : 0;
        for (int i = 0; i < n; i++)
        {
            bool isMax = i == maxIndex;
            if (!isMax && dotR <= 0) continue;
            if (!isMax && values[i] <= 0) continue;
            double r = isMax ? 3.4 : dotR;
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = isMax ? Frozen(lineColor) : UiTheme.B(UiTheme.CardBack),
                Stroke = Frozen(lineColor), StrokeThickness = isMax ? 0 : 1.2,
            };
            Canvas.SetLeft(dot, pts[i].X - r);
            Canvas.SetTop(dot, pts[i].Y - r);
            canvas.Children.Add(dot);
        }

        // 命中区:不可见矩形垫在下面,每点一格;有 tooltip 或可点的才铺
        for (int i = 0; i < n; i++)
        {
            string? tip = i < tips.Count ? tips[i] : null;
            if (tip is null && onClick is null) continue;
            double left = n > 1 ? X(i) - step / 2 : 0;
            double wide = n > 1 ? step : width;
            left = Math.Max(0, left);
            wide = Math.Min(width - left, Math.Max(6, wide));
            var hit = new Rectangle
            {
                Width = wide, Height = plotH, Fill = Brushes.Transparent,
                ToolTip = tip,
                Cursor = onClick is null ? null : Cursors.Hand,
            };
            if (onClick is not null)
            {
                int captured = i;
                hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(captured); };
            }
            Canvas.SetLeft(hit, left);
            Canvas.SetTop(hit, 0);
            canvas.Children.Add(hit);
        }

        AddXLabels(canvas, width, plotH, xLabels, X);
    }

    /// <summary>Catmull-Rom → 三次贝塞尔;控制点 Y 夹在相邻两点之间,曲线不会过冲。</summary>
    private static IEnumerable<PathSegment> SmoothSegments(IReadOnlyList<Point> pts)
    {
        int n = pts.Count;
        if (n == 1) yield break;
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = i == 0 ? pts[0] : pts[i - 1];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < n ? pts[i + 2] : pts[n - 1];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Clamp(p1.Y + (p2.Y - p0.Y) / 6, p1.Y, p2.Y));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Clamp(p2.Y - (p3.Y - p1.Y) / 6, p1.Y, p2.Y));
            yield return new BezierSegment(c1, c2, p2, true);
        }
    }

    private static double Clamp(double v, double a, double b) =>
        v < Math.Min(a, b) ? Math.Min(a, b) : v > Math.Max(a, b) ? Math.Max(a, b) : v;

    private static void AddXLabels(Canvas canvas, double width, double plotY,
        IReadOnlyList<(int Index, string Text)>? labels,
        Func<int, double>? xOf = null)
    {
        if (labels is null) return;
        foreach (var (index, text) in labels)
        {
            var label = new TextBlock { Text = text, FontSize = 10, Foreground = TextSub };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double raw = xOf is null ? index : xOf(index) - label.DesiredSize.Width / 2;
            double x = Math.Max(0, Math.Min(width - label.DesiredSize.Width, raw));
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, plotY + 3);
            canvas.Children.Add(label);
        }
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

        // 每次打开都从「今天」开始(用户 2026-10-18 要求:不要默认 7 天)
        SpanToday.IsChecked = true;
        ModelSortTokens.IsChecked = true;
        // 合并卡的分段默认项:总览看"模型",套餐页看"今天"
        BreakdownModels.IsChecked = true;
        HourToday.IsChecked = true;
        foreach (var button in new[] { SpanToday, SpanWeek, SpanMonth, SpanCustom, SpanAll })
            button.Checked += Span_Checked;
        Loaded += (_, _) => { ReloadLedger(); HookResizeRedraw(); };
        Heatmap.DayClicked += EnterDayFocus;
        // 主题/密度在设置里切换时,这个窗可能正开着:重建一遍颜色与内边距
        UiTheme.Changed += OnUiThemeChanged;
        // 玻璃外壳:此刻句柄还没建出来,Backdrop 会挂到 SourceInitialized 上滞后应用。
        Backdrop.Apply(this, UiTheme.GlassKind, UiTheme.IsDark);
    }

    private void OnUiThemeChanged()
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            RenderAll();
            Heatmap.InvalidateVisual();
            // 主题变了:标题栏的明暗与背景键的 alpha 都要重套(否则深色窗顶着白标题栏)
            Backdrop.Apply(this, UiTheme.GlassKind, UiTheme.IsDark);
        });
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
        // 官方读数有缓存(内存 + 落盘),「重新读取」要真的重抓一遍
        OpenCodeUsageStatsClient.Invalidate();
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

        // 渠道列表(tab 徽标)。**区间必须与下面的 Render 用同一个**:自定义区间/按天下钻时
        // EffectiveRange() 才是真正生效的区间,只传 _span 会让徽标停在"今天"。
        var (rangeFrom, rangeTo) = _dayFilter is { } day
            ? (day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(TimeOnly.MinValue).AddDays(1))
            : (EffectiveRange().From, EffectiveRange().To);
        _channels = ChannelUsageIndex.Build(_ledger, _span, rangeFrom, rangeTo);
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
            Render(SpendSummary.Build(_ledger, rangeFrom, rangeTo));
    }

    // ————————————————— 视图切换 —————————————————

    /// <summary>正在重建 tab 行:此时设置 IsChecked 触发的 Checked 不能走点击路径(会递归重建)。</summary>
    private bool _syncingTabs;

    private void RenderViewTabs()
    {
        _syncingTabs = true;
        TabRow.Children.Clear();
        AddTab("总览", null, isActive: _selectedChannel is null, () =>
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
            AddTab(ch.Name, badge, isActive: _selectedChannel?.Key == ch.Key, () =>
            {
                _selectedChannel = captured;
                _dayFilter = null;
                SwitchView();
            });
        }
        _syncingTabs = false;
    }

    /// <summary>
    /// 视图页签。名称与徽标分开排:徽标(本区间用量/未启用)用 11 号灰字,
    /// 换视图时不跟着权重一起变粗——页签名才是可点的那个词。
    /// </summary>
    private void AddTab(string name, string? badge, bool isActive, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(badge))
        {
            content.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 11,
                Foreground = UiTheme.B(UiTheme.TextSub),
                Margin = new Thickness(7, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var tab = new RadioButton
        {
            Style = (Style)FindResource("ViewTab"),
            Content = content,
            // Content 现在是 StackPanel(名称+徽标),不是字符串;Tag 留给诊断取名用
            Tag = name,
            GroupName = "ViewTab",
            Margin = new Thickness(0, 0, 2, 0),
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
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = sub.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = TextMain,
            });

            var pools = new StackPanel();
            // 有读数的池在前;没读数(服务端没返回)的池不占行
            foreach (var pool in sub.Pools.Where(p => p.HasReading))
                pools.Children.Add(BuildPoolLine(pool));
            if (pools.Children.Count == 0)
            {
                // 全池无读数时把**原因**带出来:未订阅账号的灰快照里写的正是这个
                // ("未订阅或无权读取…"),比"接口没有返回可用额度"有用得多
                string? reason = sub.Pools.Select(p => p.Note).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                pools.Children.Add(new TextBlock
                {
                    Text = reason is { Length: > 0 } ? reason
                        : sub.FetchedAt is { } at
                            ? $"快照 {at.LocalDateTime:HH:mm},但接口没有返回可用额度。"
                            : "还没有快照。",
                    FontSize = 11.5, Foreground = TextSub,
                });
            }
            Grid.SetColumn(pools, 1);
            row.Children.Add(pools);
            QuotaPanel.Children.Add(row);

            // 订阅之间一条极淡的分隔线:三四个订阅叠在一个卡里时,行与行才分得清
            if (sub != _subs[^1])
                QuotaPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = UiTheme.B(UiTheme.Separator),
                    Margin = new Thickness(0, 2, 0, 10),
                });
        }

        QuotaHint.Text = "";
    }

    /// <summary>一条 pool 读数:标签 + 进度条 + 百分比/金额 + 重置倒计时。</summary>
    private FrameworkElement BuildPoolLine(PoolData pool)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2.5, 0, 2.5) };
        line.Children.Add(new TextBlock
        {
            Text = pool.Label,
            FontSize = 11.5,
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSub,
        });

        if (pool.HasPercent)
        {
            // 条宽 112:默认 640 窗口下,标签+条+读数+重置时间+「网页控制台」chip
            // 必须挤在约 520 里,132 会把 chip 顶出卡片右缘(实测被裁掉半个字)。
            line.Children.Add(BuildBar(pool.Fraction, 112));
            // 网页口径把 meters 的微美分原值一起带回来了:这时写金额比写"10.6 / 100"
            // 这种百分比分数好读,也正是网页卡片上那两个数($6.38 / $60)。
            string readout = pool is { LimitAmount: { } limitUsd, UsedAmount: { } usedUsd }
                ? $"{Money.Exact(usedUsd, "$")} / {Money.Exact(limitUsd, "$")}"
                : $"已用 {Money.Short(pool.Used ?? 0, pool.Unit)}"
                    + (pool.Cap is { } cap ? $" / {Money.Short(cap, pool.Unit)}" : "");
            var reading = new TextBlock
            {
                Text = $" {pool.PercentText} · {readout}",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMain,
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(reading, FontNumeralAlignment.Tabular);
            line.Children.Add(reading);
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
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSub,
        });
        // 读数口径:网页控制台(与 opencode.ai 网页同公式)才标;默认的 API Key
        // 接口是整数、向下取整,与网页可能差 1 个百分点,不标就等于默认口径。
        if (pool.ReadingFrom is { Length: > 0 } readingFrom)
        {
            line.Children.Add(new Border
            {
                Background = UiTheme.B(UiTheme.ChipBack),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "来自 opencode.ai 网页控制台(与网页卡片同一个公式),不是 API Key 接口的整数口径",
                Child = new TextBlock
                {
                    Text = readingFrom,
                    FontSize = 10,
                    Foreground = Accent,
                },
            });
        }
        return line;
    }

    /// <summary>比例条:底轨 + 填充,颜色按用量分档(&lt;60% 绿、&lt;85% 橙、其余红)。</summary>
    private FrameworkElement BuildBar(double fraction, double width)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var track = new Border
        {
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = FieldBack,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                CornerRadius = new CornerRadius(3.5),
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
        // **只算 BigModel 这个套餐**(见 MonthForChannel):以前这里是全账户的本月金额,
        // 把 GOAT/DeepSeek 的花费也塞进了 BigModel 的预算进度里,进度条天生虚高。
        var ch = _channels.FirstOrDefault(c => c.SourceKey == "bigmodel");
        if (ch is null) return false;
        var month = MonthForChannel(ch);
        double rate = CnyRate;
        double spentCny = month.Cost * rate;
        if (spentCny <= 0 && AppSettings.Current.BigModelMonthlyBudgetCny is null) return false;

        double? budget = AppSettings.Current.BigModelMonthlyBudgetCny;
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
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
            ToolTip = $"BigModel 包月本月(自 {monthStart:yyyy-MM-dd})按公开牌价估算 {SpendFormat.TokensExact(month.Tokens)} tokens,\n"
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

    /// <summary>
    /// 美元→人民币的换算系数(设置里 0 = 金额按美元原值显示)。估算金额一律走它,
    /// 免得各处各写一份 `rate <= 0 ? 1 : rate`。
    /// </summary>
    private static double CnyRate
    {
        get
        {
            double rate = AppSettings.Current.UsdToCny;
            return rate > 0 ? rate : 1;
        }
    }

    /// <summary>
    /// 某个渠道**自然月**(本月 1 日至今)的估算费用 / token / 次数。
    ///
    /// 为什么不能直接 <c>SpendSummary.Build</c>:那是**全账户**的。
    /// BigModel 的"包月预算进度"必须只算这个套餐——把 GOAT 的花费也算进去,
    /// 预算进度条就没有意义了(用户的预算只买 BigModel)。
    /// 与别处一样跳过 <see cref="SpendAggKind.Hour"/> 行,否则总量翻倍。
    /// </summary>
    private (double Cost, long Tokens, long Requests) MonthForChannel(ChannelUsage ch)
    {
        double cost = 0;
        long tokens = 0, requests = 0;
        if (_ledger is null) return (0, 0, 0);
        var today = DateTime.Today;
        var start = new DateTime(today.Year, today.Month, 1);
        var end = start.AddMonths(1);
        foreach (var e in _ledger.Entries)
        {
            if (e.Kind == SpendAggKind.Hour) continue;
            if (e.Timestamp < start || e.Timestamp >= end) continue;
            if (e.ProviderId is not { } pid || !ch.ProviderIds.Contains(pid)) continue;
            cost += e.Cost.Total;
            tokens += e.TotalTokens;
            requests += e.Requests;
        }
        return (cost, tokens, requests);
    }

    /// <summary>当前区间的短标签(对照卡的标题里要写清"本机记录"是多长时间)。</summary>
    private string RangeLabel() =>
        _dayFilter is { } day ? day.ToString("MM-dd")
        : _customActive ? $"{_customFrom:MM-dd} ~ {_customTo:MM-dd}"
        : _span switch
        {
            SpendSpan.Today => "今天",
            SpendSpan.Week => "近 7 天",
            SpendSpan.Month => "近 30 天",
            _ => "全部",
        };

    // ————————————————— 总览渲染 —————————————————

    /// <summary>概览页的曲线/热图按画布宽度画一次；窗口可拉伸，拉宽后必须重画（v1.22.2）。</summary>
    private bool _resizeHooked;
    private DispatcherTimer? _resizeTimer;

    private void HookResizeRedraw()
    {
        if (_resizeHooked) return;
        _resizeHooked = true;
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer!.Stop();
            if (_closed || _ledger is null) return;
            var (from, to) = _dayFilter is { } day
                ? (day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(TimeOnly.MinValue).AddDays(1))
                : (EffectiveRange().From, EffectiveRange().To);
            RenderChart(SpendSummary.Build(_ledger, from, to));
            RenderHeatmap();
        };
        void OnSizeChanged(object? s, SizeChangedEventArgs e)
        {
            _resizeTimer?.Stop();
            _resizeTimer?.Start();
        }
        ChartCanvas.SizeChanged += OnSizeChanged;
        Heatmap.SizeChanged += OnSizeChanged;
    }

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
            + (rate > 0 ? $"金额按 $1 ≈ ¥{rate:0.00} 换算。" : "金额为美元原值。"));
        if (summary.UnpricedTokens > 0)
            notes.Add($"{summary.UnpricedModels} 个模型没有公开价,只计数、不计金额;金额带 * 的是有价部分小计。");
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
        RenderRecentSessions(summary);

        Footnote.Text = _ledger is { } l && l.Notes.Count > 0
            ? "读取提示:" + string.Join(";", l.Notes)
            : "";
    }

    /// <summary>
    /// 最近对话(最近 6 个会话)。会话行只由源库明细喂,所以这 6 行的合计与顶部的总量
    /// 不必相等——两套口径,各自诚实。点一行弹会话详情窗。
    /// **6 行之后补一行"其余 N 个会话"的合计**:否则用户看到 6 行、又看到顶部总量,
    /// 会以为是同一件事的两个数字(实际是"6 个"与"全部"的差别),这正是他提过的"对不上"。
    /// </summary>
    private void RenderRecentSessions(SpendSummary summary)
    {
        const int limit = 6;
        var rows = summary.SessionRows.Take(limit).ToList();
        RecentSessionSection.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderList(RecentSessionList, rows.Select(x => new RowVm
        {
            Name = string.IsNullOrWhiteSpace(x.Title) ? x.SessionId : x.Title!,
            Tokens = SpendFormat.Tokens(x.Tokens),
            TokensTip = $"{SpendFormat.TokensExact(x.Tokens)} tokens",
            Amount = SpendFormat.Amount(x.Cost, x.HasUnpriced),
            AmountTip = SpendFormat.AmountTip(x.Cost, x.HasUnpriced),
            Detail = $"{x.Agent} · {x.Project ?? "无项目"} · {x.Last:MM-dd HH:mm}",
            BarWidth = Fraction(x.Tokens, rows[0].Tokens) * BarBase,
            BarBrush = BarSessions,
            SessionId = x.SessionId
        }).ToList());

        var rest = summary.SessionRows.Skip(rows.Count).ToList();
        if (rest.Count == 0)
        {
            RecentSessionMore.Visibility = Visibility.Collapsed;
            return;
        }
        long restTokens = rest.Sum(x => x.Tokens);
        double restCost = rest.Sum(x => x.Cost);
        bool restUnpriced = rest.Any(x => x.HasUnpriced);
        RecentSessionMore.Text = $"其余 {rest.Count} 个会话 · {SpendFormat.Tokens(restTokens)} tokens · "
            + $"{SpendFormat.Amount(restCost, restUnpriced)}(合计 {rows.Count + rest.Count} 个)";
        RecentSessionMore.Visibility = Visibility.Visible;
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
            : (unpriced.Count > 0 ? $"无公开价(只计数、不计金额):{string.Join("、", unpriced)}" : "");
    }

    private static void RenderList(ItemsControl target, List<RowVm> rows)
    {
        target.ItemsSource = rows.Count > 0 ? rows : null;
    }

    // ————————————————— 每日柱图(自动降采样 + 点击下钻) —————————————————

    /// <summary>
    /// 柱状图（v1.22.3）：每日用量与时段分布改回柱形——"哪一天/哪一小时多"用高度比曲线更直接。
    /// 柱宽随格宽自适应（2.5~30），纵向渐变（越高越实），峰值柱用实色；
    /// 峰时段仍用橙色底纹、不染柱身（两个维度不打架）；每柱一块透明命中区承载悬停与下钻。
    /// overlay 存在时叠一条细折线（时段分布的"其中输出"）。
    /// </summary>
    private static void DrawBarChart(
        Canvas canvas,
        double width,
        double height,
        IReadOnlyList<double> values,
        IReadOnlyList<string?> tips,
        Color barColor,
        Color bandColor,
        Func<int, bool>? band,
        Action<int>? onClick,
        IReadOnlyList<(int Index, string Text)>? xLabels,
        double labelBand = 16,
        IReadOnlyList<double>? overlay = null,
        Color overlayColor = default)
    {
        canvas.Children.Clear();
        int n = values.Count;
        if (n == 0 || width <= 1 || height <= 1) return;

        double plotH = Math.Max(8, height - labelBand);
        const double padX = 7;
        double spanX = Math.Max(1, width - padX * 2);
        double slot = spanX / n;
        double barW = Math.Clamp(slot * 0.66, 2.5, 30);
        double Center(int i) => padX + slot * (i + 0.5);

        long max = 0;
        for (int i = 0; i < n; i++) max = Math.Max(max, (long)values[i]);

        var baseline = new Rectangle { Width = width, Height = 1, Fill = UiTheme.B(UiTheme.ChartTrack) };
        Canvas.SetLeft(baseline, 0);
        Canvas.SetTop(baseline, plotH);
        canvas.Children.Add(baseline);

        if (max <= 0)
        {
            var empty = new TextBlock { Text = "这个区间没有记录", FontSize = 11, Foreground = TextSub };
            Canvas.SetLeft(empty, padX);
            Canvas.SetTop(empty, plotH / 2 - 8);
            canvas.Children.Add(empty);
            return;
        }

        // 两条淡网格线（半格/满格）：让高度可读，又不加刻度文字把图面弄噪
        foreach (double frac in new[] { 0.5, 1.0 })
        {
            var grid = new Rectangle { Width = width, Height = 1, Fill = UiTheme.B(UiTheme.ChartTrack) };
            Canvas.SetLeft(grid, 0);
            Canvas.SetTop(grid, plotH - plotH * frac * 0.94);
            canvas.Children.Add(grid);
        }

        double topRoom = 8;
        double usable = Math.Max(4, plotH - topRoom);

        // 峰时段底纹（先画，压在柱后）
        if (band is not null)
        {
            int i = 0;
            while (i < n)
            {
                if (!band(i)) { i++; continue; }
                int j = i;
                while (j + 1 < n && band(j + 1)) j++;
                double x1 = Math.Max(0, padX + slot * i);
                double x2 = Math.Min(width, padX + slot * (j + 1));
                var rect = new Rectangle
                {
                    Width = Math.Max(2, x2 - x1),
                    Height = Math.Max(4, plotH - 4),
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = BandFill(bandColor),
                };
                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, 3);
                canvas.Children.Add(rect);
                i = j + 1;
            }
        }

        int maxIndex = 0;
        for (int i = 1; i < n; i++) if (values[i] > values[maxIndex]) maxIndex = i;

        // 统一纵向渐变：所有柱共用一支 brush，柱越高顶部越实
        var fill = new LinearGradientBrush(
            Color.FromArgb(0xE6, barColor.R, barColor.G, barColor.B),
            Color.FromArgb(0x4D, barColor.R, barColor.G, barColor.B),
            new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        var peakFill = Frozen(barColor);

        for (int i = 0; i < n; i++)
        {
            double v = Math.Max(0, values[i]);
            double h = v / max * usable;
            double x = Center(i) - barW / 2;
            if (h < 1.5)
            {
                // 零值：贴底 2px 小点，表示"这段有格子但没用量"
                var dot = new Rectangle
                {
                    Width = barW, Height = 2, RadiusX = 1, RadiusY = 1,
                    Fill = UiTheme.B(UiTheme.ChartZero),
                };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, plotH - 2);
                canvas.Children.Add(dot);
            }
            else
            {
                var bar = new Rectangle
                {
                    Width = barW,
                    Height = h,
                    RadiusX = Math.Min(3, barW / 2),
                    RadiusY = Math.Min(3, barW / 2),
                    Fill = i == maxIndex ? peakFill : fill,
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, plotH - h);
                canvas.Children.Add(bar);
            }

            // 透明命中区：悬停提示 + 下钻（与曲线版行为一致）
            bool hasTip = tips.Count > i && tips[i] is not null;
            if (hasTip || onClick is not null)
            {
                var hit = new Rectangle
                {
                    Width = Math.Max(2, slot),
                    Height = plotH,
                    Fill = Brushes.Transparent,
                };
                if (hasTip) hit.ToolTip = tips[i];
                if (onClick is not null)
                {
                    hit.Cursor = Cursors.Hand;
                    int captured = i;
                    hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(captured); };
                }
                Canvas.SetLeft(hit, padX + slot * i);
                Canvas.SetTop(hit, 0);
                canvas.Children.Add(hit);
            }
        }

        // 叠加折线（可选）
        if (overlay is { Count: > 1 })
        {
            var fig = new PathFigure
            {
                StartPoint = new Point(Center(0), plotH - Math.Max(0, overlay[0]) / max * usable),
                IsClosed = false,
            };
            for (int i = 1; i < n; i++)
                fig.Segments.Add(new LineSegment(
                    new Point(Center(i), plotH - Math.Max(0, i < overlay.Count ? overlay[i] : 0) / max * usable), false));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo,
                Stroke = Frozen(overlayColor),
                StrokeThickness = 1.4,
                StrokeDashArray = [3, 2],
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        // 横轴刻度：居中到柱心，贴边时夹回画布
        if (xLabels is not null)
        {
            foreach (var (index, text) in xLabels)
            {
                if (index < 0 || index >= n) continue;
                var tb = new TextBlock { Text = text, FontSize = 10, Foreground = TextSub };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = Math.Clamp(Center(index) - tb.DesiredSize.Width / 2, 0,
                    Math.Max(0, width - tb.DesiredSize.Width));
                Canvas.SetLeft(tb, lx);
                Canvas.SetTop(tb, plotH + 2);
                canvas.Children.Add(tb);
            }
        }
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
            ? "点柱子看那一天的明细。"
            : $"天数较多,已按{unit}聚合(峰值 {SpendFormat.Tokens(bars.Max(b => b.Tokens))})。";

        double width = ChartCanvas.ActualWidth > 0 ? ChartCanvas.ActualWidth : 500;
        const double height = 132;
        int count = bars.Count;

        var values = bars.Select(b => (double)b.Tokens).ToList();
        var tips = bars.Select(b => b.Start == b.End
            ? $"{b.Start:yyyy-MM-dd}\n{SpendFormat.TokensExact(b.Tokens)} tokens\n{SpendFormat.MoneyExact(b.Cost)}"
            : $"{b.Start:MM-dd} ~ {b.End:MM-dd}\n{SpendFormat.TokensExact(b.Tokens)} tokens\n{SpendFormat.MoneyExact(b.Cost)}"
                + (b.HasUnpriced ? "\n(含无公开价的模型)" : "")).ToList<string?>();
        var labels = new List<(int, string)>();
        foreach (var index in new[] { 0, count / 2, count - 1 }.Distinct())
            labels.Add((index, bars[index].Start.ToString("MM-dd")));

        // 下钻只对"一天"的桶有意义,聚合过的桶不给点
        Action<int>? drill = unit == "天" ? i => EnterDayFocus(bars[i].Start) : null;

        DrawBarChart(ChartCanvas, width, height, values, tips,
            AccentColor, AccentColor, band: null, onClick: drill, xLabels: labels);
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
        if (_ledger is null || _ledger.Entries.Count == 0)
        {
            HeatHint.Text = "还没有记录。";
            Heatmap.SetData(new Dictionary<DateOnly, (long, double)>(), DateOnly.FromDateTime(DateTime.Today),
                DateOnly.FromDateTime(DateTime.Today), 1, 0);
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
            Heatmap.SetData(new Dictionary<DateOnly, (long, double)>(), DateOnly.FromDateTime(DateTime.Today),
                DateOnly.FromDateTime(DateTime.Today), 1, 0);
            return;
        }

        const double cell = 12, gap = 3, step = cell + gap;
        double available = Heatmap.ActualWidth > 0 ? Heatmap.ActualWidth : 400;
        int fitColumns = Math.Max(1, (int)(available / step));
        var today = DateTime.Today;
        // 本周周一为最后一列;往回数 weekColumns 列
        var lastMonday = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        // 永远铺满可用宽(锚定今天往回数):数据短时格子里自然有空周(画淡框),
        // 数据长时以上限 60 周(约 14 个月)截断。居中/右贴都试过,看起来都像"没画出来"。
        int weekColumns = Math.Clamp(fitColumns, 1, 60);
        var firstMonday = lastMonday.AddDays(-7 * (weekColumns - 1));
        long max = byDay.Values.Max(v => v.Tokens);

        Heatmap.SetData(byDay, DateOnly.FromDateTime(firstMonday), DateOnly.FromDateTime(today), weekColumns, max);

        var earliest = byDay.Keys.Min();
        var firstVisibleMonday = DateOnly.FromDateTime(firstMonday);
        string truncated = earliest < firstVisibleMonday
            ? $"   ·   {firstVisibleMonday.AddDays(-1):yyyy-MM-dd} 之前的 {byDay.Count(d => d.Key < firstVisibleMonday)} 天没画下(宽不够),数字仍在统计里"
            : "";
        HeatHint.Text = $"共 {byDay.Count} 天有记录   ·   最忙 {byDay.Aggregate((a, b) => a.Value.Tokens > b.Value.Tokens ? a : b).Key:yyyy-MM-dd}"
            + $"   ·   点格子看那一天{truncated}";
    }

    private static Brush HeatBrush(long tokens, long max)
    {
        Color color = max <= 0 || tokens <= 0
            ? UiTheme.HeatEmpty
            : (tokens * 4d / max) switch
            {
                < 1 => UiTheme.HeatL1,
                < 2 => UiTheme.HeatL2,
                < 3 => UiTheme.HeatL3,
                _ => UiTheme.HeatL4,
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

    /// <summary>总览的「构成与明细」分段:模型 / 来源 / 项目 共用一张卡,只翻可见性。</summary>
    private void Breakdown_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (ModelList is null || AgentList is null || ProjectList is null) return;

        ModelList.Visibility = tag == "Models" ? Visibility.Visible : Visibility.Collapsed;
        AgentList.Visibility = tag == "Agents" ? Visibility.Visible : Visibility.Collapsed;
        ProjectList.Visibility = tag == "Projects" ? Visibility.Visible : Visibility.Collapsed;
        // "有算不出价的模型"这条提示只跟模型列表有关,切走就别留着
        if (ModelHint is not null)
            ModelHint.Visibility = tag == "Models" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>套餐页「时段分布」:今天 / 整个区间 共用一张卡。切档后画布才有宽度,
    /// 所以重画要等布局跑完(否则刚变可见的 Canvas ActualWidth 还是 0,画出来是空白)。</summary>
    private void HourScope_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (HourTodayPanel is null || HourRangePanel is null) return;

        bool today = tag == "Today";
        HourTodayPanel.Visibility = today ? Visibility.Visible : Visibility.Collapsed;
        HourRangePanel.Visibility = today ? Visibility.Collapsed : Visibility.Visible;

        if (!IsLoaded || _selectedChannel is not { } ch) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (today) RenderHourlyChart(ch);
            else RenderChannelPeak(ch);
        }), DispatcherPriority.Loaded);
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
            $"{SpendFormat.Tokens(ch.Tally.CacheRead)}/{SpendFormat.Tokens(ch.Tally.CacheWrite)}",
            null, "Cache");
        // —— Token 构成四格:输入 / 输出 / 缓存读 / 合计 ——
        // 「缓存写」并进 KPI 的「缓存读 / 写」那一格、「未分类」并进合计的脚注——
        // 单列出来只会把这一排挤成两行(用户实测窗口 560 宽时正是这样)。
        ChannelTokenGrid.Children.Clear();
        var t = ch.Tally;
        AddCell(ChannelTokenGrid, "输入", SpendFormat.Tokens(t.Input), null, "Tokens");
        AddCell(ChannelTokenGrid, "输出", SpendFormat.Tokens(t.Output), null, "Tokens");
        AddCell(ChannelTokenGrid, "缓存读", SpendFormat.Tokens(t.CacheRead),
            ch.CacheHit is { } ch2 ? $"命中率 {ch2:P1}" : null, "Cache");
        // 合计用短写法(与 KPI 的"总 TOKEN"同一口径),精确值进悬停——精确的 9 位数
        // 一格就要 110diu,这一排就排不下了
        var total = AddCell(ChannelTokenGrid, "合计", SpendFormat.Tokens(ch.TotalTokens),
            ch.UnclassifiedTokens > 0 ? $"含未分类 {SpendFormat.Tokens(ch.UnclassifiedTokens)}" : null,
            "Tokens");
        total.ToolTip = $"{SpendFormat.TokensExact(ch.TotalTokens)} tokens"
            + (ch.UnclassifiedTokens > 0
                ? $"\n其中 {SpendFormat.TokensExact(ch.UnclassifiedTokens)} 来源只报总量、不参与计价"
                : "");
        // 这两格的值短,列宽下限可以更松:用户窗口 560 宽(内容区约 466)也要排成一排。
        // 瓦片化后每格多了左右 22 的内边距,下限跟着上调:640 宽时仍是 5 格一排。
        AutoColumns(ChannelKpiGrid, minCell: 104);
        AutoColumns(ChannelTokenGrid, minCell: 104);

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
            // 没有额度接口的套餐(BigModel 包月):用"自己填的包月预算"当分母。
            // 不这么做的话这一页连"还剩多少"都没有,比别的套餐页整整少一块。
            if (ch.SourceKey == "bigmodel") RenderBudgetQuota(ch);
            else ChannelQuotaCard.Visibility = Visibility.Collapsed;
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
        ChannelQuotaHint.Text = "";
    }

    /// <summary>
    /// 没有额度接口的套餐(BigModel 包月)的额度区:分母 = 设置里手填的包月预算,
    /// 分子 = 本月按公开牌价估算的金额。与额度条上那一行同一口径(只算这个套餐)。
    /// </summary>
    private void RenderBudgetQuota(ChannelUsage ch)
    {
        ChannelQuotaCard.Visibility = Visibility.Visible;
        ChannelQuotaPanel.Children.Clear();

        var month = MonthForChannel(ch);
        double spentCny = month.Cost * CnyRate;
        double? budget = AppSettings.Current.BigModelMonthlyBudgetCny;
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        int dayOfMonth = Math.Max(1, (today - monthStart).Days + 1);
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        double projected = spentCny / dayOfMonth * daysInMonth;

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock
        {
            Text = "包月预算",
            FontSize = 11.5,
            Width = 46,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TextSub,
        });
        if (budget is { } b && b > 0)
        {
            line.Children.Add(BuildBar(spentCny / b, 132));
            line.Children.Add(new TextBlock
            {
                Text = $" ¥{spentCny:N0} / ¥{b:N0} · 月底约 ¥{projected:N0}",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMain,
            });
        }
        else
        {
            line.Children.Add(new TextBlock
            {
                Text = $" 本月已估算 ¥{spentCny:N0} · 还没填预算",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TextMain,
            });
        }
        ChannelQuotaPanel.Children.Add(line);

        ChannelQuotaHint.Text = budget is { } b2 && b2 > 0
            ? $"分母是设置里填的包月预算,分子是本月(自 {monthStart:MM-dd})按公开牌价估算的金额。"
            : $"没填包月预算,只显示本月估算金额(自 {monthStart:MM-dd}、{SpendFormat.Tokens(month.Tokens)} tokens)。";
    }

    // ————————————————— 账号计量对照(官方服务端 vs 本机记录) —————————————————

    /// <summary>
    /// 官方口径对照卡。**每个套餐页都有这一块**,但内容只能是这个渠道真能拿到的官方读数:
    /// GOAT 有账号级 token 计量(能算"官方 vs 本机"的差值),DeepSeek 只有余额,
    /// OpenCode Go 只给百分比,BigModel 干脆没有接口(只能拿手填预算当分母)。
    /// 宁可如实说明口径差异,也不要凑几个看起来能对上的数字——那比没有更坏。
    /// </summary>
    private void RenderAccountMetering(ChannelUsage ch)
    {
        AccountMeterGrid.Children.Clear();
        AccountModelList.ItemsSource = null;
        AccountModelSection.Visibility = Visibility.Collapsed;
        _meteringSeq++;   // 作废在途的官方明细异步加载(切页/切区间后旧响应不得再回填)

        switch (ch.SourceKey)
        {
            case "goat": RenderGoatAccountMetering(ch); return;
            case "deepseek": RenderBalanceMetering(ch); return;
            case "opencode": RenderOpenCodeMetering(ch); return;
            case "bigmodel": RenderBudgetMetering(ch); return;
            default: AccountMeterCard.Visibility = Visibility.Collapsed; return;
        }
    }

    /// <summary>对照区的异步加载代次:每次重渲染自增,回调比对,旧响应直接丢弃。</summary>
    private int _meteringSeq;

    /// <summary>
    /// GOAT:官方账期读数覆盖**所有设备、所有 key**,本机记录只有这台电脑的客户端——
    /// 差值就是其他设备/命令行直接使用的量。
    /// 上一版用户发现"官方 37.4 亿 vs 本机 34.2 亿"对不上,根因就是这个,这里把差值摆到明面上。
    /// </summary>
    private void RenderGoatAccountMetering(ChannelUsage ch)
    {
        var sub = ch.SourceKey is { } key ? _subs.FirstOrDefault(s => s.Key == key) : null;
        long? officialRead = sub?.PeriodTokens;
        bool show = officialRead is { };
        AccountMeterCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        AccountMeterTitle.Text = "账号计量对照";
        AccountModelSection.Visibility = Visibility.Visible;
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
            diff < 0 ? "—" : SpendFormat.Tokens(diff),
            diff > 0 && diffPctOfOfficial is { } p
                ? $"占官方 {p:0.0}%"
                : diff < 0 ? $"本机比官方多 {SpendFormat.Tokens(-diff)}" : "两边一致",
            "Cache");
        AutoColumns(AccountMeterGrid);

        AccountMeterHint.Text = "官方 = 服务端账期计量(含其他设备与 key);本机 = 这台电脑的客户端记录。";
        var (verdict, why) = ClassifyAccountGap(officialTokens, localTokens, periodStart);
        AccountMeterHint.Text += $"\n判定:{verdict} — {why}";
        // 账本完整度:本机读数是否可信,本身就是对账的一半(C 稿说的"这一段完整吗")。
        if (_ledger is { } lg && lg.Notes.Count > 0)
            AccountMeterHint.Text += $"\n账本:{string.Join(";", lg.Notes)}";

        // 对账历史：这一眼看到的两个数落一条，供日后做重置残留审计（同分钟去重）
        ReconcileLog.Append("goat", $"账期自 {periodStart:MM-dd HH:mm}", officialTokens, localTokens, verdict);

        _ = LoadAccountModelsAsync(ch, periodStart);
    }

    /// <summary>
    /// 把"官方 − 本机"判成一个档位，而不是只给一个数字。四档来自本项目真实踩过的坑：
    /// v1.21.3（一把过期 Key 静默停更 10.3 小时）、v1.21.6（官方当日账未结算，本机反超）、
    /// 以及"官方含其他设备/其他 Key"这条结构性差异。
    /// **落不进任何一档的宁可显示"无法判定"，不许猜**——这是本项目一贯的口径。
    /// </summary>
    private static (string Verdict, string Why) ClassifyAccountGap(
        long official, long local, DateTime periodStart)
    {
        if (official <= 0) return ("无法判定", "官方读数缺失");
        if (local <= 0) return ("本机无记录", "这台电脑没有该渠道的用量");
        long diff = official - local;
        double ratio = Math.Abs(diff) / (double)official;
        if (ratio <= 0.02) return ("两边一致", "差额在 2% 以内");

        if (diff > 0)
            return ("他端用量", "官方含其他设备或其他 Key 的用量");

        // 本机比官方多:账期未结束时最常见的原因是官方当日还没结算。
        return (DateTime.Now - periodStart).TotalDays < 31
            ? ("官方未结算", "账期还没结束,当天读数入库有延迟")
            : ("本机多记", "官方已结算仍少于本机,查客户端是否有重复计数");
    }

    /// <summary>官方逐模型明细(/internal/usage/charts,浏览器登录态):按模型聚合整个账期。</summary>
    private async Task LoadAccountModelsAsync(ChannelUsage ch, DateTime periodStart)
    {
        int seq = _meteringSeq;
        AccountModelHint.Text = "正在读取官方逐模型明细…";
        AccountModelList.ItemsSource = null;
        var from = new DateTimeOffset(periodStart, TimeZoneInfo.Local.GetUtcOffset(periodStart));
        var buckets = await CommandCodeWebSession.TryFetchModelCacheAsync(from, DateTimeOffset.Now);
        if (_closed || seq != _meteringSeq) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        if (buckets is null)
        {
            AccountModelList.ItemsSource = null;
            AccountModelHint.Text = CommandCodeWebSession.LastStatus is { } status
                && status.Contains("未启用", StringComparison.Ordinal)
                ? "要读这块:在 设置 → 数据源 打开「浏览器会话明细」并登录 commandcode.ai。"
                : "官方逐模型明细读不到:" + (CommandCodeWebSession.LastStatus ?? "未知原因");
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
        AccountModelHint.Text = $"官方账号级合计 {SpendFormat.Tokens(officialTotal)} tokens(自 {periodStart:MM-dd} 起,含所有设备与 Key)。";
    }

    /// <summary>
    /// DeepSeek(余额型):官方能给的只有"账户余额",没有 token 计量——所以这一页做的是
    /// "钱还剩多少 vs 本机这段时间花了多少",不是 GOAT 那种逐 token 对账。
    /// </summary>
    private void RenderBalanceMetering(ChannelUsage ch)
    {
        var sub = _subs.FirstOrDefault(s => s.Key == "deepseek");
        var balance = sub?.Pools.FirstOrDefault(p => p.PoolKind == "Balance");
        if (balance is null && !ch.HasData)
        {
            AccountMeterCard.Visibility = Visibility.Collapsed;
            return;
        }
        AccountMeterCard.Visibility = Visibility.Visible;
        AccountMeterTitle.Text = "账号口径对照";

        string unit = balance?.Unit ?? "¥";
        AddCell(AccountMeterGrid, "官方余额",
            balance?.Amount is { } amount ? Money.Exact(amount, unit) : "—",
            balance?.ToppedUpAmount is { } topup
                ? $"充值 {Money.Short(topup, unit)}"
                    + (balance.GrantedAmount > 0 ? $" · 赠送 {Money.Short(balance.GrantedAmount.Value, unit)}" : "")
                : null,
            "Money");
        AddCell(AccountMeterGrid, balance?.EstimateFrom is { } usedSince ? $"{usedSince}已用" : "官方累计已用",
            balance?.Used is { } used ? Money.Exact(used, unit) : "—",
            balance?.Cap is { } cap ? $"基线 {Money.Short(cap, unit)}(观测峰值)" : null,
            "Tokens");
        AddCell(AccountMeterGrid, $"本机记录({RangeLabel()})",
            ch.HasData ? SpendFormat.Amount(ch.Cost, ch.HasUnpriced) : "—",
            ch.HasData ? $"{SpendFormat.Tokens(ch.TotalTokens)} tokens · {ch.Requests:N0} 次" : "这个区间没有记录",
            "Cache");
        AutoColumns(AccountMeterGrid);

        string hint = "官方余额是账户真实读数(含这台电脑之外的消耗);本机记录只有这台电脑,两者不能相减。";
        if (balance?.Amount is { } current && ch.HasData && ch.Cost > 0)
        {
            var (from, to) = EffectiveRange();
            double days = Math.Max(1, (to - from).TotalDays);
            double perDay = ch.Cost * CnyRate / days;
            if (perDay > 0)
            {
                double left = current / perDay;
                // 余额几十块、日均几毛钱时"还能用 607 天"没有可读性,折成年更直观
                hint += left >= 365
                    ? $" 按本区间日均 ¥{perDay:0.00} 估,余额约还能用 {left / 365:0.#} 年。"
                    : $" 按本区间日均 ¥{perDay:0.00} 估,余额约还能用 {left:0} 天。";
            }
        }
        AccountMeterHint.Text = hint;
    }

    /// <summary>本机台账在 from 之后、同渠道(按套餐的 provider id 过滤)的聚合;跳过小时行防与天级行翻倍。</summary>
    private (long Tokens, long Requests) CountLocalSince(ChannelUsage ch, DateTime from)
    {
        long tokens = 0, requests = 0;
        if (_ledger is { } ledger)
        {
            foreach (var e in ledger.Entries)
            {
                if (e.Timestamp < from) continue;
                if (e.Kind == SpendAggKind.Hour) continue;
                if (e.ProviderId is not { } pid || !ch.ProviderIds.Contains(pid)) continue;
                tokens += e.TotalTokens;
                requests += e.Requests;
            }
        }
        return (tokens, requests);
    }

    /// <summary>
    /// OpenCode Go:官方额度接口只回百分比(5h/周/月);token 级的"网页版原始数据"在服务端的
    /// **账号级聚合**里(近 7/30 天、覆盖**所有设备与 Key**),要走浏览器会话通道。
    /// 这里先摆基础三格,然后异步拉官方读数:拉到就追加「官方 vs 本机」对照两格与逐模型榜;
    /// 拉不到就如实说明原因与开法。
    /// </summary>
    private void RenderOpenCodeMetering(ChannelUsage ch)
    {
        var sub = _subs.FirstOrDefault(s => s.Key == "opencode" || s.Key.StartsWith("opencode:", StringComparison.Ordinal));
        if (sub is null && !ch.HasData)
        {
            AccountMeterCard.Visibility = Visibility.Collapsed;
            return;
        }
        AccountMeterCard.Visibility = Visibility.Visible;
        AccountMeterTitle.Text = "官方口径对照";

        // 先摆骨架(官方那格显示"读取中…"),官方读数回来后再整格重建——位置不变、只填数字
        RenderOpenCodeMeterGrid(ch, stats: null);

        // 原来这句写"三个数是同一个窗口"——**是错的**:官方没有自然日档,「今天」只能取滚动
        // 24 小时,比本机的"今天"多含昨晚那一段。不如实写出来,用户就会拿两个窗口的数直接相减。
        AccountMeterHint.Text = "官方是滚动 24 小时窗口(比本机的今天多含昨晚),含所有设备与 Key;"
            + "当日读数入库有延迟,刚跑完的量可能还没进官方。";

        _ = LoadOpenCodeOfficialAsync(ch);
    }

    /// <summary>
    /// OpenCode Go 的「官方 vs 本机」对照格。**三个可比的数字必须同窗口、而且挨着摆**——
    /// 之前把"本机记录(近 30 天)"钉死在 30 天、差值却按 7 天算，并排一看就像对不上。
    /// 窗口跟随上方区间(今天 = 近 24 小时、近 7 天 = 7 天,其余 = 近 30 天,官方最多只给 30 天)。
    /// </summary>
    private void RenderOpenCodeMeterGrid(ChannelUsage ch, OpenCodeUsageStats? stats, bool record = true)
    {
        var sub = _subs.FirstOrDefault(s => s.Key == "opencode" || s.Key.StartsWith("opencode:", StringComparison.Ordinal));
        var (range, windowLabel) = OpenCodeUsageStatsClient.WindowFor(_span);
        long localTokens, localRequests;
        if (_span is SpendSpan.Today or SpendSpan.Week)
        {
            var from = range == "24h" ? DateTime.Now.AddHours(-24) : DateTime.Now.AddDays(-7);
            (localTokens, localRequests) = CountLocalSince(ch, from);
        }
        else
        {
            // 自定义/全部也按官方的 30 天上限对齐,取本机同一窗口
            (localTokens, localRequests) = CountLocalSince(ch, DateTime.Now.AddDays(-30));
        }

        AccountMeterGrid.Children.Clear();

        // 本机只有"自然日"粒度(agg_day),「今天」这一档实际算的是今天 00:00 起;
        // 官方**没有自然日档**、只有滚动 24 小时 —— 标签必须说清楚,否则两个数并排会被读成同窗口。
        string localLabel = _span == SpendSpan.Today ? "今天" : windowLabel;
        AddCell(AccountMeterGrid, $"本机记录({localLabel})",
            ch.HasData || localTokens > 0 ? SpendFormat.Tokens(localTokens) : "—",
            localTokens > 0
                ? $"{localRequests:N0} 次 · 这台电脑"
                : (ch.LifetimeTokens > 0 ? "这个窗口没有记录" : "这台电脑没有记录"),
            "Tokens");

        AddCell(AccountMeterGrid, $"官方用量({windowLabel},所有设备/Key)",
            stats is null ? "读取中…" : SpendFormat.Tokens(stats.TotalTokens),
            stats is null
                ? "走浏览器会话通道"
                : (stats.Requests > 0 ? $"{stats.Requests:N0} 次请求" : null)
                    + (stats.Cost > 0 ? $" · 官方计费 ${stats.Cost:0.00}" : null),
            "Money");

        long diff = (stats?.TotalTokens ?? 0) - localTokens;
        AddCell(AccountMeterGrid, "差值(其他设备/CLI)",
            // 官方含所有设备,理论上应 ≥ 本机;本机更多时**绝不能给一个负的"其他设备用量"**
            // ——那说明官方当天的账还没结算完(实测:官方当日值 1.75 小时只涨 340 万,
            // 同期本机涨 1.2 亿)。这里给"—",真实差额写进副标题。
            stats is null || diff < 0 ? "—" : SpendFormat.Tokens(diff),
            stats is null
                ? "等官方读数回来"
                : diff > 0 && stats.TotalTokens > 0
                    ? $"占官方 {diff * 100.0 / stats.TotalTokens:0.0}%"
                    : diff < 0
                        ? $"官方当日未结算完(本机多 {SpendFormat.Tokens(-diff)})"
                        : "两边一致",
            "Cache");

        string official = sub is null
            ? "—"
            : string.Join("/", new[] { "FiveHour", "Weekly", "Monthly" }
                .Select(kind => sub.Pools.FirstOrDefault(p => p.PoolKind == kind))
                // 三个数挤在一格(5 列的排法下格宽约 108diu),去掉重复的 %:口径见下面那行脚注
                .Select(pool => pool is { HasPercent: true } ? pool.PercentText.TrimEnd('%') : "--"));
        AddCell(AccountMeterGrid, "官方已用(5h/周/月)", official,
            sub is not null && sub.Pools.Any(p => p.ReadingFrom is { Length: > 0 })
                ? "网页控制台口径(与网页同公式)"
                : "API Key 接口只给整数百分比",
            "Money");

        if (ch.LifetimeTokens > 0)
            AddCell(AccountMeterGrid, "本机全时段累计", SpendFormat.Tokens(ch.LifetimeTokens),
                $"{ch.LifetimeRequests:N0} 次"
                    + (ch.LifetimeFirst is { } first ? $" · 自 {first:yyyy-MM-dd}" : "")
                    + " · 与上面不是同一窗口",
                "Sessions");
        AutoColumns(AccountMeterGrid);

        // 差值落到一个档位:否则"官方 1.39 亿 / 本机 1.87 亿"这种并列只会让人怀疑工具。
        if (stats is not null)
        {
            var (verdict, why) = ClassifyOpenCodeGap(stats.TotalTokens, localTokens, _span);
            AccountMeterHint.Text = $"判定:{verdict} — {why}";
            if (_ledger is { } lg2 && lg2.Notes.Count > 0)
                AccountMeterHint.Text += $"\n账本:{string.Join(";", lg2.Notes)}";

            // 对账历史：官方滚动窗口 vs 本机同窗口（同分钟去重）。
            // 离屏出图的合成读数不落——那是假数据，进了历史会污染"重置残留审计"。
            if (record)
                ReconcileLog.Append("opencode", windowLabel, stats.TotalTokens, localTokens, verdict);
        }
    }

    /// <summary>
    /// OpenCode 官方是本机之外的另一侧(含其他设备/Key),且官方只有**滚动窗口**
    /// (24h/7d/30d),而本机按自然日聚合——两者天然不同窗口,不能直接相减后当结论。
    /// 这里给四档;落不进任何一档的宁可"无法判定"。
    /// </summary>
    private static (string Verdict, string Why) ClassifyOpenCodeGap(
        long official, long local, SpendSpan span)
    {
        if (official <= 0) return ("无法判定", "官方读数缺失");
        if (local <= 0) return ("本机无记录", "这台电脑没有该渠道的用量");
        long diff = official - local;
        if (Math.Abs(diff) / (double)official <= 0.02) return ("两边一致", "差额在 2% 以内");
        if (diff > 0) return ("他端用量", "官方含其他设备或其他 Key 的用量");

        return span == SpendSpan.Today
            ? ("官方未结算", "官方当日入库有延迟,本机反超属正常")
            : ("口径不同", "官方是滚动窗口,本机是自然日区间");
    }

    /// <summary>
    /// OpenCode 官方服务端**聚合**读数(opencode.ai 控制台,浏览器登录态):账号级,覆盖所有设备与 Key。
    /// 成功后**追加**对照两格(官方账号级 / 差值)并渲染逐模型榜——这才是"跟网页版一样"的口径,
    /// 本机记录只是它的本机子集。
    ///
    /// 数据源是控制台用量页自己读的那三个接口(<c>/console/api/usage/*</c>),不是逐请求明细:
    /// 后者要翻 117 页、353 秒,而且**天然翻不完**——旧版拿它当"近 30 天"用,报出来的数字
    /// 只有真值的三分之一。
    /// </summary>
    private async Task LoadOpenCodeOfficialAsync(ChannelUsage ch)
    {
        int seq = _meteringSeq;
        AccountModelSection.Visibility = Visibility.Visible;
        AccountModelHint.Text = "正在读取官方用量…";
        AccountModelList.ItemsSource = null;

        // 官方聚合接口只给 24h / 7d / 30d 三档窗口,按当前区间映射(和网页的
        // 「Last 30 days」以及 go/status 的月度池同一个窗口口径)。
        var (range, windowLabel) = OpenCodeUsageStatsClient.WindowFor(_span);
        var stats = await OpenCodeUsageStatsClient.TryFetchAsync(range);
        if (_closed || seq != _meteringSeq) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        if (stats is null)
        {
            AccountModelSection.Visibility = Visibility.Collapsed;
            AccountModelHint.Text = OpenCodeUsageStatsClient.LastStatus is { } status
                    && status.Contains("未启用", StringComparison.Ordinal)
                ? "要读官方用量:在 设置 → 数据源 打开「浏览器会话明细」并登录 opencode.ai。"
                : "官方用量读不到:" + (OpenCodeUsageStatsClient.LastStatus ?? "未知原因");
            return;
        }

        // 官方读数到了:整格重建——「本机 / 官方 / 差值」三格同窗口、挨着摆
        RenderOpenCodeMeterGrid(ch, stats);

        var (localTokens, localRequests) = CountLocalSince(ch, range switch
        {
            "24h" => DateTime.Now.AddHours(-24),
            "7d" => DateTime.Now.AddDays(-7),
            _ => DateTime.Now.AddDays(-30),
        });
        AccountModelHint.Text = $"官方账号级口径({windowLabel},含所有设备与 Key);同窗口本机 {SpendFormat.Tokens(localTokens)} tokens · {localRequests:N0} 次。"
            + "官方金额为服务端真实计费(含峰谷时段价)。";

        RenderOfficialModels(stats);
    }

    /// <summary>官方逐模型榜(账号级)。金额用官方计费字段,不用本机牌价。</summary>
    private void RenderOfficialModels(OpenCodeUsageStats stats)
    {
        var models = stats.Models;
        long max = models.FirstOrDefault()?.TotalTokens ?? 0;
        AccountModelList.ItemsSource = models.Select(m => new RowVm
        {
            Name = ModelPrices.DisplayName(m.Model),
            Tokens = SpendFormat.Tokens(m.TotalTokens),
            Amount = m.Cost > 0 ? "$" + m.Cost.ToString("0.00", CultureInfo.InvariantCulture) : "",
            AmountTip = "官方计费金额(美元),来自服务端聚合",
            Detail = $"输入 {SpendFormat.Tokens(m.Input)} · 缓存写 {SpendFormat.Tokens(m.CacheWrite)} · 缓存读 {SpendFormat.Tokens(m.CacheRead)}"
                + $" · 输出 {SpendFormat.Tokens(m.Output)} · {m.Requests:N0} 次",
            BarWidth = Fraction(m.TotalTokens, max) * BarBase,
            BarBrush = BarModels,
        }).ToList();
    }

    /// <summary>
    /// 离屏出图用(<c>--ui-shot</c>):注入一份**合成**的官方读数,把「官方 vs 本机」对照格
    /// 与逐模型榜真的渲染一次——真实数据要浏览器登录态,而出图环境里浏览器 cookie 库是锁着的。
    ///
    /// 自增 <see cref="_meteringSeq"/> 是为了作废在途的真实加载:它回来时会把注入的内容清掉。
    /// </summary>
    internal void UiShotInjectOfficialUsage(OpenCodeUsageStats stats)
    {
        _meteringSeq++;
        if (_selectedChannel is not { } ch) return;
        AccountMeterCard.Visibility = Visibility.Visible;
        AccountMeterTitle.Text = "官方口径对照";
        AccountModelSection.Visibility = Visibility.Visible;
        AccountModelHint.Text = "(出图用合成读数,真实数据要浏览器登录态)";
        // 真实通道可能已经把格子填上了(出图环境偶发能读到 cookie)——注入是**替代**它,不是追加,
        // 所以直接走整格重建(它会先清空)。
        RenderOpenCodeMeterGrid(ch, stats, record: false);
        RenderOfficialModels(stats);
    }

    /// <summary>
    /// BigModel 包月:智谱没有公开的用量/额度接口,这一页所有数字都是本机记录按牌价估算,
    /// 对照的另一半只能是自己填的包月预算。
    /// </summary>
    private void RenderBudgetMetering(ChannelUsage ch)
    {
        if (!ch.HasData && AppSettings.Current.BigModelMonthlyBudgetCny is null)
        {
            AccountMeterCard.Visibility = Visibility.Collapsed;
            return;
        }
        AccountMeterCard.Visibility = Visibility.Visible;
        AccountMeterTitle.Text = "预算口径对照";

        var month = MonthForChannel(ch);
        double spentCny = month.Cost * CnyRate;
        double? budget = AppSettings.Current.BigModelMonthlyBudgetCny;
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        int dayOfMonth = Math.Max(1, (today - monthStart).Days + 1);
        int daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        double projected = spentCny / dayOfMonth * daysInMonth;

        AddCell(AccountMeterGrid, $"本月估算(自 {monthStart:MM-dd})", $"¥{spentCny:N0}",
            $"{SpendFormat.Tokens(month.Tokens)} tokens · {month.Requests:N0} 次", "Money");
        AddCell(AccountMeterGrid, "包月预算",
            budget is { } b ? $"¥{b:N0}" : "未设置",
            budget is null
                ? "设置 › 行为 里填"
                : spentCny > budget.Value ? "已经超出" : $"还剩 ¥{budget.Value - spentCny:N0}",
            "Tokens");
        AddCell(AccountMeterGrid, "月底预测", $"¥{projected:N0}",
            budget is { } b2
                ? (projected > b2 ? "按当前速度会超出预算" : "按当前速度在预算内")
                : null,
            "Cache");
        AutoColumns(AccountMeterGrid);

        AccountMeterHint.Text = "这页所有数字都来自本机记录、按公开牌价估算,与包月费本身无关;填了包月预算才有进度与月底预测。";
    }

    private void RenderHourlyChart(ChannelUsage ch)
    {
        ChannelHourBox.Child = null;
        long total = ch.TodayHourly.Sum(h => h.Tokens);
        // 今天没数据就别留一个 110 高的空画布
        ChannelHourBox.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (total == 0)
        {
            ChannelTodayHint.Text = "今天还没有记录。";
            return;
        }

        var canvas = new Canvas { ClipToBounds = true };
        ChannelHourBox.Child = canvas;
        // 按峰谷加价的套餐(GOAT 与 OpenCode Go,两者同一套峰时段)才在图上打橙色底纹——
        // 别的渠道那样画等于暗示它有时段价
        var peakHours = PeakHoursFor(ch.SourceKey);
        ChannelTodayHint.Text = $"{SpendFormat.Tokens(total)} tokens · 最忙 {ch.TodayHourly.OrderByDescending(h => h.Tokens).First().Hour}:00";
        canvas.SizeChanged += (_, _) => DrawHourly(canvas, ch.TodayHourly, peakHours);
        DrawHourly(canvas, ch.TodayHourly, peakHours);
    }

    private void DrawHourly(Canvas canvas, IReadOnlyList<HourBucket> hours, int[]? peakHours)
    {
        double w = ChannelHourBox.ActualWidth, h = ChannelHourBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var values = new List<double>(24);
        var outputOnly = new List<double>(24);
        var tips = new List<string?>(24);
        for (int i = 0; i < 24; i++)
        {
            var hr = hours[i];
            values.Add(hr.Tokens);
            outputOnly.Add(hr.Output);
            bool isPeak = peakHours?.Contains(i) == true;
            tips.Add(hr.Tokens <= 0
                ? $"{i:00}:00 没有记录"
                : $"{i:00}:00  {SpendFormat.TokensExact(hr.Tokens)} tokens\n"
                    + $"输入 {SpendFormat.TokensExact(hr.Input + hr.CacheRead + hr.CacheWrite)} · 输出 {SpendFormat.TokensExact(hr.Output)}\n"
                    + $"{hr.Requests} 次调用" + (isPeak ? "\n(高价时段)" : ""));
        }
        Func<int, bool>? band = peakHours is null ? null : i => peakHours.Contains(i);

        // 柱 = 合计,虚线 = 其中的输出;峰时段打浅橙底纹
        DrawBarChart(canvas, w, h, values, tips, AccentColor, PeakColor, band,
            onClick: null,
            xLabels: [(0, "0:00"), (6, "6:00"), (12, "12:00"), (18, "18:00")],
            overlay: outputOnly, overlayColor: GoodColor);
    }

    /// <summary>
    /// 时段分布:整个区间的 24 小时分布。**每个套餐页都有**——"这台机器一天里什么时候在干活"
    /// 对哪个套餐都成立。按峰谷时段加价的渠道(GOAT、OpenCode Go)才把柱子
    /// 按峰谷着色并拆峰/谷占比,其余渠道统一配色并给"最忙小时/活跃小时/金额最高小时"。
    /// </summary>
    private void RenderChannelPeak(ChannelUsage ch)
    {
        var peakHours = PeakHoursFor(ch.SourceKey);
        bool peakPriced = peakHours is not null;
        ChannelPeakCard.Visibility = Visibility.Visible;
        ChannelPeakTitle.Text = (peakPriced ? "时段分布(峰谷)" : "时段分布")
            + (ch.RangeHourlyPartial ? " · 近 35 天" : "");

        long total = ch.RangeHourly.Sum(h => h.Tokens);
        double totalCost = ch.RangeHourly.Sum(h => h.Cost);
        long todayTotal = ch.TodayHourly.Sum(h => h.Tokens);

        // 两档都没数据时整张卡收起:留着就是一块空图 + 一条说明,占掉大半屏
        ChannelPeakCard.Visibility = total == 0 && todayTotal == 0
            ? Visibility.Collapsed : Visibility.Visible;
        if (ChannelPeakCard.Visibility == Visibility.Collapsed) return;

        ChannelRangeHourBox.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (total == 0)
        {
            ChannelPeakHint.Text = "这个区间没有逐小时记录。";
            ChannelPeakGrid.Children.Clear();
            return;
        }

        ChannelRangeHourBox.Child = null;
        var canvas = new Canvas { ClipToBounds = true };
        ChannelRangeHourBox.Child = canvas;
        canvas.SizeChanged += (_, _) => DrawRangeHourly(canvas, ch.RangeHourly, peakHours, ch.RangeHourlyPartial);
        DrawRangeHourly(canvas, ch.RangeHourly, peakHours, ch.RangeHourlyPartial);

        ChannelPeakGrid.Children.Clear();

        if (!peakPriced)
        {
            var busiest = ch.RangeHourly.OrderByDescending(h => h.Tokens).First();
            var priciest = ch.RangeHourly.OrderByDescending(h => h.Cost).First();
            int active = ch.RangeHourly.Count(h => h.Tokens > 0);
            AddCell(ChannelPeakGrid, "最忙小时", $"{busiest.Hour:00}:00",
                $"{SpendFormat.Tokens(busiest.Tokens)} tokens · {busiest.Requests:N0} 次调用", "Money");
            AddCell(ChannelPeakGrid, "活跃小时", $"{active} / 24",
                $"区间内有记录的时段(共 {SpendFormat.Tokens(total)} tokens)", "Tokens");
            AddCell(ChannelPeakGrid, "金额最高小时", $"{priciest.Hour:00}:00",
                SpendFormat.Money(priciest.Cost), "Cache");
            ChannelPeakHint.Text = $"{ch.Name} 不按时段加价;这里看的是哪个时段干活最多。";
            AutoColumns(ChannelPeakGrid);
            return;
        }

        long peak = ch.RangeHourly.Where(h => peakHours!.Contains(h.Hour)).Sum(h => h.Tokens);
        double peakCost = ch.RangeHourly.Where(h => peakHours!.Contains(h.Hour)).Sum(h => h.Cost);

        AddCell(ChannelPeakGrid, "峰时段占比", $"{(double)peak / total:P1}",
            $"{PeakWindowText} · {SpendFormat.Tokens(peak)} tokens", "Money");
        AddCell(ChannelPeakGrid, "其余时段", $"{(double)(total - peak) / total:P1}",
            $"{SpendFormat.Tokens(total - peak)} tokens", "Tokens");
        AddCell(ChannelPeakGrid, "峰时段金额", SpendFormat.Amount(peakCost, ch.HasUnpriced),
            totalCost > 0 ? $"占本期金额 {(double)peakCost / totalCost:P1}" : null, "Cache");
        ChannelPeakHint.Text = "橙色底纹 = 峰时段(本地 09:00–12:00、14:00–18:00),这些时段单价更高。"
            + (ch.RangeHourlyPartial
                ? "   ·   本图只覆盖近 35 天(逐小时明细的保留期),上方 KPI 是全区间。"
                : "");
        AutoColumns(ChannelPeakGrid);
    }

    private void DrawRangeHourly(Canvas canvas, IReadOnlyList<RangeHourBucket> hours, int[]? peakHours, bool partial)
    {
        double w = ChannelRangeHourBox.ActualWidth, h = ChannelRangeHourBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 逐小时行只保留 35 天(UsageRepository.HourRetentionDays)。区间更长时必须如实说
        // "近 35 天"——写"整个区间累计"会让用户以为看到的是全史。
        string hourRangeLabel = partial ? "近 35 天" : "整个区间累计";

        var values = new List<double>(24);
        var tips = new List<string?>(24);
        for (int i = 0; i < 24; i++)
        {
            var hr = hours[i];
            values.Add(hr.Tokens);
            bool isPeak = peakHours?.Contains(i) == true;
            tips.Add(hr.Tokens <= 0
                ? $"{i:00}:00 没有记录"
                : $"{i:00}:00({hourRangeLabel})\n{SpendFormat.TokensExact(hr.Tokens)} tokens\n"
                    + $"{SpendFormat.MoneyExact(hr.Cost)} · {hr.Requests:N0} 次调用"
                    + (isPeak ? "\n(高价时段)" : ""));
        }
        Func<int, bool>? band = peakHours is null ? null : i => peakHours.Contains(i);

        DrawBarChart(canvas, w, h, values, tips, AccentColor, PeakColor, band,
            onClick: null,
            xLabels: [(0, "0:00"), (6, "6:00"), (12, "12:00"), (18, "18:00")]);
    }

    /// <summary>
    /// 性能与行为:来自客户端记录的遥测字段(推理 token/耗时/首字延迟/工具调用/重试/失败)。
    /// 这些是"这次调用花了多久、有没有卡、推理占多少",比单纯的 token 数更能说明体验。
    /// </summary>
    private void RenderPerf(ChannelUsage ch)
    {
        ChannelPerfGrid.Children.Clear();
        bool any = ch.ReasoningTokens > 0 || ch.DurationSamples > 0 || ch.ToolCalls > 0;
        // 来源没记遥测就收起整张卡:ZCode 的 model_usage 没有耗时列,这一页对它是永久空白
        ChannelPerfCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
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
        // 平均输出速度:每次请求的输出 tokens ÷ 生成耗时(总耗时扣掉首字等待)。
        // 与"平均耗时"一样是**区间平均**,不是实时读数;来源没记耗时就是 —。
        AddCell(ChannelPerfGrid, "平均输出速度", OutputSpeed(ch) is { } speed ? $"{speed:0.#} tok/s" : "—",
            OutputSpeed(ch) is null ? "来源没记耗时" : null,
            "Cache");
        AddCell(ChannelPerfGrid, "平均每请求",
            ch.AvgTokensPerRequest is { } avg ? SpendFormat.Tokens((long)avg) : "—",
            "tokens / 次", "Tokens");
        AutoColumns(ChannelPerfGrid);
    }

    /// <summary>
    /// 平均输出速度(tok/s)。分子是**每次请求的平均输出 tokens**、分母是**平均生成耗时**
    /// (总耗时减去首字等待)——总耗时里含输入处理与排队,不扣掉首字等待会把速度算低一半。
    /// 两个平均值各自的样本集可能不同,所以是估算;样本不足时返回 null。
    /// </summary>
    private static double? OutputSpeed(ChannelUsage ch)
    {
        if (ch.Tally.Output <= 0 || ch.DurationSamples <= 0 || ch.AvgSeconds is not { } avgSec || avgSec <= 0) return null;
        double genSeconds = avgSec - (ch.AvgTtftSeconds ?? 0);
        if (genSeconds <= 0.05) return null;
        double avgOutputPerRequest = (double)ch.Tally.Output / ch.DurationSamples;
        return avgOutputPerRequest / genSeconds;
    }

    private void RenderDailyChart(ChannelUsage ch)
    {
        ChannelDailyBox.Child = null;
        // 没有用量就收起整张卡:空画布留着的是一整块白。
        ChannelDailyCard.Visibility = ch.Daily.Any(d => d.Tokens > 0)
            ? Visibility.Visible : Visibility.Collapsed;
        if (ChannelDailyCard.Visibility == Visibility.Collapsed) return;

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
        var values = daily.Select(d => (double)d.Tokens).ToList();
        var tips = daily.Select(d =>
            $"{d.Day:yyyy-MM-dd}\n{SpendFormat.TokensExact(d.Tokens)} tokens\n"
            + $"{SpendFormat.MoneyExact(d.Cost)} · {d.Requests} 次调用").ToList<string?>();
        var labels = new List<(int, string)> { (0, daily[0].Day.ToString("MM-dd")) };
        if (n > 1) labels.Add((n - 1, daily[^1].Day.ToString("MM-dd")));
        if (n > 4) labels.Add((n / 2, daily[n / 2].Day.ToString("MM-dd")));

        DrawBarChart(canvas, w, h, values, tips, AccentColor, AccentColor,
            band: null, onClick: null, xLabels: labels);
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
        ChannelModelPanel.Children.Add(ModelRowHeader());
        bool first = true;
        foreach (var m in models)
        {
            if (!first)
                ChannelModelPanel.Children.Add(new Border { Background = Separator, Height = 1 });
            first = false;

            // 一排四个数(tokens / 次数 / 命中 / 金额)+ 占比条:原来每个模型占三行,
            // 一屏只看得到四五个模型,留白又全在右侧。
            double ratio = _modelsByCost
                ? maxCost <= 0 ? 0 : m.Cost / maxCost
                : max <= 0 ? 0 : (double)m.Tokens / max;

            var grid = new Grid { Margin = new Thickness(1, 6, 1, 6) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
            foreach (double w in ModelColumns)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            grid.Children.Add(new TextBlock
            {
                Text = m.Model, FontSize = 12.5, Foreground = TextMain,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = m.HasUnpriced ? "金额只覆盖有公开价的部分" : null,
            });
            string[] cells =
            [
                SpendFormat.Tokens(m.Tokens),
                $"{m.Requests:N0} 次",
                m.CacheHit is { } hit ? $"{hit:P0}" : "—",
                SpendFormat.Amount(m.Cost, m.HasUnpriced),
            ];
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = new TextBlock
                {
                    Text = cells[i], FontSize = 12, Foreground = TextMain,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                if (i == cells.Length - 1)
                    cell.ToolTip = SpendFormat.AmountTip(m.Cost, m.HasUnpriced);
                Grid.SetColumn(cell, i + 1);
                grid.Children.Add(cell);
            }

            // 占比条铺满整行(按排序键的比例):窗口多宽就多宽,不再固定 200。
            // 用浅一档的蓝:第一名的条是 100% 满宽,实心强调色会看着像条分隔线。
            // 第一名也只到 97%:完全贴边时整行一条浅蓝横线,第一眼会被读成"表头下的分隔线"。
            double fillRatio = Math.Clamp(ratio, 0.0001, 0.97);
            var track = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            track.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(fillRatio, GridUnitType.Star),
            });
            track.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1 - fillRatio, 0.0001), GridUnitType.Star),
            });
            track.Children.Add(new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5),
                Background = FieldBack, HorizontalAlignment = HorizontalAlignment.Stretch,
            });
            var fill = new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5),
                Background = BarSoft, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            track.Children.Add(fill);
            Grid.SetRow(track, 1);
            Grid.SetColumnSpan(track, ModelColumns.Length + 1);
            grid.Children.Add(track);

            ChannelModelPanel.Children.Add(grid);
        }
    }

    /// <summary>模型行的四个数值列宽(tokens / 次数 / 命中 / 金额),表头行共用。</summary>
    private static readonly double[] ModelColumns = [76, 64, 58, 74];

    /// <summary>模型分布的表头:没有它,四列裸数字看不出哪列是哪。</summary>
    private Grid ModelRowHeader()
    {
        var grid = new Grid { Margin = new Thickness(1, 0, 1, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        foreach (double w in ModelColumns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        string[] heads = ["模型", "tokens", "次数", "命中", "金额"];
        for (int i = 0; i < heads.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = heads[i], FontSize = 10.5, Foreground = TextSub,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
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
            ? $"列为最近 {shown.Count} 天(区间共 {days.Count} 天有用量)"
            : $"列为 {shown.Count} 天";

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
            + life;
    }

    /// <summary>
    /// KPI 小格:标签 + 大数字 + 可选脚注,装在一块浅灰瓦片里(白卡上的"卡中卡"),
    /// 一排数字因此有边界、不散在空白里。返回瓦片本身(调用方偶尔要挂 ToolTip)。
    /// </summary>
    private FrameworkElement AddCell(Panel host, string label, string value, string? note, string kind)
    {
        Brush accent = kind switch
        {
            "Money" => UiTheme.B(UiTheme.Warn),
            "Tokens" => UiTheme.B(UiTheme.Accent),
            "Cache" => UiTheme.B(UiTheme.Good),
            "Requests" => UiTheme.B(UiTheme.BarAgents),
            "Sessions" => UiTheme.B(UiTheme.BarSessions),
            _ => TextSub,
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            // 窄窗口下"官方已用(5h/周/月)"这类长标签必须能折行,否则会被瓦片右缘硬裁
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSub,
        });
        var number = new TextBlock
        {
            Text = value,
            FontSize = 17.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = accent,
        };
        // 等宽数字:几块瓦片并排时小数点/位数不会各自抖动
        System.Windows.Documents.Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        panel.Children.Add(number);
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

        var tile = new Border
        {
            Background = UiTheme.B(UiTheme.TileBack),
            CornerRadius = new CornerRadius(9),
            Padding = UiTheme.TilePad,
            Margin = UiTheme.TileGap,
            Child = panel,
        };
        host.Children.Add(tile);
        return tile;
    }

    /// <summary>已挂过自适应列数监听的网格(避免每次重渲染重复订阅 SizeChanged)。</summary>
    private readonly HashSet<UniformGrid> _autoCols = [];

    /// <summary>
    /// 指标网格按可用宽度决定列数。固定 3 列时窗口一拉宽,每格右边就空出一大片
    /// (实测 1400 宽下三个半格都空着);改成自适应后一排能摆下整组数字,
    /// 而且列宽一律均等——一排里几个数就是均匀铺满整行。
    /// <paramref name="minCell"/> 是列宽下限:值短的网格(KPI / Token 构成)可以给到 104,
    /// 让默认窗口那一排也能装下 5 格;其余网格留 118(瓦片左右内边距 22 要算进去,
    /// 否则一格只剩七十来像素,标签与数值都得换行)。
    /// **不"匀"末行**:曾经为了不让末行只剩一格而减列数(5 格排 3 列),结果正是用户抱怨的
    /// "一排才两三个"——宁可末行落单,也不减列。
    /// </summary>
    private void AutoColumns(UniformGrid grid, double minCell = 118)
    {
        void Apply()
        {
            int n = grid.Children.Count;
            if (n == 0) return;
            double w = grid.ActualWidth;
            if (w <= 0) return;   // 折叠页还没布局;变可见时 SizeChanged 会再调一次
            int cols = Math.Clamp((int)(w / minCell), 2, n);
            if (grid.Columns != cols) grid.Columns = cols;
        }

        if (_autoCols.Add(grid))
            grid.SizeChanged += (_, _) => Apply();
        Apply();
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

    private async void ExportCurrent(bool exportJson)
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
            // 先让界面刷一帧，再异步写盘：大区间（上万会话）同步写会僵住整个窗口
            PriceSourceText.Text = "正在导出…";
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

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
                await File.WriteAllTextAsync(dialog.FileName,
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
                await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
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
        UiTheme.Changed -= OnUiThemeChanged;
        if (_holdsLedgerRef)
        {
            _holdsLedgerRef = false;
            SpendLedgerCache.Release();
        }
        _ledger = null;
        base.OnClosed(e);
    }

    /// <summary>UiShot 用:视图 tab 的数量(第 0 个永远是"总览",之后每个套餐一个)。</summary>
    internal int UiShotTabCount => TabRow.Children.OfType<RadioButton>().Count();

    /// <summary>
    /// UiShot 用:第 i 个 tab 的标题。产物文件名要能对上人看的页面——
    /// "usage-channel-3.png" 得知道那到底是哪一页。
    /// </summary>
    internal string UiShotTabLabel(int index)
    {
        var tabs = TabRow.Children.OfType<RadioButton>().ToList();
        if (index < 0 || index >= tabs.Count) return "";
        // Tag 里是纯名称;Content 是"名称 + 用量徽标"的 StackPanel,直接 ToString 会得到类型名
        return tabs[index].Tag as string ?? tabs[index].Content?.ToString() ?? "";
    }

    /// <summary>UiShot 用:切到第 i 个视图 tab(直接驱动真实 tab 按钮,与手点同一条路径)。</summary>
    internal void UiShotOpenTab(int index)
    {
        // 切页会重建整行 tab(RenderViewTabs),所以每次都要重新取列表,不能缓存引用。
        var tabs = TabRow.Children.OfType<RadioButton>().ToList();
        if (index >= 0 && index < tabs.Count && tabs[index].IsChecked != true)
            tabs[index].IsChecked = true;
    }

    /// <summary>列表行。<see cref="BarWidth"/> 已算成像素,绑定直接用。</summary>
    public sealed class RowVm
    {
        public string Name { get; init; } = "";
        public string Tokens { get; init; } = "";

        /// <summary>token 数的精确值(列表里是 1.2M 这种短写法),悬停才给。</summary>
        public string? TokensTip { get; init; }
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
