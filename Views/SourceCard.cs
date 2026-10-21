using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // OpenCode Go 专属:账号(工作区)区块。一个工作区一把 Key、一个额度池。
    private StackPanel? _accountRows;
    private TextBox? _newKeyBox;
    private TextBox? _newLabelBox;
    private TextBlock? _accountHint;
    private readonly Dictionary<int, TextBlock> _accountStates = new();

    private bool _loading;

    /// <summary>勾选变化(窗口据此触发引擎刷新与 rail 重载)。</summary>
    public event Action? VisibilityToggled;

    /// <summary>余额基准变化(窗口据此触发重算)。</summary>
    public event Action? BasisChanged;

    /// <summary>账号增删/改名/显示变化(窗口据此重载环顺序与 rail)。</summary>
    public event Action? AccountsChanged;

    public string Key => _source.Key;

    public SourceCard(SourceDescriptor source, UsageEngine engine)
    {
        _source = source;
        _engine = engine;

        Background = UiTheme.B(UiTheme.CardBack);
        BorderBrush = UiTheme.B(UiTheme.CardBorder);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Margin = new Thickness(0, 0, 0, 16);
        // 与设置窗的 GroupCard 同一层观感:白卡压在浅灰页面底上,靠极淡投影分界
        SetResourceReference(EffectProperty, "CardShadow");

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
            Foreground = UiTheme.B(UiTheme.TextMain),
        });
        _sourceText = new TextBlock
        {
            FontSize = 11, Foreground = UiTheme.B(UiTheme.TextSub),
            Margin = new Thickness(0, 2, 0, 0),
        };
        title.Children.Add(_sourceText);
        header.Children.Add(title);
        stack.Children.Add(header);

        _statusText = new TextBlock
        {
            FontSize = 12, Foreground = UiTheme.B(UiTheme.TextSub),
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
        // async void 事件处理器一旦抛异常就是未观察异常:要么崩进程,要么静默停在
        // "正在验证 Key…"(views P2-6)。这里兜底,至少把原因写到卡片与诊断里。
        _saveButton.Click += async (_, _) =>
        {
            try { await SaveKeyAsync(); }
            catch (Exception ex)
            {
                Diagnostics.Note("保存 API Key 失败", ex);
                _hintText.Text = "保存失败:" + ex.Message;
            }
        };
        keyRow.Children.Add(_saveButton);
        stack.Children.Add(keyRow);

        _hintText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Foreground = UiTheme.B(UiTheme.TextSub),
            Margin = new Thickness(0, 7, 0, source.IsBalance ? 0 : 10),
        };
        stack.Children.Add(_hintText);

        // 余额型:环的基准(百分比的分母从哪来)
        if (source.IsBalance)
        {
            stack.Children.Add(new Border
            {
                Background = UiTheme.B(UiTheme.CardBorder),
                Height = 1, Margin = new Thickness(-16, 10, -16, 10),
            });

            var basisRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            basisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            basisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var basisLabels = new StackPanel();
            basisLabels.Children.Add(new TextBlock
            {
                Text = "环的基准", FontSize = 13,
                Foreground = UiTheme.B(UiTheme.TextMain),
            });
            _basisHint = new TextBlock
            {
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = UiTheme.B(UiTheme.TextSub),
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
                Foreground = UiTheme.B(UiTheme.TextMain),
                VerticalAlignment = VerticalAlignment.Center,
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

        // OpenCode Go 专属:账号(工作区)区块。主账号沿用自动读取链路,
        // 附加账号在这里添加——**未订阅的工作区也允许添加**(403 不拦),
        // 先加进来显示灰环,开通订阅后自动开始同步。
        if (source.Key == SourceKeys.OpenCode)
        {
            stack.Children.Add(new Border
            {
                Background = UiTheme.B(UiTheme.CardBorder),
                Height = 1, Margin = new Thickness(-16, 10, -16, 10),
            });

            stack.Children.Add(new TextBlock
            {
                Text = "账号(工作区)", FontSize = 13,
                Foreground = UiTheme.B(UiTheme.TextMain),
            });
            stack.Children.Add(new TextBlock
            {
                Text = "每个工作区一把独立 Key;主账号自动读取。", FontSize = 11,
                Foreground = UiTheme.B(UiTheme.TextSub),
                Margin = new Thickness(0, 2, 0, 4),
            });

            _accountRows = new StackPanel();
            stack.Children.Add(_accountRows);

            var addRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _newKeyBox = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            _newKeyBox.SetResourceReference(FrameworkElement.StyleProperty, "LightTextBox");
            addRow.Children.Add(_newKeyBox);
            _newLabelBox = new TextBox
            {
                Width = 92, Height = 30, Margin = new Thickness(6, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _newLabelBox.SetResourceReference(FrameworkElement.StyleProperty, "LightTextBox");
            Grid.SetColumn(_newLabelBox, 1);
            addRow.Children.Add(_newLabelBox);
            var addButton = new Button
            { Content = "添加账号", Width = 78, Height = 30, Margin = new Thickness(6, 0, 0, 0) };
            addButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            Grid.SetColumn(addButton, 2);
            // 同 SaveKeyAsync:async void 里漏出的异常会让人永远停在"正在验证…"
            addButton.Click += async (_, _) =>
            {
                try { await AddAccountAsync(); }
                catch (Exception ex)
                {
                    Diagnostics.Note("添加 OpenCode 账号失败", ex);
                    if (_accountHint is not null) _accountHint.Text = "添加失败:" + ex.Message;
                }
            };
            addRow.Children.Add(addButton);
            stack.Children.Add(addRow);

            _accountHint = new TextBlock
            {
                Text = "未订阅的工作区也能先加进来,环上显示为灰环。",
                TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Foreground = UiTheme.B(UiTheme.TextSub),
                Margin = new Thickness(0, 7, 0, 10),
            };
            stack.Children.Add(_accountHint);

            RebuildAccounts();
        }

        Child = new Border
        {
            Padding = UiTheme.Compact ? new Thickness(12, 3, 12, 3) : new Thickness(16, 4, 16, 4),
            Child = stack,
        };
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

    /// <summary>上次写进状态行的内容。状态按分钟级变化，每秒无差别重写会白白触发布局。</summary>
    private string _lastStatusKey = "";

    /// <summary>刷状态行(窗口的每秒计时器调用)。内容没变就不赋值。</summary>
    public void RefreshStatus(SourceStatus status)
    {
        string key = status.CredentialSource + "\u0001" + status.StatusText + "\u0001" + status.State;
        if (key == _lastStatusKey) return;
        _lastStatusKey = key;
        _sourceText.Text = string.IsNullOrEmpty(status.CredentialSource)
            ? "" : $"凭据来源:{status.CredentialSource}";
        _statusText.Text = status.StatusText;
        _hintText.Text = status.State switch
        {
            SourceState.Connected => "",
            SourceState.AuthenticationRequired => _source.CredentialHelp,
            _ => "正在使用最后一次成功数据;恢复后自动更新。",
        };
        if (_source.Key == SourceKeys.OpenCode) RefreshAccountStatus();
    }

    // ————————————————— 账号(工作区)区块 —————————————————

    /// <summary>重建账号列表(建卡、账号增删后调用)。程序化赋值不触发保存。</summary>
    private void RebuildAccounts()
    {
        if (_accountRows is null) return;
        _loading = true;
        _accountRows.Children.Clear();
        _accountStates.Clear();

        // 主账号行:自动读取,不可删、没有独立勾选(卡头部的「显示」管整个源)
        var mainRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        AddAccountColumns(mainRow);
        mainRow.Children.Add(new TextBlock
        {
            Text = "主账号(自动)", FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiTheme.B(UiTheme.TextMain),
        });
        var mainTail = new TextBlock
        {
            Text = TailText(_engine.OpenCodeMainKeyTail()), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Foreground = UiTheme.B(UiTheme.TextSub),
        };
        Grid.SetColumn(mainTail, 1);
        mainRow.Children.Add(mainTail);
        var mainState = new TextBlock
        {
            FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = UiTheme.B(UiTheme.TextSub),
        };
        Grid.SetColumn(mainState, 2);
        mainRow.Children.Add(mainState);
        _accountRows.Children.Add(mainRow);
        _mainStateText = mainState;

        // 附加账号行:标签可改、Key 尾号、状态、显示勾选、删除
        foreach (var account in AppSettings.Current.AccountsSnapshot())
        {
            string entry = SourceKeys.InstanceKey(SourceKeys.OpenCode, account.Id);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            AddAccountColumns(row);

            var labelBox = new TextBox
            {
                Text = account.Label.Length > 0 ? account.Label : $"账号 {account.Id}",
                FontSize = 12, Height = 26, VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0, 4, 0),
            };
            labelBox.SetResourceReference(FrameworkElement.StyleProperty, "LightTextBox");
            labelBox.LostFocus += (_, _) =>
            {
                string text = labelBox.Text?.Trim() ?? "";
                string original = account.Label.Length > 0 ? account.Label : $"账号 {account.Id}";
                if (text.Length == 0 || text == original) return;
                _engine.RenameOpenCodeAccount(account.Id, text);
                AccountsChanged?.Invoke();
            };
            row.Children.Add(labelBox);

            var tail = new TextBlock
            {
                Text = TailText(_engine.OpenCodeAccountTail(account.Id)), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                Foreground = UiTheme.B(UiTheme.TextSub),
            };
            Grid.SetColumn(tail, 1);
            row.Children.Add(tail);

            var state = new TextBlock
            {
                FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = UiTheme.B(UiTheme.TextSub),
            };
            Grid.SetColumn(state, 2);
            row.Children.Add(state);
            _accountStates[account.Id] = state;

            var visibleBox = new CheckBox
            {
                Content = "显示", IsChecked = AppSettings.Current.IsSourceVisible(entry),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            visibleBox.SetResourceReference(FrameworkElement.StyleProperty, "LightCheckBox");
            visibleBox.Checked += (_, _) => SetAccountVisible(account.Id, true);
            visibleBox.Unchecked += (_, _) => SetAccountVisible(account.Id, false);
            Grid.SetColumn(visibleBox, 3);
            row.Children.Add(visibleBox);

            var remove = MiniButton("\uE74D", "删除这个账号(Key 会从本机清除)");
            remove.Click += (_, _) => RemoveAccount(account);
            Grid.SetColumn(remove, 4);
            row.Children.Add(remove);

            _accountRows.Children.Add(row);
        }

        if (_mainStateText is { } m) m.Text = StateText(_engine.StatusForSource(SourceKeys.OpenCode));
        RefreshAccountStatus();
        _loading = false;
    }

    private TextBlock? _mainStateText;

    private static void AddAccountColumns(Grid row)
    {
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });            // 标签
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                // Key 尾号
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });             // 状态
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                // 显示
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                // 删除
    }

    private void RefreshAccountStatus()
    {
        if (_mainStateText is { } main) main.Text = StateText(_engine.StatusForSource(SourceKeys.OpenCode));
        foreach (var (id, text) in _accountStates)
            text.Text = StateText(_engine.StatusForSource(SourceKeys.InstanceKey(SourceKeys.OpenCode, id)));
    }

    private static string TailText(string tail) => tail.Length > 0 ? $"····{tail}" : "";

    private static string StateText(SourceStatus status) => status.State switch
    {
        SourceState.Connected => "已连接",
        SourceState.AuthenticationRequired => "未订阅/Key 无效",
        SourceState.Stale => "数据过期",
        SourceState.Error => "出错",
        _ => "读取中",
    };

    private void SetAccountVisible(int id, bool visible)
    {
        if (_loading) return;
        _engine.SetOpenCodeAccountVisible(id, visible);
        AccountsChanged?.Invoke();
    }

    private async Task AddAccountAsync()
    {
        if (_newKeyBox is null || _accountHint is null) return;
        string key = _newKeyBox.Text?.Trim() ?? "";
        string label = _newLabelBox?.Text?.Trim() ?? "";
        if (key.Length == 0)
        {
            _accountHint.Text = "请先粘贴那个工作区的 API Key。";
            return;
        }

        _accountHint.Text = "正在验证 Key…";
        var result = await _engine.AddOpenCodeAccountAsync(key, label);
        _accountHint.Text = result.Message;
        if (result.Ok)
        {
            _newKeyBox.Clear();
            if (_newLabelBox is { } box) box.Clear();
            RebuildAccounts();
            AccountsChanged?.Invoke();
        }
    }

    private void RemoveAccount(OpenCodeAccountInfo account)
    {
        string label = account.Label.Length > 0 ? account.Label : $"账号 {account.Id}";
        var owner = Window.GetWindow(this);
        if (owner is not null && MessageBox.Show(
                owner, $"删除账号「{label}」?它的 Key 会从本机清除,悬浮环上的那个环也会消失。",
                "PulseWin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _engine.RemoveOpenCodeAccount(account.Id);
        RebuildAccounts();
        AccountsChanged?.Invoke();
    }

    /// <summary>行尾的小图标按钮(轻量扁平样式,默认 Button 模板太重)。</summary>
    private Button MiniButton(string glyph, string toolTip)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
            },
            Width = 24, Height = 24, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = toolTip, Cursor = Cursors.Hand,
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, UiTheme.B(UiTheme.SegBack));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, UiTheme.B(UiTheme.SegHover), "bd"));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
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
            BalanceBasis.SinceTopUp => "以观测到的最高余额为满分,余额一涨即视为充值并重置。",
            BalanceBasis.BalanceOnly => "不画百分比,环上直接显示余额金额。",
            _ => "(预算 − 余额) / 预算,留空或填 0 则退回只看余额。",
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
