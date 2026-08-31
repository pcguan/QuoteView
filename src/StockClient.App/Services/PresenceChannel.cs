using System.Net.WebSockets;
using System.Text.Json;

namespace StockClient.App.Services;

/// <summary>
/// The persistent connection to the server — one WebSocket held for the whole
/// signed-in lifetime. Its being open IS the "client is running" signal: the
/// server registers the connection on auth and drops it the instant the socket
/// closes, gracefully or not (app exit, crash, kill — all send FIN/RST).
///
/// Silent death (cable pulled, power loss) sends nothing, which no TCP stack
/// can see through — the built-in 5s keep-alive pings cover that: the server
/// times the connection out after ~20s without one.
///
/// Reconnects forever with backoff; a token change (re-login, sign-out) kicks
/// the current connection immediately so the session shown server-side is
/// always the live one. Also the future server-push channel — pushed messages
/// arrive in the receive loop.
/// </summary>
public sealed class PresenceChannel : IAsyncDisposable
{
    private const string Url = "wss://nas.pcguan.cn/quoteview/api/ws";

    private readonly AccountSession _session;
    private readonly string _version;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource _kick = new();
    private Task? _loop;

    public PresenceChannel(AccountSession session, string version)
    {
        _session = session;
        _version = version;

        // Sign-in/out and token renewal all invalidate the current connection.
        _session.Changed += () =>
        {
            try { _kick.Cancel(); }
            catch (ObjectDisposedException) { }
        };
    }

    /// <summary>Raised when the server pushes a fresh-news notification.</summary>
    public event Action? NewsPushed;

    public void Start() => _loop ??= RunAsync();

    private async Task RunAsync()
    {
        var backoff = TimeSpan.FromSeconds(5);

        while (!_cts.IsCancellationRequested)
        {
            var token = _session.CurrentToken;
            if (token is null)
            {
                await IdleAsync(TimeSpan.FromSeconds(5));
                continue;
            }

            try
            {
                using var ws = new ClientWebSocket();
                // Direct to the NAS: the process-cached system proxy outlives
                // the proxy program (see DirectHttp) and would black-hole this.
                ws.Options.Proxy = null;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _cts.Token, _kick.Token);

                await ws.ConnectAsync(new Uri(Url), linked.Token);
                var auth = JsonSerializer.SerializeToUtf8Bytes(new { token, ver = _version });
                await ws.SendAsync(auth, WebSocketMessageType.Text, true, linked.Token);

                var buffer = new byte[4096];
                var first = await ws.ReceiveAsync(buffer, linked.Token);
                if (first.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("auth rejected");

                backoff = TimeSpan.FromSeconds(5);
                Probe.Log("presence: connected");

                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, linked.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;

                    // Server push. Today's only message: {"news": n} after a
                    // sweep found fresh items for somebody's watched contracts.
                    if (r.MessageType == WebSocketMessageType.Text && r.Count > 0
                        && System.Text.Encoding.UTF8.GetString(buffer, 0, r.Count)
                            .Contains("\"news\""))
                        NewsPushed?.Invoke();
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Every failure mode ends the same way: reconnect below.
            }

            if (_kick.IsCancellationRequested)
            {
                // Token changed — reconnect immediately with the new identity.
                _kick.Dispose();
                _kick = new CancellationTokenSource();
                continue;
            }

            Probe.Log($"presence: disconnected, retry in {backoff.TotalSeconds:0}s");

            // One authenticated probe per drop: if the server just force-logged
            // this session out (admin kick severs the socket), the ping's 401
            // carries the reason and the session signs out locally within
            // seconds instead of at the next 60s heartbeat.
            _ = _session.PingAsync();

            await IdleAsync(backoff);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    /// <summary>A delay that both shutdown and a token change can cut short.</summary>
    private async Task IdleAsync(TimeSpan delay)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _cts.Token, _kick.Token);
            await Task.Delay(delay, linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (_kick.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                _kick.Dispose();
                _kick = new CancellationTokenSource();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (Exception) { }
        }
        _cts.Dispose();
    }
}
