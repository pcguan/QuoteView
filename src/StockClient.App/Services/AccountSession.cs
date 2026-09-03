using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockClient.Core.Quotes;

namespace StockClient.App.Services;

/// <summary>
/// The app's account state: who is signed in, the bearer token, and — when
/// 记住密码 is on — the password itself, so an expired token can be renewed
/// without asking. Token and password are DPAPI-protected (CurrentUser scope),
/// the standard "remember me" storage on Windows: unreadable from another
/// account or machine, no key management of our own.
///
/// Every data call funnels through <see cref="CallAsync"/>: it supplies the
/// token, and on a 401 does one silent re-login (needs the remembered
/// password) and one retry. All failures degrade to "no data" — the app never
/// blocks on the server.
/// </summary>
public sealed class AccountSession
{
    private readonly AccountClient _client;
    private readonly string _path;

    private string? _token;
    private string? _password;

    public string? Username { get; private set; }
    public bool Remember { get; private set; }
    public bool AutoLogin { get; private set; }

    /// <summary>
    /// 离线模式: an account-less local user. All its data lives in its own local
    /// profile file (see GroupStore.ActiveProfile), fully isolated from real
    /// accounts; nothing is pulled from or pushed to the server. Cleared by any
    /// successful sign-in; survives restarts.
    /// </summary>
    public bool OfflineMode { get; private set; }

