using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StockClient.App.Views;

/// <summary>
/// Column chooser for the live-quote grid: every column as a flat chip — the
/// checkbox (or a click anywhere on the chip) toggles visibility, dragging a chip
/// reorders the columns. Replaces the old vertical header context menu, which
/// grew past the screen with ~20 columns and couldn't scroll.
///
/// The drag is mouse-capture based, NOT OLE DoDragDrop: OLE's DragOver events
/// arrive at a throttled, target-dependent rate, which left the ghost freezing
/// while the cursor moved. With capture, MouseMove arrives at full input rate and
/// the ghost glides pixel-for-pixel.
///
/// Changes apply to the grid immediately; persistence rides the existing
/// QuoteColumns watchers (visibility and display-index are both observed there).
/// </summary>
public partial class ColumnSettingsWindow : Window
{
    /// <summary>The default visible set (the user-chosen essentials).</summary>
    private static readonly string[] DefaultVisible =
        { "代码", "名称", "最新价", "涨跌幅", "涨跌", "行业" };

    private readonly DataGrid _grid;
    private readonly ObservableCollection<Chip> _chips = new();

    // Drag state. A press that never crosses the threshold is a click (toggle);
    // past it, the chip is picked up: ghost follows the cursor, siblings make way.
    private Point _pressPoint;
    private Point _grabOffset; // press point within the chip, so the ghost doesn't snap to the cursor tip
    private int _pressIndex = -1;
    private int _dragIndex = -1;
    private bool _armed;
    private bool _dragging;
    private GhostAdorner? _ghost;

    public ColumnSettingsWindow(DataGrid grid)
    {
        InitializeComponent();
        _grid = grid;

        // String headers only — the delete column's header is not a real column name.
        foreach (var column in grid.Columns
                     .Where(c => c.Header is string { Length: > 0 })
                     .OrderBy(c => c.DisplayIndex))
            _chips.Add(new Chip(column));

        Chips.ItemsSource = _chips;

        Chips.PreviewMouseLeftButtonDown += OnDown;
        Chips.PreviewMouseMove += OnMove;
        Chips.PreviewMouseLeftButtonUp += OnUp;
        Chips.LostMouseCapture += (_, _) => EndDrag();
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var chip in _chips) chip.Visible = true;
    }

    private void None_Click(object sender, RoutedEventArgs e)
    {
        foreach (var chip in _chips) chip.Visible = false;
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        foreach (var chip in _chips) chip.Visible = DefaultVisible.Contains(chip.Header);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(Chips);
        _pressIndex = IndexAt(_pressPoint);
        // A press on the checkbox glyph is the checkbox's own toggle; arming it
        // here would double-toggle on the way back up.
        _armed = _pressIndex >= 0 && !IsOn<ButtonBase>(e.OriginalSource as DependencyObject);
        _dragging = false;

        if (_armed && Container(_pressIndex) is { } c)
            _grabOffset = e.GetPosition(c);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_armed || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(Chips);

        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragging = true;
            _dragIndex = _pressIndex;
            _chips[_dragIndex].Dragging = true;
            ShowGhost(_dragIndex);
            Chips.CaptureMouse(); // moves keep coming even outside the chip area
        }

        _ghost?.SetPosition(new Point(p.X - _grabOffset.X, p.Y - _grabOffset.Y));

        var over = IndexAt(p);
        if (over >= 0 && over != _dragIndex)
        {
            _chips.Move(_dragIndex, over);
            _dragIndex = over;
            DimDragged();
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            Chips.ReleaseMouseCapture(); // EndDrag runs via LostMouseCapture
        }
        else if (_armed && _pressIndex >= 0 && IndexAt(e.GetPosition(Chips)) == _pressIndex)
        {
            // A click (no drag) anywhere on the chip toggles it — the label text
            // behaves like a normal checkbox label.
            _chips[_pressIndex].Visible = !_chips[_pressIndex].Visible;
        }

        _armed = false;
    }

    /// <summary>Commits the drag; also runs when capture is lost for any reason.</summary>
    private void EndDrag()
    {
        if (!_dragging) return;

        _dragging = false;
        _dragIndex = -1;
        RemoveGhost();
        ApplyOrder();
    }

    /// <summary>
    /// A floating snapshot of the dragged chip. A bitmap, not a VisualBrush: the
    /// live reorder regenerates containers mid-drag, and a brush bound to a
    /// recycled visual goes blank.
    /// </summary>
    private void ShowGhost(int index)
    {
        if (Container(index) is not { } c || c.ActualWidth < 1) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(c.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(c.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Render through a DrawingVisual so the element's layout offset doesn't
        // shift it out of the bitmap.
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
            ctx.DrawRectangle(new VisualBrush(c), null, new Rect(new Size(c.ActualWidth, c.ActualHeight)));
        rtb.Render(dv);

        _ghost = new GhostAdorner(Chips, rtb, new Size(c.ActualWidth, c.ActualHeight));
        _ghost.SetPosition(new Point(_pressPoint.X - _grabOffset.X, _pressPoint.Y - _grabOffset.Y));
        AdornerLayer.GetAdornerLayer(Chips)?.Add(_ghost);
    }

    private void RemoveGhost()
    {
        if (_ghost is null) return;
        AdornerLayer.GetAdornerLayer(Chips)?.Remove(_ghost);
        _ghost = null;
    }

    /// <summary>The in-flight chip stays dimmed in place; the ghost is "it".</summary>
    private void DimDragged()
    {
        for (var i = 0; i < _chips.Count; i++)
            if (Container(i) is { } c)
                c.Opacity = _chips[i].Dragging ? 0.3 : 1.0;
    }

    /// <summary>Pushes the chip order onto the columns' DisplayIndex.</summary>
    private void ApplyOrder()
    {
        foreach (var chip in _chips) chip.Dragging = false;
        DimDragged(); // restore all opacities

        try
        {
            var index = 0;
            foreach (var chip in _chips) chip.Column.DisplayIndex = index++;
        }
        catch (ArgumentException)
        {
            // A transient invalid permutation must not crash the window.
        }
    }

    private FrameworkElement? Container(int index) =>
        Chips.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private int IndexAt(Point p)
    {
        for (var i = 0; i < _chips.Count; i++)
        {
            if (Container(i) is not { } c) continue;

            var bounds = new Rect(c.TranslatePoint(new Point(0, 0), Chips), c.RenderSize);
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

    /// <summary>One column as a togglable chip; writes straight through to the column.</summary>
    public sealed class Chip : INotifyPropertyChanged
    {
        public Chip(DataGridColumn column)
        {
            Column = column;
            Header = column.Header as string ?? "";
        }

        public DataGridColumn Column { get; }
        public string Header { get; }

        /// <summary>True while this chip is the one being dragged.</summary>
        public bool Dragging { get; set; }

        public bool Visible
        {
            get => Column.Visibility == Visibility.Visible;
            set
            {
                if (Visible == value) return;
                Column.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

/// <summary>The bitmap snapshot of the dragged chip, gliding with the cursor.</summary>
internal sealed class GhostAdorner : Adorner
{
    private readonly ImageSource _image;
    private readonly Size _size;
    private Point _pos;

    public GhostAdorner(UIElement adorned, ImageSource image, Size size) : base(adorned)
    {
        _image = image;
        _size = size;
        IsHitTestVisible = false;
        Opacity = 0.85;
    }

    public void SetPosition(Point p)
    {
        _pos = p;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawImage(_image, new Rect(_pos, _size));
}
