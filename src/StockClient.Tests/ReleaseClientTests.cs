using System.Net;
using System.Net.Http;
using StockClient.Core.Updates;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// Manifest parsing for both update sources. Neither client is reachable from a
/// test run, so the responses are stubbed — what is pinned here is how a reply
/// turns into a ReleaseInfo, including the fields the download gate depends on
/// (size / sha256) and the rollback flag.
/// </summary>
public class ReleaseClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
    }

    private static HttpClient Http(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(body, status));

    private static Task<ReleaseInfo?> Domestic(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new DomesticReleaseClient(Http(body, status)).GetLatestAsync(default);

    private static Task<ReleaseInfo?> Github(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new GithubReleaseClient(Http(body, status)).GetLatestAsync(default);

    [Fact]
    public async Task Domestic_manifest_carries_the_download_gate_fields()
    {
        var release = await Domestic("""
            {"version":"1.1.0","url":"https://example.invalid/QuoteView-1.1.0.exe",
             "size":7340032,"sha256":"ABCDEF","notes":"说明"}
            """);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 1, 0), release!.Version);
        Assert.Equal(7340032L, release.Size);
        Assert.Equal("ABCDEF", release.Sha256);
        Assert.False(release.Force);
    }

    [Fact]
    public async Task Domestic_manifest_without_size_or_sha_still_resolves()
    {
        // An older manifest (or a hand-written one) must not break the check —
        // it only means the download falls back to the MZ header check.
        var release = await Domestic("""{"version":"v1.0.9","url":"https://example.invalid/a.exe"}""");

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 0, 9), release!.Version);
        Assert.Equal(0L, release.Size);
        Assert.Equal("", release.Sha256);
    }

    [Fact]
    public async Task Domestic_rollback_manifest_is_flagged_and_named_as_one()
    {
        var release = await Domestic("""
            {"version":"1.1.0","url":"https://example.invalid/QuoteView-1.1.0.exe","force":true}
            """);

        Assert.NotNull(release);
        Assert.True(release!.Force);
        Assert.Contains("回退", release.DisplayName);
    }

    [Fact]
    public async Task Domestic_manifest_missing_a_url_is_not_a_release()
    {
        Assert.Null(await Domestic("""{"version":"1.1.0"}"""));
        Assert.Null(await Domestic("""{"version":"nightly","url":"https://example.invalid/a.exe"}"""));
        Assert.Null(await Domestic("{}", HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Github_reads_the_hash_out_of_the_release_body()
    {
        // The asset itself carries no hash — the publisher writes it into the
        // body, and that line is the fallback source's only download anchor.
        var sha = new string('a', 64);
        var release = await Github($$"""
            {"tag_name":"v1.1.0","body":"说明\n\nSHA256: {{sha.ToUpperInvariant()}}",
             "assets":[{"name":"other.zip","browser_download_url":"https://example.invalid/o.zip","size":1},
                       {"name":"QuoteView.exe","browser_download_url":"https://example.invalid/QuoteView.exe","size":7340032}]}
            """);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 1, 0), release!.Version);
        Assert.Equal("https://example.invalid/QuoteView.exe", release.DownloadUrl);
        Assert.Equal(7340032L, release.Size);
        Assert.Equal(sha, release.Sha256);
    }

    [Fact]
    public async Task Github_body_without_a_hash_line_leaves_the_strong_check_off()
    {
        var release = await Github("""
            {"tag_name":"v1.1.0","body":"忘了写哈希",
             "assets":[{"name":"QuoteView.exe","browser_download_url":"https://example.invalid/QuoteView.exe","size":7340032}]}
            """);

        Assert.NotNull(release);
        Assert.Equal("", release!.Sha256);
    }

    [Fact]
    public async Task Github_release_without_the_expected_asset_is_not_a_release()
    {
        // Exactly the half-published state release.sh must never leave behind:
        // the release and its tag exist, the exe never got uploaded.
        Assert.Null(await Github("""{"tag_name":"v1.1.0","body":"","assets":[]}"""));
    }
}
