using System.Globalization;

namespace StockClient.Core.Quotes;

/// <summary>Candle interval.</summary>
public enum KlinePeriod
{
    Day,
    Week,
    Month,
}

/// <summary>
/// Price adjustment. Front-adjustment (qfq) is the charting default: it rebases
/// history onto today's price so the most recent candles match the live quote,
/// which is what a reader comparing the chart to the ticker expects.
/// </summary>
public enum KlineAdjust
{
    None,
    Qfq,
    Hfq,
}

/// <summary>
/// One candle. Column order mirrors the upstream row, which is NOT the usual
/// OHLC: EastMoney (and Tencent) both send date, open, <b>close</b>, high, low,
/// volume, amount — close before high. Reading it positionally in OHLC order
/// silently swaps close and high, so this record names every field.
/// </summary>
public sealed record Kline
{
    public required string Date { get; init; }
    public required double Open { get; init; }
    public required double Close { get; init; }
    public required double High { get; init; }
    public required double Low { get; init; }

    /// <summary>Shares/lots. 0 when the market doesn't report it.</summary>
    public double Volume { get; init; }

    /// <summary>Turnover in currency. 0 when not reported (KR reports 0).</summary>
    public double Amount { get; init; }

    public bool IsUp => Close >= Open;

    public static double Parse(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>A contract's candles for one period/adjust, plus the resolved name.</summary>
public sealed record KlineSeries
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required KlinePeriod Period { get; init; }
    public required KlineAdjust Adjust { get; init; }
    public required IReadOnlyList<Kline> Candles { get; init; }

    /// <summary>
    /// Which source produced this — "东财" or "腾讯(备用)". Persisted in the cache
    /// so a hit knows what it is serving, and so a Tencent-fallback cache reads
    /// back labelled as such rather than masquerading as EastMoney's full data.
    /// </summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// When this data was pulled. Persisted, and the whole reason the cache can
    /// tell a settled series from an intraday snapshot whose last candle is still
    /// moving. Default (all-zero) means a pre-1.0.14 cache file with no stamp —
    /// treated as stale so it gets refreshed once.
    /// </summary>
    public DateTimeOffset FetchedAt { get; init; }
}
