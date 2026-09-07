using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StockClient.App.Views;

/// <summary>
/// Double-clicking a blank part of the window minimizes it — a fast "stash it"
/// gesture for the K线 / 历史分时 windows, in the same muscle-memory spirit as the
/// panel's double-click-to-hide. Double-clicks that land on an interactive
/// control (buttons, dropdowns, text boxes, grids, scrollbars) belong to that
/// control and are left alone; the chart surfaces themselves are fair game.
/// </summary>
internal static class WindowMinimizeGesture
{
    public static void Attach(Window window)
    {
        // handledEventsToo: the chart handles its own left-button-down (pan /
        // legend toggle), so without this the second click never reaches us.
        window.AddHandler(
            UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnMouseDown),
            handledEventsToo: true);
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not Window window) return;
        if (LandsOnControl(e.OriginalSource as DependencyObject)) return;

        window.WindowState = WindowState.Minimized;
    }

    // A double-click that started inside an interactive control is that
    // control's gesture, not a minimize. Walk the ancestry (visual, falling back
    // to logical for content elements like Run) and bail if we hit one.
    private static bool LandsOnControl(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ButtonBase or ComboBox or ComboBoxItem or TextBoxBase
                or DataGrid or Slider or Thumb or ScrollBar or MenuItem or ListBoxItem)
                return true;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
