using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PulseWin;

/// <summary>
/// 「数据源」页里的一张凭据卡:**按 <see cref="SourceDescriptor"/> 生成**,不再是三张
/// 手写 XAML(那样加一个源要复制一整块,而且每张卡里都藏着一套命名控件要同步改代码)。
///
/// 卡内固定结构:显示勾选 + 名称 + 凭据来源/状态 + Key 输入与保存 + 提示。
/// 余额型源(见 <see cref="SourceDescriptor.IsBalance"/>)额外接一段"环的基准"区块——
/// 它不是凭据的一部分,但只有余额型源需要它。
/// </summary>
public sealed class SourceCard : Border
{
    private readonly SourceDescriptor _source;
    private readonly UsageEngine _engine;

    private readonly CheckBox _showBox;
    private readonly TextBlock _sourceText;
    private readonly TextBlock _statusText;
    private readonly TextBox _keyBox;
    private readonly Button _saveButton;
    private readonly TextBlock _hintText;
    private readonly ComboBox? _basisCombo;
    private readonly TextBlock? _basisHint;
    private readonly Grid? _budgetRow;
    private readonly TextBox? _budgetBox;

    private bool _loading;

    /// <summary>勾选变化(窗口据此触发引擎刷新与 rail 重载)。</summary>
    public event Action? VisibilityToggled;

    /// <summary>余额基准变化(窗口据此触发重算)。</summary>
    public event Action? BasisChanged;

    public string Key => _source.Key;

