namespace StockClient.Core.Quotes;

/// <summary>One extra field a market happens to report, ready for display.</summary>
public sealed record QuoteField(string Label, string Value);

/// <summary>
/// One level of the order book. Volume is in the market's own unit — 手 for
/// A-shares, 股 for US — and is 0 where the feed reports the price but no size
/// (HK, every level).
/// </summary>
public sealed record DepthLevel(double Price, double Volume);

/// <summary>
/// The order book as far as the feed reports it: five levels a side for
/// A-shares, level one only for US, price-without-size for HK, nothing for KR.
/// Levels the feed left empty are dropped, so an empty list means "not reported"
/// rather than "no orders".
/// </summary>
/// <summary>
/// Display decimals for a price context. At least 2 — the A-share convention —
/// raised to 3-4 only when a price in sight ACTUALLY carries those digits
/// (0.001-tick ETFs). Never inferred from 现价 alone: the moment it lands on a
/// round 5.90, trailing-zero stripping says "one decimal" and the whole order
/// book renders as 5.9 until the next tick moves off the round number.
/// </summary>
public static class PriceScale
{
    public static int Decimals(double now, QuoteDepth? depth = null)
    {
        var most = DigitsOf(now);
        if (depth is not null)
        {
            foreach (var level in depth.Bids) most = Math.Max(most, DigitsOf(level.Price));
            foreach (var level in depth.Asks) most = Math.Max(most, DigitsOf(level.Price));
        }
        return Math.Clamp(most, 2, 4);
    }

    private static int DigitsOf(double v)
    {
        var text = v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }
}

public sealed record QuoteDepth
{
    public IReadOnlyList<DepthLevel> Bids { get; init; } = Array.Empty<DepthLevel>();
    public IReadOnlyList<DepthLevel> Asks { get; init; } = Array.Empty<DepthLevel>();

    public bool IsEmpty => Bids.Count == 0 && Asks.Count == 0;

    /// <summary>Largest size on either side, for scaling the bars. 0 when no sizes are reported.</summary>
    public double MaxVolume =>
        Math.Max(
            Bids.Count == 0 ? 0 : Bids.Max(l => l.Volume),
            Asks.Count == 0 ? 0 : Asks.Max(l => l.Volume));
}

/// <summary>
/// A live quote.
///
/// The core properties are the fields verified to sit at the same index for
/// every market. Everything past index ~45 diverges (Tencent returns 市净率 at
/// [46] for A-shares but the English name there for HK/US), so market-specific
/// fields arrive in <see cref="Extras"/> instead, populated only from indices
/// whose meaning was confirmed for that market.
/// </summary>
public sealed record Quote
{
    public required string Code { get; init; }
    public required string Name { get; init; }

    public double Now { get; init; }
    public double Yesterday { get; init; }
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }

    /// <summary>Change amount, as reported at [31].</summary>
    public double Change { get; init; }

    /// <summary>Change percent, as reported at [32]. Already a percentage (2.98 = 2.98%).</summary>
    public double Percent { get; init; }

    /// <summary>Provider timestamp; the format differs per market.</summary>
    public string Time { get; init; } = "";

    // Structured numerics for grid columns, normalized per market at parse time
    // (amounts to the raw currency unit, caps from 亿 to raw). Null = the market
    // doesn't report it — distinct from a genuine 0. The raw per-market strings
    // stay in Extras for the detail badges (真实值).

    /// <summary>成交量, native units: 手 for A-shares, 股 elsewhere.</summary>
    public double? Volume { get; init; }

    /// <summary>成交额 in the raw currency (元/HKD/USD). Null for KR.</summary>
    public double? Amount { get; init; }

    /// <summary>换手率 %. A-shares and US.</summary>
    public double? TurnoverRate { get; init; }

    /// <summary>量比. A-shares only.</summary>
    public double? VolumeRatio { get; init; }

    /// <summary>振幅 %.</summary>
    public double? Amplitude { get; init; }

    /// <summary>均价. A-shares only.</summary>
    public double? AvgPrice { get; init; }

    /// <summary>市盈率 TTM.</summary>
    public double? PeTtm { get; init; }

    /// <summary>市净率. A-shares only — [46] is the English name for HK/US.</summary>
    public double? Pb { get; init; }

    /// <summary>流通市值, raw currency (from 亿 × 1e8).</summary>
    public double? FloatCap { get; init; }

    /// <summary>总市值, raw currency (from 亿 × 1e8).</summary>
    public double? TotalCap { get; init; }

    /// <summary>涨停价. A-shares only — [47]/[48] mean the 52-week range elsewhere.</summary>
    public double? LimitUp { get; init; }

    /// <summary>跌停价. A-shares only.</summary>
    public double? LimitDown { get; init; }

    /// <summary>52周最高. HK/US/KR.</summary>
    public double? Week52High { get; init; }

    /// <summary>52周最低. HK/US/KR.</summary>
    public double? Week52Low { get; init; }

    /// <summary>股息率 %. HK only.</summary>
    public double? DividendYield { get; init; }

    /// <summary>外盘 (手). A-shares only.</summary>
    public double? OuterVolume { get; init; }

    /// <summary>内盘 (手). A-shares only.</summary>
    public double? InnerVolume { get; init; }

    /// <summary>
    /// Order book, parsed from the same response as everything else — the feed
    /// already carries it, so this costs no extra request.
    /// </summary>
    public QuoteDepth Depth { get; init; } = new();

    /// <summary>Market-specific fields, only those confirmed for this market.</summary>
    public IReadOnlyList<QuoteField> Extras { get; init; } = Array.Empty<QuoteField>();

    public const string MissingName = "---";

    public bool IsMissing => Name == MissingName;

    public static Quote Missing(string code) => new() { Code = code, Name = MissingName };
}

/// <summary>One poll of the active group.</summary>
public sealed record QuoteTick
{
    public required string GroupId { get; init; }
    public required IReadOnlyList<Quote> Quotes { get; init; }
    public required long LatencyMs { get; init; }
    public DateTime At { get; init; } = DateTime.Now;
}
