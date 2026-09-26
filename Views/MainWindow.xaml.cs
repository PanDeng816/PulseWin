using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PulseWin;

/// <summary>
/// rail 主窗:黑色玻璃 rail,每个订阅一个"复合环"——从内到外三个同心弧
/// 表示 5小时 / 本周 / 总额度(月),月圈最粗;超过 80% 弧变红报警。
/// 无边框透明置顶、永不激活;输入由指针轮询驱动。
/// </summary>
public partial class MainWindow : Window
{
    private readonly CardWindow _card = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _ticker;

    // —— 数据 ——
    private List<SubData> _subs = new();
    private readonly List<double> _ringY = new();     // 每订阅复合环中心(窗内 y)
    private double _railHeight;
    private double _lastReloadCheck;
    private long _lastStamp = -1;      // 快照文件 mtime,未变化就不重建数据

    // —— 停靠 / 热区显隐 ——
    private DockEdge _dockEdge = DockEdge.Right;
    private bool _docked = true;
    private bool _peekVisible = true;
    private double _hideSince = -1;      // 指针离开内容的时刻(-1 = 已重置)
    private double _targetLeft, _targetTop = double.NaN;

    // —— 指针 / 拖拽 ——
    private bool _downArmed, _dragging;
    private Point _downDiu, _dragOffset;
    private int? _downRing, _hoverRing;
    private int? _cardFor;

    // —— 点击刷新 ——
    private double[] _refreshUntil = new double[4];
    private double[] _refreshStart = new double[4];
    private bool _refreshPending;   // 已请求 API,等数据回来结束动画

    private Geometry? _hitBerth;

    // —— 贴边小条(sliver)与全屏隐藏 ——
    // sliver = 自动隐藏时窗口滑到"只露出边缘一条 6pt 细条"的位置(窗口尺寸不变,
    // 命中/绘制都限定在细条上),展开就是正常的滑入动画。上游 hide until pointed at 同款。
    private bool _sliverMode;
    private bool _sliverTarget;   // 收起动画停稳后要切到的形态(见 SetPeekVisible/Tick)

    /// <summary>
    /// 全屏隐藏期间是否拒绝热区展开(见 UpdatePeek)。也用于渲染:全屏收起时不画细条。
    /// </summary>
    private bool _fullScreenHide;

    // —— 液态玻璃 ——
    // v1.7.3 已整体移除。它有两个无法调和的代价:①acrylic 的模糊区域只能是**整个
    // 窗口矩形**,跟不了 rail/卡片的圆弧形状,一开就把所有圆角变成直角(v1.7.0/1.7.1
    // 被用户连否两次);②在分层窗口上调 SetWindowCompositionAttribute 会**不可逆地
    // 打坏 per-pixel alpha**(v1.7.2 的"圆弧与透明度全没了"就是它)。而视觉效果只有
    // 80% 黑 tint 平铺一块,用户看不出"玻璃"。面板本来就是自绘黑色玻璃,不需要系统模糊。

    // —— 渲染节流 ——
    private bool _dirty = true;                  // 本帧内容是否有变化
    private Geometry? _berthCache;               // berth 轮廓(约 200 点,重建最贵)
    private Size _berthCacheSize;
    private DockEdge _berthCacheEdge;
    private bool _berthCacheDocked;
    private double _berthCacheScale;
    private readonly Dictionary<(string, double, FontWeight, Color), FormattedText> _textCache = new();

    /// <summary>请求马上走一次真实 API 刷新(由 App 接到 UsageEngine)。</summary>
    public event Action? RefreshRequested;

    /// <summary>
    /// 在 rail 形状上点了右键(屏幕 DIU 坐标)。菜单由 App 弹:只有它知道
    /// 当前有哪些动作可用(设置 / 统计 / 有没有新版本)。
    /// </summary>
    public event Action<Point>? RailMenuRequested;

    /// <summary>
    /// 菜单是否开着。开着时**不许自动隐藏**——指针这会儿在菜单上,按面板的判定
    /// 属于"离开了内容",不等这一条的话菜单一弹出来 rail 就收回去(上游专门记过这个坑)。
    /// </summary>
    private bool _menuOpen;

