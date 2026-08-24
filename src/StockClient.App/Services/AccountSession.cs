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

    /// <summary>Signed in = a token exists. It may still be stale; CallAsync heals that.</summary>
    public bool IsSignedIn => _token is not null;

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
        string? Username, bool Remember, bool AutoLogin, string? Token, string? Password);

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
            _token = Unprotect(doc.Token);
            _password = Unprotect(doc.Password);
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
                Remember ? Protect(_password) : null);
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

        Username = username;
        Remember = remember;
        AutoLogin = autoLogin;
        _token = result.Token;
        _password = remember ? password : null;
        Save();
        Changed?.Invoke();
        return null;
    }

    public void SignOut()
    {
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

        _token = renewed.Token;
        Save();
        (result, unauthorized) = await op(_token);
        return unauthorized ? fallback : result;
    }

    public Task<bool> SyncGroupsAsync(IReadOnlyList<(string Name, IReadOnlyList<string> Codes)> groups) =>
        CallAsync(t => _client.SyncAsync(t, groups, CancellationToken.None).ContinueWith(
            x => (x.Result.Ok, x.Result.Unauthorized)), false);

    public Task<IReadOnlyList<DateOnly>> DatesAsync(string code) =>
        CallAsync(t => _client.DatesAsync(t, code, CancellationToken.None).ContinueWith(
            x => (x.Result.Dates, x.Result.Unauthorized)), (IReadOnlyList<DateOnly>)Array.Empty<DateOnly>());

    public Task<string?> GetSettingsAsync() =>
        CallAsync(t => _client.GetSettingsAsync(t, CancellationToken.None).ContinueWith(
            x => (x.Result.Json, x.Result.Unauthorized)), (string?)null);

    public Task<bool> PutSettingsAsync(string settingsJson) =>
        CallAsync(t => _client.PutSettingsAsync(t, settingsJson, CancellationToken.None).ContinueWith(
            x => (x.Result.Ok, x.Result.Unauthorized)), false);

    public Task<string?> KlineJsonAsync(string secid, int klt, int fqt, int lmt) =>
        CallAsync(t => _client.KlineJsonAsync(t, secid, klt, fqt, lmt, CancellationToken.None).ContinueWith(
            x => (x.Result.Json, x.Result.Unauthorized)), (string?)null);

    public Task<IReadOnlyList<(string Name, IReadOnlyList<string> Codes)>?> GroupsAsync() =>
        CallAsync(t => _client.GroupsAsync(t, CancellationToken.None).ContinueWith(
            x => (x.Result.Groups, x.Result.Unauthorized)),
            (IReadOnlyList<(string, IReadOnlyList<string>)>?)null);

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
