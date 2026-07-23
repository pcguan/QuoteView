namespace StockClient.Core.Quotes;

/// <summary>One extra field a market happens to report, ready for display.</summary>
public sealed record QuoteField(string Label, string Value);

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
