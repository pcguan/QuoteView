using System.Net.Http;

namespace StockClient.App.Services;

/// <summary>
/// HttpClient factory for the app's own traffic — with the SYSTEM PROXY OFF.
///
/// Every endpoint this app talks to (Tencent/EastMoney quotes, the NAS server)
/// is a domestic direct connection; a proxy only ever slows it down. Worse,
/// .NET captures the system proxy ONCE per process: a local proxy client
/// (127.0.0.1:7897) switched off while the app runs leaves the cached setting
/// pointing at a dead port, and every request in the process black-holes —
/// quotes freeze, presence drops, new groups render blank — while any freshly
/// started program works fine. Measured live on 2026-09-01.
/// </summary>
public static class DirectHttp
{
    public static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
}
