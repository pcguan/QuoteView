using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Groups;

public sealed class Group
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("codes")]
    public List<string> Codes { get; set; } = new();

    /// <summary>
    /// Whether this group takes part in the stealth panel's group cycling. The
    /// initializer (not a default JSON value) makes groups from older config files
    /// — which have no "panel" key — default to included.
    /// </summary>
    [JsonPropertyName("panel")]
    public bool InPanel { get; set; } = true;
}

/// <summary>
/// The chart shown above the stealth ticker's rows. Cycled by Win+Alt+Delete:
/// none → 分时 → 五档 → none. Serialized by ORDINAL like the other enums here, so
/// members may only be appended, never reordered. None must stay 0 — that is what
/// a config without the key reads as.
/// </summary>
public enum PanelChart
{
    None,

    /// <summary>Intraday sparkline of the current contract.</summary>
    Trend,

    /// <summary>Order book of the current contract, from the 1s quote itself.</summary>
    Depth,
}

/// <summary>Which quote fields the stealth ticker shows, and in what colour.</summary>
public enum StealthField
{
    Code,
    Name,
    Price,
    Change,
    Percent,
    Open,
    High,
    Low,
    Yesterday,
    Time,

    // Appended, never reordered: StealthFieldConfig.Field serializes by ordinal,
    // so inserting mid-list would shift every saved config. All from the 1s quote.
    Volume,
    Amount,
    TotalCap,
    FloatCap,
    TurnoverRate,
    VolumeRatio,
    Amplitude,
    AvgPrice,
    PeTtm,
    Pb,

    /// <summary>
    /// The active group's name, shown once at the left of the panel rather than
    /// per row. It lives in this enum so it gets the same visibility/colour
    /// controls as every other field — the alternative was a hard-coded colour
    /// nobody could change.
    /// </summary>
    GroupName,

    // Appended for parity with the main grid's columns (the panel's field list
    // must offer everything the table can show). Same append-only rule as above.
    PrevDay,
    Return3,
    Return5,
    Return10,
    Return20,
    Return60,
    ReturnYtd,
    Speed,
    MainInflow,
    MainInflowPct,
    SuperInflow,
    BigInflow,
    MidInflow,
    SmallInflow,
    OuterVolume,
    InnerVolume,
    LimitUp,
    LimitDown,
    Week52High,
    Week52Low,
    DividendYield,
    Industry,
    Region,
    Concepts,
    Note,
}

public static class StealthFields
{
    /// <summary>
    /// Fields that carry an up/down meaning, coloured by their sign (rise/fall)
    /// rather than a single fixed colour. Yesterday is the baseline, not a move,
    /// so it stays single-colour like the code/name/time labels.
    /// </summary>
    public static bool IsSigned(StealthField field) => field
        is StealthField.Price or StealthField.Change or StealthField.Percent
        or StealthField.Open or StealthField.High or StealthField.Low
        or StealthField.PrevDay or StealthField.Return3 or StealthField.Return5
        or StealthField.Return10 or StealthField.Return20 or StealthField.Return60
        or StealthField.ReturnYtd or StealthField.Speed
        or StealthField.MainInflow or StealthField.MainInflowPct
        or StealthField.SuperInflow or StealthField.BigInflow
        or StealthField.MidInflow or StealthField.SmallInflow;

    /// <summary>
    /// Fields served by the secondary EastMoney fund-flow poll (A-shares only).
    /// The poll is demand-driven, so showing one of these — in the grid or on the
    /// stealth panel — is what turns that request on.
    /// </summary>
    public static bool IsFundFlow(StealthField field) => field
        is StealthField.Speed or StealthField.MainInflow or StealthField.MainInflowPct
        or StealthField.SuperInflow or StealthField.BigInflow
        or StealthField.MidInflow or StealthField.SmallInflow;
}

public sealed class StealthFieldConfig
{
    [JsonPropertyName("field")]
    public StealthField Field { get; set; }

    [JsonPropertyName("visible")]
    public bool Visible { get; set; }

    /// <summary>Single colour for a field with no up/down meaning. Hex like #FFFFFF.</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>Colour when the field is up (positive). Red by the A-share convention.</summary>
    [JsonPropertyName("pos")]
    public string PositiveColor { get; set; } = "#EF5350";

    /// <summary>Colour when the field is down (negative). Green by the A-share convention.</summary>
    [JsonPropertyName("neg")]
    public string NegativeColor { get; set; } = "#26A69A";
}

public sealed class StealthConfig
{
    /// <summary>Upper bound on visible rows, so a huge value can't fill the screen.</summary>
    public const int MaxRows = 20;

