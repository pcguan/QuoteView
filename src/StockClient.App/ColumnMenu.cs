using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace StockClient.App;

/// <summary>
/// Gives a grid's headers a right-click column chooser, right-aligns the headers
/// of columns whose cells are right-aligned, and reroutes each column's LEFT edge
/// from "resize the previous column" to "move this column".
///
/// Both styling parts are done here in code rather than with a XAML style because
/// BasedOn="{StaticResource {x:Type DataGridColumnHeader}}" does not resolve to
/// WPF UI's header style — columns that used it fell back to the default white
/// chrome. TryFindResource walks the live tree at runtime, which does.
/// </summary>
public static class ColumnMenu
{
    /// <param name="menu">
    /// Optional replacement for the built-in per-column checkbox menu — the quote
    /// grid passes its own (opening the tiled column-settings window) because its
    /// ~20 columns outgrow a vertical menu.
    /// </param>
    public static void Attach(System.Windows.Controls.DataGrid grid, ContextMenu? menu = null)
    {
        menu ??= BuildMenu(grid);

        // Resolved against the real resource tree, so it picks up whatever the
        // merged theme actually installed.
        var baseStyle = grid.ColumnHeaderStyle
                        ?? grid.TryFindResource(typeof(DataGridColumnHeader)) as Style;

        foreach (var column in grid.Columns)
        {
            var style = new Style(typeof(DataGridColumnHeader), baseStyle);
            style.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu));

            // Header follows the cells: a right-aligned column with a left-aligned
            // header puts the name at one edge and the value at the other.
            if (IsRightAligned(column))
            {
                style.Setters.Add(new Setter(
                    Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
            }

            // Disable the left gripper once each header loads (and re-loads after a
            // reorder), so the left edge starts a move instead of resizing the
            // neighbour. The right gripper is untouched and still resizes.
            style.Setters.Add(new EventSetter(
                FrameworkElement.LoadedEvent, new RoutedEventHandler(OnHeaderLoaded)));

            column.HeaderStyle = style;
        }
    }

    private static void OnHeaderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridColumnHeader header) DisableLeftGripper(header);
    }

    /// <summary>
    /// Turns off hit-testing on the header's left resize gripper. The gripper
    /// normally resizes the PREVIOUS column; with it out of the way the left edge
    /// falls through to the header itself, which starts a native reorder — so a
    /// left-edge drag moves the column (width preserved), while the right gripper
    /// still resizes it. No resize capability is lost: the previous column stays
    /// resizable from its own right edge.
    /// </summary>
    private static void DisableLeftGripper(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            // Match by name, or fall back to a left-aligned Thumb (the right
            // gripper is right-aligned) in case the theme renamed the part.
            if (child is Thumb thumb &&
                (thumb.Name == "PART_LeftHeaderGripper" ||
                 thumb.HorizontalAlignment == HorizontalAlignment.Left))
            {
                thumb.IsHitTestVisible = false;
                continue;
            }

            DisableLeftGripper(child);
        }
    }

    /// <summary>True when the column's cell style right-aligns its content.</summary>
    private static bool IsRightAligned(DataGridColumn column)
    {
        if (column is not DataGridTextColumn text || text.ElementStyle is null) return false;

        return text.ElementStyle.Setters.OfType<Setter>().Any(s =>
            s.Property == FrameworkElement.HorizontalAlignmentProperty &&
            s.Value is HorizontalAlignment.Right);
    }

    private static ContextMenu BuildMenu(System.Windows.Controls.DataGrid grid)
    {
        var menu = new ContextMenu();

        foreach (var column in grid.Columns)
        {
            var header = column.Header as string;
            if (string.IsNullOrWhiteSpace(header)) continue;

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };

            var captured = column;
            item.Click += (_, _) =>
            {
                // Never hide the last column: with no header left there is
                // nothing to right-click, so the menu could never come back.
                var visible = grid.Columns.Count(c => c.Visibility == Visibility.Visible);
                if (!item.IsChecked && visible <= 1)
                {
                    item.IsChecked = true;
                    return;
                }

                captured.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "显示全部列" };
        reset.Click += (_, _) =>
        {
            foreach (var c in grid.Columns) c.Visibility = Visibility.Visible;
            foreach (var i in menu.Items.OfType<MenuItem>()) i.IsChecked = true;
        };
        menu.Items.Add(reset);

        return menu;
    }
}
