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
    /// <summary>The public domestic endpoint. Fronted by nginx + CDN.</summary>
    public const string DefaultBase = "https://nas.pcguan.cn/quoteview/api";

    // Resolved per request so a settings change (e.g. falling back to a LAN IP
    // when the domain/CDN is down) takes effect without recreating the client.
    private readonly Func<string> _base;
    private string Base => Normalize(_base());

    private static string Normalize(string b) =>
        string.IsNullOrWhiteSpace(b) ? DefaultBase : b.TrimEnd('/');

    private readonly HttpClient _http;
    private readonly string _version;

    public AccountClient(HttpClient http, string version = "", Func<string>? baseUrl = null)
    {
        _http = http;
        _version = version;
        _base = baseUrl ?? (() => DefaultBase);
    }

    private void Stamp(HttpRequestMessage request, string? token = null)
    {
        if (_version.Length > 0) request.Headers.TryAddWithoutValidation("X-QV-Version", _version);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
    }

    /// <summary>
    /// Why the server rejects this token: "kicked" for an admin force logout,
    /// "" for anything else (expired, unknown, network trouble). The session
    /// consults this before its silent re-login self-heal — an admin kick must
    /// stay kicked until a human signs in again.
    /// </summary>
    public async Task<string> AuthRejectReasonAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/ping");
            Stamp(request, token);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) return "";
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Contains("kicked") ? "kicked" : "";
        }
        catch
        {
            return "";
        }
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

    public async Task<(bool Ok, bool Unauthorized, bool Conflict)> SyncAsync(
        string token,
        IReadOnlyList<(string Name, IReadOnlyList<string> Codes, bool InPanel)> groups,
        long at,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                at,
                // 轮换 (panel) is client-local since the 2026-08-27 split — not
                // sent; the server keeps serving its stored flags to old pullers.
                groups = groups.Select(g => new { name = g.Name, codes = g.Codes }),
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/sync")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            return (response.IsSuccessStatusCode,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                    response.StatusCode == System.Net.HttpStatusCode.Conflict);
        }
        catch (Exception)
        {
            return (false, false, false);
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

    /// <summary>The account's personalized watch feed (raw JSON), or null.</summary>
    public async Task<(string? Json, bool Unauthorized)> NewsAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/news");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);
            return (await response.Content.ReadAsStringAsync(ct), false);
        }
        catch (Exception)
        {
            return (null, false);
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

    /// <summary>Heartbeat: touches the session so the console's 活跃 state and
    /// "connected right now" counts are near-real-time. Unauthorized bubbles up
    /// so the session layer can re-login (which is also how a server-side token
    /// loss heals within one beat instead of waiting for user activity).</summary>
    public async Task<(bool Ok, bool Unauthorized)> PingAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/ping");
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

    /// <summary>Tells the server to drop this token, so the sign-out shows up in
    /// the console's logs. Best effort — local sign-out proceeds regardless.</summary>
    public async Task LogoutAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/logout");
            Stamp(request, token);
            using var _ = await _http.SendAsync(request, ct);
        }
        catch (Exception)
        {
            // The token dies of natural causes eventually (30-day prune).
        }
    }

    /// <summary>Change the signed-in account's password; the server verifies the
    /// old one. Null on success, otherwise a display-ready error.</summary>
    public async Task<(string? Error, bool Unauthorized)> ChangePasswordAsync(
        string token, string oldPassword, string newPassword, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { old = oldPassword, @new = newPassword });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/password")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return ("登录已失效", true);
            if (response.IsSuccessStatusCode) return (null, false);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            return (error ?? $"服务端错误 {(int)response.StatusCode}", false);
        }
        catch (Exception)
        {
            return ("无法连接服务端", false);
        }
    }

    /// <summary>The server's /kline proxy body (EastMoney-shaped JSON), or null.
    /// lmt=0 asks for the full listed history, matching the chart's own shape.</summary>
    /// <summary>Best-effort client fault report; errors are swallowed — the
    /// reporter must never become a second failure.</summary>
    public async Task<(bool Ok, bool Unauthorized)> ReportErrorAsync(
        string token, string kind, string detail, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/clientlog")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { kind, detail }),
                    System.Text.Encoding.UTF8, "application/json"),
            };
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, true);
            return (response.IsSuccessStatusCode, false);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    /// <summary>The server-archived KR daily closes (Korea has no queryable
    /// daily history upstream; the server archives each session itself).</summary>
    public async Task<(string? Json, bool Unauthorized)> KrDailyJsonAsync(
        string token, string code, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{Base}/krdaily?code={Uri.EscapeDataString(code)}");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);

            return (await response.Content.ReadAsStringAsync(ct), false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    /// <summary>Archived 成交明细 dates for a contract (GET /tickdates).</summary>
    public async Task<(IReadOnlyList<DateOnly> Dates, bool Unauthorized)> TickDatesAsync(
        string token, string code, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{Base}/tickdates?code={Uri.EscapeDataString(code)}");
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

    /// <summary>
    /// One archived session's 成交明细 (GET /ticks). The server stores the raw
    /// EastMoney detail rows; they parse here exactly like the live tape.
    /// </summary>
    public async Task<(TradeTickSnapshot? Snap, bool Unauthorized)> TicksAsync(
        string token, string code, DateOnly date, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{Base}/ticks?code={Uri.EscapeDataString(code)}&date={date:yyyy-MM-dd}");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);

            var dto = JsonSerializer.Deserialize<ArchivedTicks>(
                await response.Content.ReadAsStringAsync(ct));
            if (dto?.Details is not { Count: > 0 }) return (null, false);

            var ticks = dto.Details
                .Select(EastMoneyDetailsClient.ParseRow)
                .Where(t => t is not null)
                .Select(t => t!)
                .ToArray();
            if (ticks.Length == 0) return (null, false);

            return (new TradeTickSnapshot
            {
                Code = code,
                PrePrice = dto.PrePrice,
                Decimals = dto.Decimals > 0 ? dto.Decimals : 2,
                Ticks = ticks,
            }, false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    private sealed record ArchivedTicks
    {
        public double PrePrice { get; init; }
        public int Decimals { get; init; }
        public List<string>? Details { get; init; }
    }

    public async Task<(string? Json, bool Unauthorized)> KlineJsonAsync(
        string token, string secid, int klt, int fqt, int lmt, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{Base}/kline?secid={Uri.EscapeDataString(secid)}&klt={klt}&fqt={fqt}&lmt={lmt}");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);

            return (await response.Content.ReadAsStringAsync(ct), false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    /// <summary>The account's server-side groups plus their change stamp, or null when unreachable.</summary>
    public async Task<((IReadOnlyList<(string Name, IReadOnlyList<string> Codes, bool InPanel)> Groups, long At)? Result, bool Unauthorized)> GroupsAsync(
        string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/groups");
            Stamp(request, token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
            if (!response.IsSuccessStatusCode) return (null, false);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var at = doc.RootElement.TryGetProperty("at", out var a) ? a.GetInt64() : 0;
            var groups = new List<(string, IReadOnlyList<string>, bool)>();
            foreach (var g in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var codes = g.TryGetProperty("codes", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                var panel = !g.TryGetProperty("panel", out var pnl) || pnl.ValueKind != JsonValueKind.False;
                groups.Add((name, codes, panel));
            }
            return ((groups, at), false);
        }
        catch (Exception)
        {
            return (null, false);
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
