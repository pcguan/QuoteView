using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Quotes;

/// <summary>
/// One contract's settled daily closes as 昨日涨幅 last saw them, plus the
/// freshness mark of the fetch that produced them (trading-date stamp + whether
/// it ran after the close — the same two-fetches-a-day rule the kline cache uses).
/// </summary>
public sealed record DailyCloseEntry
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Trading date (market-local) of the fetch, yyyy-MM-dd.</summary>
    [JsonPropertyName("stamp")]
    public required string Stamp { get; init; }

    [JsonPropertyName("settled")]
    public bool Settled { get; init; }

    [JsonPropertyName("candles")]
    public required IReadOnlyList<DailyClose> Candles { get; init; }
}

public sealed record DailyClose
{
    [JsonPropertyName("d")]
    public required string Date { get; init; }

    [JsonPropertyName("c")]
    public required double Close { get; init; }
}

/// <summary>
/// On-disk copy of the per-contract daily closes, so a restart shows 昨日涨幅
/// instantly instead of re-crawling every contract — settled candles are
/// immutable, only the freshness marks decide when to refetch.
///
///   %APPDATA%\StockClient\returns\daily.json
/// </summary>
public sealed class DailyCloseCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public DailyCloseCache(string? path = null) =>
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "returns", "daily.json");

    public Dictionary<string, DailyCloseEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);

            var items = JsonSerializer.Deserialize<List<DailyCloseEntry>>(
                File.ReadAllText(_path), Options);
            return (items ?? new())
                .Where(i => !string.IsNullOrEmpty(i.Code))
                .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, DailyCloseEntry> items)
    {
        if (items.Count == 0) return;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.Values.ToList(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort: a failed write just means a re-crawl next start.
        }
    }
}

/// <summary>
/// On-disk copy of the last fund-flow extras per contract (主力/超大单… and 涨速).
/// After the close these ARE the day's final numbers, so a group opened in the
/// evening shows them immediately instead of blank-until-the-poll — and when
/// EastMoney is throttled they keep the columns honest with the latest close.
///
///   %APPDATA%\StockClient\returns\extras.json
/// </summary>
public sealed class ExtraCache
{
    private const int MaxEntries = 2000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public ExtraCache(string? path = null) =>
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "returns", "extras.json");

    public Dictionary<string, QuoteExtra> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);

            var items = JsonSerializer.Deserialize<List<QuoteExtra>>(
                File.ReadAllText(_path), Options);
            return (items ?? new())
                .Where(i => !string.IsNullOrEmpty(i.Code))
                .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IReadOnlyDictionary<string, QuoteExtra> items)
    {
        if (items.Count == 0) return;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                items.Values.Take(MaxEntries).ToList(), Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