    /// <summary>Upper bound on the pixel gap between rows.</summary>
    public const int MaxRowGap = 20;

    [JsonPropertyName("fields")]
    public List<StealthFieldConfig> Fields { get; set; } = new();

    /// <summary>0 = fully invisible, 10 = fully opaque.</summary>
    [JsonPropertyName("shade")]
    public int Shade { get; set; } = 7;

    /// <summary>
    /// How many contracts the panel shows, one per row, starting from the current
    /// one. 1 is the original single line. Capped at <see cref="MaxRows"/>.
    /// </summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 1;

    /// <summary>Vertical gap in pixels between rows when several are shown. 0 = tight.</summary>
    [JsonPropertyName("rowGap")]
    public int RowGap { get; set; }

    /// <summary>
    /// Pre-1.0.17 flag for the sparkline. Kept only so an existing config still
    /// opens with its chart on — <see cref="Chart"/> is what the panel reads now.
    /// It is written back in step with <see cref="Chart"/> so downgrading doesn't
    /// lose the setting either.
    /// </summary>
    [JsonPropertyName("trend")]
    public bool ShowTrend { get; set; }

    /// <summary>Which chart rides above the ticker rows. None by default.</summary>
    [JsonPropertyName("chart")]
    public PanelChart Chart { get; set; }

    /// <summary>Last position on screen. Null until the panel has been moved.</summary>
    [JsonPropertyName("left")]
    public double? Left { get; set; }

    [JsonPropertyName("top")]
    public double? Top { get; set; }

    public static StealthConfig CreateDefault() => new()
    {
        Fields =
        {
            new() { Field = StealthField.Code, Visible = true, Color = "#FFFFFF" },
            new() { Field = StealthField.Name, Visible = true, Color = "#FFFFFF" },
            new() { Field = StealthField.Percent, Visible = true, Color = "#EF5350" },
            new() { Field = StealthField.Price, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Change, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Open, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.High, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Low, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Yesterday, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Time, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Volume, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Amount, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.TotalCap, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.FloatCap, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.TurnoverRate, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.VolumeRatio, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Amplitude, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.AvgPrice, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.PeTtm, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Pb, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.GroupName, Visible = true, Color = "#8B93A3" },
            new() { Field = StealthField.PrevDay, Visible = false },
            new() { Field = StealthField.Return3, Visible = false },
            new() { Field = StealthField.Return5, Visible = false },
            new() { Field = StealthField.Return10, Visible = false },
            new() { Field = StealthField.Return20, Visible = false },
            new() { Field = StealthField.Return60, Visible = false },
            new() { Field = StealthField.ReturnYtd, Visible = false },
            new() { Field = StealthField.Speed, Visible = false },
            new() { Field = StealthField.MainInflow, Visible = false },
            new() { Field = StealthField.MainInflowPct, Visible = false },
            new() { Field = StealthField.SuperInflow, Visible = false },
            new() { Field = StealthField.BigInflow, Visible = false },
            new() { Field = StealthField.MidInflow, Visible = false },
            new() { Field = StealthField.SmallInflow, Visible = false },
            new() { Field = StealthField.OuterVolume, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.InnerVolume, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.LimitUp, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.LimitDown, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Week52High, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Week52Low, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.DividendYield, Visible = false, Color = "#FFFFFF" },
            new() { Field = StealthField.Industry, Visible = false, Color = "#8B93A3" },
            new() { Field = StealthField.Region, Visible = false, Color = "#8B93A3" },
            new() { Field = StealthField.Concepts, Visible = false, Color = "#8B93A3" },
            new() { Field = StealthField.Note, Visible = false, Color = "#8B93A3" },
        },
    };

    /// <summary>Adds any field missing from an older file, so upgrades don't lose entries.</summary>
    public StealthConfig Normalize()
    {
        Fields ??= new List<StealthFieldConfig>();

        foreach (var d in CreateDefault().Fields)
        {
            if (Fields.All(f => f.Field != d.Field)) Fields.Add(d);
        }

        Shade = Math.Clamp(Shade, 0, 10);
        Rows = Math.Clamp(Rows, 1, MaxRows);
        RowGap = Math.Clamp(RowGap, 0, MaxRowGap);

        // Config written before the chart became a three-way choice: carry the old
        // on/off flag over once, then keep the two in step.
        if (Chart == PanelChart.None && ShowTrend) Chart = PanelChart.Trend;
        ShowTrend = Chart == PanelChart.Trend;

        return this;
    }
}

