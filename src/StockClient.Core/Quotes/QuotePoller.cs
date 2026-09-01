using System.Diagnostics;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Polls the watched codes once per second — but only the markets that can
/// actually be trading.
///
/// Every tick is a single batched request; issuing one request per code would
/// be ~20x the traffic and is what gets a client rate-limited. Since v1.1.0
/// the tick only carries codes whose market is in (or near) its session (see
/// <see cref="IMarketClock.IsLive"/>); every 30th tick polls EVERYTHING so
/// closed markets still refresh their settled values twice a minute, and a new
/// target (or a resume) restarts that count so its first tick covers the whole
/// table. Overnight with all six markets closed that is one request per 30s
/// instead of one per second, for data that cannot move.
///
/// A dead upstream backs off too: after three consecutive failures only every
/// fifteenth tick still probes, so an outage stops costing a connection a
/// second while recovery is still noticed within ~15s.
/// </summary>
public sealed class QuotePoller : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Every Nth tick includes closed markets' codes too.</summary>
    private const int FullSweepEvery = 30;

    /// <summary>Consecutive failures before the loop starts skipping ticks.</summary>
    private const int FailuresBeforeBackoff = 3;

    /// <summary>While backing off, only every Nth tick actually asks upstream.</summary>
    private const int BackoffEvery = 15;

    private readonly TencentQuoteClient _client;
    private readonly IMarketClock _clock = new MarketClock();
    private readonly object _gate = new();
    private int _tick;
    private int _failStreak;

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
            // 代码集合也要比：同组内增删合约同样会把整张表重建成占位行
            // （AddCode/RemoveCodes/TransferCodes 都走 RebuildRows），groupId
            // 却没变——只认 groupId 的话，闭市时段那张表要空等最多 29 拍。
            var next = codes?.ToArray() ?? Array.Empty<string>();
            var changed = _groupId != groupId
                || !_codes.SequenceEqual(next, StringComparer.OrdinalIgnoreCase);
            _groupId = groupId;
            _codes = next;

            if (groupId is null || _codes.Length == 0)
            {
                StopLoop();
                return;
            }

            // A new target must poll EVERYTHING on its first tick: the rows were
            // just rebuilt as "无效代码" placeholders, and a group of closed
            // markets would otherwise sit blank until the next 30-tick sweep.
            // (StartLoop is a no-op on a running loop, so _tick has to be reset
            // here rather than there.)
            if (changed) _tick = 0;

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
            // Waking from minimize/sleep: refresh everything at once rather
            // than leaving closed markets on stale values for half a minute.
            _tick = 0;
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

        int tick;
        lock (_gate)
        {
            if (_groupId is null || _codes.Length == 0) return;
            groupId = _groupId;
            codes = _codes;
            tick = _tick++;   // reset by SetTarget/Resume on another thread
        }

        // Upstream is down (or throttling us): stop hammering it once per
        // second. Every 15th tick still probes, so recovery is noticed within
        // ~15s without the dead-connection storm in between.
        if (_failStreak >= FailuresBeforeBackoff && tick % BackoffEvery != 0) return;

        // Closed markets only ride the periodic full sweep (and the first tick,
        // tick == 0, so a fresh target fills everything at once).
        if (tick % FullSweepEvery != 0)
        {
            var liveByMarket = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            codes = codes.Where(code =>
            {
                if (!CodeMapper.TryParse(code, out var prefix, out _)) return true;
                if (!liveByMarket.TryGetValue(prefix, out var live))
                {
                    live = !Enum.TryParse<Market>(prefix, out var market) || _clock.IsLive(market);
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

            _failStreak = 0;
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

            _failStreak++;
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
