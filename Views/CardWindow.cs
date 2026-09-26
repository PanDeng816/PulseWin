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

    /// <summary>只移动位置不重绘(rail 滑入/拖动时让卡片跟着走,避免与 rail 重叠)。</summary>
    public void MoveTo(Point topLeft)
    {
        Left = topLeft.X;
        Top = topLeft.Y;
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
        _ticker.Stop();
        Hide();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_sub is not { } sub) return;
        CardRenderer.Draw(dc, sub, _body, _showStale, Native.Scale(this), DateTimeOffset.UtcNow);
    }
}
