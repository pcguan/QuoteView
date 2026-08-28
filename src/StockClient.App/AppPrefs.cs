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
        int PanelShadeRestore = 0, bool UpdateToast = true, string AutoUpdateMode = "");

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

    public const string AutoSilent = "silent";
    public const string AutoInstant = "instant";
    public const string AutoOff = "off";

    /// <summary>
    /// 自动更新的三档模式: silent (default) applies once the machine has been
    /// input-idle long enough; instant applies the moment a release is found;
    /// off never auto-applies (bar/toast still prompt). Migrates from the old
    /// AutoUpdate bool the first time it is read.
    /// </summary>
    public static string AutoUpdateMode
    {
        get => _doc.AutoUpdateMode is AutoSilent or AutoInstant or AutoOff
            ? _doc.AutoUpdateMode
            : _doc.AutoUpdate ? AutoSilent : AutoOff;
        set { if (AutoUpdateMode == value) return; _doc = _doc with { AutoUpdateMode = value }; Save(); }
    }

    /// <summary>Desktop toast when a new release is found (default on) — the
    /// in-app update bar shows regardless; this is the one visible from the
    /// desktop / stealth mode. Toggle lives in 系统设置.</summary>
    public static bool UpdateToast
    {
        get => _doc.UpdateToast;
        set { if (_doc.UpdateToast == value) return; _doc = _doc with { UpdateToast = value }; Save(); }
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
