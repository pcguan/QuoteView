namespace StockClient.Core.Quotes;

/// <summary>
/// Period returns (昨日/3日/5日/…/年初) computed from settled daily candles and
/// the live quote — replacing the EastMoney ulist baselines (v1.1.0).
///
/// The whole trick is anchoring "yesterday": the candle whose close equals the
/// QUOTE's own previous close is the session the feed currently calls 昨收, so
/// every derived value flips exactly when the quote itself rolls over — no
/// clock guessing, no rollover races (the entire bug family the old
/// snapshot-chain machinery kept producing). From that anchor, N日涨幅 is just
/// 现价 ÷ 收盘(N个交易日前) − 1, where 1 session ago IS the anchor.
///
/// Pure functions over immutable history — covered by unit tests.
/// </summary>
public static class PeriodReturns
{
    /// <summary>
    /// Index of the candle whose close matches the quote's previous close,
    /// searched newest-first (the newest match is by definition the session
    /// the quote refers to). -1 when nothing matches — history stale or the
    /// contract had a rights adjustment the candles don't reflect yet.
    /// </summary>
    public static int YesterdayIndex(IReadOnlyList<Kline> candles, double yesterdayClose)
    {
        if (yesterdayClose <= 0) return -1;

        var tolerance = Math.Max(yesterdayClose * 1e-3, 0.005);
        for (var i = candles.Count - 1; i >= 0; i--)
            if (Math.Abs(candles[i].Close - yesterdayClose) <= tolerance)
                return i;
        return -1;
    }

    /// <summary>
    /// The close <paramref name="daysAgo"/> trading sessions before today,
    /// where 1 session ago is the anchored yesterday. Null when the history
    /// doesn't reach that far.
    /// </summary>
    public static double? Baseline(IReadOnlyList<Kline> candles, int yesterdayIndex, int daysAgo)
    {
        if (yesterdayIndex < 0 || daysAgo < 1) return null;

        var i = yesterdayIndex - (daysAgo - 1);
        if (i < 0 || i >= candles.Count) return null;

        var close = candles[i].Close;
        return close > 0 ? close : null;
    }

    /// <summary>
    /// 年初至今 baseline: the last close of the year BEFORE <paramref name="thisYear"/>
    /// (today's year in the exchange's own calendar — NOT the anchor candle's:
    /// in the first days of January yesterday still belongs to the old year,
    /// and年初至今 must then measure from that very close, not the year before it).
    /// </summary>
    public static double? YearStartBaseline(
        IReadOnlyList<Kline> candles, int yesterdayIndex, int thisYear)
    {
        if (yesterdayIndex < 0) return null;

        for (var i = yesterdayIndex; i >= 0; i--)
        {
            var date = candles[i].Date;
            if (date.Length < 4 || !int.TryParse(date[..4], out var year)) return null;
            if (year < thisYear)
                return candles[i].Close > 0 ? candles[i].Close : null;
        }

        // History doesn't reach back into the previous year: with a fresh
        // listing that is CORRECT only when the first candle is this year's
        // first session — can't tell from here, so report nothing.
        return null;
    }

    /// <summary>Return from <paramref name="baseline"/> to <paramref name="price"/>, in %.</summary>
    public static double? Percent(double price, double? baseline) =>
        price > 0 && baseline is { } b and > 0 ? (price / b - 1) * 100 : null;

    /// <summary>
    /// The last completed session's move: anchor close ÷ its predecessor. This
    /// is what 昨日涨幅 shows at every moment, flipping with the quote's own
    /// rollover.
    /// </summary>
    public static double? PrevDayPercent(IReadOnlyList<Kline> candles, int yesterdayIndex)
    {
        if (yesterdayIndex < 1) return null;

        var prev = candles[yesterdayIndex - 1].Close;
        return prev > 0 ? (candles[yesterdayIndex].Close / prev - 1) * 100 : null;
    }
}