/// <summary>
/// Persisted layout of one live-quote grid column. Keyed by header text so it
/// survives across launches; the delete-button column has no header and isn't
/// stored, staying pinned last.
/// </summary>
public sealed class QuoteColumnState
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>DisplayIndex — the left-to-right position.</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>Width in pixels.</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("visible")]
    public bool Visible { get; set; } = true;
}

/// <summary>
/// The account-synced slice of <see cref="GroupConfig"/>: everything that is a
/// personal preference (field visibility/colours, column layout, notes, the
/// aggregate mode, pane width) and nothing that is structural (groups live in
/// their own sync channel; the active group is a per-machine cursor).
/// </summary>
public sealed class SettingsPayload
{
    [JsonPropertyName("stealth")]
    public StealthConfig? Stealth { get; set; }

    [JsonPropertyName("quoteColumns")]
    public List<QuoteColumnState>? QuoteColumns { get; set; }

    [JsonPropertyName("notes")]
    public Dictionary<string, string>? Notes { get; set; }

    [JsonPropertyName("aggEqual")]
    public bool AggEqualWeight { get; set; }

    [JsonPropertyName("paneWidth")]
    public double GroupPaneWidth { get; set; }

    /// <summary>Change stamp of this payload, unix seconds. Newer wins.</summary>
    [JsonPropertyName("at")]
    public long At { get; set; }

    public static SettingsPayload From(GroupConfig config) => new()
    {
        Stealth = config.Stealth,
        QuoteColumns = config.QuoteColumns,
        Notes = config.Notes,
        AggEqualWeight = config.AggEqualWeight,
        GroupPaneWidth = config.GroupPaneWidth,
        At = config.PrefsUpdatedAt,
    };

    /// <summary>Overwrites the preference slice of <paramref name="config"/>.</summary>
    public void ApplyTo(GroupConfig config)
    {
        if (Stealth is not null) config.Stealth = Stealth;
        if (QuoteColumns is not null) config.QuoteColumns = QuoteColumns;
        if (Notes is not null)
            config.Notes = new Dictionary<string, string>(Notes, StringComparer.OrdinalIgnoreCase);
        config.AggEqualWeight = AggEqualWeight;
        config.GroupPaneWidth = GroupPaneWidth;
        config.PrefsUpdatedAt = At;
    }
}

public sealed class GroupConfig
{
    [JsonPropertyName("stealth")]
    public StealthConfig Stealth { get; set; } = StealthConfig.CreateDefault();

    [JsonPropertyName("groups")]
    public List<Group> Groups { get; set; } = new();

    /// <summary>The one group being polled. Exactly one, or none when empty.</summary>
    [JsonPropertyName("activeGroupId")]
    public string? ActiveGroupId { get; set; }

    /// <summary>
    /// When the preference slice (SettingsPayload) last changed, unix seconds.
    /// The tiebreaker for account-settings sync: at startup the server copy is
    /// applied only when it is NOT older than this — without it, a layout tweak
    /// made while signed out was silently rolled back by the next start's pull.
    /// </summary>
    [JsonPropertyName("prefsAt")]
    public long PrefsUpdatedAt { get; set; }

    /// <summary>
    /// The account this local file belongs to. Null = pre-account data, adopted
    /// by whoever signs in first; a DIFFERENT signed-in user triggers a restore
    /// from the server instead, so accounts never leak groups into each other.
    /// </summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    /// <summary>
    /// How each group's aggregate move (整体涨跌幅) is weighted. False = free-float
    /// cap weighted at yesterday's close, the CSI-index method; true = equal
    /// weight, "the average performance of these contracts".
    /// </summary>
    [JsonPropertyName("aggEqual")]
    public bool AggEqualWeight { get; set; }

    /// <summary>Width of the group sidebar, dragged via its splitter. 0 = default.</summary>
    [JsonPropertyName("paneWidth")]
    public double GroupPaneWidth { get; set; }

    /// <summary>Live-quote column layout. Empty until the user first customises it.</summary>
    [JsonPropertyName("quoteColumns")]
    public List<QuoteColumnState> QuoteColumns { get; set; } = new();

    /// <summary>
    /// Free-text notes, keyed by contract code — deliberately NOT per group.
    /// A note is about the contract itself ("等回踩 40 加"), so the same holding
    /// appearing in 半导体 and 自选 must show the same note; storing it on the group
    /// would silently fork into two copies the moment a contract is in two groups.
    ///
    /// Local only: it lives in groups.json and goes nowhere else.
    /// </summary>
    [JsonPropertyName("notes")]
    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static GroupConfig CreateDefault() => new()
    {
        Groups = { new Group { Id = "default", Name = "自选股", Codes = { "SH600519", "SZ000651", "HK00700", "USAAPL", "KR005930" } } },
        ActiveGroupId = "default",
        Stealth = StealthConfig.CreateDefault(),
    };
}

