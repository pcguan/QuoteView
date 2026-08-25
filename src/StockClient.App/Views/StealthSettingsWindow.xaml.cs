using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StockClient.Core.Groups;

namespace StockClient.App.Views;

/// <summary>
/// One place to configure the stealth panel — row count, opacity, and each
/// field's visibility and colour — replacing the old right-click submenus.
/// Changes apply live (the panel redraws) and persist immediately.
/// </summary>
public partial class StealthSettingsWindow : Window
{
    private static readonly (StealthField Field, string Name)[] FieldNames =
    {
        (StealthField.Code, "合约编码"),
        (StealthField.Name, "合约名称"),
        (StealthField.Price, "最新价"),
        (StealthField.Change, "涨跌额"),
        (StealthField.Percent, "涨跌幅"),
        (StealthField.Open, "今开"),
        (StealthField.High, "最高"),
        (StealthField.Low, "最低"),
        (StealthField.Yesterday, "昨收"),
        (StealthField.Time, "时间"),
        (StealthField.Volume, "成交量"),
        (StealthField.Amount, "成交额"),
        (StealthField.TotalCap, "总市值"),
        (StealthField.FloatCap, "流通市值"),
        (StealthField.TurnoverRate, "换手率"),
        (StealthField.VolumeRatio, "量比"),
        (StealthField.Amplitude, "振幅"),
        (StealthField.AvgPrice, "均价"),
        (StealthField.PeTtm, "市盈TTM"),
        (StealthField.Pb, "市净率"),
        (StealthField.GroupName, "分组名（面板左侧）"),
        (StealthField.PrevDay, "昨日涨幅"),
        (StealthField.Return3, "3日涨幅"),
        (StealthField.Return5, "5日涨幅"),
        (StealthField.Return10, "10日涨幅"),
        (StealthField.Return20, "20日涨幅"),
        (StealthField.Return60, "60日涨幅"),
        (StealthField.ReturnYtd, "年初至今"),
        (StealthField.Speed, "涨速（A股）"),
        (StealthField.MainInflow, "主力净流入（A股）"),
        (StealthField.MainInflowPct, "主力占比（A股）"),
        (StealthField.SuperInflow, "超大单（A股）"),
        (StealthField.BigInflow, "大单（A股）"),
        (StealthField.MidInflow, "中单（A股）"),
        (StealthField.SmallInflow, "小单（A股）"),
        (StealthField.OuterVolume, "外盘"),
        (StealthField.InnerVolume, "内盘"),
        (StealthField.LimitUp, "涨停价"),
        (StealthField.LimitDown, "跌停价"),
        (StealthField.Week52High, "52周最高"),
        (StealthField.Week52Low, "52周最低"),
        (StealthField.DividendYield, "股息率"),
        (StealthField.Industry, "行业"),
        (StealthField.Region, "地区"),
        (StealthField.Concepts, "概念"),
        (StealthField.Note, "备注"),
    };

    private readonly GroupConfig _root;
    private readonly StealthConfig _live;
    private readonly IList<NamedStealthTemplate> _templates;
    private readonly Action _save;
    private readonly Action _onChanged;

    /// <summary>
    /// The WORKING COPY the controls write to — a buffered clone of the
    /// selected template. Nothing reaches the template or the panel until
    /// 「保存」; switching templates discards it.
    /// </summary>
    private StealthConfig _editing;

    /// <summary>The template being used/edited — always set: the panel is
    /// always ON a template.</summary>
    private NamedStealthTemplate _template = null!;

    private bool _seeding;

    public StealthSettingsWindow(GroupConfig root, Action save, Action onChanged)
    {
        InitializeComponent();

        _root = root;
        _live = root.Stealth;
        _templates = root.StealthTemplates;
        _template = _templates.FirstOrDefault(t => t.Name == root.ActiveStealthTemplate)
                    ?? _templates[0];
        _editing = StealthConfigOps.Clone(_template.Stealth);
        _save = save;
        _onChanged = onChanged;
        FillTemplates(select: _template.Name);

        // Set initial values BEFORE wiring events so seeding doesn't fire Apply.
        RowsSlider.Value = _editing.Rows;
        RowsValue.Text = _editing.Rows.ToString();
        RowGapSlider.Value = _editing.RowGap;
        RowGapValue.Text = _editing.RowGap.ToString();
        ShadeSlider.Value = _editing.Shade;
        ShadeValue.Text = _editing.Shade.ToString();

        RowsSlider.ValueChanged += (_, e) =>
        {
            if (_seeding) return;
            var v = (int)e.NewValue;
            RowsValue.Text = v.ToString();
            _editing.Rows = v;
            Apply();
        };

        RowGapSlider.ValueChanged += (_, e) =>
        {
            if (_seeding) return;
            var v = (int)e.NewValue;
            RowGapValue.Text = v.ToString();
            _editing.RowGap = v;
            UpdateGapPreview();
            Apply();
        };

        BuildGapPreview();

        ShadeSlider.ValueChanged += (_, e) =>
        {
            if (_seeding) return;
            var v = (int)e.NewValue;
            ShadeValue.Text = v.ToString();
            _editing.Shade = v;
            Apply();
        };

        // Same three states the Win+Alt+Delete cycle walks; here they're pickable
        // directly, and ShowTrend is kept in step so an older build still reads
        // the setting.
        foreach (var (button, kind) in new[]
                 {
                     (ChartNone, PanelChart.None),
                     (ChartTrend, PanelChart.Trend),
                     (ChartDepth, PanelChart.Depth),
                 })
        {
            var captured = kind;
            button.IsChecked = _editing.Chart == kind;
            button.Checked += (_, _) =>
            {
                if (_seeding) return;
                _editing.Chart = captured;
                _editing.ShowTrend = captured == PanelChart.Trend;
                Apply();
            };
        }

        BuildFields();
    }

