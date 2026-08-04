using System.IO;
using System.Windows;
using System.Windows.Input;

namespace StockClient.App;

/// <summary>
/// Diagnostic log.
///
/// Exists because inspecting this UI from the outside (UI Automation, window
/// bitmaps) could not distinguish "the handler never ran" from "the handler ran
/// and decided not to act". The app reports its own state instead of me
/// inferring it.
///
/// On by default now, writing to %APPDATA%\StockClient\panel.log. It used to
/// require --uiprobe, which is useless for the one bug it exists to catch: the
/// panel vanishing during ordinary use, some unpredictable time after launch,
/// from a desktop shortcut nobody passes arguments to. A log you have to know in
/// advance you'll need is not a log. --uiprobe &lt;path&gt; still overrides the
/// destination.
/// </summary>
public static class Probe
{
    /// <summary>
    /// Roll at 8MB. The panel heartbeat is ~1 line per 5s, so a week of running
    /// stays well under this; the cap is only here so a runaway loop can't fill
    /// the disk of the machine we're trying to diagnose.
    /// </summary>
    private const long MaxBytes = 8L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    public static bool Enabled => _path is not null;

    public static string? Path => _path;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StockClient", "panel.log");

    /// <summary>
    /// Appends rather than truncates: the interesting run is the one that already
    /// happened, and a restart after the panel vanished must not erase it.
    /// </summary>
    public static void Enable(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Roll(path);
            _path = path;

            var version = typeof(Probe).Assembly.GetName().Version?.ToString() ?? "?";
            Log($"=== start pid={Environment.ProcessId} v{version} " +
                $"os={Environment.OSVersion.Version} {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
        catch
        {
            // If the log can't be opened, run without one. Refusing to start
            // because diagnostics failed would be worse than the bug.
            _path = null;
        }
    }

    private static void Roll(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;

            File.Move(path, path + ".old", overwrite: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    public static void Log(string message)
    {
        if (_path is null) return;

        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {message}\n");
            }
        }
        catch
        {
            // A diagnostic must never become the outage. This runs on the UI
            // thread once per heartbeat; letting an IO hiccup escape would
            // surface as the app's own crash dialog and bury the real cause.
        }
    }

    /// <summary>Who actually holds keyboard focus right now.</summary>
    public static string Focused()
    {
        var e = Keyboard.FocusedElement;
        if (e is null) return "null";

        var name = (e as FrameworkElement)?.Name;
        return string.IsNullOrEmpty(name) ? e.GetType().Name : $"{e.GetType().Name}#{name}";
    }
}
