using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
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

    // —— 渲染节流 ——
    private bool _dirty = true;                  // 本帧内容是否有变化
    private Geometry? _berthCache;               // berth 轮廓(约 200 点,重建最贵)
    private Size _berthCacheSize;
    private DockEdge _berthCacheEdge;
    private bool _berthCacheDocked;
    private readonly Dictionary<(string, double, FontWeight, Color), FormattedText> _textCache = new();

    /// <summary>请求马上走一次真实 API 刷新(由 App 接到 UsageEngine)。</summary>
    public event Action? RefreshRequested;

    private const bool AutoHideEnabled = true;
    private const double HotZoneWidth = 16;
    private const double HideDelaySeconds = 0.9;
    private const double PeekStep = 0.5;
    /// <summary>点击刷新后动画的安全上限(正常情况下 SnapshotsChanged 会提前结束它)。</summary>
    private const double RefreshAnimationTimeoutS = 45;

    // —— rail / 复合环几何(pt 单位,渲染 ×Pt.U) ——
    private const double RailWpt = 56;
    private const double UnitHpt = 84;
    private const double UnitGappt = 10;
    private const double PadTopPt = 26;
    private const double PadBottomPt = 22;
    private const double RingCenterInUnit = 30; // 环心距 unit 顶

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
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
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

    /// <summary>托盘"显示/隐藏":手动切换停靠 rail 的滑入滑出。</summary>
    public void ToggleRailVisible()
    {
        if (!_docked) return;
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
        RebuildLayout();

        // 正在悬停的卡片要跟着换成新数据:ShowSubCard 在同一条环上会提前返回,
        // 不在这里重配的话,鼠标停在环上不动时卡片会一直显示打开那一刻的旧值。
        if (_card.IsVisible && _cardFor is { } shown && shown < _subs.Count)
            ShowSubCard(shown, force: true);

        // 引擎回报了新数据:结束"刷新中"动画(点击环触发的)
        if (fromEngine && _refreshPending)
        {
            _refreshPending = false;
            Array.Clear(_refreshStart);
            Array.Clear(_refreshUntil);
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

    private PoolData? PoolOf(SubData sub, string kind) =>
        sub.Pools.FirstOrDefault(p => p.PoolKind == kind);

    /// <summary>设置里的渲染参数(报警阈值/不透明度)变了:作废缓存并重绘。</summary>
    public void ApplySettings()
    {
        _textCache.Clear();
        _dirty = true;
        InvalidateVisual();
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        _dirty = false;

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

        if (AnimatePeek()) _dirty = true;
        if (refreshing) _dirty = true;   // 亮段扫动是逐帧动画

        // 显示期间每秒保活置顶一次(防止被后来的置顶窗口压住),并强制重画一帧。
        // 分层窗口(AllowsTransparency)滑出到屏外期间, WPF 会把失效请求丢掉且之后不再补:
        // 只靠"数据变了才重绘"的话, 环上会永久停在旧数字(卡片是新的、环是旧的)。
        if (_peekVisible && (_tickCount++ % 60 == 0))
        {
            Native.BringToTopmost(this);
            _dirty = true;
        }

        // 只在内容真的变了才重绘:静止且藏屏外时一帧都不画
        if (_dirty) InvalidateVisual();
    }

    private int _tickCount;

    // ————————————————— 指针 —————————————————

    private void HandlePointer(Point diu, bool leftDown, double now)
    {
        Point rel = new(diu.X - Left, diu.Y - Top);
        bool inWindow = rel.X >= -2 && rel.Y >= -2 && rel.X <= Width + 2 && rel.Y <= Height + 2;
        int? ring = inWindow ? RingUnder(rel) : null;
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

        int? newHover = overCard && _hoverRing is { } keep
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
        UpdatePeek(diu, overContent, leftDown, now);

        // rail 滑入/拖动中位置在变:每帧把卡窗贴回 rail 旁
        if (_card.IsVisible && _hoverRing is { }) PositionCard();
    }

    private void UpdatePeek(Point diu, bool overContent, bool leftDown, double now)
    {
        if (!AutoHideEnabled || !_docked || _dragging) return;
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
        _peekVisible = show;
        _hideSince = -1;
        switch (_dockEdge)
        {
            case DockEdge.Right:
                _targetLeft = show ? wa.Right - Width : wa.Right + 2;
                _targetTop = FitTop(wa, Height);
                break;
            case DockEdge.Left:
                _targetLeft = show ? wa.X : wa.X - Width - 2;
                _targetTop = FitTop(wa, Height);
                break;
            default:
                _targetLeft = Left;
                _targetTop = show ? wa.Y : wa.Y - Height - 2;
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
        var (win, body) = CardLayout.SubCard(sub.Pools.Count, stale);
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

    private const double OuterRingDpt = 44;   // 月弧中线直径(外圈)
    private const double OuterWpt = 4;
    private const double InnerRingDpt = 34;   // 5小时弧(内圈,细,贴近外圈)
    private const double InnerWpt = 2.5;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double now = _clock.Elapsed.TotalSeconds;

        var full = new Rect(0, 0, Width, Height);
        var berth = BerthGeometry(full);
        _hitBerth = berth;
        // 用缓存取画笔:设置里改了不透明度后自动跟着变
        dc.DrawGeometry(CachedBrush(PanelPalette.Surface), HairlinePen, berth);

        for (int s = 0; s < _subs.Count; s++)
            DrawComposite(dc, _subs[s], RingCenter(s), s, now);
    }

    /// <summary>berth 轮廓约 200 个采样点,是每帧最贵的构建;尺寸/停靠不变时复用。</summary>
    private Geometry BerthGeometry(Rect full)
    {
        if (_berthCache is not null && _berthCacheSize == full.Size &&
            _berthCacheEdge == _dockEdge && _berthCacheDocked == _docked)
        {
            return _berthCache;
        }
        _berthCache = RailGeometry.Berth(full, _docked ? _dockEdge : DockEdge.Floating, _docked, 1);
        _berthCache.Freeze();
        _berthCacheSize = full.Size;
        _berthCacheEdge = _dockEdge;
        _berthCacheDocked = _docked;
        return _berthCache;
    }

    private void DrawComposite(DrawingContext dc, SubData sub, Point c, int idx, double now)
    {
        bool vertical = IsVertical;
        var five = PoolOf(sub, "FiveHour");
        var month = PoolOf(sub, "Monthly");

        bool refreshing = _refreshStart[idx] > 0 && now < _refreshUntil[idx];

        // 从内到外两圈:5小时(内,细)→ 总额度(外,粗)
        DrawArcLayer(dc, c, Pt.P(InnerRingDpt), Pt.P(InnerWpt), five, idx, now, vertical, refreshing);
        DrawArcLayer(dc, c, Pt.P(OuterRingDpt), Pt.P(OuterWpt), month, idx, now, vertical, refreshing);

        // 环心小图标(品牌:白羊剪影 / GO 方块)
        double icon = Pt.P(14);
        if (sub.Key == "goat")
        {
            var bmp = Icons.GoatBitmap();
            if (bmp is not null)
                dc.DrawImage(bmp, new Rect(c.X - icon / 2, c.Y - icon / 2, icon, icon));
        }
        else
        {
            var g = Icons.OpenCodeGeometry().Clone();
            double s = icon / 24.0;
            g.Transform = new MatrixTransform(s, 0, 0, s, c.X - icon / 2, c.Y - icon / 2);
            dc.DrawGeometry(WhiteBrush, null, g);
        }

        // 用量百分比(变色报警),不显示账户名。
        bool avail = month is { IsAvailable: true };
        double mf = avail ? Math.Clamp(month!.Fraction, 0, 1) : 0;
        Color mtint = avail ? UsageTint.For(mf, month!.IsSpent) : PanelPalette.Track;
        string pct = avail ? month!.PercentText : "--";
        var pctFt = NewText(pct, Pt.P(13), FontWeights.SemiBold, CachedBrush(mtint));
        double outerR = Pt.P(OuterRingDpt) / 2;
        if (vertical)
        {
            // 侧轨:百分比放在环下方
            double pctTop = c.Y + outerR + Pt.P(6);
            dc.DrawText(pctFt, new Point(c.X - pctFt.Width / 2, pctTop));
        }
        else
        {
            // 顶轨:环下方没有空间(会画到窗外),改放环右侧
            dc.DrawText(pctFt, new Point(c.X + outerR + Pt.P(5), c.Y - pctFt.Height / 2));
        }
    }

    private void DrawArcLayer(DrawingContext dc, Point c, double midD, double lineW, PoolData? pool,
        int idx, double now, bool vertical, bool refreshing)
    {
        double midR = midD / 2;

        // 轨道(18% 白)恒画,表示该圈存在
        dc.DrawEllipse(null, CachedRingPen(TrackBrush, lineW), c, midR, midR);

        if (pool is null || !pool.IsAvailable) return;

        bool spent = pool.IsSpent;
        double frac = Math.Clamp(pool.Fraction, 0, 1);
        Color tint = UsageTint.For(frac, spent);

        // hover:给最外层月弧画光晕
        if (_hoverRing == idx && frac > 0.004 && pool.PoolKind == "Monthly")
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
        if (refreshing && pool.PoolKind == "Monthly")
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
