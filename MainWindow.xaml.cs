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

    // —— 停靠 / 热区显隐 ——
    private DockEdge _dockEdge = DockEdge.Right;
    private bool _docked = true;
    private bool _peekVisible = true;
    private double _hideSeconds;
    private double _targetLeft, _targetTop = double.NaN;

    // —— 指针 / 拖拽 ——
    private bool _downArmed, _dragging;
    private Point _downDiu, _dragOffset;
    private int? _downRing, _hoverRing;
    private int? _cardFor;

    // —— 点击刷新 ——
    private readonly double[] _refreshUntil = new double[8];
    private readonly double[] _refreshStart = new double[8];

    private Geometry? _hitBerth;

    private const bool AutoHideEnabled = true;
    private const double HotZoneWidth = 16;
    private const double HideDelaySeconds = 0.9;
    private const double PeekStep = 0.5;

    // —— rail / 复合环几何(pt 单位,渲染 ×Pt.U) ——
    private const double RailWpt = 56;
    private const double UnitHpt = 84;
    private const double UnitGappt = 10;
    private const double PadTopPt = 26;
    private const double PadBottomPt = 22;
    private const double RingCenterInUnit = 30; // 环心距 unit 顶

    private static readonly Brush SurfaceBrush = Frz(PanelPalette.Surface);
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

    public MainWindow()
    {
        InitializeComponent();
        Native.ApplyToolWindowStyle(this);
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

    public void ReloadData()
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
        RebuildLayout();
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
    }

    private PoolData? PoolOf(SubData sub, string kind) =>
        sub.Pools.FirstOrDefault(p => p.PoolKind == kind);

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;

        if (_lastReloadCheck == 0 || now - _lastReloadCheck > 15)
        {
            _lastReloadCheck = now;
            ReloadData();
        }

        for (int i = 0; i < _subs.Count; i++)
        {
            if (_refreshUntil[i] > 0 && now > _refreshUntil[i])
            {
                _refreshStart[i] = 0;
                _refreshUntil[i] = 0;
            }
        }

        if (Native.CursorPosition() is { } px)
        {
            double scale = DpiScale;
            Point diu = new(px.X / scale, px.Y / scale);
            HandlePointer(diu, Native.LeftButtonDown, now);
        }

        AnimatePeek();

        // 显示期间每秒保活置顶一次(防止被后来的置顶窗口压住)
        if (_peekVisible && (_tickCount++ % 60 == 0))
            Native.BringToTopmost(this);

        InvalidateVisual();
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
                MoveOrSnap(new Point(diu.X - _dragOffset.X, diu.Y - _dragOffset.Y));
        }
        else if (_downArmed)
        {
            if (!_dragging && _downRing is { } ri)
            {
                ReloadData();
                _refreshStart[ri] = now;
                _refreshUntil[ri] = now + 0.9;
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
        }

        bool overContent = overBerth || ring != null || overCard || _dragging || leftDown;
        UpdatePeek(diu, overContent, leftDown);

        // rail 滑入/拖动中位置在变:每帧把卡窗贴回 rail 旁
        if (_card.IsVisible && _hoverRing is { }) PositionCard();
    }

    private void UpdatePeek(Point diu, bool overContent, bool leftDown)
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
            if (overContent || inHot) _hideSeconds = 0;
            else
            {
                _hideSeconds += 0.016;
                if (_hideSeconds > HideDelaySeconds) SetPeekVisible(false, wa);
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
        if (show) _hideSeconds = 0;
        else { HideCard(); _hoverRing = null; }

        // 滑入/滑出后把窗口提到置顶最上,防止被其他置顶窗口压住
        Native.BringToTopmost(this);
    }

    private void AnimatePeek()
    {
        if (!AutoHideEnabled || !_docked || _dragging || double.IsNaN(_targetTop)) return;
        if (Math.Abs(Left - _targetLeft) > 0.5) Left += (_targetLeft - Left) * PeekStep;
        if (Math.Abs(Top - _targetTop) > 0.5) Top += (_targetTop - Top) * PeekStep;
    }

    // ————————————————— 拖拽 / 停靠 —————————————————

    private void ToFloating()
    {
        if (!_docked) return;
        _docked = false;
        _dockEdge = DockEdge.Floating;
        HideCard();
        ApplyRailSize();
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
        _hideSeconds = 0;
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

    private void ShowSubCard(int ringIndex)
    {
        if (ringIndex < 0 || ringIndex >= _subs.Count) return;
        if (_card.IsVisible && _cardFor == ringIndex) return; // 已显示,位置交给每帧跟随
        var sub = _subs[ringIndex];
        var (win, body) = CardLayout.SubCard(sub.Pools.Count);
        _cardFor = ringIndex;
        _card.Configure(sub, win, body);
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
        var berth = RailGeometry.Berth(full, _docked ? _dockEdge : DockEdge.Floating, _docked, 1);
        _hitBerth = berth;
        dc.DrawGeometry(SurfaceBrush, HairlinePen, berth);

        for (int s = 0; s < _subs.Count; s++)
            DrawComposite(dc, _subs[s], RingCenter(s), s, now);
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

        if (!vertical) return; // 顶轨信息看卡片

        // 环下:总额度百分比(变色报警),不显示账户名
        bool avail = month is { IsAvailable: true };
        double mf = avail ? Math.Clamp(month!.Fraction, 0, 1) : 0;
        Color mtint = avail ? UsageTint.For(mf, month!.IsSpent) : PanelPalette.Track;
        string pct = avail ? month!.PercentText : "--";
        var pctFt = NewText(pct, Pt.P(13), FontWeights.SemiBold, Frz(mtint));
        double pctTop = c.Y + Pt.P(OuterRingDpt) / 2 + Pt.P(6);
        dc.DrawText(pctFt, new Point(c.X - pctFt.Width / 2, pctTop));
    }

    private void DrawArcLayer(DrawingContext dc, Point c, double midD, double lineW, PoolData? pool,
        int idx, double now, bool vertical, bool refreshing)
    {
        double midR = midD / 2;

        // 轨道(18% 白)恒画,表示该圈存在
        dc.DrawEllipse(null, RailGeometry.RingPen(TrackBrush, lineW), c, midR, midR);

        if (pool is null || !pool.IsAvailable) return;

        bool spent = pool.IsSpent;
        double frac = Math.Clamp(pool.Fraction, 0, 1);
        Color tint = UsageTint.For(frac, spent);

        // hover:给最外层月弧画光晕
        if (_hoverRing == idx && frac > 0.004 && pool.PoolKind == "Monthly")
        {
            var glow = Frz(Color.FromArgb((byte)(0.35 * 255), tint.R, tint.G, tint.B));
            dc.DrawGeometry(null, RailGeometry.RingPen(glow, lineW + Pt.P(8)),
                RailGeometry.Arc(c, midR, -90, frac * 360));
        }

        // 用量弧(刷新时变暗;满额整圈)
        double sweep = spent ? 360 : frac * 360;
        if (sweep > 0.5)
        {
            double op = refreshing ? 0.3 : 1;
            var arc = Frz(Color.FromArgb((byte)(op * 255 * 0.98), tint.R, tint.G, tint.B));
            dc.DrawGeometry(null, RailGeometry.RingPen(arc, lineW),
                RailGeometry.Arc(c, midR, -90, sweep));
        }

        // 刷新亮段(画在最外圈)
        if (refreshing && pool.PoolKind == "Monthly")
        {
            double phase = ((now - _refreshStart[idx]) % Dock.RefreshPeriodS) / Dock.RefreshPeriodS;
            if (phase < 0) phase = 0;
            dc.DrawGeometry(null, RailGeometry.RingPen(Frz(tint), lineW),
                RailGeometry.Arc(c, midR, -90 + phase * 360, Dock.RefreshSweep * 360));
        }
    }

    private FormattedText NewText(string s, double fontSize, FontWeight weight, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            fontSize, brush, DpiScale);
}
