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
/// </summary>
public sealed class TrendRepository
{
    /// <summary>How long a cached day-series is served before a refetch.</summary>
    private const int StaleSeconds = 15;

    private readonly EastMoneyTrendClient _client;
    private readonly IMarketClock _clock;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TrendRepository(EastMoneyTrendClient client, IMarketClock clock)
    {
        _client = client;
        _clock = clock;
    }

    public async Task<TrendSeries?> GetAsync(Contract contract, CancellationToken cancellationToken)
    {
        var date = _clock.TradingDate(contract.Market);
        _cache.TryGetValue(contract.Code, out var cached);

        var fresh = cached is not null
                    && cached.Date == date
                    && (DateTime.UtcNow - cached.FetchedAtUtc).TotalSeconds < StaleSeconds;
        if (fresh) return cached!.Series;

        try
        {
            var series = await _client.FetchAsync(contract, cancellationToken);
            if (series.Points.Count == 0) return cached?.Series; // keep last good on an empty pull

            _cache[contract.Code] = new Entry(date, DateTime.UtcNow, series);
            return series;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested == false)
        {
            return cached?.Series; // best-effort thumbnail: keep showing the last series
        }
    }

    private sealed record Entry(DateOnly Date, DateTime FetchedAtUtc, TrendSeries Series);
}