    private void FillTemplates(string? select)
    {
        _seeding = true;
        TemplateBox.ItemsSource = _templates.Select(t => t.Name).ToList();
        TemplateBox.SelectedItem = select ?? _template.Name;
        _seeding = false;
        TemplateDeleteButton.IsEnabled = _templates.Count > 1;
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_seeding || TemplateBox.SelectedItem is not string name) return;
        var template = _templates.FirstOrDefault(t => t.Name == name);
        if (template is null || ReferenceEquals(template, _template)) return;

        UseTemplate(template);
    }

    /// <summary>
    /// Switches the panel to a template: its SAVED state applies immediately
    /// (selection means USE — the panel is always on some template), and the
    /// editor reloads with a fresh buffered copy. Unsaved edits to the previous
    /// template are discarded, same as closing without 保存.
    /// </summary>
    private void UseTemplate(NamedStealthTemplate template)
    {
        _template = template;
        _root.ActiveStealthTemplate = template.Name;
        StealthConfigOps.CopyInto(_live, template.Stealth);
        _save();
        _onChanged();

        _editing = StealthConfigOps.Clone(template.Stealth);
        TemplateDeleteButton.IsEnabled = _templates.Count > 1;
        Reseed();
    }

    /// <summary>Re-points every control at _editing without firing Apply.</summary>
    private void Reseed()
    {
        _seeding = true;
        RowsSlider.Value = _editing.Rows;
        RowsValue.Text = _editing.Rows.ToString();
        RowGapSlider.Value = _editing.RowGap;
        RowGapValue.Text = _editing.RowGap.ToString();
        ShadeSlider.Value = _editing.Shade;
        ShadeValue.Text = _editing.Shade.ToString();
        ChartNone.IsChecked = _editing.Chart == PanelChart.None;
        ChartTrend.IsChecked = _editing.Chart == PanelChart.Trend;
        ChartDepth.IsChecked = _editing.Chart == PanelChart.Depth;
        UpdateGapPreview();
        _seeding = false;

        FieldsPanel.Children.Clear();
        BuildFields();
    }

    private void TemplateNew_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptName($"模板{_templates.Count + 1}");
        if (name is not { Length: > 0 }) return;
        if (_templates.Any(t => t.Name == name)) name += "(2)";

        // Fresh templates start from the DEFAULTS (white fields, red-up
        // green-down). Creating one switches to it, per "选中即使用" — the
        // panel shows the default look until edits are 保存'd into it.
        var template = new NamedStealthTemplate
        {
            Name = name,
            Stealth = StealthConfig.CreateDefault(),
        };
        _templates.Add(template);
        FillTemplates(select: name);
        UseTemplate(template);
    }

    private void TemplateRename_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptName(_template.Name);
        if (name is not { Length: > 0 } || name == _template.Name) return;
        if (_templates.Any(t => !ReferenceEquals(t, _template) && t.Name == name)) name += "(2)";

        // Rename only — buffered edits stay buffered, so renaming never
        // silently applies or discards half-finished changes. Hence the manual
        // dropdown refresh instead of FillTemplates: SelectTarget would re-clone
        // the template and throw the draft away.
        _template.Name = name;
        _save();

        _seeding = true;
        TemplateBox.ItemsSource = _templates.Select(t => t.Name).ToList();
        TemplateBox.SelectedItem = name;
        _seeding = false;

        _root.ActiveStealthTemplate = _template.Name;
        _save();
    }

    private void TemplateSave_Click(object sender, RoutedEventArgs e)
    {
        // Persist the buffered edits into the template, then refresh the panel
        // (this template IS the active one) — 保存 is when edits take effect.
        _template.Stealth = StealthConfigOps.Clone(_editing);
        StealthConfigOps.CopyInto(_live, _editing);
        _save();
        _onChanged();
    }

    private void TemplateDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_templates.Count <= 1) return;   // the panel must stay on something

        _templates.Remove(_template);
        FillTemplates(select: _templates[0].Name);
        UseTemplate(_templates[0]);
    }

    /// <summary>Tiny name prompt, same dark styling as this window.</summary>
    private string? PromptName(string suggestion)
    {
        var box = new TextBox { Text = suggestion, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "保存", Width = 76, IsDefault = true };
        var cancel = new Button
        {
            Content = "取消", Width = 76, Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        body.Children.Add(new TextBlock { Text = "模板名称：", Foreground = Frozen("#8B93A3") });
        body.Children.Add(box);
        body.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "另存为模板",
            Owner = this,
            Width = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Frozen("#12161F"),
            FontFamily = new FontFamily("Microsoft YaHei"),
            Content = body,
        };
        ok.Click += (_, _) => { dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private void BuildFields()
    {
        foreach (var field in _editing.Fields)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());                              // name (stretch)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // colour(s)

            var captured = field;

            var check = new CheckBox
            {
                Content = FieldName(field.Field),
                IsChecked = field.Visible,
                Foreground = Frozen("#EDF1F7"),
                VerticalAlignment = VerticalAlignment.Center,
                // WPF UI's implicit CheckBox style carries a MinWidth wide enough
                // that the star column refuses to give ground, which pushed the
                // colour pickers past the right edge and clipped them.
                MinWidth = 0,
            };
            check.Checked += (_, _) => { captured.Visible = true; Apply(); };
            check.Unchecked += (_, _) =>
            {
                // Keep at least one ROW field visible: an empty line would show
                // nothing. The group name doesn't count — it is drawn beside the
                // rows, not in them, so leaving only it selected would still give
                // blank rows, and turning it off on its own is perfectly fine.
                var isRowField = captured.Field != StealthField.GroupName;
                var rowFieldsLeft = _editing.Fields.Count(
                    f => f.Visible && f.Field != StealthField.GroupName);

                if (isRowField && rowFieldsLeft <= 1)
                {
                    check.IsChecked = true;
                    return;
                }

                captured.Visible = false;
                Apply();
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            // Signed fields (price/change/percent/open/high/low) get a rise colour
            // and a fall colour; the rest get one colour. Setting both the same is
            // how you'd make a signed field single-colour.
            var colours = new StackPanel { Orientation = Orientation.Horizontal };
            if (StealthFields.IsSigned(field.Field))
            {
                colours.Children.Add(Picker(field.PositiveColor, h => captured.PositiveColor = h, "上涨颜色", 78));
                colours.Children.Add(Picker(field.NegativeColor, h => captured.NegativeColor = h, "下跌颜色", 78, leftMargin: 6));
            }
            else
            {
                colours.Children.Add(Picker(field.Color, h => captured.Color = h, "颜色", 100));
            }

            Grid.SetColumn(colours, 1);
            grid.Children.Add(colours);

            FieldsPanel.Children.Add(grid);
        }
    }

    /// <summary>
    /// A full colour picker (HSV field + hue strip + hex box) that writes the
    /// chosen hex back via <paramref name="setColor"/>. It applies while dragging,
    /// so the panel shows the colour before the popup is closed.
    /// </summary>
    private ColorPickerButton Picker(string currentHex, Action<string> setColor, string tooltip, double width, double leftMargin = 0) =>
        new(currentHex, hex => { setColor(hex); Apply(); }, tooltip, width, leftMargin);

    private void Apply()
    {
        // Every edit is buffered against the selected template; nothing reaches
        // the panel until 「保存」. (Kept as a hook for the field controls.)
    }

    /// <summary>Sample rows that mirror the panel's layout, for the gap preview.</summary>
    private static readonly (string Code, string Name, string Pct, string Color)[] GapSample =
    {
        ("600519", "贵州茅台", "+5.95%", "#EF5350"),
        ("000651", "格力电器", "+1.58%", "#EF5350"),
        ("920992", "中科美菱", "-3.76%", "#26A69A"),
    };

    private void BuildGapPreview()
    {
        foreach (var (code, name, pct, color) in GapSample)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Cell(code, "#FFFFFF", "Consolas"));
            row.Children.Add(Cell(name, "#FFFFFF", "Microsoft YaHei", 7));
            row.Children.Add(Cell(pct, color, "Consolas", 7));
            GapPreview.Children.Add(row);
        }

        UpdateGapPreview();
    }

    /// <summary>Applies the current gap to the preview, the same way the panel does.</summary>
    private void UpdateGapPreview()
    {
        var gap = _editing.RowGap;
        for (var i = 0; i < GapPreview.Children.Count; i++)
            if (GapPreview.Children[i] is FrameworkElement fe)
                fe.Margin = new Thickness(0, i == 0 ? 0 : gap, 0, 0);
    }

    private static TextBlock Cell(string text, string hex, string font, double left = 0) => new()
    {
        Text = text,
        FontSize = 12,
        FontFamily = new FontFamily(font),
        Foreground = Frozen(hex),
        Margin = new Thickness(left, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string FieldName(StealthField field) =>
        FieldNames.FirstOrDefault(f => f.Field == field).Name is { Length: > 0 } n ? n : field.ToString();

    private static Brush Frozen(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }
}
