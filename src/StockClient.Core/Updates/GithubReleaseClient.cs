using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Updates;

/// <summary>
/// Reads the latest release from the public GitHub repo. Anonymous — no token
/// ships in the client; the unauthenticated rate limit (60/hr) is plenty for an
/// occasional update check. This is the fallback source, behind the domestic mirror.
/// </summary>
public sealed class GithubReleaseClient
{
    private const string LatestUrl =
        "https://api.github.com/repos/pcguan/QuoteView/releases/latest";
    private const string AssetName = "QuoteView.exe";

    private readonly HttpClient _http;

    public GithubReleaseClient(HttpClient http) => _http = http;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("QuoteView-Updater"); // GitHub 403s without a UA

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null; // 404 = no releases yet

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var dto = JsonSerializer.Deserialize<ReleaseDto>(json);
        if (dto?.TagName is null) return null;

        if (!Version.TryParse(dto.TagName.TrimStart('v', 'V'), out var version)) return null;

        var asset = dto.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset?.DownloadUrl is null) return null;

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = asset.DownloadUrl,
            DisplayName = dto.TagName,
            Notes = dto.Body ?? "",
            Source = "GitHub",
            Size = asset.Size ?? 0,
        };
    }

    private sealed record ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; init; }
    }

    private sealed record AssetDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; init; }
        [JsonPropertyName("size")] public long? Size { get; init; }
    }
}
