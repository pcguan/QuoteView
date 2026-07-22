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