    public void EnterOfflineMode()
    {
        OfflineMode = true;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Raised once when the server reports this session was force
    /// logged out by an administrator.</summary>
    public event Action? Kicked;

    private void KickedByAdmin()
    {
        _token = null;
        AutoLogin = false;   // stay signed out across restarts until a HUMAN logs in
        Save();
        Changed?.Invoke();
        Kicked?.Invoke();
    }

    /// <summary>Signed in = a token exists. It may still be stale; CallAsync heals that.</summary>
    public bool IsSignedIn => _token is not null;

    /// <summary>The live bearer token, for the presence channel's auth frame.</summary>
    internal string? CurrentToken => _token;

    /// <summary>Raised whenever sign-in state changes, for UI labels.</summary>
    public event Action? Changed;

    public AccountSession(AccountClient client, string? path = null)
    {
        _client = client;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StockClient", "account.json");
        Load();
    }

    private sealed record Persisted(
        string? Username, bool Remember, bool AutoLogin, string? Token, string? Password,
        List<SavedEntry>? Accounts = null, bool Offline = false);

    private sealed record SavedEntry(string Username, string Password);

    /// <summary>Every account whose password was remembered, for quick switching.</summary>
    private readonly List<(string User, string Pass)> _saved = new();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (doc is null) return;

            Username = doc.Username;
            Remember = doc.Remember;
            AutoLogin = doc.AutoLogin;
            OfflineMode = doc.Offline;
            _token = Unprotect(doc.Token);
            _password = Unprotect(doc.Password);

            foreach (var entry in doc.Accounts ?? new List<SavedEntry>())
            {
                if (Unprotect(entry.Password) is { } pass && entry.Username.Length > 0)
                    _saved.Add((entry.Username, pass));
            }

            // Pre-multi-account files: the single remembered login seeds the list.
            if (_saved.Count == 0 && Username is { } u && _password is { } p && Remember)
                _saved.Add((u, p));
        }
        catch (Exception)
        {
            // Unreadable state file = signed out; the user just logs in again.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var doc = new Persisted(
                Username, Remember, AutoLogin,
                Protect(_token),
                Remember ? Protect(_password) : null,
                _saved.Select(a => new SavedEntry(a.User, Protect(a.Pass) ?? "")).ToList(),
                OfflineMode);
            File.WriteAllText(_path, JsonSerializer.Serialize(doc));
        }
        catch (Exception)
        {
            // Best effort — worst case is logging in again next start.
        }
    }

    /// <summary>Null on success, otherwise a display-ready error.</summary>
    public Task<string?> SignInAsync(string username, string password, bool remember, bool autoLogin) =>
        AuthAsync(username, password, remember, autoLogin, register: false);

    /// <summary>Registers and signs in. Null on success.</summary>
    public Task<string?> RegisterAsync(string username, string password, bool remember, bool autoLogin) =>
        AuthAsync(username, password, remember, autoLogin, register: true);

    private async Task<string?> AuthAsync(
        string username, string password, bool remember, bool autoLogin, bool register)
    {
        var result = register
            ? await _client.RegisterAsync(username, password, CancellationToken.None)
            : await _client.LoginAsync(username, password, CancellationToken.None);
        if (!result.Ok) return result.Error ?? "登录失败";

        // One session per machine: the token this login replaces is dead weight
        // that would sit in the console's session list for 30 days.
        if (_token is { } replaced && replaced != result.Token)
            _ = _client.LogoutAsync(replaced, CancellationToken.None);

        Username = username;
        Remember = remember;
        AutoLogin = autoLogin;
        OfflineMode = false;   // signing in leaves 离线模式
        _token = result.Token;
        _password = remember ? password : null;

        if (remember)
        {
            _saved.RemoveAll(a => string.Equals(a.User, username, StringComparison.OrdinalIgnoreCase));
            _saved.Insert(0, (username, password));   // most recent first
        }

        Save();
        Changed?.Invoke();
        return null;
    }

    /// <summary>Accounts available for one-click switching, most recent first.</summary>
    public IReadOnlyList<string> RememberedUsers => _saved.Select(a => a.User).ToArray();

    /// <summary>
    /// One-click switch to a remembered account. A stale saved password is
    /// dropped from the quick list so it doesn't keep failing silently — the
    /// caller falls back to the manual form.
    /// </summary>
    public async Task<string?> SwitchToAsync(string username)
    {
        var entry = _saved.FirstOrDefault(
            a => string.Equals(a.User, username, StringComparison.OrdinalIgnoreCase));
        if (entry.User is null) return "该账户没有记住密码";

        var error = await SignInAsync(entry.User, entry.Pass, remember: true, autoLogin: true);
        if (error is not null && error.Contains("密码"))
        {
            _saved.RemoveAll(a => string.Equals(a.User, username, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        return error;
    }

    public void SignOut()
    {
        // Server first (fire-and-forget) so the console's log shows the sign-out
        // and the token stops working immediately, not in 30 days.
        if (_token is { } token) _ = _client.LogoutAsync(token, CancellationToken.None);

        _token = null;
        _password = null;
        AutoLogin = false;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Start-of-app hook: with 自动登录 on and only a password stored (token was
    /// dropped or never saved), sign in now so the first sync isn't a miss.
    /// With a stored token there is nothing to do — it is used on demand.
    /// </summary>
    public async Task TryAutoLoginAsync()
    {
        if (OfflineMode) return;   // 离线模式 never touches the server
        if (!AutoLogin || _token is not null) return;
        if (Username is null || _password is null) return;

        var result = await _client.LoginAsync(Username, _password, CancellationToken.None);
        if (result.Ok)
        {
            _token = result.Token;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Runs an authed operation with one silent re-login on 401.</summary>
    private async Task<T> CallAsync<T>(
        Func<string, Task<(T Result, bool Unauthorized)>> op, T fallback)
    {
        var token = _token;
        if (token is null)
        {
            // 自动登录 with remembered password but no live token yet.
            if (!AutoLogin || Username is null || _password is null) return fallback;
            var login = await _client.LoginAsync(Username, _password, CancellationToken.None);
            if (!login.Ok) return fallback;
            _token = token = login.Token;
            Save();
            Changed?.Invoke();
        }

        var (result, unauthorized) = await op(token);
        if (!unauthorized) return result;

        // A 401 has two very different meanings: a lost/expired token should
        // self-heal silently, an ADMIN FORCE-LOGOUT must not — the self-heal
        // is exactly what used to log the "kicked" client straight back in.
        if (await _client.AuthRejectReasonAsync(token, CancellationToken.None) == "kicked")
        {
            KickedByAdmin();
            return fallback;
        }

        // Stale token. One renewal attempt with the remembered password.
        _token = null;
        if (Username is null || _password is null)
        {
            Save();
            Changed?.Invoke();
            return fallback;
        }

        var renewed = await _client.LoginAsync(Username, _password, CancellationToken.None);
        if (!renewed.Ok)
        {
            Save();
            Changed?.Invoke();
            return fallback;
        }

        // The stale token is already invalid server-side (that's why we're
        // here), so no logout needed for it — just adopt the new one.
        _token = renewed.Token;
        Save();
        (result, unauthorized) = await op(_token);
        return unauthorized ? fallback : result;
    }

    public enum SyncOutcome { Ok, Conflict, Failed }

    public Task<SyncOutcome> SyncGroupsAsync(
        IReadOnlyList<(string Name, IReadOnlyList<string> Codes, bool InPanel)> groups, long at) =>
        CallAsync(t => _client.SyncAsync(t, groups, at, CancellationToken.None).ContinueWith(
            x => (x.Result.Ok ? SyncOutcome.Ok
                  : x.Result.Conflict ? SyncOutcome.Conflict
                  : SyncOutcome.Failed,
                  x.Result.Unauthorized)), SyncOutcome.Failed);

    public Task<(IReadOnlyList<(string Name, IReadOnlyList<string> Codes, bool InPanel)> Groups, long At)?> GroupsWithAtAsync() =>
        CallAsync(t => _client.GroupsAsync(t, CancellationToken.None).ContinueWith(
            x => (x.Result.Result, x.Result.Unauthorized)),
            ((IReadOnlyList<(string, IReadOnlyList<string>, bool)>, long)?)null);

    public Task<IReadOnlyList<DateOnly>> DatesAsync(string code) =>
        CallAsync(t => _client.DatesAsync(t, code, CancellationToken.None).ContinueWith(
            x => (x.Result.Dates, x.Result.Unauthorized)), (IReadOnlyList<DateOnly>)Array.Empty<DateOnly>());

    /// <summary>Archived 成交明细 dates for a contract, or empty.</summary>
    public Task<IReadOnlyList<DateOnly>> TickDatesAsync(string code) =>
        CallAsync(t => _client.TickDatesAsync(t, code, CancellationToken.None).ContinueWith(
            x => (x.Result.Dates, x.Result.Unauthorized)), (IReadOnlyList<DateOnly>)Array.Empty<DateOnly>());

    /// <summary>One archived session's 成交明细, or null.</summary>
    public Task<TradeTickSnapshot?> TicksAsync(string code, DateOnly date) =>
        CallAsync(t => _client.TicksAsync(t, code, date, CancellationToken.None).ContinueWith(
            x => (x.Result.Snap, x.Result.Unauthorized)), (TradeTickSnapshot?)null);

    /// <summary>The personalized watch feed for this account, or null.</summary>
    public Task<string?> NewsJsonAsync() =>
        CallAsync(t => _client.NewsAsync(t, CancellationToken.None), (string?)null);

    public Task<string?> GetSettingsAsync() =>
        CallAsync(t => _client.GetSettingsAsync(t, CancellationToken.None).ContinueWith(
            x => (x.Result.Json, x.Result.Unauthorized)), (string?)null);

    public Task<bool> PutSettingsAsync(string settingsJson) =>
        CallAsync(t => _client.PutSettingsAsync(t, settingsJson, CancellationToken.None).ContinueWith(
            x => (x.Result.Ok, x.Result.Unauthorized)), false);

    /// <summary>Null on success; the remembered password follows the change.</summary>
    public async Task<string?> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        if (_token is null) return "未登录";

        var (error, unauthorized) = await _client.ChangePasswordAsync(
            _token, oldPassword, newPassword, CancellationToken.None);
        if (unauthorized) return "登录已失效，请重新登录";
        if (error is not null) return error;

        if (Remember) _password = newPassword;
        Save();
        return null;
    }

    /// <summary>Heartbeat, with the standard 401-relogin healing.</summary>
    public Task<bool> PingAsync() =>
        CallAsync(t => _client.PingAsync(t, CancellationToken.None).ContinueWith(
            x => (x.Result.Ok, x.Result.Unauthorized)), false);

    public Task<bool> ReportErrorAsync(string kind, string detail) =>
        CallAsync(t => _client.ReportErrorAsync(t, kind, detail, CancellationToken.None).ContinueWith(
            x => (x.Result.Ok, x.Result.Unauthorized)), false);

    public Task<string?> KrDailyJsonAsync(string code, CancellationToken ct = default) =>
        CallAsync(t => _client.KrDailyJsonAsync(t, code, ct).ContinueWith(
            x => (x.Result.Json, x.Result.Unauthorized)), (string?)null);

    public Task<string?> KlineJsonAsync(string secid, int klt, int fqt, int lmt,
        CancellationToken ct = default) =>
        CallAsync(t => _client.KlineJsonAsync(t, secid, klt, fqt, lmt, ct).ContinueWith(
            x => (x.Result.Json, x.Result.Unauthorized)), (string?)null);

    public Task<TrendSeries?> TrendAsync(string code, DateOnly date) =>
        CallAsync(t => _client.TrendAsync(t, code, date, CancellationToken.None).ContinueWith(
            x => (x.Result.Series, x.Result.Unauthorized)), (TrendSeries?)null);

    private static string? Protect(string? plain)
    {
        if (plain is null) return null;
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Unprotect(string? stored)
    {
        if (stored is null) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
