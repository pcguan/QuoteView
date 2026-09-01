using System.Diagnostics;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Polls the watched codes once per second — but only the markets that can
/// actually be trading.
///
/// Every tick is a single batched request; issuing one request per code would
/// be ~20x the traffic and is what gets a client rate-limited. Since v1.1.0
/// the tick only carries codes whose market is in (or near) its session —
/// local [08:30, close+30min] on a weekday; every 30th tick polls EVERYTHING
/// so closed markets still refresh their settled values twice a minute and a
/// fresh start fills the whole table. Overnight with all six markets closed
/// that is one request per 30s instead of one per second, for data that
/// cannot move.
/// </summary>
public sealed class QuotePoller : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Every Nth tick includes closed markets' codes too.</summary>
    private const int FullSweepEvery = 30;

    private static readonly TimeSpan SessionMargin = TimeSpan.FromMinutes(30);

    private readonly TencentQuoteClient _client;
    private readonly IMarketClock _clock = new MarketClock();
    private readonly object _gate = new();
    private int _tick;

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

    /// <summary>Weekday and inside [08:30 local, close + 30min] — all six
    /// covered markets open 09:00-09:30 local, so a fixed early edge covers
    /// the pre-open auction without needing per-market open times.</summary>
    private bool MarketLive(Market market)
    {
        var date = _clock.TradingDate(market);
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

        var now = _clock.LocalTime(market);
        var close = MarketInfo.Of(market).Close;
        return now >= new TimeOnly(8, 30) && now <= close.Add(SessionMargin);
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

        // Closed markets only ride the periodic full sweep (and the first tick,
        // _tick == 0, so a fresh start fills everything at once).
        if (_tick++ % FullSweepEvery != 0)
        {
            var liveByMarket = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            codes = codes.Where(code =>
            {
                if (!CodeMapper.TryParse(code, out var prefix, out _)) return true;
                if (!liveByMarket.TryGetValue(prefix, out var live))
                {
                    live = !Enum.TryParse<Market>(prefix, out var market) || MarketLive(market);
                    liveByMarket[prefix] = live;
                }
                return live;
            }).ToArray();

            if (codes.Length == 0) return;   // everything closed — wait for the sweep
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
