using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockClient.Core.Quotes;

/// <summary>
/// On-disk K-line cache, laid out by contract then period/adjust then trading date:
///
///   %APPDATA%\StockClient\klines\{CODE}\{period}_{adjust}\{yyyy-MM-dd}.json
///
/// Same "once per trading date" model as <see cref="Contracts.ContractCache"/>.
/// A whole day's full history is fetched at most once per contract; every later
/// open that day reads the file. Because the cache is keyed by trading date, the
/// front-adjustment recompute problem takes care of itself: a new trading date
/// misses the cache and refetches, so an ex-rights day is picked up on its first
/// open with no special detection.
///
/// The date is passed in (computed from the market's own clock by the caller), so
/// this stays a plain file store.
/// </summary>
public sealed class KlineCache
{
    public const int RetainDays = 7;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public KlineCache(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient",
            "klines");

    public string Root => _root;

    private string DirectoryFor(string code, KlinePeriod period, KlineAdjust adjust) =>
        Path.Combine(_root, Safe(code), $"{period}_{adjust}".ToLowerInvariant());

    private string PathFor(string code, KlinePeriod period, KlineAdjust adjust, DateOnly date) =>
        Path.Combine(DirectoryFor(code, period, adjust), date.ToString("yyyy-MM-dd") + ".json");

    /// <summary>Cached series for the date, or null when absent or unreadable.</summary>
    public KlineSeries? TryLoad(string code, KlinePeriod period, KlineAdjust adjust, DateOnly date)
    {
        var path = PathFor(code, period, adjust, date);
        if (!File.Exists(path)) return null;

        try
        {
            var series = JsonSerializer.Deserialize<KlineSeries>(File.ReadAllText(path), Options);
            // Empty treated as absent, so a bad save can't pin a chart to zero for the day.
            return series?.Candles is { Count: > 0 } ? series : null;
        }
        catch (Exception)
        {
            // Corrupt cache must not be fatal — refetching is the fallback.
            return null;
        }
    }

    public void Save(KlineSeries series, DateOnly date)
    {
        if (series.Candles.Count == 0) return;

        var dir = DirectoryFor(series.Code, series.Period, series.Adjust);
        Directory.CreateDirectory(dir);

        var path = PathFor(series.Code, series.Period, series.Adjust, date);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(series, Options));
        File.Move(tmp, path, overwrite: true);

        Prune(series.Code, series.Period, series.Adjust);
    }

    /// <summary>Keeps only the newest <see cref="RetainDays"/> dated files for one series.</summary>
    public void Prune(string code, KlinePeriod period, KlineAdjust adjust)
    {
        var dir = DirectoryFor(code, period, adjust);
        if (!Directory.Exists(dir)) return;

        var dated = Directory.GetFiles(dir, "*.json")
            .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)))
            .Where(x => DateOnly.TryParseExact(x.Name, "yyyy-MM-dd", out _))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var (file, _) in dated.Skip(RetainDays))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // Housekeeping only.
            }
        }
    }

    /// <summary>Codes are already normalized (SH600519), but guard the path anyway.</summary>
    private static string Safe(string code) =>
        string.Concat(code.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
}
