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

        // GitHub assets carry no hash of their own; the release body does —
        // the publisher writes "SHA256: <hex>" so the fallback source gets the
        // same download verification the domestic manifest has. A truncated
        // upload once made asset size self-consistent with the broken file.
        var sha = System.Text.RegularExpressions.Regex.Match(
            dto.Body ?? "", @"SHA-?256[:：]\s*([0-9a-fA-F]{64})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = asset.DownloadUrl,
            DisplayName = dto.TagName,
            Notes = dto.Body ?? "",
            Source = "GitHub",
            Size = asset.Size ?? 0,
            Sha256 = sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : "",
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
