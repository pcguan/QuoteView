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
/// <b>The running candle is dropped, not drawn.</b> During the session the last
/// row upstream returns is unfinished — its close is just the price at that
/// instant, and high/low/volume only cover the day so far — so it is cut off and
/// the chart ends at the previous close. Once the market has closed, the day's
/// candle is final and is fetched once and cached. That is the whole reason the
/// fetch time is stamped on the series: a cache written during the session has to
/// be refetched after the bell, but is otherwise good all day (it can't go stale —
/// it holds nothing but settled candles).
///
/// So a contract costs at most two full fetches a day: one if it was opened during
/// the session, one after the close.
///
/// BOTH sources are cached, tagged with which one served them. Not caching the
/// Tencent fallback would mean re-hitting Tencent on every open while EastMoney is
/// throttled — which is exactly how Tencent would get throttled too. The tag lets
/// a hit report "腾讯(备用)·缓存" honestly; the next trading date refetches, so if
/// EastMoney has recovered by then the full data comes back on its own.
/// </summary>
public sealed class KlineRepository
{
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
        var settled = _clock.IsAfterClose(contract.Market, _now());

        var cached = _cache.TryLoad(contract.Code, period, adjust, date);
        if (cached is not null && IsFresh(cached, contract.Market, settled))
            return (cached, Label(cached));

        try
        {
            var series = await _east.FetchAsync(contract, period, adjust, count, cancellationToken);
            return Store(series with { Source = "东财" }, date, settled);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A cache that only needed the closing candle added still draws the
            // whole history. Better that than swapping it for Tencent's fallback,
            // which is same-day only for BJ/US/KR and would wipe the history.
            if (cached is not null) return (cached, Label(cached));

            var series = await _tencent.FetchAsync(contract, period, adjust, cancellationToken);
            return Store(series with { Source = "腾讯(备用)" }, date, settled);
        }
    }

    /// <summary>
    /// A cached series holds settled candles only, so it stays good for the whole
    /// day — except for the one transition that matters: taken during the session,
    /// read after the close, where today's candle is now available and missing
    /// from it.
    /// </summary>
    private bool IsFresh(KlineSeries cached, Market market, bool settled) =>
        !settled || _clock.IsAfterClose(market, cached.FetchedAt);

    private (KlineSeries, string) Store(KlineSeries series, DateOnly date, bool settled)
    {
        var stored = series with
        {
            Candles = DropRunning(series.Candles, date, settled),
            FetchedAt = _now(),
        };

        if (stored.Candles.Count > 0) _cache.Save(stored, date);
        return (stored, stored.Source);
    }

    /// <summary>
    /// Cuts the still-running candle while the market is open.
    ///
    /// Matched on the date rather than just taking the last row off: on a weekend
    /// or a holiday the last row is an earlier session's, already final, and must
    /// stay. For week/month periods EastMoney labels the running bucket with its
    /// latest trading day, so the same check catches those too.
    /// </summary>
    private static IReadOnlyList<Kline> DropRunning(
        IReadOnlyList<Kline> candles, DateOnly tradingDate, bool settled)
    {
        if (settled || candles.Count == 0) return candles;

        return candles[^1].Date == tradingDate.ToString("yyyy-MM-dd")
            ? candles.Take(candles.Count - 1).ToArray()
            : candles;
    }

    private static string Label(KlineSeries series) =>
        string.IsNullOrEmpty(series.Source) ? "缓存" : $"{series.Source}·缓存";
}
