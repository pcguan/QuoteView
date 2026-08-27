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

        BuildChips();
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
        ChartNone.IsChecked = _editing.Chart == PanelChart.None;
        ChartTrend.IsChecked = _editing.Chart == PanelChart.Trend;
        ChartDepth.IsChecked = _editing.Chart == PanelChart.Depth;
        UpdateGapPreview();
        _seeding = false;

        BuildChips();
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

    // ---- field chips: 勾选 + 拖拽排序 + 双击调色 --------------------------
    //
    // Mirrors the main grid's 列设置: every field is a flat chip — a click (or
    // its checkbox) toggles visibility, a capture-based drag reorders (full
    // input rate, ghost glides pixel-for-pixel), and a DOUBLE-click opens the
    // colour editor. Chip order IS _editing.Fields order — what the panel
    // renders. All of it stays buffered until 「保存」.

    private readonly ObservableCollection<FieldChip> _chips = new();

    private Point _pressPoint;
    private Point _grabOffset;
    private int _pressIndex = -1;
    private int _dragIndex = -1;
    private bool _armed;
    private bool _draggingChip;
    private GhostAdorner? _ghost;
    private bool _chipsWired;

    private void BuildChips()
    {
        _chips.Clear();
        foreach (var field in _editing.Fields)
            _chips.Add(new FieldChip(field, FieldName(field.Field), CanHide));

        if (_chipsWired) return;
        _chipsWired = true;
        FieldChips.ItemsSource = _chips;
        FieldChips.PreviewMouseLeftButtonDown += Chips_Down;
        FieldChips.PreviewMouseMove += Chips_Move;
        FieldChips.PreviewMouseLeftButtonUp += Chips_Up;
        FieldChips.LostMouseCapture += (_, _) => EndChipDrag();
    }

    /// <summary>At least one ROW field must stay visible — an all-blank line
    /// shows nothing. 分组名 doesn't count: it draws beside the rows.</summary>
    private bool CanHide(StealthFieldConfig field) =>
        field.Field == StealthField.GroupName
        || _editing.Fields.Count(f => f.Visible && f.Field != StealthField.GroupName) > 1;

    private void Chips_Down(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(FieldChips);
        _pressIndex = ChipIndexAt(_pressPoint);

        // Double-click = colour editor. Click #1 of the pair already toggled
        // visibility on its way up — undo it, "edit colours" must not also hide.
        if (e.ClickCount == 2 && _pressIndex >= 0
            && !IsOn<ButtonBase>(e.OriginalSource as DependencyObject))
        {
            var chip = _chips[_pressIndex];
            chip.Visible = !chip.Visible;
            _armed = false;
            e.Handled = true;
            EditColors(chip);
            return;
        }

        // A press on the checkbox glyph is the checkbox's own toggle; arming
        // here would double-toggle on the way back up.
        _armed = _pressIndex >= 0 && !IsOn<ButtonBase>(e.OriginalSource as DependencyObject);
        _draggingChip = false;
        if (_armed && ChipContainer(_pressIndex) is { } c) _grabOffset = e.GetPosition(c);
    }

    private void Chips_Move(object sender, MouseEventArgs e)
    {
        if (!_armed || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(FieldChips);

        if (!_draggingChip)
        {
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _draggingChip = true;
            _dragIndex = _pressIndex;
            ShowGhost(_dragIndex);
            FieldChips.CaptureMouse();
        }

        _ghost?.SetPosition(new Point(p.X - _grabOffset.X, p.Y - _grabOffset.Y));

        var over = ChipIndexAt(p);
        if (over >= 0 && over != _dragIndex)
        {
            _chips.Move(_dragIndex, over);
            _dragIndex = over;
            DimDragged();
        }

        // Edge auto-scroll: the chip grid lives in a capped viewport.
        var vy = e.GetPosition(FieldsScroll).Y;
        if (vy < 28)
            FieldsScroll.ScrollToVerticalOffset(FieldsScroll.VerticalOffset - (28 - vy));
        else if (vy > FieldsScroll.ViewportHeight - 28)
            FieldsScroll.ScrollToVerticalOffset(
                FieldsScroll.VerticalOffset + (vy - (FieldsScroll.ViewportHeight - 28)));
    }

    private void Chips_Up(object sender, MouseButtonEventArgs e)
    {
        if (_draggingChip)
        {
            FieldChips.ReleaseMouseCapture();   // EndChipDrag runs via LostMouseCapture
        }
        else if (_armed && _pressIndex >= 0
                 && ChipIndexAt(e.GetPosition(FieldChips)) == _pressIndex)
        {
            _chips[_pressIndex].Visible = !_chips[_pressIndex].Visible;
        }

        _armed = false;
    }

    private void EndChipDrag()
    {
        if (!_draggingChip) return;

        _draggingChip = false;
        _dragIndex = -1;
        RemoveGhost();
        foreach (var chip in _chips) chip.Dragging = false;
        DimDragged();

        _editing.Fields.Clear();
        foreach (var chip in _chips) _editing.Fields.Add(chip.Field);
        Apply();
    }

    private void ShowGhost(int index)
    {
        _chips[index].Dragging = true;
        DimDragged();

        if (ChipContainer(index) is not { } c || c.ActualWidth < 1) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(c.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(c.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
            ctx.DrawRectangle(new VisualBrush(c), null,
                new Rect(new Size(c.ActualWidth, c.ActualHeight)));
        rtb.Render(dv);

        _ghost = new GhostAdorner(FieldChips, rtb, new Size(c.ActualWidth, c.ActualHeight));
        _ghost.SetPosition(new Point(_pressPoint.X - _grabOffset.X, _pressPoint.Y - _grabOffset.Y));
        AdornerLayer.GetAdornerLayer(FieldChips)?.Add(_ghost);
    }

    private void RemoveGhost()
    {
        if (_ghost is null) return;
        AdornerLayer.GetAdornerLayer(FieldChips)?.Remove(_ghost);
        _ghost = null;
    }

    private void DimDragged()
    {
        for (var i = 0; i < _chips.Count; i++)
            if (ChipContainer(i) is { } c)
                c.Opacity = _chips[i].Dragging ? 0.3 : 1.0;
    }

    private FrameworkElement? ChipContainer(int index) =>
        FieldChips.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private int ChipIndexAt(Point p)
    {
        for (var i = 0; i < _chips.Count; i++)
        {
            if (ChipContainer(i) is not { } c) continue;
            var bounds = new Rect(c.TranslatePoint(new Point(0, 0), FieldChips), c.RenderSize);
            if (bounds.Contains(p)) return i;
        }
        return -1;
    }

    private static bool IsOn<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---- 全选 / 全清 / 默认 / 一键排序 ------------------------------------

    private void FieldsAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var chip in _chips) chip.Visible = true;
    }

    private void FieldsNone_Click(object sender, RoutedEventArgs e)
    {
        // 名称 stays on first, so the ≥1-row-field guard never trips mid-loop.
        foreach (var chip in _chips.Where(c => c.Field.Field == StealthField.Name))
            chip.Visible = true;
        foreach (var chip in _chips.Where(c => c.Field.Field != StealthField.Name))
            chip.Visible = false;
    }

    private void FieldsDefault_Click(object sender, RoutedEventArgs e)
    {
        var def = StealthConfig.CreateDefault().Normalize();
        _editing.Fields.Clear();
        _editing.Fields.AddRange(def.Fields);
        BuildChips();
        Apply();
    }

    /// <summary>一键排序: checked fields close ranks at the front keeping their
    /// relative order; unchecked follow, also in relative order.</summary>
    private void FieldsCompact_Click(object sender, RoutedEventArgs e)
    {
        var ordered = _editing.Fields.Where(f => f.Visible)
            .Concat(_editing.Fields.Where(f => !f.Visible)).ToList();
        _editing.Fields.Clear();
        _editing.Fields.AddRange(ordered);
        BuildChips();
        Apply();
    }

    /// <summary>Double-click editor: the field's colour(s) via the full picker.</summary>
    private void EditColors(FieldChip chip)
    {
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        void AddRow(string label, string hex, Action<string> set)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock
            {
                Text = label,
                Foreground = Frozen("#8B93A3"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var picker = Picker(hex, h => { set(h); chip.RefreshSwatches(); }, label, 100);
            Grid.SetColumn(text, 0);
            Grid.SetColumn(picker, 1);
            row.Children.Add(text);
            row.Children.Add(picker);
            body.Children.Add(row);
        }

        if (chip.Signed)
        {
            AddRow("上涨颜色", chip.Field.PositiveColor, h => chip.Field.PositiveColor = h);
            AddRow("下跌颜色", chip.Field.NegativeColor, h => chip.Field.NegativeColor = h);
        }
        else
        {
            AddRow("颜色", chip.Field.Color, h => chip.Field.Color = h);
        }

        var close = new Button
        {
            Content = "完成",
            Padding = new Thickness(14, 3, 14, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        body.Children.Add(close);

        var dialog = new Window
        {
            Title = chip.Name + " 颜色",
            Owner = Window.GetWindow(this),
            Width = 300,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Frozen("#12161F"),
            FontFamily = new FontFamily("Microsoft YaHei"),
            Content = body,
        };
        close.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    /// <summary>One field as a togglable chip; writes straight into the draft.</summary>
    private sealed class FieldChip : INotifyPropertyChanged
    {
        private readonly Func<StealthFieldConfig, bool> _canHide;

        public FieldChip(StealthFieldConfig field, string name,
            Func<StealthFieldConfig, bool> canHide)
        {
            Field = field;
            Name = name;
            Signed = StealthFields.IsSigned(field.Field);
            _canHide = canHide;
        }

        public StealthFieldConfig Field { get; }
        public string Name { get; }
        public bool Signed { get; }

        /// <summary>True while this chip is the one being dragged.</summary>
        public bool Dragging { get; set; }

        public bool Visible
        {
            get => Field.Visible;
            set
            {
                if (Field.Visible == value) return;
                if (!value && !_canHide(Field))
                {
                    Notify(nameof(Visible));   // snap a refused checkbox back
                    return;
                }
                Field.Visible = value;
                Notify(nameof(Visible));
            }
        }

        public Brush Swatch1 => BrushOf(Signed ? Field.PositiveColor : Field.Color);
        public Brush Swatch2 => BrushOf(Field.NegativeColor);
        public Visibility Swatch2Visible => Signed ? Visibility.Visible : Visibility.Collapsed;

        public void RefreshSwatches()
        {
            Notify(nameof(Swatch1));
            Notify(nameof(Swatch2));
        }

        private static Brush BrushOf(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Transparent; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
