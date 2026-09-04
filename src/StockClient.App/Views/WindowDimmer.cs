using System.Windows;
using System.Windows.Input;

namespace StockClient.App.Views;

/// <summary>
/// Shift+滚轮 dims any window — adjusts its opacity (透明度), the same "亮度"
/// gesture the stealth panel uses (there it steps the shade). One shared,
/// remembered value across every normal window (<see cref="AppPrefs.WindowOpacity"/>),
/// clamped so a window can never go fully invisible. Attach once per window
/// after InitializeComponent. (The stealth panel is NOT attached — it drives its
/// own shade through the low-level hook.)
/// </summary>
internal static class WindowDimmer
{
    private const double Min = 0.2;
    private const double Max = 1.0;
    private const double Step = 0.05;

    public static void Attach(Window window)
    {
        window.Opacity = AppPrefs.WindowOpacity;

        // Preview pass so the gesture wins over any inner control's own wheel
        // handling (chart zoom, list scroll) while Shift is held.
        window.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;

            var next = Math.Clamp(
                AppPrefs.WindowOpacity + (e.Delta > 0 ? Step : -Step), Min, Max);
            AppPrefs.WindowOpacity = next;
            window.Opacity = next;
            e.Handled = true;
        };
    }
}
