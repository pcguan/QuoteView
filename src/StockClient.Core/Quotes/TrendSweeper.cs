using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// After the A-share close, fetches the day's intraday trend for every SH/SZ
/// contract across all groups and persists it through <see cref="TrendCache"/> —
/// the history page reads those files.
///
/// SH/SZ only, by design: one market means one close time, so "after the close"
/// is a single well-defined moment. Other markets are ignored entirely.
///
/// Politeness rules, learned from trends2 being the throttling-prone endpoint
/// (v1.0.18): strictly sequential, a fixed gap between requests, a bounded batch
/// per round, and a file-existence check first so contracts the user already
/// looked at (the repository persists those on view) cost nothing. Failures are
/// not retried in-round — the next 10-minute pass picks up whatever is missing.
/// </summary>
public sealed class TrendSweeper : IAsyncDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(10);

    /// <summary>Gap between consecutive fetches — ~40 contracts a minute.</summary>
    private static readonly TimeSpan FetchGap = TimeSpan.FromMilliseconds(1500);

    /// <summary>Fetches per round, so one pass can't run unbounded.</summary>
    private const int MaxPerRound = 150;

    private readonly EastMoneyTrendClient _primary;
    private readonly TencentTrendClient _fallback;
    private readonly TrendCache _cache;
    private readonly IMarketClock _clock;
    private readonly Func<IReadOnlyList<Contract>> _targets;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// A weekday the feed answered with an older session's data — a holiday.
    /// One probe settles it; the rest of the day is skipped without a request.
    /// </summary>
    private DateOnly _holiday;

    /// <summary>One line per notable event, for the app's diagnostic log.</summary>
    public event Action<string>? Progress;

    public TrendSweeper(
        EastMoneyTrendClient primary, TencentTrendClient fallback, TrendCache cache,
        IMarketClock clock, Func<IReadOnlyList<Contract>> targets)
    {
        _primary = primary;
        _fallback = fallback;
        _cache = cache;
        _clock = clock;
        _targets = targets;
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepOnceAsync(cancellationToken);

            using var timer = new PeriodicTimer(CheckEvery);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SweepOnceAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The sweep is a convenience; it must never take the app down with it.
            Progress?.Invoke($"快照循环异常终止: {ex.Message}");
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        if (!_clock.IsAfterClose(Market.SH, DateTimeOffset.Now)) return;

        var date = _clock.TradingDate(Market.SH);
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        if (date == _holiday) return;

        // Snapshot + filter + dedupe. The provider marshals to the UI thread
        // itself, since group membership is UI-owned state.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<Contract>();
        foreach (var contract in _targets())
        {
            if (contract.Market is not (Market.SH or Market.SZ)) continue;
            if (!seen.Add(contract.Code)) continue;
            if (_cache.TryLoad(contract.Code, date) is null) missing.Add(contract);
        }

        if (missing.Count == 0) return;

        Progress?.Invoke($"分时快照 {date:yyyy-MM-dd}: 待补 {missing.Count} 只，开始拉取");
        var done = 0;
        var failed = 0;

        foreach (var contract in missing.Take(MaxPerRound))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var series = await FetchAsync(contract, cancellationToken);
            if (series is null)
            {
                failed++;
            }
            else if (!EndsOn(series, date))
            {
                // An older session came back: today is a holiday. The first such
                // answer settles it for every contract — stop asking.
                _holiday = date;
                Progress?.Invoke($"分时快照 {date:yyyy-MM-dd}: 非交易日（返回的是旧数据），本日不再尝试");
                return;
            }
            else
            {
                _cache.Save(series, date);
                done++;
            }

            await Task.Delay(FetchGap, cancellationToken);
        }

        Progress?.Invoke($"分时快照 {date:yyyy-MM-dd}: 本轮成功 {done}，失败 {failed}"
                         + (failed > 0 || missing.Count > MaxPerRound ? "（余下的 10 分钟后续拉）" : ""));
    }

    /// <summary>EastMoney first, Tencent second — the repository's own order.</summary>
    private async Task<TrendSeries?> FetchAsync(Contract contract, CancellationToken cancellationToken)
    {
        try
        {
            var primary = await _primary.FetchAsync(contract, cancellationToken);
            if (primary.Points.Count > 0) return primary;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through.
        }

        try
        {
            var backup = await _fallback.FetchAsync(contract, cancellationToken);
            return backup?.Points.Count > 0 ? backup : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Whether the series' last minute falls on the given date — TrendPoint
    /// carries the full "yyyy-MM-dd HH:mm" timestamp, so a stale answer is visible.</summary>
    private static bool EndsOn(TrendSeries series, DateOnly date) =>
        series.Points.Count > 0
        && series.Points[^1].Time.StartsWith(date.ToString("yyyy-MM-dd"), StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        _cts?.Cancel();
        loop = _loop;
        _loop = null;

        if (loop is not null)
        {
            try { await loop; }
            catch (Exception) { /* already logged inside */ }
        }

        _cts?.Dispose();
    }
}
