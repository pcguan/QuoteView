using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockClient.Core.Quotes;

/// <summary>
/// On-disk cache for a settled day's intraday trend:
///
///   %APPDATA%\StockClient\trends\{CODE}\{yyyy-MM-dd}.json
///
/// <b>Only ever written after the close.</b> During the session the series keeps
/// growing, and neither source can be asked for "just the new minutes" — both
/// return the whole day — so caching a partial day to disk would save nothing and
/// risk serving a truncated line. After the close the day is final, and re-opening
/// the app costs zero requests for it.
///
/// A file existing therefore means "settled", which is what lets the repository
/// serve it without checking anything else. Same date-keyed, N-day-retained shape
/// as <see cref="KlineCache"/>.
/// </summary>
public sealed class TrendCache
{
    public const int RetainDays = 7;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public TrendCache(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient",
            "trends");

    public string Root => _root;

    private string DirectoryFor(string code) => Path.Combine(_root, Safe(code));

    private string PathFor(string code, DateOnly date) =>
        Path.Combine(DirectoryFor(code), date.ToString("yyyy-MM-dd") + ".json");

    /// <summary>The settled series for that date, or null when absent or unreadable.</summary>
    public TrendSeries? TryLoad(string code, DateOnly date)
    {
        var path = PathFor(code, date);
        if (!File.Exists(path)) return null;

        try
        {
            var series = JsonSerializer.Deserialize<TrendSeries>(File.ReadAllText(path), Options);
            // Empty counts as absent, so a bad write can't pin the chart to blank.
            return series?.Points is { Count: > 0 } ? series : null;
        }
        catch (Exception)
        {
            // A corrupt cache must not be fatal — refetching is the fallback.
            return null;
        }
    }

    public void Save(TrendSeries series, DateOnly date)
    {
        if (series.Points.Count == 0) return;

        var dir = DirectoryFor(series.Code);
        Directory.CreateDirectory(dir);

        var path = PathFor(series.Code, date);
        var tmp = path + ".tmp";

        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(series, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Best-effort: a failed write just means it gets fetched again.
            return;
        }

        Prune(series.Code);
    }

    /// <summary>Snapshot dates on disk for one contract, newest first.</summary>
    public IReadOnlyList<DateOnly> Dates(string code)
    {
        var dir = DirectoryFor(code);
        if (!Directory.Exists(dir)) return Array.Empty<DateOnly>();

        return Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => DateOnly.TryParseExact(n, "yyyy-MM-dd", out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>()
            .OrderByDescending(d => d)
            .ToArray();
    }

    /// <summary>Keeps only the newest <see cref="RetainDays"/> dated files for one contract.</summary>
    public void Prune(string code)
    {
        var dir = DirectoryFor(code);
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

    private static string Safe(string code) =>
        string.Concat(code.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
}
