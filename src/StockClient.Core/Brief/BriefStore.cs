namespace StockClient.Core.Brief;

/// <summary>
/// Finds the briefs the pipeline dropped on this machine.
///
///   %APPDATA%\StockClient\brief\brief-YYYYMMDD.json
///
/// This is the local CACHE of what <see cref="BriefClient"/> downloaded, not the
/// source of truth — the pipeline publishes to the NAS and every client fetches
/// from there. Keeping a copy means a day already read stays readable offline.
///
/// Nothing here generates or derives anything; it stores and reads bytes.
/// </summary>
public sealed class BriefStore
{
    private readonly string _root;

    public BriefStore(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient",
            "brief");

    public string Root => _root;

    /// <summary>Trading days available locally, newest first.</summary>
    public IReadOnlyList<string> AvailableDays()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();

        try
        {
            return Directory.GetFiles(_root, "brief-*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is { Length: 14 })
                .Select(name => name!["brief-".Length..])
                .Where(day => day.Length == 8 && day.All(char.IsDigit))
                .OrderByDescending(day => day, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Loads one day, or null when absent/unreadable/malformed.</summary>
    public DailyBrief? Load(string day)
    {
        var path = Path.Combine(_root, $"brief-{day}.json");

        try
        {
            return File.Exists(path) ? DailyBrief.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The newest brief present, or null when there are none.</summary>
    public DailyBrief? Latest() =>
        AvailableDays() is [var newest, ..] ? Load(newest) : null;

    /// <summary>Caches a downloaded brief verbatim. Best effort — a failed write just means it refetches.</summary>
    public void Save(string day, string json)
    {
        try
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, $"brief-{day}.json");
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            Prune();
        }
        catch (Exception)
        {
            // Cache only.
        }
    }

    /// <summary>Keeps the newest <see cref="RetainDays"/> days.</summary>
    public void Prune()
    {
        foreach (var day in AvailableDays().Skip(RetainDays))
        {
            try
            {
                File.Delete(Path.Combine(_root, $"brief-{day}.json"));
            }
            catch (Exception)
            {
                // Housekeeping only.
            }
        }
    }

    public const int RetainDays = 30;
}
