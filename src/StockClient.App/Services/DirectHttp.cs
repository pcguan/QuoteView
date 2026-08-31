using System.Net;
using System.Net.Http;

namespace StockClient.App.Services;

/// <summary>
/// HttpClient factory for the app's own traffic, honouring the 网络代理 preference.
///
/// Default is NO proxy: every endpoint this app talks to (Tencent/EastMoney
/// quotes, the NAS server) is a domestic direct connection. Worse than useless,
/// .NET captures the system proxy ONCE per process: a local proxy client
/// (127.0.0.1:7897) switched off while the app runs leaves the cached setting
/// pointing at a dead port, and every request in the process black-holes —
/// quotes freeze, presence drops, new groups render blank — while any freshly
/// started program works fine. Measured live on 2026-09-01.
///
/// The other two modes exist for networks that genuinely need one: "system"
/// keeps .NET's default behaviour, "manual" uses the configured host:port.
/// Clients are built once at startup, so mode changes apply on restart; the
/// presence websocket builds per connection and picks changes up live.
/// </summary>
public static class DirectHttp
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler();
        switch (AppPrefs.ProxyMode)
        {
            case AppPrefs.ProxySystem:
                break;   // .NET default: the system proxy as captured at start
            case AppPrefs.ProxyManual when ManualProxy() is { } proxy:
                handler.Proxy = proxy;
                break;
            default:
                handler.UseProxy = false;
                break;
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>Same policy for a websocket's options (built per connection).</summary>
    public static void Apply(System.Net.WebSockets.ClientWebSocketOptions options)
    {
        switch (AppPrefs.ProxyMode)
        {
            case AppPrefs.ProxySystem:
                break;   // default = system proxy
            case AppPrefs.ProxyManual when ManualProxy() is { } proxy:
                options.Proxy = proxy;
                break;
            default:
                options.Proxy = null;
                break;
        }
    }

    private static IWebProxy? ManualProxy()
    {
        var address = AppPrefs.ProxyAddress.Trim();
        if (address.Length == 0) return null;
        if (!address.Contains("://")) address = "http://" + address;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0
            ? new WebProxy(uri)
            : null;
    }
}
