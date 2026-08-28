using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StockClient.Core.Groups;

namespace StockClient.App.Views;

/// <summary>
/// The 简洁面板 page of the settings window — row count, opacity, and each
/// field's order, visibility and colour. Edits buffer against the selected
/// template; 「保存」 applies them to the panel and persists.
/// </summary>
public partial class StealthSettingsView : UserControl
{
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

    public StealthSettingsView(GroupConfig root, Action save, Action onChanged)
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
        GapPreview.Opacity = _editing.Shade / 10.0;

        ShadeSlider.ValueChanged += (_, e) =>
        {
            if (_seeding) return;
            var v = (int)e.NewValue;
            ShadeValue.Text = v.ToString();
            _editing.Shade = v;
            GapPreview.Opacity = v / 10.0;   // the sample previews 透明度 live, like 行距
            Apply();
        };

        _seeding = true;
        FontSlider.Value = _editing.FontSize;
        FontValue.Text = _editing.FontSize.ToString();
        HeaderBox.IsChecked = _editing.ShowHeader;
        _seeding = false;
        RebuildHeaderPicker();
        UpdateSampleFont();

        FontSlider.ValueChanged += (_, e) =>
        {
            if (_seeding) return;
            var v = (int)e.NewValue;
            FontValue.Text = v.ToString();
            _editing.FontSize = v;
            UpdateSampleFont();   // the sample previews the size live, like 行距
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

        UpdateFieldsSummary();
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
        GapPreview.Opacity = _editing.Shade / 10.0;
        FontSlider.Value = _editing.FontSize;
        FontValue.Text = _editing.FontSize.ToString();
        HeaderBox.IsChecked = _editing.ShowHeader;
        ChartNone.IsChecked = _editing.Chart == PanelChart.None;
        ChartTrend.IsChecked = _editing.Chart == PanelChart.Trend;
        ChartDepth.IsChecked = _editing.Chart == PanelChart.Depth;
        UpdateGapPreview();
        _seeding = false;

        RebuildHeaderPicker();
        UpdateSampleFont();
        UpdateFieldsSummary();
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
            Owner = Window.GetWindow(this),
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

    private void OpenFields_Click(object sender, RoutedEventArgs e)
    {
        var window = new StealthFieldsWindow(_editing, Apply)
            { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        UpdateFieldsSummary();
    }

    private void Header_Changed(object sender, RoutedEventArgs e)
    {
        if (_seeding) return;
        _editing.ShowHeader = HeaderBox.IsChecked == true;
        Apply();
    }

    /// <summary>The picker's seed hex is a ctor arg, so a template switch (new
    /// draft) rebuilds the control rather than mutating it.</summary>
    private void RebuildHeaderPicker() =>
        HeaderColorHost.Content = new ColorPickerButton(
            _editing.HeaderColor,
            hex => { _editing.HeaderColor = hex; Apply(); },
            "列名整体颜色", 100);

    private void UpdateSampleFont()
    {
        foreach (var child in GapPreview.Children)
            if (child is StackPanel row)
                foreach (var cell in row.Children)
                    if (cell is TextBlock text)
                        text.FontSize = _editing.FontSize;
    }

    private void UpdateFieldsSummary() => FieldsSummary.Text =
        $"已显示 {_editing.Fields.Count(f => f.Visible)} / {_editing.Fields.Count} 个字段";

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
