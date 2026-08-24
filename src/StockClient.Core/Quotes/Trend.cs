namespace StockClient.Core.Quotes;

/// <summary>One minute of the intraday line: price, running average, volume.</summary>
public sealed record TrendPoint
{
    /// <summary>Full timestamp, e.g. "2026-07-16 09:30". US night session crosses midnight.</summary>
    public required string Time { get; init; }

    public required double Price { get; init; }

    /// <summary>Session VWAP up to this minute — the average line.</summary>
    public required double AvgPrice { get; init; }

    public double Volume { get; init; }

    /// <summary>Clock time HH:mm for the axis, dropping the date.</summary>
    public string Clock => Time.Length >= 16 ? Time[^5..] : Time;
}

/// <summary>
/// A contract's intraday trend for one day, plus the previous close it is drawn
/// against. Intraday change is measured relative to <see cref="PreClose"/>.
/// </summary>
public sealed record TrendSeries
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required double PreClose { get; init; }
    public required IReadOnlyList<TrendPoint> Points { get; init; }

    /// <summary>Day-level closing metrics, attached by the server's after-close
    /// enrichment. Null on older snapshots and intraday series.</summary>
    public TrendDaySummary? Summary { get; init; }
}

/// <summary>The day's closing metrics for a snapshot: change %, turnover, volume
/// and the outer/inner split (A-share only, which is all the sweep covers).</summary>
public sealed record TrendDaySummary
{
    public double Percent { get; init; }
    public double Amount { get; init; }
    public double Volume { get; init; }
    public double Outer { get; init; }
    public double Inner { get; init; }
}
