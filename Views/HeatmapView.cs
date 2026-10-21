using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 活动热图（v1.22.3，P2-15）：由 420 个 Rectangle 改自绘。
///
/// 原来每格一个 Rectangle + 一个 ToolTip 字符串 + 一个 lambda，每次 RenderAll
/// 重建 420 个可视元素；现在只挂一个 OnRender，悬停/点击按坐标反算格子。
/// 观感与行为不变：铺满可用宽、本周周一为最后一列、点格下钻、淡框表示空周。
/// </summary>
internal sealed class HeatmapView : FrameworkElement
{
    private const double Cell = 12;
    private const double Gap = 3;
    private const double Step = Cell + Gap;
    private const int MaxColumns = 60;

    private static readonly Typeface LabelFace =
        new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

    // 色阶跟主题走:深色下浅绿格子会刺眼,UiTheme 里另配了一套暗底绿
    private static Brush EmptyFill => UiTheme.B(UiTheme.HeatEmpty);
    private static Brush L1 => UiTheme.B(UiTheme.HeatL1);
    private static Brush L2 => UiTheme.B(UiTheme.HeatL2);
    private static Brush L3 => UiTheme.B(UiTheme.HeatL3);
    private static Brush L4 => UiTheme.B(UiTheme.HeatL4);
    private static Brush LabelBrush => UiTheme.B(UiTheme.TextSub);
    private static readonly Pen HoverPen;

    static HeatmapView()
    {
        var pen = new Pen(Frozen(Color.FromRgb(0x1D, 0x1D, 0x1F)), 1.4);
        pen.Freeze();
        HoverPen = pen;
    }

    private Dictionary<DateOnly, (long Tokens, double Cost)> _byDay = new();
    private DateOnly _firstMonday = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private int _weekColumns = 33;
    private long _max;
    private int _hoverCol = -1;
    private int _hoverRow = -1;

    /// <summary>点格子：参数是那一天。</summary>
    public event Action<DateOnly>? DayClicked;

    public void SetData(
        Dictionary<DateOnly, (long Tokens, double Cost)> byDay,
        DateOnly firstMonday,
        DateOnly today,
        int weekColumns,
        long max)
    {
        _byDay = byDay;
        _firstMonday = firstMonday;
        _today = today;
        _weekColumns = Math.Clamp(weekColumns, 1, MaxColumns);
        _max = max;
        _hoverCol = _hoverRow = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // 透明底：让整块区域接住鼠标（否则只有格子本身可点）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        for (int col = 0; col < _weekColumns; col++)
        {
            for (int row = 0; row < 7; row++)
            {
                DateOnly day = _firstMonday.AddDays(col * 7 + row);
                if (day > _today) break;
                long tokens = _byDay.TryGetValue(day, out var v) ? v.Tokens : 0;
                var rect = new Rect(col * Step, row * Step, Cell, Cell);
                dc.DrawRoundedRectangle(BrushFor(tokens, _max), null, rect, 2.5, 2.5);
                if (col == _hoverCol && row == _hoverRow)
                    dc.DrawRoundedRectangle(null, HoverPen, rect, 2.5, 2.5);
            }
        }

        // 列首恰逢月初（或第一列）时标月份
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int col = 0; col < _weekColumns; col++)
        {
            DateOnly colFirst = _firstMonday.AddDays(col * 7);
            if (col > 0 && colFirst.Month == _firstMonday.AddDays((col - 1) * 7).Month) continue;
            var text = new FormattedText(
                $"{colFirst.Month}月", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelFace, 10.5, LabelBrush, dpi);
            dc.DrawText(text, new Point(col * Step, 7 * Step - 2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        int col = (int)(p.X / Step);
        int row = (int)(p.Y / Step);
        bool inRange = col >= 0 && col < _weekColumns && row >= 0 && row < 7
                       && (p.X % Step) < Cell && (p.Y % Step) < Cell;
        if (!inRange)
        {
            if (SetHover(-1, -1)) return;
            Cursor = Cursors.Arrow;
            return;
        }

        DateOnly day = _firstMonday.AddDays(col * 7 + row);
        if (day > _today)
        {
            SetHover(-1, -1);
            return;
        }

        if (SetHover(col, row))
        {
            if (_byDay.TryGetValue(day, out var v) && v.Tokens > 0)
            {
                ToolTip = $"{day:yyyy-MM-dd}\n{SpendFormat.TokensExact(v.Tokens)} tokens\n"
                          + $"{SpendFormat.MoneyExact(v.Cost)}\n(点击看这一天)";
                Cursor = Cursors.Hand;
            }
            else
            {
                ToolTip = $"{day:yyyy-MM-dd}\n没有记录";
                Cursor = Cursors.Arrow;
            }
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1, -1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point p = e.GetPosition(this);
        int col = (int)(p.X / Step);
        int row = (int)(p.Y / Step);
        if (col < 0 || col >= _weekColumns || row < 0 || row >= 7) return;
        DateOnly day = _firstMonday.AddDays(col * 7 + row);
        if (day > _today) return;
        e.Handled = true;
        DayClicked?.Invoke(day);
    }

    private bool SetHover(int col, int row)
    {
        if (col == _hoverCol && row == _hoverRow) return false;
        _hoverCol = col;
        _hoverRow = row;
        InvalidateVisual();
        return true;
    }

    private static Brush BrushFor(long tokens, long max)
    {
        if (max <= 0 || tokens <= 0) return EmptyFill;
        return (tokens * 4d / max) switch
        {
            < 1 => L1,
            < 2 => L2,
            < 3 => L3,
            _ => L4,
        };
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
