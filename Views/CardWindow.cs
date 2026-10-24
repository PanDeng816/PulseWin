using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PulseWin;

/// <summary>详情卡窗:黑色玻璃圆角方框。显示一个订阅的 5小时/本周/总额度三池明细。</summary>
public sealed class CardWindow : Window
{
    private SubData? _sub;
    private Rect _body;
    private bool _showStale;
    private ImageBrush? _frosted;
    private readonly DispatcherTimer _ticker;

    public CardWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Native.ApplyToolWindowStyle(this);

        // 每秒重绘一次:剩余时间倒计时实时走动。隐藏时停表,不做无用重绘。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) =>
        {
            if (_sub is not null && IsVisible) InvalidateVisual();
        };
    }

    public void Configure(SubData sub, Size winSize, Rect body, bool showStale)
    {
        _sub = sub;
        _body = body;
        _showStale = showStale;
        Width = winSize.Width; Height = winSize.Height;
        InvalidateVisual();
    }

    /// <summary>
    /// 只移动位置不重绘(rail 滑入/拖动时让卡片跟着走,避免与 rail 重叠)。
    /// 窗口**不可见时**顺手重抓一次磨砂底:这是唯一不会把卡片自己抓进画面的时机,
    /// 调用方(ShowSubCard)也正是"先摆位、再显示"。
    /// </summary>
    public void MoveTo(Point topLeft)
    {
        Left = topLeft.X;
        Top = topLeft.Y;
        if (IsVisible) return;
        _frosted = Backdrop.FrostedFill(new Point(Left, Top), _body, Native.Scale(this));
        InvalidateVisual();
    }

    /// <summary>把窗口显示出来(自绘黑色玻璃圆角卡;v1.7.3 起不再有系统 acrylic 分支)。</summary>
    public void ShowCard()
    {
        if (!IsVisible) Show();
        if (!_ticker.IsEnabled) _ticker.Start();
        Native.BringToTopmost(this); // 卡窗也要压在其他置顶窗口之上
    }

    public void HideCard()
    {
        _sub = null;
        _frosted = null; // 下次显示前按新位置重抓:背景可能已经换了窗口
        _ticker.Stop();
        Hide();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_sub is not { } sub) return;

        // 先铺磨砂底(背后内容糊成色块),再让 CardRenderer 叠 80% 的面色 ——
        // 与 rail「真实背景 + 同一条 80% 面」等价,只是这里把背景换成了模糊版,
        // 否则压在白网页上时背后的字会从 20% 的缝里透上来。
        if (_frosted is not null)
        {
            dc.PushClip(GlassPaint.RoundRect(_body, Card.CornerRadius));
            dc.DrawRectangle(_frosted, null, _body);
            dc.Pop();
        }

        CardRenderer.Draw(dc, sub, _body, _showStale, Native.Scale(this), DateTimeOffset.UtcNow);
    }
}
