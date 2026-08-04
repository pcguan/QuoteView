using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockClient.Core.Contracts;

/// <summary>
/// On-disk contract cache, laid out by exchange then trading date:
///
///   %APPDATA%\StockClient\contracts\{MARKET}\{yyyy-MM-dd}\symbols.json
///
/// Each market keeps its own dated folders because markets roll over to a new
/// trading date at different moments (US is ~12h offset from the A-share
/// markets), so one shared "today" folder would be wrong for someone.
/// </summary>
public sealed class ContractCache
{
    public const int RetainDays = 7;
    private const string FileName = "symbols.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        // Keep Chinese names as-is instead of \uXXXX escapes: the file stays
        // readable and is ~3x smaller.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public ContractCache(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient",
            "contracts");

    public string Root => _root;

    public string DirectoryFor(Market market, DateOnly date) =>
        Path.Combine(_root, market.ToString(), date.ToString("yyyy-MM-dd"));

    public string PathFor(Market market, DateOnly date) =>
        Path.Combine(DirectoryFor(market, date), FileName);

    /// <summary>Returns the cached list, or null when absent or unreadable.</summary>
    public SymbolFile? TryLoad(Market market, DateOnly date)
    {
        var path = PathFor(market, date);
        if (!File.Exists(path)) return null;

        try
        {
            var file = JsonSerializer.Deserialize<SymbolFile>(File.ReadAllText(path), Options);
            // An empty list is treated as absent so a bad fetch can't pin a
            // market to zero results for the rest of the day.
            return file?.Symbols is { Count: > 0 } ? file : null;
        }
        catch (Exception)
        {
            // Corrupt cache must not be fatal — refetching is cheap.
            return null;
        }
    }

    public void Save(Market market, DateOnly date, IReadOnlyList<Contract> contracts)
    {
        var file = new SymbolFile
        {
            Market = market.ToString(),
            TradingDate = date.ToString("yyyy-MM-dd"),
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Count = contracts.Count,
            Symbols = contracts,
        };

        var dir = DirectoryFor(market, date);
        Directory.CreateDirectory(dir);

        // Write-then-replace so a crash mid-write can't leave a truncated file.
        var path = PathFor(market, date);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Keeps only the newest <see cref="RetainDays"/> dated folders for a market.
    /// Ordering is by folder name, which sorts correctly because the names are
    /// zero-padded ISO dates.
    /// </summary>
    public void Prune(Market market)
    {
        var marketDir = Path.Combine(_root, market.ToString());
        if (!Directory.Exists(marketDir)) return;

        var dated = Directory.GetDirectories(marketDir)
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir)))
            .Where(x => DateOnly.TryParseExact(x.Name, "yyyy-MM-dd", out _))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var (dir, _) in dated.Skip(RetainDays))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                // Pruning is housekeeping; a locked folder must not break startup.
            }
        }
    }
}
