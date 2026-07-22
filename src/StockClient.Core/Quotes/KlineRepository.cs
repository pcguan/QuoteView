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

    public KlineRepository(
        EastMoneyKlineClient east, TencentKlineClient tencent, KlineCache cache, IMarketClock clock)
    {
        _east = east;
        _tencent = tencent;
        _cache = cache;
        _clock = clock;
    }

    public async Task<(KlineSeries Series, string Source)> GetAsync(
        Contract contract, KlinePeriod period, KlineAdjust adjust, int count, CancellationToken cancellationToken)
    {
        var date = _clock.TradingDate(contract.Market);

        var cached = _cache.TryLoad(contract.Code, period, adjust, date);
        if (cached is not null)
        {
            var label = string.IsNullOrEmpty(cached.Source) ? "缓存" : $"{cached.Source}·缓存";
            return (cached, label);
        }

        try
        {
            var series = (await _east.FetchAsync(contract, period, adjust, count, cancellationToken))
                with { Source = "东财" };
            if (series.Candles.Count > 0) _cache.Save(series, date);
            return (series, series.Source);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            var series = (await _tencent.FetchAsync(contract, period, adjust, cancellationToken))
                with { Source = "腾讯(备用)" };
            if (series.Candles.Count > 0) _cache.Save(series, date);
            return (series, series.Source);
        }
    }
}
