using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Updates;

/// <summary>One GitHub release, reduced to what the updater needs.</summary>
public sealed record GithubRelease
{
    /// <summary>Tag, e.g. "v1.1.0".</summary>
    public required string TagName { get; init; }

    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";

    /// <summary>Direct download URL of the QuoteView.exe asset, or null if absent.</summary>
    public string? ExeUrl { get; init; }

    /// <summary>Parses the tag (leading "v" optional) to a comparable version.</summary>
    public Version? Version =>
        System.Version.TryParse(TagName.TrimStart('v', 'V'), out var v) ? v : null;
}

/// <summary>
/// Reads the latest release from the public GitHub repo. Anonymous — no token
/// ships in the client; the unauthenticated rate limit (60/hr) is plenty for an
/// occasional update check.
/// </summary>
public sealed class GithubReleaseClient
{
    private const string LatestUrl =
        "https://api.github.com/repos/pcguan/QuoteView/releases/latest";
    private const string AssetName = "QuoteView.exe";

    private readonly HttpClient _http;

    public GithubReleaseClient(HttpClient http) => _http = http;

    /// <summary>Latest published release, or null when there are none / on failure.</summary>
    public async Task<GithubRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        // GitHub requires a User-Agent or it 403s.
        request.Headers.UserAgent.ParseAdd("QuoteView-Updater");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null; // 404 = no releases yet

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var dto = JsonSerializer.Deserialize<ReleaseDto>(json);
        if (dto?.TagName is null) return null;

        var asset = dto.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));

        return new GithubRelease
        {
            TagName = dto.TagName,
            Name = dto.Name ?? dto.TagName,
            Notes = dto.Body ?? "",
            HtmlUrl = dto.HtmlUrl ?? "",
            ExeUrl = asset?.DownloadUrl,
        };
    }

    private sealed record ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; init; }
    }

    private sealed record AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; init; }
    }
}
