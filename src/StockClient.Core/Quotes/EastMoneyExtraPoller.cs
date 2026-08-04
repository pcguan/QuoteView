namespace StockClient.Core.Quotes;

/// <summary>
/// The secondary poll: EastMoney A-share fund-flow / 涨速, beside the primary
/// Tencent quote. Deliberately separate so it's isolated — if EastMoney throttles
/// or fails, only these columns go stale; the 1s price flow is untouched.
///
/// Slow (5s) and on-demand: started only while the relevant columns are on and the
/// group has A-shares. Adaptive backoff doubles the delay (to 30s) on repeated
/// failure and snaps back on success, so a throttled endpoint isn't hammered.
/// </summary>
public sealed class EastMoneyExtraPoller : IAsyncDisposable
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly EastMoneyQuoteClient _client;
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
            _targets = targets;
            if (targets.Count == 0) StopLoop();
            else StartLoop();
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

                await Task.Delay(delay, cancellationToken);
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
