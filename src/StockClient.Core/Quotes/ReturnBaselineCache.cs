using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockClient.Core.Quotes;

/// <summary>
/// On-disk baselines, one file for everything:
///
///   %APPDATA%\StockClient\returns\baselines.json
///
/// Not keyed by date, because entries carry their own trading date — markets roll
/// over at different times, so a single "today" would be wrong for half of them.
/// Each contract is refreshed when ITS market has moved on.
///
/// Replacing an entry carries the old previous-close forward as
/// <see cref="ReturnBaselines.PriorClose"/>, which is what yields yesterday's
/// return without a second request.
/// </summary>
public sealed class ReturnBaselineCache
{
    /// <summary>
    /// Chain-format version, carried in the day record's otherwise-unused Date
    /// slot. v2 = value-keyed chain (prev/prior pairs verified against idle
    /// feed snapshots). Older files predate that and may hold mispaired
    /// entries — e.g. Friday's close stored as Friday's "previous close" —
    /// so they are dropped wholesale and rebuilt within a session.
    /// </summary>
    private const string FormatVersion = "v2";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public ReturnBaselineCache(string? path = null) =>
        // Fully qualified: the property below is named FilePath precisely so this
        // type doesn't shadow System.IO.Path, but the ctor runs before that helps.
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "returns", "baselines.json");

    public string FilePath => _path;

    public Dictionary<string, ReturnBaselines> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);

            var day = JsonSerializer.Deserialize<ReturnBaselineDay>(File.ReadAllText(_path), Options);
            if (day?.Items is not { Count: > 0 } || day.Date != FormatVersion)
                return new(StringComparer.OrdinalIgnoreCase);

            return day.Items
                .Where(i => !string.IsNullOrEmpty(i.Code))
                .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, ReturnBaselines> items)
    {
        if (items.Count == 0) return;

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        var day = new ReturnBaselineDay { Date = FormatVersion, Items = items.Values.ToArray() };

        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(day, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort: a failed write just means it gets fetched again.
        }
    }
}
