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
        int PanelShadeRestore = 0, bool UpdateToast = true, string AutoUpdateMode = "",
        string ProxyMode = "", string ProxyAddress = "", string ApiBase = "",
        int UpdateDelayHours = 0, bool StealthIntroShown = false,
        int BigTradeWan = 100, double WindowOpacity = 1.0);

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

    /// <summary>
    /// Overrides the account/API endpoint. Empty = the public domain. Set this
    /// to a LAN URL (e.g. http://192.168.x.x:8388/quoteview/api, or the direct
    /// container http://&lt;NAS&gt;:8388) to keep working when the domain or CDN
    /// is down. Applies to the account API, /kline proxy and /krdaily.
    /// </summary>
    public static string ApiBase
    {
        get => _doc.ApiBase;
        set { var v = (value ?? "").Trim(); if (_doc.ApiBase == v) return; _doc = _doc with { ApiBase = v }; Save(); }
    }

    /// <summary>Hours to defer an available auto-update. 0 = update as soon as
    /// the mode allows (canary machines). A few hours lets a canary catch a bad
    /// release before the rest of the fleet installs it.</summary>
    public static int UpdateDelayHours
    {
        get => Math.Clamp(_doc.UpdateDelayHours, 0, 72);
        set { var v = Math.Clamp(value, 0, 72); if (_doc.UpdateDelayHours == v) return; _doc = _doc with { UpdateDelayHours = v }; Save(); }
    }

    /// <summary>The one-time stealth-panel gesture intro balloon has been shown.</summary>
    /// <summary>所有普通窗口的透明度(Shift+滚轮调),记住并跨窗口共享。0.2..1.0。</summary>
    public static double WindowOpacity
    {
        get => Math.Clamp(_doc.WindowOpacity <= 0 ? 1.0 : _doc.WindowOpacity, 0.2, 1.0);
        set
        {
            var v = Math.Clamp(value, 0.2, 1.0);
            if (Math.Abs(_doc.WindowOpacity - v) < 1e-6) return;
            _doc = _doc with { WindowOpacity = v };
            Save();
        }
    }

    /// <summary>成交明细里认定「大单」的成交额门槛,单位万元;0 = 不高亮。1..99999 万。</summary>
    public static int BigTradeWan
    {
        get => _doc.BigTradeWan;
        set
        {
            var v = Math.Clamp(value, 0, 99999);
            if (_doc.BigTradeWan == v) return;
            _doc = _doc with { BigTradeWan = v };
            Save();
        }
    }

    public static bool StealthIntroShown
    {
        get => _doc.StealthIntroShown;
        set { if (_doc.StealthIntroShown == value) return; _doc = _doc with { StealthIntroShown = value }; Save(); }
    }

    public const string ProxyOff = "off";
    public const string ProxySystem = "system";
    public const string ProxyManual = "manual";

    /// <summary>
    /// 网络代理三档: off (default) forces direct connections — every endpoint
    /// is domestic and a process-cached system proxy that outlives the proxy
    /// program black-holes the whole app (measured 2026-09-01); system uses
    /// whatever Windows has configured; manual uses <see cref="ProxyAddress"/>.
    /// HttpClients are built once at startup, so changes apply on restart
    /// (the presence websocket reconnects pick them up live).
    /// </summary>
    public static string ProxyMode
    {
        get => _doc.ProxyMode is ProxySystem or ProxyManual ? _doc.ProxyMode : ProxyOff;
        set { if (ProxyMode == value) return; _doc = _doc with { ProxyMode = value }; Save(); }
    }

    /// <summary>Manual proxy, "host:port" (scheme optional, http assumed).</summary>
    public static string ProxyAddress
    {
        get => _doc.ProxyAddress;
        set { if (_doc.ProxyAddress == value) return; _doc = _doc with { ProxyAddress = value }; Save(); }
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
