using System.Text.Encodings.Web;
using System.Text.Json;

namespace StockClient.Core.Boards;

/// <summary>
/// On-disk board catalog cache, one file per trading date:
///
///   %APPDATA%\StockClient\boards\{yyyy-MM-dd}\boards.json
///
/// Boards are an A-share-wide concept (not per-market), so — unlike the contract
/// cache — there is a single dated folder rather than one per exchange. Keyed by
/// the SH trading date.
/// </summary>
public sealed class BoardCache
{
    public const int RetainDays = 7;
    private const string FileName = "boards.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _root;

    public BoardCache(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient",
            "boards");

    public string Root => _root;

    public string DirectoryFor(DateOnly date) =>
        Path.Combine(_root, date.ToString("yyyy-MM-dd"));

    public string PathFor(DateOnly date) =>
        Path.Combine(DirectoryFor(date), FileName);

    /// <summary>Returns the cached catalog, or null when absent or unreadable.</summary>
    public BoardFile? TryLoad(DateOnly date)
    {
        var path = PathFor(date);
        if (!File.Exists(path)) return null;

        try
        {
            var file = JsonSerializer.Deserialize<BoardFile>(File.ReadAllText(path), Options);
            // Empty is treated as absent so a bad fetch can't pin the day to zero.
            return file?.Boards is { Count: > 0 } ? file : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(DateOnly date, IReadOnlyList<Board> boards)
    {
        var file = new BoardFile
        {
            TradingDate = date.ToString("yyyy-MM-dd"),
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Count = boards.Count,
            Boards = boards,
        };

        var dir = DirectoryFor(date);
        Directory.CreateDirectory(dir);

        var path = PathFor(date);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Keeps only the newest <see cref="RetainDays"/> dated folders.</summary>
    public void Prune()
    {
        if (!Directory.Exists(_root)) return;

        var dated = Directory.GetDirectories(_root)
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
                // Housekeeping only.
            }
        }
    }
}
