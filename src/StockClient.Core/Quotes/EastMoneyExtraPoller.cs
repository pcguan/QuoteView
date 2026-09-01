using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// The secondary poll: EastMoney A-share fund-flow / 涨速, beside the primary
/// Tencent quote. Deliberately separate so it's isolated — if EastMoney throttles
/// or fails, only these columns go stale; the 1s price flow is untouched.
///
/// Slow (5s) and on-demand: started only while the relevant columns are on and the
/// group has A-shares. Adaptive backoff doubles the delay (to 30s) on repeated
/// failure and snaps back on success, so a throttled endpoint isn't hammered.
///
/// OUTSIDE the A-share session it drops to one poll every 10 minutes: fund flow
/// is a running daily total that stops moving at the bell, so the 5s cadence
/// spent all night re-fetching a settled number from the one upstream that
/// throttles hardest. The value stays on screen either way — the client caches
/// the last set to disk.
/// </summary>
public sealed class EastMoneyExtraPoller : IAsyncDisposable
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    private readonly EastMoneyQuoteClient _client;
    private readonly IMarketClock _clock = new MarketClock();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IReadOnlyList<(string Code, string SecId)> _targets = Array.Empty<(string, string)>();

    public event Action<IReadOnlyDictionary<string, QuoteExtra>>? Tick;

    public EastMoneyExtraPoller(EastMoneyQuoteClient client) => _client = client;

    /// <summary>Sets the A-share targets, or empty to idle. Starts/stops accordingly.</summary>
    public void SetTarget(IReadOnlyList<(string Code, string SecId)> targets)
    {
        lock (_gate)
        {
            var changed = !targets.Select(t => t.Code).SequenceEqual(
                _targets.Select(t => t.Code), StringComparer.OrdinalIgnoreCase);
            _targets = targets;

            if (targets.Count == 0)
            {
                StopLoop();
                return;
            }

            // A CHANGED target polls at once rather than waiting out the delay
            // in flight — up to 10 minutes of it outside the session, which
            // would leave a newly added contract's columns blank. Restarting
            // cancels that delay; an unchanged target leaves the loop alone.
            if (changed) StopLoop();
            StartLoop();
        }
    }

    public void Stop()
    {
        lock (_gate) StopLoop();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_targets.Count > 0) StartLoop();
        }
    }

    private void StartLoop()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    private void StopLoop()
    {
        _cts?.Cancel();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = Base;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var ok = await PollOnceAsync(cancellationToken);
                delay = ok
                    ? Base
                    : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, delay.TotalSeconds * 2));

                // Targets are filtered to SH/SZ/BJ by the caller and those three
                // share a session, so one market answers for the whole batch.
                await Task.Delay(_clock.IsLive(Market.SH) ? delay : Idle, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop / target change.
        }
    }

    private async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(string, string)> targets;
        lock (_gate)
        {
            if (_targets.Count == 0) return true;
            targets = _targets;
        }

        try
        {
            var extras = await _client.GetAsync(targets, cancellationToken);
            if (extras.Count > 0) Tick?.Invoke(extras);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Silent by design: the fund-flow columns just don't refresh this tick.
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }

        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }
    }
}