/// <summary>Reads/writes groups.json under %APPDATA%\StockClient.</summary>
public sealed class GroupStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keep Chinese group names readable instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public GroupStore(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "groups.json");

    public string FilePath => _path;

    /// <summary>
    /// Applies an account's server-side settings onto the stored config file.
    /// Runs at startup BEFORE the view model loads, so every view simply reads
    /// the merged result — no live re-apply plumbing anywhere.
    /// </summary>
    public void MergeSettings(string settingsJson)
    {
        SettingsPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SettingsPayload>(settingsJson);
        }
        catch (JsonException)
        {
            return;   // a bad blob must never break startup
        }
        if (payload is null) return;

        var config = Load();

        // Last write wins: a local change made while signed out or offline
        // carries a newer stamp than the server copy, and must survive the
        // next start's pull. Equal stamps apply (covers fresh installs and
        // pre-stamp payloads, both at 0).
        if (payload.At < config.PrefsUpdatedAt) return;

        payload.ApplyTo(config);
        config.Stealth = config.Stealth.Normalize();
        Save(config);
    }

    public GroupConfig Load()
    {
        if (!File.Exists(_path)) return GroupConfig.CreateDefault();

        try
        {
            var config = JsonSerializer.Deserialize<GroupConfig>(File.ReadAllText(_path), Options);
            return config is null ? GroupConfig.CreateDefault() : Normalize(config);
        }
        catch (Exception)
        {
            // A corrupt file must not brick startup.
            TryBackup();
            return GroupConfig.CreateDefault();
        }
    }

    /// <summary>Enforces the "exactly one active group" rule on load.</summary>
    private static GroupConfig Normalize(GroupConfig config)
    {
        config.Groups ??= new List<Group>();

        // Deserialization builds a case-sensitive dictionary; codes are compared
        // case-insensitively everywhere else, so rebuild it that way.
        config.Notes = config.Notes is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(config.Notes, StringComparer.OrdinalIgnoreCase);
        config.Groups.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Id));

        foreach (var group in config.Groups)
        {
            group.Codes ??= new List<string>();
            group.Codes.RemoveAll(string.IsNullOrWhiteSpace);
        }

        if (config.ActiveGroupId is null || config.Groups.All(g => g.Id != config.ActiveGroupId))
            config.ActiveGroupId = config.Groups.FirstOrDefault()?.Id;

        config.Stealth = (config.Stealth ?? StealthConfig.CreateDefault()).Normalize();
        config.QuoteColumns ??= new List<QuoteColumnState>();

        return config;
    }

    private void TryBackup()
    {
        try
        {
            File.Move(_path, $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}");
        }
        catch
        {
            // Best-effort.
        }
    }

    public void Save(GroupConfig config)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-replace so a crash mid-write can't truncate the real file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Writes just the groups (names + codes + which is active) to a file the user
    /// picks — deliberately NOT the whole config, so an export/import round-trip
    /// moves watchlists between machines without dragging along local-only prefs
    /// (stealth panel, column layout).
    /// </summary>
    public static void ExportGroups(string path, IEnumerable<Group> groups, string? activeGroupId)
    {
        var payload = new GroupsExport
        {
            Groups = groups.ToList(),
            ActiveGroupId = activeGroupId,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>Reads an exported file, cleaning out empty groups/codes. Throws on garbage.</summary>
    public static GroupsExport ImportGroups(string path)
    {
        var payload = JsonSerializer.Deserialize<GroupsExport>(File.ReadAllText(path), Options)
                      ?? throw new InvalidDataException("文件内容无法解析为分组");

        payload.Groups ??= new List<Group>();
        payload.Groups.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Id));
        foreach (var group in payload.Groups)
        {
            group.Codes ??= new List<string>();
            group.Codes.RemoveAll(string.IsNullOrWhiteSpace);
        }

        if (payload.Groups.Count == 0)
            throw new InvalidDataException("文件里没有有效分组");

        return payload;
    }
}

/// <summary>The portable subset of the config: groups and which one is active.</summary>
public sealed class GroupsExport
{
    [JsonPropertyName("groups")]
    public List<Group> Groups { get; set; } = new();

    [JsonPropertyName("activeGroupId")]
    public string? ActiveGroupId { get; set; }
}
