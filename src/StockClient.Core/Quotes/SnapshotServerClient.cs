using System.Text;
using System.Text.Json;

namespace StockClient.Core.Quotes;

/// <summary>
/// Talks to the QuoteView snapshot server (the NAS container behind the same
/// host that serves updates and briefs):
///
///   POST /register            -> { "id": "…" }            once, persisted
///   POST /sync                -> groups+codes, every 5 min
///   GET  /dates?code=         -> { "dates": ["yyyy-MM-dd", …] }
///   GET  /trend?code=&date=   -> TrendSeries, the client's own JSON shape
///
/// The server fetches the after-close snapshots for the union of every client's
/// SH/SZ contracts, so no client fetches history from the exchanges itself.
/// Everything here is best-effort: the app must behave identically with the
/// server unreachable, minus the history that only it has.
/// </summary>
public sealed class SnapshotServerClient
{
    private const string Base = "https://nas.pcguan.cn/quoteview/api";

    private readonly HttpClient _http;
    private readonly string _idPath;
    private string? _id;

    public SnapshotServerClient(HttpClient http, string? idPath = null)
    {
        _http = http;
        _idPath = idPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "server-id.txt");
    }

    /// <summary>
    /// The persistent client id — read from disk, or negotiated once and saved.
    /// Null when the server can't be reached; callers just try again later.
    /// </summary>
    public async Task<string?> EnsureIdAsync(CancellationToken cancellationToken)
    {
        if (_id is not null) return _id;

        try
        {
            if (File.Exists(_idPath))
            {
                var stored = (await File.ReadAllTextAsync(_idPath, cancellationToken)).Trim();
                if (stored.Length == 32) return _id = stored;
            }
        }
        catch (Exception)
        {
            // Unreadable id file: negotiate a fresh one below.
        }

        try
        {
            using var response = await _http.PostAsync($"{Base}/register", null, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var id = doc.RootElement.GetProperty("id").GetString();
            if (id is not { Length: 32 }) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(_idPath)!);
            await File.WriteAllTextAsync(_idPath, id, cancellationToken);
            return _id = id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Pushes the current groups; false when the server is unreachable.</summary>
    public async Task<bool> SyncAsync(
        IReadOnlyList<(string Name, IReadOnlyList<string> Codes)> groups,
        CancellationToken cancellationToken)
    {
        var id = await EnsureIdAsync(cancellationToken);
        if (id is null) return false;

        try
        {
            // Anonymous types keep the lowercase names the server expects.
            var payload = JsonSerializer.Serialize(new
            {
                id,
                groups = groups.Select(g => new { name = g.Name, codes = g.Codes }),
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{Base}/sync", content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Snapshot dates the server holds for a contract, newest first.
    /// Empty on any failure — the caller falls back to its local cache.</summary>
    public async Task<IReadOnlyList<DateOnly>> DatesAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync(
                $"{Base}/dates?code={Uri.EscapeDataString(code)}", cancellationToken);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("dates").EnumerateArray()
                .Select(e => DateOnly.TryParseExact(e.GetString(), "yyyy-MM-dd", out var d)
                    ? d : (DateOnly?)null)
                .OfType<DateOnly>()
                .OrderByDescending(d => d)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<DateOnly>();
        }
    }

    /// <summary>One day's series, or null when absent/unreachable. The payload is
    /// the client's own TrendSeries shape, stored server-side verbatim.</summary>
    public async Task<TrendSeries?> TrendAsync(string code, DateOnly date, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync(
                $"{Base}/trend?code={Uri.EscapeDataString(code)}&date={date:yyyy-MM-dd}",
                cancellationToken);

            var series = JsonSerializer.Deserialize<TrendSeries>(json);
            return series?.Points is { Count: > 0 } ? series : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
