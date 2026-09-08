using System.Windows;

namespace StockClient.App.Views;

/// <summary>
/// Remembers a window's size, position and maximized state across sessions, keyed
/// by a short name. Fixed-key windows (main, history, …) use Attach; a chart
/// window keys by its contract (kline:CODE) so each contract's chart reopens where
/// that chart last sat, and drives Restore/Save itself as the contract switches.
/// Restores on open — unless the saved rect would strand the window off the
/// current desktop (a monitor was unplugged) — and saves on close. Attach once
/// after InitializeComponent, before the window is shown.
/// </summary>
internal static class WindowPlacement
{
    public static void Attach(Window window, string key)
    {
        Restore(window, key);
        window.Closing += (_, _) => Save(window, key);
    }

    /// <summary>Move/size the window to a key's saved placement, unless it would
    /// strand it off the current desktop. For a window whose key changes over its
    /// life (a chart that switches contract), call this + Save yourself rather
    /// than Attach.</summary>
    public static void Restore(Window window, string key)
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
    }

    /// <summary>Record the window's current placement under a key.</summary>
    public static void Save(Window window, string key)
    {
        // RestoreBounds is the NORMAL rect even while maximized, so the
        // remembered size is the one the window returns to.
        var r = window.RestoreBounds;
        if (r.Width > 100 && r.Height > 100)
            AppPrefs.SavePlacement(key,
                new AppPrefs.WinPlace(r.Left, r.Top, r.Width, r.Height,
                    window.WindowState == WindowState.Maximized));
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
