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
    /// Long on purpose: retrying INTO an active throttle keeps it alive, which
    /// is how a half-rebuilt cache stayed half-rebuilt all evening.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

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
    /// True when the market's session for its current baseline date cannot be
    /// trading any more: a later calendar day (weekend, overnight), or past the
    /// close (with settle margin) on the day itself. Only in this window is an
    /// idle feed snapshot trusted — mid-session, 现价 merely CROSSING 昨收
    /// would fake one.
    /// </summary>
    private bool PostWindow(Market market)
    {
        var date = BaselineDate(market);
        return _clock.TradingDate(market) != date
               || _clock.IsAfterClose(market, DateTimeOffset.Now);
    }

    /// <summary>
    /// Brings the given contracts up to date. A contract is due when its market's
    /// baseline date moved past what is stored, or after its session closed and
    /// the post-close rollover hasn't been captured yet (<c>Settled</c>).
    /// Returns true when anything changed, so the caller can refresh its rows.
    /// </summary>
    public async Task<bool> RefreshAsync(
        IReadOnlyList<Contract> contracts, CancellationToken cancellationToken)
    {
        if (contracts.Count == 0) return false;
        if (DateTimeOffset.Now < _failedUntil) return false;

        var stale = new List<(string Code, string SecId, DateOnly Date)>();
        var marketOf = new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase);

        foreach (var contract in contracts)
        {
            // Korea is skipped outright: measured, EastMoney returns the same
            // percentage for every period there, which IsUsable rejects anyway —
            // no reason to spend a slot in the batch on it.
            if (contract.Market == Market.KR) continue;

            var date = BaselineDate(contract.Market);
            var stamp = date.ToString("yyyy-MM-dd");

            // An entry with NO prior yet (fresh cache, new contract, or a fetch
            // that raced ahead of the feed's own rollover) keeps retrying every
            // pass — otherwise a morning fetch that lands before 东财 rolls 昨收
            // stamps the new date and leaves 昨日涨幅 blank for the whole
            // session. The chain completes at the first observed roll and the
            // retries stop with it.
            if (_known.TryGetValue(contract.Code, out var have) && have.Date == stamp
                && have.PriorClose > 0
                && (have.Settled || !PostWindow(contract.Market)))
                continue;

            marketOf[contract.Code] = contract.Market;
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

        if (fetched.Count == 0)
        {
            // A throttled EastMoney often answers HTTP 200 with an EMPTY list —
            // that is a failure too. Without this backoff the 10-minute sweep
            // re-hit the throttled host every tick and kept itself throttled.
            _failedUntil = DateTimeOffset.Now + RetryAfterFailure;
            return false;
        }

        foreach (var (code, next) in fetched)
        {
            // The chain advances on the previous-close VALUE, not the calendar:
            // f18 flips exactly once per session (at its close), while the
            // clock-guessed date stamp lies inside rollover windows — stamping
            // a weekend fetch "Friday" once paired Friday's close against
            // Wednesday's and displayed the ratio as 昨日涨幅.
            var post = marketOf.TryGetValue(code, out var market) && PostWindow(market);
            var implied = post ? next.ImpliedPrior : 0;
            var settledNow = implied > 0;

            if (!_known.TryGetValue(code, out var old))
            {
                _known[code] = next with
                {
                    PriorClose = implied,
                    PriorDate = "",
                    PriorFromFeed = implied > 0,
                    Settled = settledNow,
                };
                continue;
            }

            var rolled = old.PrevClose > 0 && next.PrevClose > 0
                && Math.Abs(next.PrevClose - old.PrevClose) > old.PrevClose * 2e-4;
            var date = MaxDate(next.Date, old.Date);

            if (!rolled)
            {
                // Same session state: adopt the fresh period baselines, keep the
                // chain. Advancing onto a new baseline date re-arms Settled so
                // the NEXT close gets captured too.
                var advanced = date != old.Date;
                _known[code] = next with
                {
                    Date = date,
                    PriorClose = old.PriorClose,
                    PriorDate = old.PriorDate,
                    PriorFromFeed = old.PriorFromFeed,
                    Settled = settledNow || (!advanced && old.Settled),
                };
                continue;
            }

            // Session boundary crossed. The idle snapshot names the exact
            // predecessor; a stored entry that doesn't match it is NOT adjacent
            // (an unobserved double rollover) and must not be paired.
            var adjacent = implied > 0 && Math.Abs(old.PrevClose - implied) <= implied * 1e-3;
            _known[code] = implied > 0 && !adjacent
                ? next with
                {
                    Date = date, PriorClose = implied, PriorDate = "",
                    PriorFromFeed = true, Settled = settledNow,
                }
                : next with
                {
                    Date = date, PriorClose = old.PrevClose, PriorDate = old.Date,
                    PriorFromFeed = adjacent, Settled = settledNow,
                };
        }

        _cache.Save(_known);
        return true;
    }

    private static string MaxDate(string a, string b) =>
        string.CompareOrdinal(a, b) >= 0 ? a : b;
}
