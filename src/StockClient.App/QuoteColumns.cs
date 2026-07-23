using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StockClient.Core.Groups;

namespace StockClient.App;

/// <summary>
/// Persists the live-quote grid's column layout — order, width, visibility — to
/// the group config, and restores it on load. Complements <see cref="ColumnMenu"/>
/// (which toggles visibility but keeps nothing): this listens to the same changes
/// and writes them, so a customised layout survives relaunch.
///
/// Columns are keyed by header text. The header-less delete column is neither
/// stored nor moved, so it stays pinned on the right.
/// </summary>
public static class QuoteColumns
{
    public static void Attach(DataGrid grid, List<QuoteColumnState> saved, Action save)
    {
        Restore(grid, saved);

        // Debounced: dragging a column edge fires WidthProperty continuously, and
        // one disk write per settled change is enough.
        var debounce = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            Snapshot(grid, saved);
            save();
        };

        void Schedule()
        {
            debounce.Stop();
            debounce.Start();
        }

        grid.ColumnReordered += (_, _) => Schedule();

        // Width and visibility have no plain events; watch the DPs. Registered
        // AFTER Restore so applying the saved layout doesn't trigger a save.
        foreach (var column in grid.Columns)
        {
            if (string.IsNullOrEmpty(column.Header?.ToString())) continue;

            DependencyPropertyDescriptor
                .FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))
                ?.AddValueChanged(column, (_, _) => Schedule());
            DependencyPropertyDescriptor
                .FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn))
                ?.AddValueChanged(column, (_, _) => Schedule());
            // ColumnReordered only fires for header drags; the column-settings
            // window reorders by assigning DisplayIndex, which needs the DP watch.
            DependencyPropertyDescriptor
                .FromProperty(DataGridColumn.DisplayIndexProperty, typeof(DataGridColumn))
                ?.AddValueChanged(column, (_, _) => Schedule());
        }
    }

    private static void Restore(DataGrid grid, List<QuoteColumnState> saved)
    {
        if (saved.Count == 0) return;

        var byKey = saved
            .Where(s => !string.IsNullOrEmpty(s.Key))
            .GroupBy(s => s.Key)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var column in grid.Columns)
        {
            var key = column.Header?.ToString();
            if (string.IsNullOrEmpty(key) || !byKey.TryGetValue(key, out var state)) continue;

            if (state.Width > 0) column.Width = new DataGridLength(state.Width);
            column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // DisplayIndex last, assigned in saved order from 0. WPF requires the
        // values stay in range and unique while being set, so it's wrapped: a bad
        // saved file must not stop the grid from loading.
        try
        {
            var index = 0;
            foreach (var state in saved.OrderBy(s => s.Order))
            {
                var column = grid.Columns.FirstOrDefault(c => c.Header?.ToString() == state.Key);
                if (column is not null && index < grid.Columns.Count) column.DisplayIndex = index++;
            }
        }
        catch (ArgumentException)
        {
            // Leave the columns in their declared order.
        }
    }

    private static void Snapshot(DataGrid grid, List<QuoteColumnState> saved)
    {
        saved.Clear();

        foreach (var column in grid.Columns)
        {
            var key = column.Header?.ToString();
            if (string.IsNullOrEmpty(key)) continue;

            saved.Add(new QuoteColumnState
            {
                Key = key,
                Order = column.DisplayIndex,
                Width = column.ActualWidth,
                Visible = column.Visibility == Visibility.Visible,
            });
        }
    }
}
