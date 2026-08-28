using System.IO;
using System.Text.Json;

namespace StockClient.App;

/// <summary>
/// Machine-level app preferences — %APPDATA%\StockClient\app.json. Deliberately
/// outside the profile stores (groups.json / offline.json): these follow the
/// installation, not the signed-in user or the offline profile.
/// </summary>
public static class AppPrefs
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StockClient", "app.json");

    private sealed record Doc(bool AutoUpdate = true, bool PanelOpen = false,
        int PanelShadeRestore = 0);

    private static Doc _doc = Load();

    private static Doc Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Doc>(System.IO.File.ReadAllText(File)) ?? new Doc();
        }
        catch
        {
            // Unreadable prefs = defaults; nothing here is worth failing startup for.
        }
        return new Doc();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(_doc));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>静默自动更新 (default on): apply a pending release once the
    /// machine has been input-idle long enough. The update bar still shows for
    /// manual updating either way.</summary>
    public static bool AutoUpdate
    {
        get => _doc.AutoUpdate;
        set { if (_doc.AutoUpdate == value) return; _doc = _doc with { AutoUpdate = value }; Save(); }
    }

    /// <summary>The shade the panel had before a double-click blackout, so a
    /// second double-click can restore it — remembered across restarts (the
    /// panel may well be reopened invisible). 0 = nothing pending.</summary>
    public static int PanelShadeRestore
    {
        get => _doc.PanelShadeRestore;
        set { if (_doc.PanelShadeRestore == value) return; _doc = _doc with { PanelShadeRestore = value }; Save(); }
    }

    /// <summary>Whether the stealth panel was up when the app last ran, so a
    /// restart (auto-update included) restores it instead of leaving a bare
    /// minimized window where the ticker used to be.</summary>
    public static bool PanelOpen
    {
        get => _doc.PanelOpen;
        set { if (_doc.PanelOpen == value) return; _doc = _doc with { PanelOpen = value }; Save(); }
    }
}
