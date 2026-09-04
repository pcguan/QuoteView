using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace StockClient.App.Views;

/// <summary>
/// App-consistent chrome for a window, attached once after InitializeComponent:
///   · a DARK native title bar (DWM immersive dark mode), so the standalone
///     windows match the dark content instead of showing a white caption bar;
///   · Shift+滚轮 dims the window — steps its opacity (透明度), the same "亮度"
///     gesture the stealth panel uses. One shared, remembered value across every
///     window (<see cref="AppPrefs.WindowOpacity"/>), clamped so a window can
///     never go fully invisible.
///
/// A window with a TRANSPARENT background (the main window's Mica backdrop) would
/// render WHITE under reduced opacity — the transparent area exposes the raw
/// window surface instead of blending dark. So while dimmed, such a window gets a
/// solid dark base swapped in; the transparent (Mica) background returns at full
/// opacity. (The stealth panel is NOT attached — it drives its own shade.)
/// </summary>
internal static class WindowDimmer
{
    private const double Min = 0.2;
    private const double Max = 1.0;
    private const double Step = 0.05;

    private static readonly Brush DimBase = Frozen("#0F1420");

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Win10 2004+/Win11, 19 on 1809–1909.
    private const int DwmDarkMode = 20;
    private const int DwmDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Attach(Window window)
    {
        window.Opacity = AppPrefs.WindowOpacity;

        Brush? transparentBg = null;   // the Mica background to restore, once detected
        var checkedBg = false;

        // HWND exists at SourceInitialized (before the first paint): set the dark
        // title bar, and — if we start up already dimmed — fix a Mica backdrop
        // so it doesn't flash white.
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var on = 1;
                if (DwmSetWindowAttribute(hwnd, DwmDarkMode, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmDarkModeLegacy, ref on, sizeof(int));
            }

            checkedBg = true;
            if (IsTransparent(window.Background))
            {
                transparentBg = window.Background;
                if (AppPrefs.WindowOpacity < Max) window.Background = DimBase;
            }
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

            if (!checkedBg)
            {
                checkedBg = true;
                if (IsTransparent(window.Background)) transparentBg = window.Background;
            }
            if (transparentBg is not null)
                window.Background = next < Max ? DimBase : transparentBg;

            e.Handled = true;
        };
    }

    /// <summary>A null or fully-transparent (A=0) background — the Mica case.</summary>
    private static bool IsTransparent(Brush? brush) =>
        brush is null || (brush is SolidColorBrush scb && scb.Color.A == 0);

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
