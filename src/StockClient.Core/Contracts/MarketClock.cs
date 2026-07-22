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
}

public sealed class MarketClock : IMarketClock
{
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
