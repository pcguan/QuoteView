using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StockClient.App;

/// <summary>
/// Manual drag-to-reorder for any ItemsControl (ListBox, DataGrid). WPF has no
/// built-in row reordering, so this drives DragDrop by hand and calls back with
/// the from/to item indices; the caller moves the underlying list.
///
/// A move threshold separates a drag from a click, so the host's existing
/// gestures — selecting a group, double-clicking to open a chart or rename — keep
/// working. Drags starting on a button (the delete icon) are ignored.
/// </summary>
public static class DragReorder
{
    private const string Format = "StockClient.ReorderIndex";

    /// <param name="canDrag">
    /// Optional gate: when it returns false, dragging is disabled. Used to block
    /// reordering while the grid is showing a sorted view (the displayed order
    /// wouldn't match the underlying list, so a drag would move the wrong item).
    /// </param>
    public static void Enable(ItemsControl control, Action<int, int> onMove, Func<bool>? canDrag = null)
    {
        var start = default(Point);
        var armed = false;

        control.AllowDrop = true;

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
        };

        control.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;

            var from = (int)e.Data.GetData(Format)!;
            var to = ContainerIndex(control, e.OriginalSource as DependencyObject);

            // Dropped past the last row → move to the end.
            if (to < 0) to = control.Items.Count - 1;

            if (from >= 0 && to >= 0 && from != to) onMove(from, to);
        };
    }

    /// <summary>Index of the item whose container the element sits in, or -1.</summary>
    private static int ContainerIndex(ItemsControl control, DependencyObject? source)
    {
        while (source is not null && source != control)
        {
            var item = control.ItemContainerGenerator.ItemFromContainer(source);
            if (item != DependencyProperty.UnsetValue) return control.Items.IndexOf(item);
            source = VisualTreeHelper.GetParent(source);
        }

        return -1;
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
