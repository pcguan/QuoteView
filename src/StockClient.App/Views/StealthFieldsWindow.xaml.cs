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
/// The panel's field chooser as its own window, sized so every field is visible
/// at once — no scrolling. Same interaction as the main grid's 列设置: click a
/// chip (or its checkbox) to toggle, capture-drag to reorder, DOUBLE-click for
/// the colour editor. Operates directly on the settings page's template draft;
/// nothing reaches the panel until 「保存」 there.
/// </summary>
public partial class StealthFieldsWindow : Window
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

    private readonly StealthConfig _editing;
    private readonly Action _apply;

    public StealthFieldsWindow(StealthConfig editing, Action apply)
    {
        InitializeComponent();
        _editing = editing;
        _apply = apply;
        BuildChips();
    }

    private void Apply() => _apply();

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // ---- field chips: 勾选 + 拖拽排序 + 双击调色 --------------------------
    //
    // Mirrors the main grid's 列设置: every field is a flat chip — a click (or
    // its checkbox) toggles visibility, a capture-based drag reorders (full
    // input rate, ghost glides pixel-for-pixel), and a DOUBLE-click opens the
    // colour editor. Chip order IS _editing.Fields order — what the panel
    // renders. All of it stays buffered until 「保存」.

    private readonly ObservableCollection<object> _chips = new();

    /// <summary>What every chip can do regardless of kind.</summary>
    private interface IChip
    {
        bool Visible { get; set; }
    }

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
        // The header rides the same grid as a pseudo-field: its checkbox is the
        // 显隐, its swatch the one colour. Pinned first, not part of the order.
        _chips.Add(new HeaderChip(_editing));
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
            var chip = (IChip)_chips[_pressIndex];
            chip.Visible = !chip.Visible;
            _armed = false;
            e.Handled = true;
            if (chip is FieldChip field) EditColors(field);
            else if (chip is HeaderChip header) EditHeaderColor(header);
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
            // The header chip toggles and recolours like any other but has no
            // position in the field order — nothing to drag.
            if (_pressIndex < 0 || _chips[_pressIndex] is not FieldChip) return;

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
        if (over >= 0 && over != _dragIndex && _chips[over] is FieldChip)
        {
            _chips.Move(_dragIndex, over);
            _dragIndex = over;
            DimDragged();
        }

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
            var chip = (IChip)_chips[_pressIndex];
            chip.Visible = !chip.Visible;
        }

        _armed = false;
    }

    private void EndChipDrag()
    {
        if (!_draggingChip) return;

        _draggingChip = false;
        _dragIndex = -1;
        RemoveGhost();
        foreach (var chip in _chips.OfType<FieldChip>()) chip.Dragging = false;
        DimDragged();

        _editing.Fields.Clear();
        foreach (var chip in _chips.OfType<FieldChip>()) _editing.Fields.Add(chip.Field);
        Apply();
    }

    private void ShowGhost(int index)
    {
        if (_chips[index] is FieldChip dragged) dragged.Dragging = true;
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
                c.Opacity = _chips[i] is FieldChip { Dragging: true } ? 0.3 : 1.0;
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
        foreach (var chip in _chips.Cast<IChip>()) chip.Visible = true;
    }

    private void FieldsNone_Click(object sender, RoutedEventArgs e)
    {
        // 名称 stays on first, so the ≥1-row-field guard never trips mid-loop.
        foreach (var chip in _chips.OfType<FieldChip>()
                     .Where(c => c.Field.Field == StealthField.Name))
            chip.Visible = true;
        foreach (var chip in _chips.Cast<IChip>()
                     .Where(c => c is not FieldChip f || f.Field.Field != StealthField.Name))
            chip.Visible = false;
    }

    private void FieldsDefault_Click(object sender, RoutedEventArgs e)
    {
        var def = StealthConfig.CreateDefault().Normalize();
        _editing.Fields.Clear();
        _editing.Fields.AddRange(def.Fields);
        _editing.ShowHeader = true;
        _editing.HeaderColor = "#7E8798";
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

    /// <summary>Single-colour editor for the header pseudo-field.</summary>
    private void EditHeaderColor(HeaderChip chip)
    {
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = "颜色", Foreground = Frozen("#8B93A3"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var picker = Picker(_editing.HeaderColor,
            h => { _editing.HeaderColor = h; chip.RefreshSwatches(); }, "列名整体颜色", 100);
        Grid.SetColumn(text, 0);
        Grid.SetColumn(picker, 1);
        row.Children.Add(text);
        row.Children.Add(picker);
        body.Children.Add(row);

        var close = new Button
        {
            Content = "完成", Padding = new Thickness(14, 3, 14, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        body.Children.Add(close);

        var dialog = new Window
        {
            Title = "列名颜色",
            Owner = this,
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

    /// <summary>The header line as a chip: checkbox = 显隐, swatch = its one
    /// colour (whole line, not per column). Pinned first, never reordered.</summary>
    private sealed class HeaderChip : INotifyPropertyChanged, IChip
    {
        private readonly StealthConfig _editing;

        public HeaderChip(StealthConfig editing) => _editing = editing;

        public string Name => "列名（表头）";

        public bool Visible
        {
            get => _editing.ShowHeader;
            set
            {
                if (_editing.ShowHeader == value) return;
                _editing.ShowHeader = value;
                Notify(nameof(Visible));
            }
        }

        public Brush Swatch1
        {
            get
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(_editing.HeaderColor)); }
                catch { return Brushes.Transparent; }
            }
        }

        public Brush Swatch2 => Brushes.Transparent;
        public Visibility Swatch2Visible => Visibility.Collapsed;

        public void RefreshSwatches() => Notify(nameof(Swatch1));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One field as a togglable chip; writes straight into the draft.</summary>
    private sealed class FieldChip : INotifyPropertyChanged, IChip
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

    private static string FieldName(StealthField field) =>
        FieldNames.FirstOrDefault(f => f.Field == field).Name is { Length: > 0 } n ? n : field.ToString();

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