    public SourceCard(SourceDescriptor source, UsageEngine engine)
    {
        _source = source;
        _engine = engine;

        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE6));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Margin = new Thickness(0, 0, 0, 14);

        var stack = new StackPanel();

        // 头部:名称 + 右侧"显示"勾选
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _showBox = new CheckBox { Content = "显示", HorizontalAlignment = HorizontalAlignment.Right };
        _showBox.SetResourceReference(FrameworkElement.StyleProperty, "LightCheckBox");
        DockPanel.SetDock(_showBox, System.Windows.Controls.Dock.Right);
        _showBox.Checked += (_, _) => Toggle();
        _showBox.Unchecked += (_, _) => Toggle();
        header.Children.Add(_showBox);

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = source.DisplayName, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
        });
        _sourceText = new TextBlock
        {
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x8B)),
            Margin = new Thickness(0, 2, 0, 0),
        };
        title.Children.Add(_sourceText);
        header.Children.Add(title);
        stack.Children.Add(header);

        _statusText = new TextBlock
        {
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x8B)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        stack.Children.Add(_statusText);

        // Key 输入 + 保存
        var keyRow = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyBox = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
        _keyBox.SetResourceReference(FrameworkElement.StyleProperty, "LightTextBox");
        keyRow.Children.Add(_keyBox);
        _saveButton = new Button
        {
            Content = "保存", Width = 60, Height = 30, Margin = new Thickness(8, 0, 0, 0),
        };
        _saveButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        Grid.SetColumn(_saveButton, 1);
        _saveButton.Click += async (_, _) => await SaveKeyAsync();
        keyRow.Children.Add(_saveButton);
        stack.Children.Add(keyRow);

        _hintText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x8B)),
            Margin = new Thickness(0, 7, 0, source.IsBalance ? 0 : 10),
        };
        stack.Children.Add(_hintText);

        // 余额型:环的基准(百分比的分母从哪来)
        if (source.IsBalance)
        {
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
                Height = 1, Margin = new Thickness(-16, 10, -16, 10),
            });

            var basisRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            basisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            basisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var basisLabels = new StackPanel();
            basisLabels.Children.Add(new TextBlock
            {
                Text = "环的基准", FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            });
            _basisHint = new TextBlock
            {
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x8B)),
                Margin = new Thickness(0, 2, 0, 0),
            };
            basisLabels.Children.Add(_basisHint);
            basisRow.Children.Add(basisLabels);

            _basisCombo = new ComboBox
            {
                Width = 150, Height = 30, VerticalAlignment = VerticalAlignment.Center,
            };
            _basisCombo.SetResourceReference(FrameworkElement.StyleProperty, "LightCombo");
            _basisCombo.Items.Add("自上次充值");
            _basisCombo.Items.Add("只看余额");
            _basisCombo.Items.Add("我的预算");
            _basisCombo.SelectionChanged += (_, _) => BasisSelectionChanged();
            Grid.SetColumn(_basisCombo, 1);
            basisRow.Children.Add(_basisCombo);
            stack.Children.Add(basisRow);

            _budgetRow = new Grid { Margin = new Thickness(0, 10, 0, 10), Visibility = Visibility.Collapsed };
            _budgetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _budgetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var budgetLabels = new StackPanel();
            budgetLabels.Children.Add(new TextBlock
            {
                Text = "预算金额", FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            });
            budgetLabels.Children.Add(new TextBlock
            {
                Text = "你认为的“满箱”是多少", FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0x86, 0x8B)),
                Margin = new Thickness(0, 2, 0, 0),
            });
            _budgetRow.Children.Add(budgetLabels);
            _budgetBox = new TextBox
            {
                Width = 150, Height = 30, VerticalContentAlignment = VerticalAlignment.Center,
            };
            _budgetBox.SetResourceReference(FrameworkElement.StyleProperty, "LightTextBox");
            _budgetBox.LostFocus += (_, _) => SaveBudget();
            Grid.SetColumn(_budgetBox, 1);
            _budgetRow.Children.Add(_budgetBox);
            stack.Children.Add(_budgetRow);
        }

        Child = new Border { Padding = new Thickness(16, 4, 16, 4), Child = stack };
        LoadFromSettings();
    }

    /// <summary>从设置回填(界面初始化用;程序化赋值不该触发保存)。</summary>
    public void LoadFromSettings()
    {
        _loading = true;
        var s = AppSettings.Current;
        _showBox.IsChecked = s.IsSourceVisible(_source.Key);
        if (_source.IsBalance)
        {
            _basisCombo!.SelectedIndex = s.DeepSeekBasis switch
            {
                BalanceBasis.BalanceOnly => 1,
                BalanceBasis.Budget => 2,
                _ => 0,
            };
            _budgetBox!.Text = s.DeepSeekBudget?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
            UpdateBasisHint();
        }
        _loading = false;
    }

    /// <summary>刷状态行(窗口的每秒计时器调用)。</summary>
    public void RefreshStatus(SourceStatus status)
    {
        _sourceText.Text = string.IsNullOrEmpty(status.CredentialSource)
            ? "" : $"凭据来源:{status.CredentialSource}";
        _statusText.Text = status.StatusText;
        _hintText.Text = status.State switch
        {
            SourceState.Connected => "",
            SourceState.AuthenticationRequired => _source.CredentialHelp,
            _ => "正在使用最后一次成功数据;恢复后自动更新。",
        };
    }

    private void Toggle()
    {
        if (_loading) return;
        VisibilityToggled?.Invoke();
    }

    /// <summary>勾选状态(窗口读完写设置用)。</summary>
    public bool IsChecked => _showBox.IsChecked == true;

    /// <summary>勾选被程序改回时抑制事件。</summary>
    public void SetCheckedQuiet(bool value)
    {
        _loading = true;
        _showBox.IsChecked = value;
        _loading = false;
    }

    /// <summary>把当前 Key 输入框内容发给引擎验证并保存。</summary>
    public async Task<bool> SaveKeyAsync()
    {
        var key = _keyBox.Text?.Trim();
        if (string.IsNullOrEmpty(key)) return false;
        _hintText.Text = "正在验证 Key…";
        bool ok = await _engine.SaveManualKeyAsync(_source.Id, key);
        _hintText.Text = ok ? "已保存并连接成功。" : "验证失败:Key 无效或网络异常。";
        if (ok) _keyBox.Clear();
        return ok;
    }

    private void BasisSelectionChanged()
    {
        if (_loading) return;
        AppSettings.Current.DeepSeekBasis = _basisCombo!.SelectedIndex switch
        {
            1 => BalanceBasis.BalanceOnly,
            2 => BalanceBasis.Budget,
            _ => BalanceBasis.SinceTopUp,
        };
        UpdateBasisHint();
        BasisChanged?.Invoke();
    }

    private void UpdateBasisHint()
    {
        if (_basisHint is null || _budgetRow is null || _basisCombo is null) return;
        var basis = AppSettings.Current.DeepSeekBasis;
        _budgetRow.Visibility = basis == BalanceBasis.Budget ? Visibility.Visible : Visibility.Collapsed;
        _basisHint.Text = basis switch
        {
            BalanceBasis.SinceTopUp =>
                "以本程序观察到的最高余额为满分:余额上涨只可能是充值,所以一涨就重置回满。"
                + "首次运行没有历史峰值,环会从 0% 开始,直到真的花了钱。",
            BalanceBasis.BalanceOnly =>
                "不画百分比,环上直接显示余额金额(短写法,精确值在悬停卡里)。",
            _ => "以你填的金额为满分:(预算 − 余额) / 预算。留空或填 0 则退回只看余额。",
        };
    }

    private void SaveBudget()
    {
        if (_loading || _budgetBox is null) return;
        string text = _budgetBox.Text?.Trim() ?? "";
        double? value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && parsed > 0 ? parsed : null;
        AppSettings.Current.DeepSeekBudget = value;
        _budgetBox.Text = value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        BasisChanged?.Invoke();
    }
}
