namespace StockClient.Core.Brief;

/// <summary>
/// Finds the briefs the pipeline dropped on this machine.
///
///   %APPDATA%\StockClient\brief\brief-YYYYMMDD.json
///
/// Read-only, and offline: the pipeline runs elsewhere and copies its output in.
/// If nothing is there the view says so — it never falls back to fetching or
/// generating anything itself, which would defeat the point of a pipeline whose
/// every number is traceable to a file it wrote.
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
}
