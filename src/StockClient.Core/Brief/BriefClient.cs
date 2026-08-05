using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Brief;

/// <summary>
/// Which days the server has, newest first. Named catalog rather than index to
/// stay clear of <see cref="BriefIndex"/>, which is a market index.
/// </summary>
public sealed record BriefCatalog
{
    [JsonPropertyName("days")]
    public IReadOnlyList<string> Days { get; init; } = Array.Empty<string>();

    [JsonPropertyName("latest")]
    public string? Latest { get; init; }
}

/// <summary>
/// Fetches briefs over HTTP, the same way updates are fetched.
///
/// Every client reaches the same URL, so a machine needs nothing set up beyond
/// the exe. (The first cut copied files to individual desktops over SSH, which
/// only ever reached the two machines this pipeline could log into — that isn't
/// distribution.)
///
/// Downloads are cached to disk, so a machine that has read a day once can still
/// read it offline, and a network failure shows the last good brief instead of
/// an empty page.
/// </summary>
public sealed class BriefClient
{
    private const string BaseUrl = "https://nas.pcguan.cn/quoteview/brief";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly BriefStore _cache;

    public BriefClient(BriefStore? cache = null, HttpClient? http = null)
    {
        _cache = cache ?? new BriefStore();
        _http = http ?? new HttpClient { Timeout = Timeout };
    }

    public async Task<BriefCatalog?> GetCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/index.json", cancellationToken);
            return JsonSerializer.Deserialize<BriefCatalog>(json);
        }
        catch (Exception)
        {
            // Offline, or the server is down: the caller falls back to whatever
            // days are already cached locally.
            return null;
        }
    }

    /// <summary>
    /// One day's brief: cache first, then the server. A downloaded brief is
    /// cached before being returned, so it stays readable offline.
    /// </summary>
    public async Task<DailyBrief?> GetAsync(string day, CancellationToken cancellationToken)
    {
        if (_cache.Load(day) is { } cached) return cached;

        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/brief-{day}.json", cancellationToken);

            var brief = DailyBrief.Parse(json);
            if (brief is null) return null;      // malformed: don't cache it

            _cache.Save(day, json);
            return brief;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
