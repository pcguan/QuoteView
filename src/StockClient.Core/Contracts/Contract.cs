using System.Text.Json.Serialization;

namespace StockClient.Core.Contracts;

/// <summary>
/// One tradable instrument. Pinyin is stored to make search work, but is never
/// shown in the UI.
/// </summary>
public sealed record Contract
{
    /// <summary>Normalized code, e.g. SH600519 / BJ430418 / KR005930.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Full pinyin, e.g. guizhoumaotai. Empty for non-Chinese names.</summary>
    [JsonPropertyName("py")]
    public string Pinyin { get; init; } = "";

    /// <summary>Pinyin initials, e.g. gzmt.</summary>
    [JsonPropertyName("pyi")]
    public string PinyinInitials { get; init; } = "";

    /// <summary>Board / instrument category, tagged from the fetch filter.</summary>
    [JsonPropertyName("type")]
    public ContractType Type { get; init; } = ContractType.Unknown;

    /// <summary>Listing date as yyyy-MM-dd. Null when the market doesn't report it (KR).</summary>
    [JsonPropertyName("ipo")]
    public string? ListedOn { get; init; }

    /// <summary>Industry, e.g. 饰品 / 医疗保健. Null for KR.</summary>
    [JsonPropertyName("ind")]
    public string? Industry { get; init; }

    /// <summary>Region, e.g. 北京板块. A-shares only.</summary>
    [JsonPropertyName("reg")]
    public string? Region { get; init; }

    /// <summary>概念板块, comma separated. A-shares only.</summary>
    [JsonPropertyName("con")]
    public string? Concepts { get; init; }

    /// <summary>总股本 (shares). Not price-derived, so safe to cache.</summary>
    [JsonPropertyName("ts")]
    public long? TotalShares { get; init; }

    /// <summary>流通股 (shares).</summary>
    [JsonPropertyName("fs")]
    public long? FloatShares { get; init; }

    /// <summary>
    /// EastMoney market number (f13), the prefix its K-line secid needs:
    /// 1 = SH, 0 = SZ/BJ, 116 = HK, 105/106/107 = US (NASDAQ/NYSE/AMEX),
    /// 177 = KR. Stored because it is the only way to know which of the three
    /// US exchange numbers a symbol lives on — it can't be derived from the code.
    /// Null in caches written before this field existed; those refresh on their
    /// own trading-date rollover.
    /// </summary>
    [JsonPropertyName("mkt")]
    public int? MarketNumber { get; init; }

    [JsonIgnore]
    public Market Market => Enum.Parse<Market>(Code[..2]);

    /// <summary>
    /// EastMoney K-line secid, e.g. 1.600519 / 0.920418 / 116.00700 / 105.AAPL /
    /// 177.005930. Falls back to a per-market guess for pre-upgrade caches; the
    /// guess is exact for every market except US, where 105 (NASDAQ) is the most
    /// common but not guaranteed until the cache refreshes with a real f13.
    /// </summary>
    [JsonIgnore]
    public string EastMoneySecId
    {
        get
        {
            var number = MarketNumber ?? Market switch
            {
                Market.SH => 1,
                Market.SZ or Market.BJ => 0,
                Market.HK => 116,
                Market.KR => 177,
                _ => 105,
            };

            return $"{number}.{Code[2..]}";
        }
    }

    /// <summary>Localized category for display.</summary>
    [JsonIgnore]
    public string TypeDisplay => Type.Display();

    /// <summary>Share counts are only readable in 亿股.</summary>
    [JsonIgnore]
    public string TotalSharesDisplay => Yi(TotalShares);

    [JsonIgnore]
    public string FloatSharesDisplay => Yi(FloatShares);

    private static string Yi(long? shares) =>
        shares is null or 0 ? "" : (shares.Value / 1e8).ToString("0.##") + "亿";
}

/// <summary>One market's contract list for one trading date, as cached on disk.</summary>
public sealed record SymbolFile
{
    [JsonPropertyName("market")]
    public required string Market { get; init; }

    [JsonPropertyName("tradingDate")]
    public required string TradingDate { get; init; }

    [JsonPropertyName("fetchedAtUtc")]
    public required DateTimeOffset FetchedAtUtc { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("symbols")]
    public required IReadOnlyList<Contract> Symbols { get; init; }
}
