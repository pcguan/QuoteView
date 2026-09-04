using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace StockClient.App.Views;

/// <summary>
/// App-consistent chrome for a plain window, attached once after
/// InitializeComponent:
///   · a DARK native title bar (DWM immersive dark mode), so the standalone
///     windows match the dark content instead of showing a white caption bar;
///   · Shift+滚轮 dims the window — steps its opacity (透明度), the same "亮度"
///     gesture the stealth panel uses. One shared, remembered value across every
///     window (<see cref="AppPrefs.WindowOpacity"/>), clamped so a window can
///     never go fully invisible.
/// (The stealth panel is NOT attached — it drives its own shade through the
/// low-level hook, and its chrome is custom.)
/// </summary>
internal static class WindowDimmer
{
    private const double Min = 0.2;
    private const double Max = 1.0;
    private const double Step = 0.05;

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Win10 2004+/Win11, 19 on 1809–1909.
    private const int DwmDarkMode = 20;
    private const int DwmDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Attach(Window window)
    {
        window.Opacity = AppPrefs.WindowOpacity;

        // Set the dark title bar before the window is first painted (HWND exists
        // at SourceInitialized), so there is no white-caption flash.
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DwmDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmDarkModeLegacy, ref on, sizeof(int));
        };

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
