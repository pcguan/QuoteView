using System.Diagnostics;

namespace StockClient.Core.Quotes;

/// <summary>
/// Polls the active group once per second.
///
/// Exactly one group is polled — the active one. Every tick is a single batched
/// request covering all of its codes; issuing one request per code would be ~20x
/// the traffic and is what gets a client rate-limited.
/// </summary>
public sealed class QuotePoller : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TencentQuoteClient _client;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _groupId;
    private string[] _codes = Array.Empty<string>();

    public event Action<QuoteTick>? Tick;
    public event Action<string>? Failed;

    public QuotePoller(TencentQuoteClient client) => _client = client;

    /// <summary>Points the poller at a group, or pass null to stop.</summary>
    public void SetTarget(string? groupId, IEnumerable<string>? codes)
    {
        lock (_gate)
        {
            _groupId = groupId;
            _codes = codes?.ToArray() ?? Array.Empty<string>();

            if (groupId is null || _codes.Length == 0)
            {
                StopLoop();
                return;
            }

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
            if (_groupId is not null && _codes.Length > 0) StartLoop();
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
        // Fire at once so the table isn't blank for the first second.
        await PollOnceAsync(cancellationToken);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Awaiting inside the loop is what prevents pile-up: a slow tick
                // delays the next one rather than stacking a second request.
                await PollOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop / target change.
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        string groupId;
        string[] codes;

        lock (_gate)
        {
            if (_groupId is null || _codes.Length == 0) return;
            groupId = _groupId;
            codes = _codes;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var quotes = await _client.GetQuotesAsync(codes, cancellationToken);

            // Drop results for a group the user already switched away from.
            lock (_gate)
            {
                if (_groupId != groupId) return;
            }

            Tick?.Invoke(new QuoteTick
            {
                GroupId = groupId,
                Quotes = quotes,
                LatencyMs = sw.ElapsedMilliseconds,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_groupId != groupId) return;
            }

            Failed?.Invoke(ex.Message);
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
