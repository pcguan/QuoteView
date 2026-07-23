using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StockClient.App.Views;

/// <summary>
/// Column chooser for the live-quote grid: every column as a flat chip — the
/// checkbox controls visibility, dragging a chip reorders the columns. Replaces
/// the old vertical header context menu, which grew past the screen with ~20
/// columns and couldn't scroll. Changes apply to the grid immediately;
/// persistence rides the existing QuoteColumns watchers (visibility and
/// display-index are both observed there).
/// </summary>
public partial class ColumnSettingsWindow : Window
{
    /// <summary>The default visible set (the user-chosen essentials).</summary>
    private static readonly string[] DefaultVisible =
        { "代码", "名称", "最新价", "涨跌幅", "涨跌", "行业" };

    private const string DragFormat = "QuoteView.ColumnChip";

    private readonly DataGrid _grid;
    private readonly ObservableCollection<Chip> _chips = new();

    // Drag state: index pressed on, and whether it turned into a drag (a press
    // that never moves past the threshold is a click and toggles the chip).
    private Point _pressPoint;
    private int _pressIndex = -1;
    private bool _armed;
    private bool _dragging;

    public ColumnSettingsWindow(DataGrid grid)
    {
        InitializeComponent();
        _grid = grid;

        foreach (var column in grid.Columns
                     .Where(c => !string.IsNullOrEmpty(c.Header?.ToString()))
                     .OrderBy(c => c.DisplayIndex))
            _chips.Add(new Chip(column));

        Chips.ItemsSource = _chips;

        Chips.PreviewMouseLeftButtonDown += OnDown;
        Chips.PreviewMouseMove += OnMove;
        Chips.PreviewMouseLeftButtonUp += OnUp;
        Chips.DragOver += OnDragOver;
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
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_armed || _dragging || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(Chips);
        if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragging = true;
        DragDrop.DoDragDrop(Chips, new DataObject(DragFormat, _pressIndex), DragDropEffects.Move);
        ApplyOrder(); // commit whatever the live reorder ended on (drop or cancel)
        _armed = false;
    }

    /// <summary>Live reorder: the chip follows the cursor while dragging.</summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var from = CurrentDragIndex();
        var over = IndexAt(e.GetPosition(Chips));
        // Once moved, the cursor sits over the dragged chip itself (from == over),
        // which is what keeps this from oscillating.
        if (from < 0 || over < 0 || over == from) return;

        _chips.Move(from, over);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        // A click (no drag) anywhere on the chip toggles it — so the label text
        // behaves like a normal checkbox label.
        if (_armed && !_dragging && _pressIndex >= 0 &&
            IndexAt(e.GetPosition(Chips)) == _pressIndex)
            _chips[_pressIndex].Visible = !_chips[_pressIndex].Visible;

        _armed = false;
    }

    /// <summary>Where the dragged chip currently sits (it moves during the drag).</summary>
    private int CurrentDragIndex()
    {
        for (var i = 0; i < _chips.Count; i++)
            if (_chips[i].Dragging) return i;

        // First DragOver: mark the pressed chip as the one in flight.
        if (_pressIndex >= 0 && _pressIndex < _chips.Count)
        {
            _chips[_pressIndex].Dragging = true;
            return _pressIndex;
        }

        return -1;
    }

    /// <summary>Pushes the chip order onto the columns' DisplayIndex.</summary>
    private void ApplyOrder()
    {
        foreach (var chip in _chips) chip.Dragging = false;

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

    private int IndexAt(Point p)
    {
        for (var i = 0; i < _chips.Count; i++)
        {
            if (Chips.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;

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
            Header = column.Header?.ToString() ?? "";
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
