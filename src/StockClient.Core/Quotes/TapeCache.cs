using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockClient.Core.Quotes;

/// <summary>
/// On-disk cache for TODAY's live 逐笔 tape, per contract:
///
///   %APPDATA%\StockClient\ticks\{CODE}\{yyyy-MM-dd}.json
///
/// Unlike <see cref="TrendCache"/> (which only persists a settled day), this DOES
/// write the still-running day. The point is resilience: reopening a chart,
/// switching contracts, or a blocked/failed details request should fall back to
/// today's accumulated tape instead of a blank window. The view model seeds its
/// <see cref="TapeAccumulator"/> from here on open and writes the merged tape back
/// as it polls (throttled). Files are keyed by whole trading day, so a new day is a
/// new file and a stale day is simply never loaded; only today's file is read, and
/// old ones are pruned.
/// </summary>
public sealed class TapeCache
{
    public const int RetainDays = 3;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public TapeCache(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient",
            "ticks");

    private string DirectoryFor(string code) => Path.Combine(_root, Safe(code));

    private string PathFor(string code, DateOnly date) =>
        Path.Combine(DirectoryFor(code), date.ToString("yyyy-MM-dd") + ".json");

    /// <summary>Today's cached tape for a contract, or null when absent, unreadable,
    /// or empty (empty counts as absent so a bad write can't pin the tape to blank).</summary>
    public TradeTickSnapshot? TryLoad(string code, DateOnly date)
    {
        var path = PathFor(code, date);
        if (!File.Exists(path)) return null;

        try
        {
            var snap = JsonSerializer.Deserialize<TradeTickSnapshot>(File.ReadAllText(path), Options);
            return snap?.Ticks is { Count: > 0 } ? snap : null;
        }
        catch (Exception)
        {
            // A corrupt cache must not be fatal — the live poll refills it.
            return null;
        }
    }

    public void Save(string code, DateOnly date, TradeTickSnapshot snapshot)
    {
        if (snapshot.Ticks.Count == 0) return;

        var dir = DirectoryFor(code);
        Directory.CreateDirectory(dir);

        var path = PathFor(code, date);
        var tmp = path + ".tmp";

        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Best-effort: a failed write just means the tape is re-fetched.
            return;
        }

        Prune(code);
    }

    /// <summary>Keeps only the newest <see cref="RetainDays"/> dated files per contract.</summary>
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
