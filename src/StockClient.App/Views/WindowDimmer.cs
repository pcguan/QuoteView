using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace StockClient.App.Views;

/// <summary>
/// App-consistent chrome for a window, attached once after InitializeComponent:
///   · a DARK native title bar (DWM immersive dark mode), so the standalone
///     windows match the dark content instead of showing a white caption bar;
///   · Shift+滚轮 dims the window — the same "亮度" gesture the stealth panel uses.
///
/// The dim is a black SCRIM laid over the content, NOT Window.Opacity. Reducing a
/// window's opacity looked white on the main window: it uses a Mica (transparent)
/// backdrop, and a non-layered window under reduced opacity exposes the backdrop /
/// a white base rather than blending dark. A scrim is plain WPF content — it just
/// darkens whatever is beneath it, identically on Mica and solid windows, and can
/// never go white. One shared, remembered level across every window
/// (<see cref="AppPrefs.WindowOpacity"/>, 1 = 原样, down to 0.2 = 最暗). (The
/// stealth panel is NOT attached — it drives its own shade.)
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
        // Overlay a scrim on top of the content. Wrapping keeps every x:Named
        // element (they are the same objects, only re-parented) so code-behind
        // and bindings are unaffected.
        var scrim = new Border
        {
            Background = Brushes.Black,
            IsHitTestVisible = false,
            Opacity = ScrimFor(AppPrefs.WindowOpacity),
        };
        if (window.Content is UIElement inner)
        {
            window.Content = null;
            var host = new Grid();
            host.Children.Add(inner);
            host.Children.Add(scrim);   // last child = on top
            window.Content = host;
        }

        // HWND exists at SourceInitialized (before the first paint): dark title bar.
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
            scrim.Opacity = ScrimFor(next);
            e.Handled = true;
        };
    }

    /// <summary>Scrim opacity for a brightness level: 1.0 → 0 (透明), 0.2 → 0.8 (很暗).</summary>
    private static double ScrimFor(double brightness) => Math.Clamp(Max - brightness, 0, 0.8);
}
