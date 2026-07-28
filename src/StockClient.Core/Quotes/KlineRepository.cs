using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// The one entry point for K-line data: cache first, then EastMoney, then Tencent.
///
/// Keyed by the market's own trading date (via <see cref="IMarketClock"/>), so a
/// contract's full history is fetched at most once per trading day — every other
/// open that day reads the cache, which is what keeps the request rate low enough
/// to stay clear of throttling. A new trading date misses the cache and refetches,
/// which also makes front-adjustment always correct across ex-rights days without
/// any special handling.
///
/// The trading date alone is NOT enough to decide freshness, though: a series
/// pulled during the session ends on an unfinished candle (close = the price at
/// that moment, high/low/volume only partial). Serving that for the rest of the
/// day is what made the chart disagree with the quote after the close. So the
/// fetch time is stamped on the series, and a snapshot taken before the close is
/// topped up — the last candles are re-pulled and merged in, a couple of hundred
/// bytes rather than the full history — until the close settles it.
///
/// BOTH sources are cached, tagged with which one served them. Not caching the
/// Tencent fallback would mean re-hitting Tencent on every open while EastMoney is
/// throttled — which is exactly how Tencent would get throttled too. The tag lets
/// a hit report "腾讯(备用)·缓存" honestly; the next trading date refetches, so if
/// EastMoney has recovered by then the full data comes back on its own.
/// </summary>
public sealed class KlineRepository
{
    /// <summary>
    /// Minimum spacing between top-ups of the same series. The chart re-polls on
    /// a timer and a window can be reopened at will; this keeps that down to one
    /// small request per contract per interval.
    /// </summary>
    private static readonly TimeSpan TopUpInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many trailing candles a top-up re-pulls. Two, so the previous candle
    /// comes along as an overlap check rather than relying on the last one alone.
    /// </summary>
    private const int TopUpCount = 2;

    private readonly EastMoneyKlineClient _east;
    private readonly TencentKlineClient _tencent;
    private readonly KlineCache _cache;
    private readonly IMarketClock _clock;
    private readonly Func<DateTimeOffset> _now;

    public KlineRepository(
        EastMoneyKlineClient east, TencentKlineClient tencent, KlineCache cache, IMarketClock clock,
        Func<DateTimeOffset>? now = null)
    {
        _east = east;
        _tencent = tencent;
        _cache = cache;
        _clock = clock;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public async Task<(KlineSeries Series, string Source)> GetAsync(
        Contract contract, KlinePeriod period, KlineAdjust adjust, int count, CancellationToken cancellationToken)
    {
        var date = _clock.TradingDate(contract.Market);

        var cached = _cache.TryLoad(contract.Code, period, adjust, date);
        if (cached is not null)
        {
            // Pulled after the close: that day's candle is final, so the cache
            // stands for the rest of the day — the common case, no request.
            if (_clock.IsAfterClose(contract.Market, cached.FetchedAt))
                return (cached, Label(cached));

            // Intraday snapshot. Too soon to bother upstream again, serve as is.
            if (_now() - cached.FetchedAt < TopUpInterval)
                return (cached, Label(cached));

            var topped = await TopUpAsync(contract, cached, cancellationToken);
            if (topped is not null)
            {
                _cache.Save(topped, date);
                return (topped, Label(topped));
            }

            // Top-up failed (throttled, offline). Stale beats blank.
            return (cached, Label(cached));
        }

        try
        {
            var series = (await _east.FetchAsync(contract, period, adjust, count, cancellationToken))
                with { Source = "东财", FetchedAt = _now() };
            if (series.Candles.Count > 0) _cache.Save(series, date);
            return (series, series.Source);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var series = (await _tencent.FetchAsync(contract, period, adjust, cancellationToken))
                with { Source = "腾讯(备用)", FetchedAt = _now() };
            if (series.Candles.Count > 0) _cache.Save(series, date);
            return (series, series.Source);
        }
    }

    private static string Label(KlineSeries series) =>
        string.IsNullOrEmpty(series.Source) ? "缓存" : $"{series.Source}·缓存";

    /// <summary>
    /// Re-pulls the last few candles and merges them over the cached series, so
    /// the day's still-moving candle catches up without refetching the history.
    /// Null when upstream didn't answer, leaving the caller on the old data.
    ///
    /// Always goes to EastMoney, even for a series the Tencent fallback served:
    /// the merge is by date, so mixing is safe, and a cache stuck on the fallback
    /// gets accurate recent candles as soon as EastMoney is reachable again.
    /// </summary>
    private async Task<KlineSeries?> TopUpAsync(
        Contract contract, KlineSeries cached, CancellationToken cancellationToken)
    {
        KlineSeries latest;
        try
        {
            latest = await _east.FetchAsync(
                contract, cached.Period, cached.Adjust, TopUpCount, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (latest.Candles.Count == 0) return null;

        return cached with
        {
            Candles = Splice(cached.Candles, latest.Candles),
            FetchedAt = _now(),
        };
    }

    /// <summary>
    /// Replaces the cached tail from the first fresh candle's date onwards with
    /// the fresh candles.
    ///
    /// Cutting at a date instead of merging candle-by-candle is what makes this
    /// safe for week/month periods: EastMoney labels the running bucket with its
    /// latest trading day, so the same week comes back under a different date as
    /// the week goes on. Merging by date would leave both labels sitting there as
    /// two candles; cutting drops the whole stale tail first.
    /// </summary>
    private static IReadOnlyList<Kline> Splice(IReadOnlyList<Kline> cached, IReadOnlyList<Kline> latest)
    {
        var from = latest[0].Date;

        var spliced = cached
            .Where(k => string.CompareOrdinal(k.Date, from) < 0)
            .ToList();

        spliced.AddRange(latest);
        return spliced;
    }
}
