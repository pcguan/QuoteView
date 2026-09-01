namespace StockClient.Core.Contracts;

/// <summary>
/// Decides which trading date a market is currently on.
///
/// The date must be computed in the EXCHANGE's timezone, not the client's.
/// Using the client's local date breaks US badly: at Beijing 00:00 the US list
/// would refresh while New York is still mid-session on the previous day, and
/// the stamp would then read "today", so the real US rollover twelve hours later
/// is skipped — leaving the US list permanently one trading day stale.
///
/// Market-local calendar date is a sound stand-in for "trading day" here because
/// none of these six markets has a continuous session that crosses its own local
/// midnight (SH/SZ/BJ 9:30-15:00, HK 9:30-16:00, KR 9:00-15:30, US 9:30-16:00).
/// Weekends and holidays simply have no session, so at worst the list is fetched
/// once for a day that never trades — still never more than once per day. That
/// avoids shipping a holiday calendar for four countries, which would go stale
/// and cause missed refreshes.
/// </summary>
public interface IMarketClock
{
    /// <summary>Current trading date for the market, in the exchange's own timezone.</summary>
    DateOnly TradingDate(Market market);

    /// <summary>
    /// True when the instant falls after that market's close (plus a settle
    /// margin) on its own local date — i.e. that day's candle is final.
    ///
    /// Data taken before this point contains an unfinished candle for the day,
    /// which is why the K-line cache can't simply be keyed by trading date alone.
    /// </summary>
    bool IsAfterClose(Market market, DateTimeOffset instant);

    /// <summary>Current wall-clock time in the exchange's own timezone.</summary>
    TimeOnly LocalTime(Market market);

    /// <summary>
    /// True when <paramref name="instant"/> lands after a SPECIFIC trading
    /// date's close (plus settle margin) — the calendar-aware form of the
    /// overload above, which compares times of day only.
    ///
    /// The difference matters wherever the judged date isn't today: a fetch at
    /// Saturday 09:00 IS after Friday's close, even though 09:00 is not "after
    /// close" on any clock face.
    /// </summary>
    bool IsAfterClose(Market market, DateOnly date, DateTimeOffset instant);

    /// <summary>
    /// True when this market could be trading right now: a weekday inside
    /// [08:30 local, close + 30min]. All six covered markets open 09:00-09:30
    /// local, so one early edge covers every pre-open auction without needing
    /// per-market open times. Pollers use it to stop asking closed markets for
    /// data that cannot move.
    /// </summary>
    bool IsLive(Market market);

    /// <summary>
    /// The trading date a daily-K fetch should be keyed and judged by, plus
    /// whether that date's candle is final.
    ///
    /// On a weekday it is today, settled once the close (plus settle margin)
    /// has passed — the long-standing rule that lets an intraday fetch be
    /// replaced once after the bell. On a WEEKEND it rolls back to Friday and
    /// reports settled: no session happens on Saturday or Sunday, so keying by
    /// the calendar date only re-fetched, every weekend day and twice per day,
    /// history that could not have changed.
    /// </summary>
    (DateOnly Date, bool Settled) KlineDay(Market market);
}

public sealed class MarketClock : IMarketClock
{
    /// <summary>
    /// Grace period after the bell before the day's candle is trusted as final.
    /// The upstream feeds keep settling the close for a few minutes (closing
    /// auction prints, then the history endpoint catching up), so treating the
    /// bell itself as the cutoff would freeze a not-quite-final candle for the
    /// rest of the day.
    /// </summary>
    private static readonly TimeSpan SettleMargin = TimeSpan.FromMinutes(20);

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, TimeZoneInfo> _zones = new();

    public MarketClock(Func<DateTimeOffset>? utcNow = null) =>
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public DateOnly TradingDate(Market market)
    {
        var info = MarketInfo.Of(market);
        var zone = ResolveZone(info.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(_utcNow(), zone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    public TimeOnly LocalTime(Market market)
    {
        var info = MarketInfo.Of(market);
        var local = TimeZoneInfo.ConvertTime(_utcNow(), ResolveZone(info.TimeZoneId));
        return TimeOnly.FromDateTime(local.DateTime);
    }

    public bool IsLive(Market market)
    {
        if (TradingDate(market).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

        var now = LocalTime(market);
        return now >= SessionOpenEdge && now <= MarketInfo.Of(market).Close.Add(SettleMargin);
    }

    private static readonly TimeOnly SessionOpenEdge = new(8, 30);

    public (DateOnly Date, bool Settled) KlineDay(Market market)
    {
        var date = TradingDate(market);
        if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            return (date, IsAfterClose(market, _utcNow()));

        // Weekend: the last session ended before it began, so its candle is
        // final by construction — no time-of-day test can say so, because
        // IsAfterClose only looks at the clock, not the calendar.
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            date = date.AddDays(-1);
        return (date, true);
    }

    public bool IsAfterClose(Market market, DateOnly date, DateTimeOffset instant)
    {
        var info = MarketInfo.Of(market);
        var local = TimeZoneInfo.ConvertTime(instant, ResolveZone(info.TimeZoneId));
        var localDate = DateOnly.FromDateTime(local.DateTime);

        if (localDate != date) return localDate > date;
        return TimeOnly.FromDateTime(local.DateTime) >= info.Close.Add(SettleMargin);
    }

    public bool IsAfterClose(Market market, DateTimeOffset instant)
    {
        var info = MarketInfo.Of(market);
        var local = TimeZoneInfo.ConvertTime(instant, ResolveZone(info.TimeZoneId));
        return TimeOnly.FromDateTime(local.DateTime) >= info.Close.Add(SettleMargin);
    }

    private TimeZoneInfo ResolveZone(string id)
    {
        if (_zones.TryGetValue(id, out var cached)) return cached;

        // DST must come from the OS timezone database; computing it by hand goes
        // wrong every time the rules change.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        _zones[id] = zone;
        return zone;
    }
}
