using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 环色选择器:一个色块按钮,点开是预设色板 + "自动"档 + 自定义 HEX 输入。
/// Value 为 null 表示"自动"(按用量绿→红着色)——色块此时显示一条绿→红渐变,
/// 让这个默认含义(离上限还有多远)在界面上看得见。
/// </summary>
public partial class TintPicker : UserControl
{
    /// <summary>
    /// 预设色板:rail 是暗色玻璃底,只有亮色系在环上看得清。首位放 DeepSeek
    /// 品牌蓝,其余按色相环排一整圈,方便按观感挑。
    /// </summary>
    private static readonly string[] Presets =
    {
        "#4D6BFE", "#38BDF8", "#22D3EE", "#2DD4BF", "#34D399",
        "#A3E635", "#FBBF24", "#FB923C", "#F87171", "#FB7185",
        "#F472B6", "#E879F9", "#C084FC", "#A78BFA", "#818CF8",
        "#93C5FD", "#6EE7B7", "#FCD34D", "#FCA5A5", "#CBD5E1",
    };

    private static readonly Brush AutoGradient = CreateAutoGradient();

    private static Brush CreateAutoGradient()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xEA, 0xB3, 0x08), 0.5));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 1));
        b.Freeze();
        return b;
    }

    /// <summary>当前值(#RRGGBB);null = 自动着色。</summary>
    public string? Value
    {
        get => _value;
        set
        {
            _value = AppSettings.NormalizeTint(value);
            RefreshSwatch();
        }
    }
    private string? _value;

    /// <summary>用户选了新颜色(或切回自动)。程序化赋 Value 不触发。
    /// 用 EventHandler 而不是 Action:XAML 里要能直接写 ValueChanged="处理器"。</summary>
    public event EventHandler? ValueChanged;

    public TintPicker()
    {
        InitializeComponent();
        foreach (var hex in Presets) Palette.Children.Add(Chip(hex));
        PalettePopup.Closed += (_, _) => SwatchButton.IsChecked = false;
        RefreshSwatch();
    }

    private Button Chip(string hex)
    {
        var chip = new Button
        {
            Style = (Style)Resources["Chip"],
            Background = ChipBrush(hex),
            BorderBrush = Brushes.Transparent,
            Tag = hex,
        };
        chip.Click += (_, _) => Select(hex);
        return chip;
    }

    private static Brush ChipBrush(string hex)
    {
        var color = ParseHex(hex);
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Color ParseHex(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Transparent; }
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        PalettePopup.IsOpen = !PalettePopup.IsOpen;
        SwatchButton.IsChecked = PalettePopup.IsOpen;
        if (PalettePopup.IsOpen)
        {
            RefreshSelection();
            HexBox.Text = _value ?? "";
        }
    }

    private void Select(string? hex)
    {
        _value = hex;
        RefreshSwatch();
        PalettePopup.IsOpen = false;
        SwatchButton.IsChecked = false;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSwatch()
    {
        if (_value is { } hex)
        {
            SwatchFill.Background = ChipBrush(hex);
            SwatchText.Text = hex;
        }
        else
        {
            SwatchFill.Background = AutoGradient;
            SwatchText.Text = "自动 · 按用量";
        }
    }

    /// <summary>给当前值对应的色块画选中描边。</summary>
    private void RefreshSelection()
    {
        foreach (Button chip in Palette.Children.OfType<Button>())
            chip.BorderBrush = (string)chip.Tag == _value ? Brushes.White : Brushes.Transparent;
    }

    private void Auto_Click(object sender, RoutedEventArgs e) => Select(null);

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyHexText(); e.Handled = true; }
        else if (e.Key == Key.Escape) { PalettePopup.IsOpen = false; e.Handled = true; }
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e) => ApplyHexText();

    private void HexApply_Click(object sender, RoutedEventArgs e) => ApplyHexText();

    /// <summary>
    /// HEX 手输。写法不合法的回弹成当前值——悄悄丢弃比报错温和,而且色块上
    /// 始终显示真实生效的值,不会"看起来存进去了其实没存"。
    /// </summary>
    private void ApplyHexText()
    {
        if (AppSettings.NormalizeTint(HexBox.Text) is not { } hex)
        {
            HexBox.Text = _value ?? "";
            return;
        }
        Select(hex);
    }
}
