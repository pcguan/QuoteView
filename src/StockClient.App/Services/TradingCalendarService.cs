using System.IO;
using System.Net.Http;
using System.Text.Json;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.Services;

/// <summary>
/// Keeps <see cref="TradingCalendar"/> fed: a copy cached on disk for an instant,
/// offline-safe start, refreshed from 深交所 each launch over a rolling window
/// around today so upcoming holidays are known ahead of time. Read once per launch
/// — the calendar barely changes.
/// </summary>
public static class TradingCalendarService
{
    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StockClient", "trading-calendar.json");

    private sealed record Doc(string From, string To, List<string> Days);

    /// <summary>Load the cached calendar into TradingCalendar (fast, offline-safe).</summary>
    public static void LoadCached()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(CacheFile));
            if (doc is null
                || !DateOnly.TryParse(doc.From, out var from)
                || !DateOnly.TryParse(doc.To, out var to)) return;

            var days = doc.Days
                .Select(s => DateOnly.TryParse(s, out var d) ? d : (DateOnly?)null)
                .OfType<DateOnly>();
            TradingCalendar.Load(days, from, to);
        }
        catch
        {
            // A corrupt cache just leaves the weekday fallback in place.
        }
    }

    /// <summary>Fetch a rolling window (≈6 months back … 2 ahead) from 深交所 and
    /// refresh both TradingCalendar and the disk cache. Silent on failure — the
    /// cached copy (or the weekday fallback) stands.</summary>
    public static async Task RefreshAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            var monthStart = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
            var first = monthStart.AddMonths(-6);
            var lastMonth = monthStart.AddMonths(2);
            var to = lastMonth.AddMonths(1).AddDays(-1);   // last day of the final month

            var days = await new SzseCalendarClient(http).GetAsync(first, lastMonth, ct);
            if (days is null || days.Count == 0) return;

            TradingCalendar.Load(days, first, to);

            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var doc = new Doc(
                first.ToString("yyyy-MM-dd"),
                to.ToString("yyyy-MM-dd"),
                days.Select(d => d.ToString("yyyy-MM-dd")).ToList());
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(doc));
        }
        catch
        {
            // Keep whatever's already loaded.
        }
    }
}
