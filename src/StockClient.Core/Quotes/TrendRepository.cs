using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Serves today's intraday trend for one contract at a time, cached in memory for
/// the trading day and refetched only once it goes stale.
///
/// Unlike K-lines, today's trend keeps growing through the session, so the cache
/// cannot be write-once: each entry carries a fetch time and is refreshed past
/// <see cref="StaleSeconds"/>. trends2 returns the whole day in one request, so a
/// refresh is a single call with no incremental stitching. Keyed by the market's
/// trading date, so it rolls over on its own; a network hiccup keeps the last good
/// series rather than blanking the chart.
///
/// <b>Two sources</b>, same shape as <see cref="KlineRepository"/>: EastMoney
/// first, Tencent when it doesn't answer. EastMoney throttles this path with
/// connection resets — measured, with its own kline path on the same host still
/// working — and without a fallback that showed up as a chart drawing nothing at
/// all, which is indistinguishable from a bug in the chart. Tencent covers
/// A-shares and HK fully; US/KR come back with a single point there, which is
/// still better than an empty panel.
/// </summary>
public sealed class TrendRepository
{
    /// <summary>How long a cached day-series is served before a refetch.</summary>
    private const int StaleSeconds = 15;

    private readonly EastMoneyTrendClient _client;
    private readonly TencentTrendClient? _fallback;
    private readonly IMarketClock _clock;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TrendRepository(
        EastMoneyTrendClient client, IMarketClock clock, TencentTrendClient? fallback = null)
    {
        _client = client;
        _clock = clock;
        _fallback = fallback;
    }

    public async Task<TrendSeries?> GetAsync(Contract contract, CancellationToken cancellationToken)
    {
        var date = _clock.TradingDate(contract.Market);
        _cache.TryGetValue(contract.Code, out var cached);

        var fresh = cached is not null
                    && cached.Date == date
                    && (DateTime.UtcNow - cached.FetchedAtUtc).TotalSeconds < StaleSeconds;
        if (fresh) return cached!.Series;

        var series = await TryFetch(contract, cancellationToken);

        // Nothing usable from either source: keep showing the last good series.
        if (series is null || series.Points.Count == 0) return cached?.Series;

        _cache[contract.Code] = new Entry(date, DateTime.UtcNow, series);
        return series;
    }

    /// <summary>
    /// EastMoney, then Tencent. An empty result counts as a miss, not a success:
    /// a throttled trends2 can answer 200 with no rows at all.
    /// </summary>
    private async Task<TrendSeries?> TryFetch(Contract contract, CancellationToken cancellationToken)
    {
        try
        {
            var primary = await _client.FetchAsync(contract, cancellationToken);
            if (primary.Points.Count > 0) return primary;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through to the backup.
        }

        if (_fallback is null) return null;

        try
        {
            return await _fallback.FetchAsync(contract, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record Entry(DateOnly Date, DateTime FetchedAtUtc, TrendSeries Series);
}
