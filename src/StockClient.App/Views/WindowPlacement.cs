using System.Windows;

namespace StockClient.App.Views;

/// <summary>
/// Remembers a window's size, position and maximized state across sessions, keyed
/// by a short name (so all chart windows share one placement, the history window
/// its own, …). Restores on open — unless the saved rect would strand the window
/// off the current desktop (a monitor was unplugged) — and saves on close.
/// Attach once after InitializeComponent, before the window is shown.
/// </summary>
internal static class WindowPlacement
{
    public static void Attach(Window window, string key)
    {
        var p = AppPrefs.Placement(key);
        if (p is not null && p.W > 100 && p.H > 100 && OnScreen(p))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = p.L;
            window.Top = p.T;
            window.Width = p.W;
            window.Height = p.H;
            if (p.Max) window.WindowState = WindowState.Maximized;
        }

        window.Closing += (_, _) =>
        {
            // RestoreBounds is the NORMAL rect even while maximized, so the
            // remembered size is the one the window returns to.
            var r = window.RestoreBounds;
            if (r.Width > 100 && r.Height > 100)
                AppPrefs.SavePlacement(key,
                    new AppPrefs.WinPlace(r.Left, r.Top, r.Width, r.Height,
                        window.WindowState == WindowState.Maximized));
        };
    }

    /// <summary>At least a corner's worth of the window lands on the virtual desktop.</summary>
    private static bool OnScreen(AppPrefs.WinPlace p)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        return p.L < vr - 40 && p.L + p.W > vl + 40 && p.T < vb - 40 && p.T + p.H > vt + 40;
    }
}
