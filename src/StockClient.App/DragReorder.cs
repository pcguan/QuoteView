using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace StockClient.App;

/// <summary>
/// Manual drag-to-reorder for any ItemsControl (ListBox, DataGrid). WPF has no
/// built-in row reordering, so this drives DragDrop by hand and calls back with
/// the from/to item indices; the caller moves the underlying list.
///
/// While dragging it paints an insertion line (an adorner) at the exact drop
/// boundary — above or below the row under the cursor, decided by which half the
/// cursor is in — so it's unambiguous where the row will land.
///
/// A move threshold separates a drag from a click, so the host's existing
/// gestures keep working. Drags starting on a button (the delete icon) are ignored.
/// </summary>
public static class DragReorder
{
    private const string Format = "StockClient.ReorderIndex";

    /// <param name="canDrag">
    /// Optional gate: when it returns false, dragging is disabled (e.g. while the
    /// grid shows a sorted view, where the displayed order wouldn't match the list).
    /// </param>
    public static void Enable(ItemsControl control, Action<int, int> onMove, Func<bool>? canDrag = null)
    {
        var start = default(Point);
        var armed = false;
        InsertionAdorner? adorner = null;

        control.AllowDrop = true;

        void RemoveAdorner()
        {
            if (adorner is null) return;
            AdornerLayer.GetAdornerLayer(control)?.Remove(adorner);
            adorner = null;
        }

        void ShowLine(double y)
        {
            var layer = AdornerLayer.GetAdornerLayer(control);
            if (layer is null) return;
            if (adorner is null)
            {
                adorner = new InsertionAdorner(control);
                layer.Add(adorner);
            }
            adorner.SetY(y);
        }

        control.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(control);
            var source = e.OriginalSource as DependencyObject;
            armed = (canDrag?.Invoke() ?? true) && ContainerIndex(control, source) >= 0 && !OnButton(source);
        };

        control.PreviewMouseMove += (_, e) =>
        {
            if (!armed || e.LeftButton != MouseButtonState.Pressed) return;

            var p = e.GetPosition(control);
            if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var from = ContainerIndex(control, e.OriginalSource as DependencyObject);
            armed = false;
            if (from < 0) return;

            DragDrop.DoDragDrop(control, new DataObject(Format, from), DragDropEffects.Move);
            RemoveAdorner(); // drag ended (dropped or cancelled)
        };

        control.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;

            var (_, lineY) = InsertAt(control, e);
            ShowLine(lineY);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };

        control.DragLeave += (_, e) =>
        {
            // DragLeave also fires when crossing between child rows; only drop the
            // line when the cursor actually leaves the control.
            var p = e.GetPosition(control);
            if (p.X < 0 || p.Y < 0 || p.X > control.ActualWidth || p.Y > control.ActualHeight)
                RemoveAdorner();
        };

        control.Drop += (_, e) =>
        {
            RemoveAdorner();
            if (!e.Data.GetDataPresent(Format)) return;

            var from = (int)e.Data.GetData(Format)!;
            var (insert, _) = InsertAt(control, e);

            // `insert` is a slot in 0..count ("place before the row now at insert").
            // After removing `from`, everything past it shifts down by one.
            var to = from < insert ? insert - 1 : insert;
            if (from >= 0 && from < control.Items.Count && to >= 0 && to != from) onMove(from, to);
        };
    }

    /// <summary>
    /// The drop slot (0..count) and the y (in control coords) to draw the line at:
    /// top half of a row → before it, bottom half → after it, empty area → the end.
    /// </summary>
    private static (int Insert, double LineY) InsertAt(ItemsControl control, DragEventArgs e)
    {
        var container = Container(control, e.OriginalSource as DependencyObject);
        var y = e.GetPosition(control).Y;

        if (container is not null)
        {
            var idx = control.ItemContainerGenerator.IndexFromContainer(container);
            var top = container.TranslatePoint(new Point(0, 0), control).Y;
            var h = container.ActualHeight;
            return y < top + h / 2 ? (idx, top) : (idx + 1, top + h);
        }

        // Past the last row / empty area → append at the end, line under the last row.
        var count = control.Items.Count;
        var lineY = control.ActualHeight;
        if (count > 0 &&
            control.ItemContainerGenerator.ContainerFromIndex(count - 1) is FrameworkElement last)
            lineY = last.TranslatePoint(new Point(0, last.ActualHeight), control).Y;
        return (count, lineY);
    }

    /// <summary>The item container (DataGridRow / ListBoxItem) an element sits in, or null.</summary>
    private static FrameworkElement? Container(ItemsControl control, DependencyObject? source)
    {
        while (source is not null && source != control)
        {
            if (control.ItemContainerGenerator.ItemFromContainer(source) != DependencyProperty.UnsetValue)
                return source as FrameworkElement;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static int ContainerIndex(ItemsControl control, DependencyObject? source)
    {
        var container = Container(control, source);
        return container is null ? -1 : control.ItemContainerGenerator.IndexFromContainer(container);
    }

    private static bool OnButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}

/// <summary>A horizontal insertion line with a small left marker, painted while dragging.</summary>
internal sealed class InsertionAdorner : Adorner
{
    private static readonly Brush Accent = Freeze(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Pen LinePen = FreezePen(Accent, 2);

    private double _y;

    public InsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

    public void SetY(double y)
    {
        _y = y;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = (AdornedElement as FrameworkElement)?.ActualWidth ?? 0;
        dc.DrawLine(LinePen, new Point(0, _y), new Point(width, _y));

        // A small triangle at the left end, so the line reads as an insertion caret.
        var tip = new StreamGeometry();
        using (var c = tip.Open())
        {
            c.BeginFigure(new Point(0, _y - 4), true, true);
            c.LineTo(new Point(6, _y), true, false);
            c.LineTo(new Point(0, _y + 4), true, false);
        }
        tip.Freeze();
        dc.DrawGeometry(Accent, null, tip);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FreezePen(Brush brush, double thickness)
    {
        var p = new Pen(brush, thickness);
        p.Freeze();
        return p;
    }
}
