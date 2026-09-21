namespace StockClient.Core.Contracts;

/// <summary>
/// A-share (SH/SZ/BJ) trading calendar: the authoritative weekend + holiday + 调休
/// set, sourced once from 深交所's official monthList (see SzseCalendarClient).
/// Process-wide and swapped atomically. Until it's loaded — or for a date outside
/// the range it covers — callers fall back to a plain Mon–Fri check, which is
/// still right for weekends (the common non-trading case) and only misses the odd
/// out-of-range holiday. A-share only; HK/US/KR keep the weekday fallback.
/// </summary>
public static class TradingCalendar
{
    private sealed record Snapshot(HashSet<DateOnly> Days, DateOnly From, DateOnly To);

    private static volatile Snapshot? _snap;

    /// <summary>Replace the known A-share trading days spanning [from, to] inclusive.</summary>
    public static void Load(IEnumerable<DateOnly> tradingDays, DateOnly from, DateOnly to) =>
        _snap = new Snapshot(new HashSet<DateOnly>(tradingDays), from, to);

    /// <summary>True once a calendar has been loaded (from disk or the exchange).</summary>
    public static bool Loaded => _snap is not null;

    /// <summary>The exchange calendar's coverage, for the loader to decide a refresh.</summary>
    public static (DateOnly From, DateOnly To)? Range =>
        _snap is { } s ? (s.From, s.To) : null;

    /// <summary>Is this an A-share trading day? Uses the loaded calendar within its
    /// range; before load or outside it, falls back to Mon–Fri.</summary>
    public static bool IsAShareTradingDay(DateOnly d)
    {
        var s = _snap;
        return s is not null && d >= s.From && d <= s.To
            ? s.Days.Contains(d)
            : d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    public static bool IsAShareMarket(Market m) => m is Market.SH or Market.SZ or Market.BJ;

    /// <summary>A normalized code (e.g. SH600519 / SZ000001 / BJ920992) is A-share
    /// by its two-letter market prefix.</summary>
    public static bool IsAShareCode(string code) =>
        code.Length >= 2
        && Enum.TryParse<Market>(code[..2], ignoreCase: true, out var m)
        && IsAShareMarket(m);

    /// <summary>The right non-trading test for a date, market-aware: the exchange
    /// calendar for A-share, a weekday check otherwise.</summary>
    public static bool IsTradingDay(Market market, DateOnly d) =>
        IsAShareMarket(market)
            ? IsAShareTradingDay(d)
            : d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