    public void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        _hideSince = -1;
        // 菜单一弹出就把 hover 卡片收掉。菜单锚点就在 rail 的那个环上,所以菜单
        // 出现后指针坐标仍落在该环的判定范围内 —— 不主动清掉的话卡片会一直留着,
        // 正好挡在菜单上(用户报的"右键菜单被详细界面挡住")。
        if (open)
        {
            _hoverRing = null;
            HideCard();
        }
        _dirty = true;
    }

    /// <summary>菜单弹出时用的锚点(rail 上的那个点),供 App 定位。</summary>
    public Point MenuAnchor { get; private set; }

    /// <summary>rail 当前是否露在外面(右键菜单与托盘菜单的文案要用)。</summary>
    public bool IsRailVisible => !_docked || _peekVisible;

    /// <summary>当前已加载的数据源(托盘仪表复用,避免每轮同步都重读一次快照文件)。</summary>
    public IReadOnlyList<SubData> Subs => _subs;

    private const bool AutoHideEnabled = true;
    private const double HotZoneWidth = 16;
    private const double HideDelaySeconds = 0.9;
    private const double PeekStep = 0.5;
    /// <summary>点击刷新后动画的安全上限(正常情况下 SnapshotsChanged 会提前结束它)。</summary>
    private const double RefreshAnimationTimeoutS = 45;

    /// <summary>
    /// 主循环的两档节拍。16ms(≈60fps)只用在"真的有东西在动"的时候:滑入/滑出、
    /// 拖动、hover 动画、刷新扫光、环心呼吸。静止且藏屏外时降到 100ms——
    /// 指针轮询、光标命中、保活/全屏判定都不需要 60Hz,而 60Hz 空转是这台机器上
    /// 常驻 CPU 与重绘的主要来源(用户实测 idle CPU 会随 hover 状态长期偏高)。
    /// 热区探入仍需及时:100ms 内识别到指针进入热区,动画本身还是 60fps 跑的
    /// (SetPeekVisible 后 _dirty/动画立即切回快档)。
    /// </summary>
    private const int TickFastMs = 16;
    private const int TickSlowMs = 100;

    /// <summary>贴边小条的尺寸(pt,上游 DockLayout.collapsedWidth/collapsedHeight)。</summary>
    private const double SliverWidthPt = 6;
    private const double SliverLengthPt = 96;

    /// <summary>刷新亮段的最短显示时长:短于它,用户会以为没刷新(上游规格 650ms)。</summary>
    private const double MinRefreshAnimationS = 0.65;

    // —— rail / 复合环几何(pt 单位,渲染 ×Pt.U) ——
    // 环尺寸**固定**,不随订阅数缩放:多一个源就整体变长,不缩小环。
    // 单元高度由"环心 + 环半径 + 读数行高"加出来,所以环与环之间不留多余空白。
    // 环的尺寸与间距来自设置三档(尺寸三档的默认档比 v1.5 的小一圈、细一号)。
    private static double RailWpt => RingSizeTable(AppSettings.Current.RingSize).RailW;

    /// <summary>环外缘半径 = 中线直径/2 + 线宽/2(环的最外沿,不是中心线)。</summary>
    private static double RingOuterRadius => OuterRingDpt / 2 + OuterWpt / 2;

    /// <summary>环心距单元顶,即环外缘上方留 4pt。</summary>
    private static double RingCenterInUnit => RingOuterRadius + 4;

    /// <summary>
    /// rail 两端的留白。两个约束决定了这两个数:
    /// ① 都**大于 berth 的圆角半径(26pt)**——圆角是从上下边缘往里缩的,留白小于它,
    ///    首尾读数就会落进圆角区域,看着像被挤出去;
    /// ② `PadBottom = PadTop + (环外缘到单元顶那段)`,这样**第一个环到 rail 顶的距离
    ///    和最后一个环(或它下方读数)到 rail 底的距离相等**,上下对称。
    /// </summary>
    private const double PadTopPt = 24;
    private static double PadBottomPt => PadTopPt + (RingCenterInUnit - RingOuterRadius);

    /// <summary>相邻单元之间的间距(三档)。</summary>
    private static double UnitGappt => AppSettings.Current.RingSpacing switch
    {
        0 => 2,
        2 => 10,
        _ => 4,
    };

    /// <summary>环外缘到读数的间距。</summary>
    private const double RingToTextGap = 6;
    /// <summary>读数行高(13pt)。中文字体行距比拉丁大,算小了文字会被下一个环压住。</summary>
    private const double TextLineHeight = 17;

    /// <summary>
    /// 环几何三档:{rail 宽, 外环中线直径, 外环线宽, 内环中线直径, 内环线宽}。
    /// Small=36/3.2、Standard=40/3.6(默认,比 v1.5 的 44/4 小一圈细一号)、Large=44/4(=v1.5 原样)。
    /// 约束:内环比外环小一圈;rail 宽 = 外环外缘直径 + 两侧各 4pt。
    /// </summary>
    private static (double RailW, double OuterD, double OuterW, double InnerD, double InnerW) RingSizeTable(
        int size) => size switch
    {
        0 => (48, 36, 3.2, 26, 2.2),
        2 => (56, 44, 4.0, 34, 2.5),
        _ => (52, 40, 3.6, 30, 2.4),
    };

    private static double OuterRingDpt => RingSizeTable(AppSettings.Current.RingSize).OuterD;
    private static double OuterWpt => RingSizeTable(AppSettings.Current.RingSize).OuterW;
    private static double InnerRingDpt => RingSizeTable(AppSettings.Current.RingSize).InnerD;
    private static double InnerWpt => RingSizeTable(AppSettings.Current.RingSize).InnerW;

    /// <summary>
    /// berth 外形(圆角/喇叭口/贴边细条宽)相对 v1.5 基准轨宽(56pt)的缩放。
    /// Dock.CornerRadius(26pt)这些常量是按 56pt 轨宽定的:轨宽收到 48pt 后,
    /// 圆角半径(34.7diu)会超过轨半宽(32diu),左上圆弧被窗口裁掉 —— 这正是
    /// "圆弧没了"的由来。按比例缩,三档的圆角与轨宽关系才一致。
    /// </summary>
    private static double BerthScale => RailWpt / 56.0;

    /// <summary>环下是否显示读数(设置项)。关掉后 rail 是一条纯环列。</summary>
    private bool ShowPercent => AppSettings.Current.ShowPercent;

    /// <summary>单元高度:显示读数时"环 + 读数",否则只装环。</summary>
    private double UnitHpt => ShowPercent
        ? RingCenterInUnit + RingOuterRadius + RingToTextGap + TextLineHeight
        : RingCenterInUnit + RingOuterRadius;

    private static readonly Brush TrackBrush = Frz(PanelPalette.Track);
    private static readonly Brush WhiteBrush = Frz(PanelPalette.Primary);
    private static readonly Brush DimBrush = Frz(PanelPalette.Dim);
    private static readonly Pen HairlinePen = MakeHairline();

    private static Pen MakeHairline()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);
        p.Freeze();
        return p;
    }

    private static Brush Frz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>
    /// 画笔/Pen 缓存。每帧重建 SolidColorBrush 和 Pen 是纯浪费——颜色其实只有
    /// 绿/红/深红三种加上几个透明度变体,按值复用即可。只在 UI 线程访问。
    /// </summary>
    private static readonly Dictionary<Color, Brush> BrushCache = new();
    private static readonly Dictionary<(Brush, double), Pen> PenCache = new();

    private static Brush CachedBrush(Color c)
    {
        if (BrushCache.TryGetValue(c, out var b)) return b;
        b = Frz(c);
        BrushCache[c] = b;
        return b;
    }

    private static Pen CachedRingPen(Brush brush, double width)
    {
        if (PenCache.TryGetValue((brush, width), out var p)) return p;
        p = RailGeometry.RingPen(brush, width);
        PenCache[(brush, width)] = p;
        return p;
    }

    public MainWindow()
    {
        InitializeComponent();
        Native.ApplyToolWindowStyle(this);
        Native.HookScreenChanges(this);
        // 右键走窗口过程,几何与拖动同一个形状:能拖的地方就能右键(细条状态也一样)
        Native.HookContextMenu(this, HitArea, point =>
        {
            MenuAnchor = point;
            RailMenuRequested?.Invoke(point);
        });
        // 从慢档起步:启动后 rail 很快就自动藏起来,不该一上来就 60fps 空转。
        // 有动效/可见时 Tick 会自己切回快档。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickSlowMs) };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    public void RequestExit()
    {
        _card.HideCard();
        _card.Close();
        _ticker.Stop();
        Close();
    }

    /// <summary>
    /// 离屏出图专用(<c>--rail-shot</c>):注入合成数据、停掉主循环,让 rail 停在
    /// 一个可预测的可见形态上。**不读快照、不碰指针**——这是验证"环长什么样"的
    /// 确定性手段(环是自绘的,截图之外没法核对)。
    /// </summary>
    internal void InjectForShot(List<SubData> subs)
    {
        _ticker.Stop();
        _docked = true;
        _dockEdge = DockEdge.Right;
        _peekVisible = true;
        _subs = subs;
        _refreshUntil = new double[subs.Count];
        _refreshStart = new double[subs.Count];
        RebuildLayout();
        InvalidateVisual();
    }

    /// <summary>托盘菜单:手动切换停靠 rail 的滑入滑出。</summary>
    public void ToggleRailVisible()
    {
        if (!_docked) return;
        Diagnostics.Note($"手动切换浮窗: {(_peekVisible ? "隐藏" : "显示")}");
        var wa = Native.WorkingAreaUnderPointer(DpiScale);
        SetPeekVisible(!_peekVisible, wa);
    }

    /// <summary>把 rail 显示出来(第二次启动本程序时,唤醒已有实例用)。</summary>
    public void ShowRail()
    {
        if (!_docked)
        {
            // 浮动状态下"显示"= 提到最上层并回停靠位
            var area = Native.WorkingAreaUnderPointer(DpiScale);
            DockTo(DockEdge.Right, area);
            return;
        }
        if (!_peekVisible)
            SetPeekVisible(true, Native.WorkingAreaUnderPointer(DpiScale));
        Native.BringToTopmost(this);
    }

    private double DpiScale => Native.Scale(this);
    private bool IsVertical => !_docked || _dockEdge != DockEdge.Top;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.HookHitTest(this, HitArea);
        ReloadData();
        var wa = Native.WorkingAreaUnderPointer(DpiScale);
        DockTo(DockEdge.Right, wa);
    }

    private bool HitArea(Point rel) =>
        _hitBerth is { } g && g.FillContains(rel);

    /// <summary>sliver 在窗口内的矩形(停靠缘一侧、沿 rail 居中)。</summary>
    private Rect SliverRect()
    {
        double w = Pt.P(SliverWidthPt), len = Pt.P(SliverLengthPt) * BerthScale;
        // 细条必须画在**窗口露在屏内**的那一侧。侧轨隐藏时窗口只滑到"露出停靠缘 6pt"处:
        // 右停靠 → 窗口左缘压住屏幕右缘,露出来的是**窗口的左边 6pt**;左停靠则相反
        // (露出的是窗口右边)。这里以前写反了,细条整个画在屏外,所以"贴边小条"看起来
        // 根本不存在(用户报的"自动隐藏没用"就是这个)。
        return IsVertical
            ? new Rect(_dockEdge == DockEdge.Left ? Width - w : 0, (Height - len) / 2, w, len)
            : new Rect((Width - len) / 2, Height - w, len, w);
    }

    // ————————————————— 数据 —————————————————

    public void ReloadData(bool fromEngine = false)
    {
        var subs = SnapshotSource.LoadAll();
        if (subs.Count == 0)
        {
            subs.Add(new SubData
            {
                Key = "goat", Name = "GOAT", AccountLabel = "no data",
                Pools =
                {
                    new PoolData { Label = "5小时", PoolKind = "FiveHour", IsAvailable = false },
                    new PoolData { Label = "本周", PoolKind = "Weekly", IsAvailable = false },
                    new PoolData { Label = "总额度", PoolKind = "Monthly", IsAvailable = false },
                },
            });
        }
        _subs = subs;
        if (_refreshUntil.Length < subs.Count)
        {
            _refreshUntil = new double[subs.Count];
            _refreshStart = new double[subs.Count];
        }
        _lastStamp = SnapshotSource.Stamp();
        // 消耗预测的采样:每池记一笔(时间, 已用比例),BurnRate 用它估速率
        double nowS = _clock.Elapsed.TotalSeconds;
        foreach (var sub in _subs)
            foreach (var pool in sub.Pools.Where(p2 => p2.IsAvailable && p2.HasPercent))
                BurnRate.Observe($"{sub.Key}:{pool.PoolKind}", Math.Clamp(pool.Fraction, 0, 1), nowS);
        RebuildLayout();

        // 正在悬停的卡片要跟着换成新数据:ShowSubCard 在同一条环上会提前返回,
        // 不在这里重配的话,鼠标停在环上不动时卡片会一直显示打开那一刻的旧值。
        // (菜单开着时卡片本就不该显示,见 SetMenuOpen。)
        if (!_menuOpen && _card.IsVisible && _cardFor is { } shown && shown < _subs.Count)
            ShowSubCard(shown, force: true);

        // 引擎回报了新数据:结束"刷新中"动画(点击环触发的)
        if (fromEngine && _refreshPending)
        {
            _refreshPending = false;
            // **至少亮够 650ms**:本地读数常常几十毫秒就回来了,亮段一闪而过,
            // 用户什么都没看见 = 以为没刷新(上游为此定了这条下限)。
            double now = _clock.Elapsed.TotalSeconds;
            for (int i = 0; i < _refreshStart.Length; i++)
            {
                if (_refreshStart[i] > 0)
                    _refreshUntil[i] = Math.Max(_refreshStart[i] + MinRefreshAnimationS, now);
            }
        }

        _dirty = true;
        InvalidateVisual();
    }

    private void RebuildLayout()
    {
        _ringY.Clear();
        double y = PadTopPt;
        for (int s = 0; s < _subs.Count; s++)
        {
            _ringY.Add(y + RingCenterInUnit);
            y += UnitHpt;
            if (s < _subs.Count - 1) y += UnitGappt;
        }
        _railHeight = y + PadBottomPt;
        // 池数变化会改变 rail 长度;窗口尺寸必须跟着走,否则环会画到窗外
        ApplyRailSize();
    }

    /// <summary>
    /// 设置变了:作废缓存并按新设置重新布局。显示源/读数这两个开关会改变 rail
    /// 的内容与长度,所以必须走一次 ReloadData(它会重算尺寸)——只重绘不够。
    /// </summary>
    public void ApplySettings()
    {
        _textCache.Clear();
        ReloadData();
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        _dirty = false;
        bool animate = false;   // 本帧有没有动效在跑(决定快/慢档)
        _pointerOnRail = false;

        // 快照文件没变(mtime 相同)就不重建数据对象:引擎每 60s 才落盘一次
        if (_lastReloadCheck == 0 || now - _lastReloadCheck > 15)
        {
            _lastReloadCheck = now;
            if (SnapshotSource.Stamp() != _lastStamp) ReloadData();
        }

        bool refreshing = false;
        for (int i = 0; i < _subs.Count; i++)
        {
            if (_refreshUntil[i] > 0 && now > _refreshUntil[i])
            {
                _refreshStart[i] = 0;
                _refreshUntil[i] = 0;
                _refreshPending = false;
                _dirty = true;
            }
            if (_refreshStart[i] > 0 && now < _refreshUntil[i]) refreshing = true;
        }

        if (Native.CursorPosition() is { } px)
        {
            double scale = DpiScale;
            Point diu = new(px.X / scale, px.Y / scale);
            HandlePointer(diu, Native.LeftButtonDown, now);
        }

        if (AnimatePeek()) { _dirty = true; animate = true; _wasAnimating = true; }
        else if (_wasAnimating)
        {
            // 滑入/滑出刚停:补一次失效。分层窗口在滑出屏外时 WPF 会丢掉失效请求且
            // 之后不再补,不补这一下,sliver 细条可能停在"还没画出来"的状态。
            _wasAnimating = false;
            // 动画停稳才切成 sliver 形态:滑出途中继续画完整 rail,否则 rail 会
            // 在滑到一半时"闪"成一条细线,观感很跳。
            _sliverMode = _sliverTarget;
            _dirty = true;
        }
        if (refreshing) { _dirty = true; animate = true; }   // 亮段扫动是逐帧动画
        if (_hoverRing is not null) animate = true;          // hover 时卡片/光晕要跟手

        // ZCode / DSH 是不是在跑:每秒各读一次。ZCode 读它日志的增量(只读新增字节),
        // DSH 读它的会话投影(明文小 JSON,只挑最近写过的几个);在跑的时候环心图标要呼吸,
        // 那是逐帧动画,所以每帧都得重画。
        if ((int)now != _activityPollSecond)
        {
            _activityPollSecond = (int)now;
            bool zcodeChanged = _activity.Poll();
            bool dshChanged = _dshActivity.Poll();
            if (zcodeChanged || dshChanged) _dirty = true;
        }
        if (AnyClientWorking) { _dirty = true; animate = true; }

        // 显示期间每秒保活置顶一次(防止被后来的置顶窗口压住),并强制重画一帧。
        // 分层窗口(AllowsTransparency)滑出到屏外期间, WPF 会把失效请求丢掉且之后不再补:
        // 只靠"数据变了才重绘"的话, 环上会永久停在旧数字(卡片是新的、环是旧的)。
        // **菜单开着时必须跳过**:菜单窗也是 topmost 且激活在前,这一下会把 rail 提到
        // 菜单上面——右键菜单弹两秒后被 rail 盖住,就是它干的。
        // 用时间判定(2 秒一次)而不是 tick 计数:慢档下 tick 间隔是变的,
        // 按 60 计数会在慢档变成 6 秒一次,置顶保活就形同虚设。
        if (_peekVisible && !_menuOpen)
        {
            if (_lastKeepAlive == 0 || now - _lastKeepAlive >= 1)
            {
                _lastKeepAlive = now;
                Native.BringToTopmost(this);
                _dirty = true;
            }
        }

        // 全屏隐藏:前台窗口盖满所在显示器(±8px 容差)时把 rail 收起来;退出全屏后
        // 交还给正常的热区逻辑(不自动弹回,不打扰)。用时间判定(2 秒一次),理由同上。
        // **开关关掉时必须把状态清回 false**:否则 UpdatePeek 会一直以为"还在全屏隐藏"
        // 而拒绝展开,rail 就永远回不来了。
        if (!AppSettings.Current.HideInFullScreen)
        {
            if (_fullScreenHide) _fullScreenHide = false;
        }
        else if (!_dragging && !_menuOpen
            && (_lastFullScreenCheck == 0 || now - _lastFullScreenCheck >= 2))
        {
            _lastFullScreenCheck = now;
            bool fs = Native.ForegroundIsFullScreen();
            if (fs != _fullScreenHide)
            {
                _fullScreenHide = fs;
                // 记一条:这个开关以前从来没生效过(入口点缺失 + 热区立刻拉回),
                // 现在留痕,方便验证与排查。
                Diagnostics.Note($"全屏隐藏: 前台全屏 = {fs}");
                if (fs)
                {
                    HideCard();
                    SetPeekVisible(false, Native.WorkingAreaUnderPointer(DpiScale));
                }
                _dirty = true;
            }
        }

        // 只在内容真的变了才重绘:静止且藏屏外时一帧都不画
        if (_dirty) InvalidateVisual();

        // 帧率自适应:只在**真的有东西在动**时用快档。静止不动时即使 rail 露在屏幕上,
        // 也不需要 60fps——指针轮询与命中用慢档完全够(探入热区后 SetPeekVisible 让
        // 动画跑起来,下一帧就切回快档,手感不受影响)。
        //
        // **唯一的例外是指针正停在 rail 上**:那时用户随时可能在环上单击触发刷新,
        // 或开始拖动。100ms 慢档下按下与抬起有可能落在同一帧两侧被判成"没有拖动",
        // 于是就用快档保证命中不被漏掉(指针不在 rail 上时照旧慢档,CPU 仍是 0)。
        //
        // **不要为了"响应快"让 _peekVisible 常驻快档**:那是 60Hz 永久空转,
        // 实测 idle CPU 会长期停在个位数百分比(用户抱怨"挺占内存"其实也含这份重绘)。
        SetTempo(animate || _pointerOnRail);
    }

    /// <summary>本帧指针是否落在 rail(或详情卡)上——决定要不要用快档(见 Tick)。</summary>
    private bool _pointerOnRail;

    /// <summary>切换主循环节拍(只在档位变化时改 Interval,避免每帧写属性)。</summary>
    private void SetTempo(bool fast)
    {
        int want = fast ? TickFastMs : TickSlowMs;
        if (_tempoMs == want) return;
        _tempoMs = want;
        _ticker.Interval = TimeSpan.FromMilliseconds(want);
    }

    private int _tempoMs = TickSlowMs;
    private double _lastKeepAlive;
    private double _lastFullScreenCheck;
    private bool _wasAnimating;

    // —— 客户端活动(环心呼吸):两个客户端都走 Command Code 网关,所以都点 GOAT 这一圈 ——
    private readonly ZCodeActivity _activity = new();
    private readonly DshActivity _dshActivity = new();
    private int _activityPollSecond = -1;

    /// <summary>有没有客户端正在跑一轮(ZCode 读它的日志,DSH 读它的会话投影)。</summary>
    private bool AnyClientWorking => _activity.IsWorking || _dshActivity.IsWorking;

    // ————————————————— 指针 —————————————————

    private void HandlePointer(Point diu, bool leftDown, double now)
    {
        Point rel = new(diu.X - Left, diu.Y - Top);
        bool inWindow = rel.X >= -2 && rel.Y >= -2 && rel.X <= Width + 2 && rel.Y <= Height + 2;
        // sliver 模式(贴在屏边的小条)下窗口大部分在屏外,而环心仍在窗口中央——
        // 命中半径会盖住露出的那 6pt 细条。悬停细条只该"展开",不该弹出一张
        // 指向屏外环心的详情卡,所以这里不给它环命中。
        int? ring = inWindow && !_sliverMode ? RingUnder(rel) : null;
        bool overBerth = inWindow && _hitBerth is { } g && g.FillContains(rel);

        if (leftDown)
        {
            if (!_downArmed)
            {
                if (overBerth)
                {
                    _downArmed = true;
                    _dragging = false;
                    _downDiu = diu;
                    _dragOffset = new Point(diu.X - Left, diu.Y - Top);
                    _downRing = ring;
                }
            }
            else if (!_dragging && (diu - _downDiu).Length > 6)
            {
                _dragging = true;
                _hoverRing = null;
                HideCard();
                if (_docked) ToFloating();
            }

            if (_dragging)
            {
                MoveOrSnap(new Point(diu.X - _dragOffset.X, diu.Y - _dragOffset.Y));
                _dirty = true;
            }
        }
        else if (_downArmed)
        {
            if (!_dragging && _downRing is { } ri)
            {
                // 点击环 = 立刻打一次真实 API。只重读本地快照是看不出变化的
                // (引擎最多 60s 才落盘一次),所以这里同时通知引擎去拉新数据。
                ReloadData();
                RefreshRequested?.Invoke();
                _refreshPending = true;
                _refreshStart[ri] = now;
                _refreshUntil[ri] = now + RefreshAnimationTimeoutS;
                _dirty = true;
            }
            _downArmed = false;
            _dragging = false;
            _downRing = null;
        }

        bool overCard = _card.IsVisible &&
            diu.X >= _card.Left - 4 && diu.X <= _card.Left + _card.Width + 4 &&
            diu.Y >= _card.Top - 4 && diu.Y <= _card.Top + _card.Height + 4;

        // 菜单开着的时候**不显示卡片**:菜单就弹在 rail 旁边,指针坐标必然还在某个
        // 环的范围内,不挡这一条的话卡片会立刻又冒出来盖住菜单。
        int? newHover = _menuOpen ? null
            : overCard && _hoverRing is { } keep
                ? keep
                : (!_dragging && (_docked || !_peekVisible) ? ring : null);
        if (newHover != _hoverRing)
        {
            _hoverRing = newHover;
            if (newHover is { } hi) ShowSubCard(hi);
            else HideCard();
            _dirty = true;
        }

        bool overContent = overBerth || ring != null || overCard || _dragging || leftDown;
        _pointerOnRail = inWindow || overCard;
        UpdatePeek(diu, overContent, leftDown, now);

        // rail 滑入/拖动中位置在变:每帧把卡窗贴回 rail 旁
        if (_card.IsVisible && _hoverRing is { }) PositionCard();
    }

    private void UpdatePeek(Point diu, bool overContent, bool leftDown, double now)
    {
        if (!AutoHideEnabled || !_docked || _dragging || _menuOpen) return;
        // 全屏隐藏期间**不许热区把它拉回来**:全屏看视频时指针本来就常在屏幕边缘
        // (视频控制条、鼠标乱晃),允许热区展开的话"全屏时隐藏"瞬间就被撤销——
        // 这正是用户觉得这个开关"没什么用"的原因。退出全屏后(_fullScreenHide 归 false)
        // 热区恢复正常,不自动弹回,不打扰。
        if (_fullScreenHide) return;
        var wa = Native.WorkingAreaUnderPointer(DpiScale);

        // 热区只覆盖 rail 自己所在的那段区域(垂直居中的主界面高度范围),
        // 避免把鼠标甩到屏边其它高度(如关窗口角)误触发。
        double railTop = FitTop(wa, Height);
        double railLeft = wa.X + Math.Max(0, (wa.Width - Width) / 2);
        bool inHot = _dockEdge switch
        {
            DockEdge.Right => diu.X >= wa.Right - HotZoneWidth
                && diu.Y >= railTop - 6 && diu.Y <= railTop + Height + 6,
            DockEdge.Left => diu.X <= wa.X + HotZoneWidth
                && diu.Y >= railTop - 6 && diu.Y <= railTop + Height + 6,
            _ => diu.Y <= wa.Y + HotZoneWidth
                && diu.X >= railLeft - 6 && diu.X <= railLeft + Width + 6,
        };

        if (_peekVisible)
        {
            if (overContent || inHot) _hideSince = -1;
            else
            {
                // 用真实时间差而非累加固定帧长:DispatcherTimer 在负载下会漂移
                if (_hideSince < 0) _hideSince = now;
                if (now - _hideSince > HideDelaySeconds) SetPeekVisible(false, wa);
            }
        }
        else if (inHot)
        {
            SetPeekVisible(true, wa);
        }
    }

    private void SetPeekVisible(bool show, Rect wa)
    {
        // —— 隐藏的两种形态 ——
        // sliver 开:窗口只滑到"露出停靠缘 6pt",那 6pt 细条就是收起后的信号灯;
        // sliver 关:**完全滑出屏外**(多退 2pt,避免窗口边缘还压着屏幕边界)。
        // v1.7.0 引入 sliver 时把两个分支写成了同一个"留 6pt",于是"关掉贴边小条"
        // 也照样在屏幕边缘留一条黑边——这正是用户说的"自动隐藏没什么用":
        // 开与关看不出区别,而且那条黑边既不是细条也不是隐藏。
        _peekVisible = show;
        _hideSince = -1;
        bool sliver = !show && AppSettings.Current.HideToSliver && !_fullScreenHide;
        _sliverTarget = sliver;
        // 展开时立刻退出细条形态(要滑的就是完整 rail);收起时先保持完整形态滑出去,
        // 动画停稳后(见 Tick)再切细条,免得滑到一半变成一条线。
        if (show) _sliverMode = false;
        double sliverW = Pt.P(SliverWidthPt);
        switch (_dockEdge)
        {
            case DockEdge.Right:
                _targetLeft = show ? wa.Right - Width
                    : sliver ? wa.Right - sliverW
                    : wa.Right + 2;
                _targetTop = FitTop(wa, Height);
                break;
            case DockEdge.Left:
                _targetLeft = show ? wa.X
                    : sliver ? wa.X - Width + sliverW
                    : wa.X - Width - 2;
                _targetTop = FitTop(wa, Height);
                break;
            default:
                // 顶轨:sliver 是水平细条,只露出顶部;关掉则整条滑出屏幕上方
                _targetLeft = show ? wa.X + Math.Max(0, (wa.Width - Width) / 2) : Left;
                _targetTop = show ? wa.Y
                    : sliver ? wa.Y - Height + sliverW
                    : wa.Y - Height - 2;
                break;
        }
        if (!show) { HideCard(); _hoverRing = null; }
        _dirty = true;

        // 滑入的那一帧窗口还在屏外, 那次失效会被丢弃; 这里补提交一次, 保证滑进来的
        // 第一帧就是当前数据, 而不是屏外时的旧内容。
        if (show) InvalidateVisual();

        // 滑入/滑出后把窗口提到置顶最上,防止被其他置顶窗口压住
        Native.BringToTopmost(this);
    }

    private bool AnimatePeek()
    {
        if (!AutoHideEnabled || !_docked || _dragging || double.IsNaN(_targetTop)) return false;
        bool moved = false;
        if (Math.Abs(Left - _targetLeft) > 0.5) { Left += (_targetLeft - Left) * PeekStep; moved = true; }
        if (Math.Abs(Top - _targetTop) > 0.5) { Top += (_targetTop - Top) * PeekStep; moved = true; }
        return moved;
    }

    // ————————————————— 拖拽 / 停靠 —————————————————

    private void ToFloating()
    {
        if (!_docked) return;
        _docked = false;
        _dockEdge = DockEdge.Floating;
        HideCard();
        ApplyRailSize();
        _dirty = true;
    }

    private void MoveOrSnap(Point tl)
    {
        var wa = Native.WorkingAreaUnderPointer(DpiScale);
        double snap = Dock.DockSnapDistance;
        Size s = RailSize();
        Rect r = new(tl.X, tl.Y, s.Width, s.Height);

        if (r.Right - wa.Right > -snap && r.Top < wa.Bottom && r.Bottom > wa.Y)
            DockTo(DockEdge.Right, wa);
        else if (wa.X - r.Left > -snap && r.Top < wa.Bottom && r.Bottom > wa.Y)
            DockTo(DockEdge.Left, wa);
        else if (wa.Y - r.Top > -snap && r.Left < wa.Right && r.Right > wa.X)
            DockTo(DockEdge.Top, wa);
        else
        {
            if (_docked) ToFloating();
            double x = Math.Clamp(tl.X, wa.X, Math.Max(wa.X, wa.Right - Width));
            double y = Math.Clamp(tl.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - Height));
            Left = x; Top = y;
        }
    }

    private void DockTo(DockEdge edge, Rect wa)
    {
        _docked = true;
        _dockEdge = edge;
        _peekVisible = true;
        _hideSince = -1;
        ApplyRailSize();
        switch (edge)
        {
            case DockEdge.Right:
                Left = wa.Right - Width; Top = FitTop(wa, Height);
                break;
            case DockEdge.Left:
                Left = wa.X; Top = FitTop(wa, Height);
                break;
            default:
                Left = wa.X + Math.Max(0, (wa.Width - Width) / 2); Top = wa.Y;
                break;
        }
        _targetLeft = Left;
        _targetTop = Top;
        _dirty = true;
        InvalidateVisual();
    }

    private Size RailSize()
    {
        double w = Pt.P(RailWpt);
        double h = Pt.P(_railHeight);
        return IsVertical ? new Size(w, h) : new Size(h, w);
    }

    private void ApplyRailSize()
    {
        var s = RailSize();
        Width = s.Width;
        Height = s.Height;
    }

    private double FitTop(Rect wa, double h) => h >= wa.Height ? wa.Y : wa.Y + (wa.Height - h) / 2;

    // ————————————————— 卡片 —————————————————

    private void ShowSubCard(int ringIndex, bool force = false)
    {
        if (ringIndex < 0 || ringIndex >= _subs.Count) return;
        if (!force && _card.IsVisible && _cardFor == ringIndex) return; // 已显示,位置交给每帧跟随
        var sub = _subs[ringIndex];
        bool stale = sub.IsStale(DateTimeOffset.UtcNow);
        var (win, body) = CardLayout.SubCard(sub.Pools.Count, stale, sub.HourlySpend is not null);
        _cardFor = ringIndex;
        _card.Configure(sub, win, body, stale);
        _card.ShowCard();
        PositionCard();
    }

    /// <summary>把卡窗放到当前 rail 位置旁。每帧调用,rail 滑入/拖动时卡片跟着走,不会与 rail 重叠。</summary>
    private void PositionCard()
    {
        if (!_card.IsVisible || _cardFor is not { } ringIndex) return;
        var wa = Native.WorkingAreaUnderPointer(DpiScale);
        double gap = CardLayout.GapToRail;
        Point c = RingCenter(ringIndex);
        Point ringWorld = new(Left + c.X, Top + c.Y);
        double winX, winY;
        if (IsVertical)
        {
            bool railOnLeft = (Left + Width / 2) <= wa.X + wa.Width / 2;
            winX = railOnLeft ? Left + Width + gap : Left - gap - _card.Width;
            winY = ringWorld.Y - _card.Height / 2;
        }
        else
        {
            winX = ringWorld.X - _card.Width / 2;
            winY = Top + Height + gap;
        }

        double loY = wa.Y + 2, hiY = wa.Bottom - _card.Height - 2;
        winY = hiY >= loY ? Math.Clamp(winY, loY, hiY) : wa.Y + 2;
        double loX = wa.X + 2, hiX = wa.Right - _card.Width - 2;
        winX = hiX >= loX ? Math.Clamp(winX, loX, hiX) : wa.X + 2;

        _card.MoveTo(new Point(winX, winY));
    }

    private void HideCard()
    {
        _cardFor = null;
        _card.HideCard();
    }

    // ————————————————— 几何 —————————————————

    private Point RingCenter(int ringIndex)
    {
        double along = Pt.P(_ringY[ringIndex]);
        return IsVertical
            ? new Point(Width / 2, along)
            : new Point(along, Height / 2);
    }

    private int? RingUnder(Point rel)
    {
        double rr = Pt.P(OuterRingDpt) / 2 + 8;
        for (int i = 0; i < _subs.Count; i++)
        {
            var c = RingCenter(i);
            double dx = rel.X - c.X, dy = rel.Y - c.Y;
            if (dx * dx + dy * dy <= rr * rr) return i;
        }
        return null;
    }

    // ————————————————— 复合环渲染 —————————————————

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double now = _clock.Elapsed.TotalSeconds;

        if (_sliverMode)
        {
            DrawSliver(dc);
            return;
        }

        var full = new Rect(0, 0, Width, Height);
        var berth = BerthGeometry(full);
        _hitBerth = berth;
        // 面板永远是自绘的黑色玻璃(不透明度可调):v1.7.3 起不再有系统 acrylic 分支。
        dc.DrawGeometry(CachedBrush(PanelPalette.Surface), HairlinePen, berth);

        for (int s = 0; s < _subs.Count; s++)
            DrawComposite(dc, _subs[s], RingCenter(s), s, now);
    }

    /// <summary>
    /// 贴边小条:圆角细条,贴停靠缘、沿 rail 居中。有源越过警报阈值时整条染上警报色
    /// (上游的 alert tint)——细条是 rail 收起来后仅存的信号,该说话时不能沉默。
    /// </summary>
    private void DrawSliver(DrawingContext dc)
    {
        var r = SliverRect();
        double rad = Math.Min(r.Width, r.Height) / 2;
        Color fill = Color.FromRgb(0x0A, 0x0A, 0x0E);
        byte alpha = (byte)(AppSettings.Current.SurfaceOpacity * 255);
        fill = Color.FromArgb(alpha, fill.R, fill.G, fill.B);

        double worst = _subs.Where(s2 => s2.Pools.Count > 0)
            .Select(s2 => s2.Pools.Where(p2 => p2.IsAvailable).Select(p2 => p2.Fraction).DefaultIfEmpty(0).Max())
            .DefaultIfEmpty(0).Max();
        if (worst >= UsageTint.AlarmThreshold)
        {
            Color alert = UsageTint.For(worst, false);
            fill = Color.FromArgb(0xE6, alert.R, alert.G, alert.B);
        }
        dc.DrawRoundedRectangle(CachedBrush(fill), HairlinePen, r, rad, rad);
        _hitBerth = RailGeometry.SquircleRoundRect(r.X, r.Y, r.Width, r.Height, rad);
    }

    /// <summary>berth 轮廓约 200 个采样点,是每帧最贵的构建;尺寸/停靠不变时复用。</summary>
    private Geometry BerthGeometry(Rect full)
    {
        if (_berthCache is not null && _berthCacheSize == full.Size &&
            _berthCacheEdge == _dockEdge && _berthCacheDocked == _docked &&
            _berthCacheScale == BerthScale)
        {
            return _berthCache;
        }
        _berthCache = RailGeometry.Berth(full, _docked ? _dockEdge : DockEdge.Floating, _docked, 1, BerthScale);
        _berthCache.Freeze();
        _berthCacheSize = full.Size;
        _berthCacheEdge = _dockEdge;
        _berthCacheDocked = _docked;
        _berthCacheScale = BerthScale;
        return _berthCache;
    }

    private void DrawComposite(DrawingContext dc, SubData sub, Point c, int idx, double now)
    {
        bool vertical = IsVertical;
        var five = PoolOf(sub, "FiveHour");
        var month = PoolOf(sub, "Monthly");
        // 余额型数据源(DeepSeek)只有一个池:没有 5 小时/本月的分片,
        // 钱也没有"窗口"这回事,所以它只占一圈。
        var balance = PoolOf(sub, "Balance");

        bool refreshing = _refreshStart[idx] > 0 && now < _refreshUntil[idx];

        if (balance is not null)
        {
            DrawArcLayer(dc, c, Pt.P(OuterRingDpt), Pt.P(OuterWpt), balance, sub, idx, now, vertical, refreshing);
        }
        else
        {
            // 从内到外两圈:5小时(内,细)→ 总额度(外,粗)。
            // 但**空着的 5 小时圈不画**(设置项 HideIdleInnerRing,默认开):
            // 大多数源的 5 小时窗口是 0%,那一圈空轨道只增加噪音;省掉它之后
            // "内圈出现"本身就表示这个源正在被用。外圈的位置与环心不动,
            // 所以源之间不会因为有没有内圈而错位。
            if (ShowInnerRing(sub))
                DrawArcLayer(dc, c, Pt.P(InnerRingDpt), Pt.P(InnerWpt), five, sub, idx, now, vertical, refreshing);
            DrawArcLayer(dc, c, Pt.P(OuterRingDpt), Pt.P(OuterWpt), month, sub, idx, now, vertical, refreshing);
        }

        DrawCentreIcon(dc, sub, c);

        // 环下的读数(变色报警),不显示账户名。余额型取它的唯一池。
        // 设置里关掉读数后,这里什么都不画——rail 就是一条纯环列。
        if (!ShowPercent) return;

        var headline = balance ?? month;
        bool avail = headline is { IsAvailable: true };
        double mf = avail ? Math.Clamp(headline!.Fraction, 0, 1) : 0;
        Color mtint = avail ? TintFor(sub, mf, headline!.IsSpent) : PanelPalette.Track;
        string label = avail ? headline!.DisplayText : "--";
        // 百分比只有 "100%" 这么宽,金额却没有上限("¥1234.5" 比它长得多),
        // 所以余额型读数用小一号,再叠一层宽度保护,极端值也不会顶到 rail 边
        bool moneyReadout = headline is { PoolKind: "Balance", HasPercent: false };
        var labelFt = FitLabel(label, mtint, moneyReadout ? 12 : 13);
        double outerR = Pt.P(OuterRingDpt) / 2;
        if (vertical)
        {
            // 侧轨:读数紧贴在环下方
            double labelTop = c.Y + outerR + Pt.P(RingToTextGap);
            dc.DrawText(labelFt, new Point(c.X - labelFt.Width / 2, labelTop));
        }
        else
        {
            // 顶轨:环下方没有空间(会画到窗外),改放环右侧
            dc.DrawText(labelFt, new Point(c.X + outerR + Pt.P(5), c.Y - labelFt.Height / 2));
        }
    }

    /// <summary>
    /// 取一个能塞进 rail 宽度的读数:先按给定字号试,放不下就按比例缩。
    /// 字号量化到 0.5pt —— 否则 FormattedText 缓存会被连续变化的字号撑爆。
    /// </summary>
    private FormattedText FitLabel(string text, Color color, double baseSizePt)
    {
        var brush = CachedBrush(color);
        // 两侧各留 9pt 的呼吸空间:rail 只有 56pt 宽,读数紧贴边会显得要溢出来
        double available = Pt.P(RailWpt) - Pt.P(18);
        double size = Pt.P(baseSizePt);

        var fitted = NewText(text, size, FontWeights.SemiBold, brush);
        if (fitted.Width <= available) return fitted;

        double step = Pt.P(0.5);
        double target = Math.Max(size * available / fitted.Width, Pt.P(9));
        double quantized = Math.Max(Math.Floor(target / step) * step, Pt.P(9));
        return NewText(text, quantized, FontWeights.SemiBold, brush);
    }

    /// <summary>环心图标:GOAT 用官方羊,DeepSeek 用官方鲸鱼,其余用 GO 方块——都是白色单色。</summary>
    private void DrawCentreIcon(DrawingContext dc, SubData sub, Point c)
    {
        // 鲸鱼在 24 viewBox 里是"宽而扁"的(24×17.7),同样宽度下比方形图标矮一截,
        // 所以给它多 3pt 让细节看得清。
        double box = Pt.P(sub.Key == "deepseek" ? 17 : 14);

        // ZCode / DSH 正在跑一轮时,环心图标轻轻呼吸(92%~100%,约 1.2 秒一轮)。
        // **只有 GOAT 环会呼吸**:这两个客户端都走 Command Code 网关,烧的正是这一圈额度。
        if (AnyClientWorking && sub.Key == "goat")
            box *= BreathScale(_clock.Elapsed.TotalSeconds);

        switch (sub.Key)
        {
            case "goat":
                var bitmap = Icons.GoatBitmap();
                if (bitmap is not null)
                    dc.DrawImage(bitmap, new Rect(c.X - box / 2, c.Y - box / 2, box, box));
                break;

            case "deepseek":
                var whale = Icons.DeepSeekGeometry().Clone();
                double whaleScale = box / 24.0;
                whale.Transform = new MatrixTransform(
                    whaleScale, 0, 0, whaleScale, c.X - box / 2, c.Y - box / 2);
                dc.DrawGeometry(WhiteBrush, null, whale);
                break;

            default:
                var geometry = Icons.OpenCodeGeometry().Clone();
                double scale = box / 24.0;
                geometry.Transform = new MatrixTransform(
                    scale, 0, 0, scale, c.X - box / 2, c.Y - box / 2);
                dc.DrawGeometry(WhiteBrush, null, geometry);
                break;
        }
    }

    /// <summary>呼吸的周期(秒)与幅度:一个周期结束回到原样,所以看起来是"呼气—吸气"而不是闪烁。</summary>
    private const double BreathPeriodS = 1.2;
    private const double BreathDepth = 0.08;

    /// <summary>环心图标的呼吸缩放:在 (1 - BreathDepth) ~ 1 之间按余弦来回。</summary>
    private static double BreathScale(double seconds)
    {
        double phase = (seconds % BreathPeriodS) / BreathPeriodS;
        return 1.0 - BreathDepth * (1 - Math.Cos(phase * 2 * Math.PI)) / 2;
    }

    /// <summary>
    /// 环的颜色。**默认含义是"离上限还有多远"**(绿→红),不是"这是哪个产品"
    /// ——产品由环心图标表示。设置里给某个源固定颜色是可选的(per-account),
    /// 但**封顶时一定是深红**:那是唯一不许被自定义色盖掉的状态。
    /// </summary>
    private static Color TintFor(SubData sub, double fraction, bool spent)
    {
        Color automatic = UsageTint.For(fraction, spent);
        if (spent) return automatic;
        return PanelPalette.Parse(AppSettings.Current.TintFor(sub.Key)) ?? automatic;
    }

    /// <summary>最外圈:GOAT/GO 的月弧,或余额型数据源唯一的那一圈。</summary>
    private static bool IsOutermost(string poolKind) => poolKind is "Monthly" or "Balance";

    /// <summary>
    /// 这个源要不要画内圈(5 小时环)。
    ///
    /// **判定 = "5 小时窗口内是否被用过"**:刚重置(还没用)的窗口 `Used` 是 0,
    /// 此时只画外圈;一旦这个源动过(哪怕只花掉 0.1%),内圈就出现——所以
    /// "哪个源正在被用"看一眼就知道,不必逐个去读百分比。
    ///
    /// 关掉设置项(<see cref="AppSettings.HideIdleInnerRing"/>)就退回旧行为:总是两圈。
    /// 读数缺失(还没拿到数据)时按"要画"处理,免得数据没到就少一圈、来了又跳出来。
    /// </summary>
    private static bool ShowInnerRing(SubData sub)
    {
        if (!AppSettings.Current.HideIdleInnerRing) return true;
        var five = PoolOf(sub, "FiveHour");
        // 没有这一池的源(比如将来某个只报月额度的):不硬造一圈出来
        if (five is null) return false;
        if (!five.IsAvailable || five.Used is null) return true;   // 读数还没到,保持原样
        return five.Used > 0;
    }

    private static PoolData? PoolOf(SubData sub, string kind) =>
        sub.Pools.FirstOrDefault(p => p.PoolKind == kind);

    private void DrawArcLayer(DrawingContext dc, Point c, double midD, double lineW, PoolData? pool,
        SubData sub, int idx, double now, bool vertical, bool refreshing)
    {
        double midR = midD / 2;
        dc.DrawEllipse(null, CachedRingPen(TrackBrush, lineW), c, midR, midR);

        if (pool is null || !pool.IsAvailable) return;

        bool spent = pool.IsSpent;
        double frac = Math.Clamp(pool.Fraction, 0, 1);
        Color tint = TintFor(sub, frac, spent);

        // hover:给最外层弧画光晕
        if (_hoverRing == idx && frac > 0.004 && IsOutermost(pool.PoolKind))
        {
            dc.DrawGeometry(null,
                CachedRingPen(CachedBrush(Color.FromArgb(0x59, tint.R, tint.G, tint.B)), lineW + Pt.P(8)),
                RailGeometry.Arc(c, midR, -90, frac * 360));
        }

        // 用量弧(刷新时变暗;满额整圈)
        double sweep = spent ? 360 : frac * 360;
        if (sweep > 0.5)
        {
            double op = refreshing ? 0.3 : 1;
            var arc = CachedBrush(Color.FromArgb((byte)(op * 255 * 0.98), tint.R, tint.G, tint.B));
            dc.DrawGeometry(null, CachedRingPen(arc, lineW),
                RailGeometry.Arc(c, midR, -90, sweep));
        }

        // 刷新亮段(画在最外圈)
        if (refreshing && IsOutermost(pool.PoolKind))
        {
            double phase = ((now - _refreshStart[idx]) % Dock.RefreshPeriodS) / Dock.RefreshPeriodS;
            if (phase < 0) phase = 0;
            dc.DrawGeometry(null, CachedRingPen(CachedBrush(tint), lineW),
                RailGeometry.Arc(c, midR, -90 + phase * 360, Dock.RefreshSweep * 360));
        }
    }

    /// <summary>
    /// 文字缓存:FormattedText 的构建(字体回退解析)不便宜,而内容变化很少。
    /// DPI 变化时 FormattedText 需要重建,所以缓存键里带上 dpi。
    /// </summary>
    private FormattedText NewText(string s, double fontSize, FontWeight weight, Brush brush)
    {
        double dpi = DpiScale;
        var color = brush is SolidColorBrush scb ? scb.Color : Colors.White;
        var key = (s, fontSize, weight, color);
        if (_textCacheScale != dpi)
        {
            _textCache.Clear();
            _textCacheScale = dpi;
        }
        if (_textCache.TryGetValue(key, out var cached)) return cached;

        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(TextFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize, CachedBrush(color), dpi);
        _textCache[key] = ft;
        return ft;
    }

    private static readonly FontFamily TextFontFamily = new("Segoe UI, Microsoft YaHei UI");
    private double _textCacheScale = -1;
}
