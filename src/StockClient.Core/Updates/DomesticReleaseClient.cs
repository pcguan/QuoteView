using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Updates;

/// <summary>
/// Reads the version manifest from the domestic mirror (NAS nginx). This is the
/// primary source — fast in China — with GitHub as the fallback.
///
/// Manifest at <c>https://nas.pcguan.cn/quoteview/latest.json</c>:
/// <code>{ "version": "1.0.0", "url": ".../QuoteView-1.0.0.exe", "notes": "..." }</code>
/// </summary>
public sealed class DomesticReleaseClient
{
    public const string ManifestUrl = "https://nas.pcguan.cn/quoteview/latest.json";

    private readonly HttpClient _http;

    public DomesticReleaseClient(HttpClient http) => _http = http;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        // Cache-bust so a stale CDN/proxy copy can't pin the version.
        var url = $"{ManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var dto = JsonSerializer.Deserialize<ManifestDto>(json);
        if (dto?.Version is null || string.IsNullOrWhiteSpace(dto.Url)) return null;

        if (!Version.TryParse(dto.Version.TrimStart('v', 'V'), out var version)) return null;

        var force = dto.Force ?? false;

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = dto.Url!,
            // A forced version is usually LOWER than what the client runs, and
            // the prompt says 「发现新版本 …」 — say so in the name, or the user
            // reads a downgrade as an upgrade.
            DisplayName = force ? $"v{version}（版本回退）" : "v" + version,
            Notes = dto.Notes ?? "",
            Source = "国内源", // internal only; the UI deliberately doesn't show it
            Size = dto.Size ?? 0,
            Sha256 = dto.Sha256 ?? "",
            Force = force,
        };
    }

    private sealed record ManifestDto
    {
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("notes")] public string? Notes { get; init; }
        [JsonPropertyName("size")] public long? Size { get; init; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

        /// <summary>Set only by a rollback publish (tools/release.sh --rollback).</summary>
        [JsonPropertyName("force")] public bool? Force { get; init; }
    }
}
