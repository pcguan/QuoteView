using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Serves the baselines a contract's period returns are measured from, fetching
/// each contract at most once per ITS OWN trading day.
///
/// Why per-market rather than one global "today": the six markets roll over at
/// different hours, so a single date would keep re-fetching Shanghai every US
/// evening, or leave New York a day stale every Asian morning.
///
/// The returns themselves are NOT stored — they move with the live price and are
/// computed against these numbers on every quote tick.
/// </summary>
public sealed class ReturnBaselineRepository
{
    /// <summary>
    /// Backoff after a failed fetch. EastMoney throttles this host in bursts, and
    /// there is nothing time-critical here — the data only changes once a day.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly ReturnBaselineClient _client;
    private readonly ReturnBaselineCache _cache;
    private readonly IMarketClock _clock;

    private readonly Dictionary<string, ReturnBaselines> _known;
    private DateTimeOffset _failedUntil = DateTimeOffset.MinValue;

    public ReturnBaselineRepository(
        ReturnBaselineClient client, ReturnBaselineCache cache, IMarketClock clock)
    {
        _client = client;
        _cache = cache;
        _clock = clock;
        _known = cache.Load();
    }

    /// <summary>Everything currently known, live price not applied.</summary>
    public IReadOnlyDictionary<string, ReturnBaselines> Current => _known;

    /// <summary>
    /// The trading date a baseline fetch actually DESCRIBES. The calendar date
    /// alone is wrong inside the [midnight, session-open) window — New York
    /// rolls past midnight hours before its session opens (Beijing noon), and
    /// a fetch there re-reads the OLD session under the new date: two entries
    /// carrying the same previous close, i.e. a fake 0.00% 昨日涨幅 rendered
    /// as blank (the AMKR/ASX/TSM report). Before 09:00 local — every covered
    /// market opens 09:00-09:30 — the feed still describes the previous
    /// weekday's session.
    /// </summary>
    private DateOnly BaselineDate(Market market)
    {
        var date = _clock.TradingDate(market);
        if (_clock.LocalTime(market) < new TimeOnly(9, 0)) date = date.AddDays(-1);
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            date = date.AddDays(-1);
        return date;
    }

    /// <summary>
    /// Brings the given contracts up to date, fetching only those whose own market
    /// has moved past what is stored. Returns true when anything changed, so the
    /// caller can refresh its rows.
    /// </summary>
    public async Task<bool> RefreshAsync(
        IReadOnlyList<Contract> contracts, CancellationToken cancellationToken)
    {
        if (contracts.Count == 0) return false;
        if (DateTimeOffset.Now < _failedUntil) return false;

        var stale = new List<(string Code, string SecId, DateOnly Date)>();

        foreach (var contract in contracts)
        {
            // Korea is skipped outright: measured, EastMoney returns the same
            // percentage for every period there, which IsUsable rejects anyway —
            // no reason to spend a slot in the batch on it.
            if (contract.Market == Market.KR) continue;

            var date = BaselineDate(contract.Market);
            var stamp = date.ToString("yyyy-MM-dd");

            if (_known.TryGetValue(contract.Code, out var have) && have.Date == stamp) continue;

            stale.Add((contract.Code, contract.EastMoneySecId, date));
        }

        if (stale.Count == 0) return false;

        IReadOnlyDictionary<string, ReturnBaselines> fetched;
        try
        {
            fetched = await _client.GetAsync(stale, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _failedUntil = DateTimeOffset.Now + RetryAfterFailure;
            return false;
        }

        if (fetched.Count == 0) return false;

        foreach (var (code, next) in fetched)
        {
            if (!_known.TryGetValue(code, out var old))
            {
                _known[code] = next;
                continue;
            }

            // A date REGRESSION (edge of the pre-open window) must never
            // overwrite a newer entry — it would wreck the prev/prior chain
            // 昨日涨幅 is built from.
            if (string.CompareOrdinal(next.Date, old.Date) < 0) continue;

            // Carry the outgoing previous close forward on a NEW day — that
            // pairing is what makes yesterday's return available at all. A
            // same-day refetch keeps the existing chain instead of collapsing
            // prior onto itself.
            _known[code] = old.Date != next.Date
                ? next with { PriorClose = old.PrevClose, PriorDate = old.Date }
                : next with { PriorClose = old.PriorClose, PriorDate = old.PriorDate };
        }

        _cache.Save(_known);
        return true;
    }
}
