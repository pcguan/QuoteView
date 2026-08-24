using System.Text;
using System.Text.Json;

namespace StockClient.Core.Quotes;

/// <summary>Outcome of a login/register call: a token, or a display-ready error.</summary>
public sealed record AuthResult(string? Token, string? Error)
{
    public bool Ok => Token is not null;
}

/// <summary>
/// Account-level transport to the QuoteView server:
///
///   POST /register, /login       -> { token }   (or { error })
///   POST /sync                   -> groups+codes, Bearer auth
///   GET  /dates, /trend          -> snapshot queries, Bearer auth
///
/// Pure transport — no token storage, no retry policy. The App-side session
/// owns credentials, remember-me, and the 401-then-relogin dance; this class
/// just reports Unauthorized so the session can react.
/// </summary>
public sealed class AccountClient
{
    private const string Base = "https://nas.pcguan.cn/quoteview/api";

    private readonly HttpClient _http;
    private readonly string _version;

    public AccountClient(HttpClient http, string version = "")
    {
        _http = http;
        _version = version;
    }

    private void Stamp(HttpRequestMessage request, string? token = null)
    {
        if (_version.Length > 0) request.Headers.TryAddWithoutValidation("X-QV-Version", _version);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
    }

    public Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct) =>
        AuthAsync("/login", username, password, ct);

    public Task<AuthResult> RegisterAsync(string username, string password, CancellationToken ct) =>
        AuthAsync("/register", username, password, ct);

    private async Task<AuthResult> AuthAsync(string path, string username, string password, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { username, password });
            using var request = new HttpRequestMessage(HttpMethod.Post, Base + path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            Stamp(request);
            using var response = await _http.SendAsync(request, ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (response.IsSuccessStatusCode
                && doc.RootElement.TryGetProperty("token", out var token)
                && token.GetString() is { Length: 64 } t)
                return new AuthResult(t, null);

            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            return new AuthResult(null, error ?? $"服务端错误 {(int)response.StatusCode}");
        }
        catch (Exception)
        {
            return new AuthResult(null, "无法连接服务端");
        }
    }

    public async Task<(bool Ok, bool Unauthorized)> SyncAsync(
        string token,
        IReadOnlyList<(string Name, IReadOnlyList<string> Codes)> groups,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                groups = groups.Select(g => new { name = g.Name, codes = g.Codes }),
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sync")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            return (response.IsSuccessStatusCode,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    public async Task<(IReadOnlyList<DateOnly> Dates, bool Unauthorized)> DatesAsync(
        string token, string code, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{Base}/dates?code={Uri.EscapeDataString(code)}");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (Array.Empty<DateOnly>(), true);
            if (!response.IsSuccessStatusCode) return (Array.Empty<DateOnly>(), false);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var dates = doc.RootElement.GetProperty("dates").EnumerateArray()
                .Select(e => DateOnly.TryParseExact(e.GetString(), "yyyy-MM-dd", out var d)
                    ? d : (DateOnly?)null)
                .OfType<DateOnly>()
                .OrderByDescending(d => d)
                .ToArray();
            return (dates, false);
        }
        catch (Exception)
        {
            return (Array.Empty<DateOnly>(), false);
        }
    }

    /// <summary>The account's stored settings JSON (the inner object), or null.</summary>
    public async Task<(string? Json, bool Unauthorized)> GetSettingsAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/settings");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("settings", out var settings)
                ? (settings.GetRawText(), false)
                : (null, false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    public async Task<(bool Ok, bool Unauthorized)> PutSettingsAsync(
        string token, string settingsJson, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/settings")
            {
                Content = new StringContent(
                    $"{{\"settings\":{settingsJson}}}", Encoding.UTF8, "application/json"),
            };
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            return (response.IsSuccessStatusCode,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    public async Task<(TrendSeries? Series, bool Unauthorized)> TrendAsync(
        string token, string code, DateOnly date, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{Base}/trend?code={Uri.EscapeDataString(code)}&date={date:yyyy-MM-dd}");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);

            var series = JsonSerializer.Deserialize<TrendSeries>(
                await response.Content.ReadAsStringAsync(ct));
            return (series?.Points is { Count: > 0 } ? series : null, false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }
}
