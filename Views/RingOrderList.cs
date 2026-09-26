using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 「圆环顺序」可重排列表:一行一个数据源,拖动行首手柄或用上下箭头调序。
///
/// **为什么不用下拉框**:从前是"3 个源的 6 种排列"逐条写死在代码里——源一多就是
/// 阶乘爆炸(4 个 = 24 项、5 个 = 120 项),下拉框根本放不下,而且加源要同时改三处。
/// 可重排列表把界面项数从 N! 降到 N,加源也不用动这个控件。
///
/// 顺序**直接就是** rail 上从上到下的顺序(见 <see cref="AppSettings.VisibleSources"/>)。
/// </summary>
public sealed class RingOrderList : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly List<string> _order = new();

    /// <summary>用户调序后触发(参数是新顺序,只含已知源键)。</summary>
    public event Action<List<string>>? OrderChanged;

    public RingOrderList()
    {
        Content = _rows;
    }

    /// <summary>当前顺序(含未显示源的兜底:它们排在已显示的后面)。</summary>
    public List<string> Order => new(_order);

    /// <summary>
    /// 按设置重建列表。只列**当前已显示**的源——rail 上不画的源没有位置可言,
    /// "显示哪些"由数据源页的勾选决定(那个页面才是它的归属)。
    /// 顺序即 rail 上从上到下。
    /// </summary>
    public void Load(IReadOnlyList<string> visible)
    {
        _order.Clear();
        foreach (var key in visible)
        {
            if (SourceCatalog.IsKnownKey(key) && !_order.Contains(key)) _order.Add(key);
        }
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        if (_order.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "没有已显示的圆环。在「数据源」页勾选要显示的源。",
                FontSize = 12,
                Foreground = Frozen(Color.FromRgb(0x8E, 0x8E, 0x93)),
                Margin = new Thickness(2, 6, 0, 6),
            });
            return;
        }
        for (int i = 0; i < _order.Count; i++)
            _rows.Children.Add(BuildRow(_order[i], i));
    }

    private FrameworkElement BuildRow(string key, int index)
    {
        var source = SourceCatalog.ByKey(key)!;

        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 手柄
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 箭头

        // 行首手柄:拖动整行。用字符而不是图片,免得多带一份资源。
        var handle = new TextBlock
        {
            Text = "\uE76F",   // Segoe MDL2: GripperBarHorizontal
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Frozen(Color.FromRgb(0xB0, 0xB0, 0xB6)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.SizeAll,
        };
        Grid.SetColumn(handle, 0);
        grid.Children.Add(handle);

        var name = new TextBlock
        {
            Text = source.DisplayName,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Frozen(Color.FromRgb(0x1D, 0x1D, 0x1F)),
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var arrows = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrows.Children.Add(ArrowButton("\uE74A", "上移", index > 0, () => Move(index, index - 1)));
        arrows.Children.Add(ArrowButton("\uE74B", "下移", index < _order.Count - 1, () => Move(index, index + 1)));
        Grid.SetColumn(arrows, 2);
        grid.Children.Add(arrows);

        // 整行可拖(不只是手柄):靶子更大,不容易拖空
        AttachDrag(grid, handle, index);
        return grid;
    }

    private Button ArrowButton(string glyph, string tip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
            },
            Width = 26, Height = 24,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tip,
            IsEnabled = enabled,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
        };
        // 轻量扁平样式:默认 Button 模板在亮色卡里太重
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, Frozen(Color.FromRgb(0xEB, 0xEB, 0xEF)));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromRgb(0xE0, 0xE0, 0xE4)), "bd"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "bd"));
        template.Triggers.Add(disabled);
        button.Template = template;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>把第 from 行移到 to(箭头按钮与拖动落点都走这里)。</summary>
    private void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _order.Count || to >= _order.Count) return;
        string key = _order[from];
        _order.RemoveAt(from);
        _order.Insert(to, key);
        Rebuild();
        OrderChanged?.Invoke(Order);
    }

    // ————————————— 拖动 —————————————
    //
    // **拖动只在松手时落位,拖动过程中不重建可视树**。第一版是"越过中线就交换、
    // 立刻 Rebuild",但重建会把正被鼠标捕获的那一行从树上摘掉,捕获随之丢失,
    // 表现为"拖到一半就断"。松手落位既避开了这个坑,也没牺牲什么——
    // 拖动只有几行,松手即到位。
    private int _dragIndex = -1;

    private void AttachDrag(FrameworkElement row, FrameworkElement handle, int index)
    {
        // 手柄上按下即开始拖;整行也允许,但从箭头按钮上按下时不拖(那是点击)
        handle.MouseLeftButtonDown += (_, e) => BeginDrag(row, index, e);
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsWithinButton(d)) return;
            BeginDrag(row, index, e);
        };
    }

    private void BeginDrag(FrameworkElement row, int index, MouseButtonEventArgs e)
    {
        _dragIndex = index;
        row.CaptureMouse();
        row.Opacity = 0.5;
        row.MouseLeftButtonUp += OnDragEnd;
        e.Handled = true;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement row)
        {
            _dragIndex = -1;
            return;
        }
        row.ReleaseMouseCapture();
        row.MouseLeftButtonUp -= OnDragEnd;
        row.Opacity = 1.0;

        if (_dragIndex >= 0)
        {
            double y = e.GetPosition(_rows).Y;
            double rowH = row.ActualHeight > 0 ? row.ActualHeight : 30;
            int target = Math.Clamp((int)(y / rowH), 0, _order.Count - 1);
            int from = _dragIndex;
            _dragIndex = -1;
            if (target != from) Move(from, target);
        }
    }

    private static bool IsWithinButton(DependencyObject d)
    {
        for (var current = d; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
