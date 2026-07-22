namespace StockClient.Core.Contracts;

public enum Market
{
    SH,
    SZ,
    BJ,
    HK,
    US,
    KR,
}

/// <summary>
/// Instrument category.
///
/// Tagged from the filter that returned the row, not from the response's f19.
/// f19 does echo the `t:` code, but it is 0 for every Korean row, and relying on
/// a response field is the same mistake that made Beijing rows look like
/// Shenzhen ones.
/// </summary>
public enum ContractType
{
    Unknown,

    /// <summary>No board subdivision available (US / KR common stock).</summary>
    Stock,

    /// <summary>主板</summary>
    MainBoard,

    /// <summary>科创板</summary>
    Star,

    /// <summary>创业板 (incl. HK GEM)</summary>
    ChiNext,

    /// <summary>北交所</summary>
    BeijingBoard,

    /// <summary>ETF</summary>
    Etf,

    /// <summary>债券（含港股人民币主权债）</summary>
    Bond,

    /// <summary>优先股</summary>
    PreferredStock,

    /// <summary>存托凭证 (ADR)</summary>
    Adr,

    /// <summary>封闭式基金</summary>
    Fund,

    /// <summary>权证</summary>
    Warrant,

    /// <summary>单位 (SPAC Unit)</summary>
    Unit,

    /// <summary>信托 / REITs 等其它</summary>
    Other,
}

public static class ContractTypes
{
    public static string Display(this ContractType type) => type switch
    {
        ContractType.Stock => "股票",
        ContractType.MainBoard => "主板",
        ContractType.Star => "科创板",
        ContractType.ChiNext => "创业板",
        ContractType.BeijingBoard => "北交所",
        ContractType.Etf => "ETF",
        ContractType.Bond => "债券",
        ContractType.PreferredStock => "优先股",
        ContractType.Adr => "存托凭证",
        ContractType.Fund => "封闭基金",
        ContractType.Warrant => "权证",
        ContractType.Unit => "单位",
        ContractType.Other => "其它",
        _ => "-",
    };
}

/// <summary>One EastMoney `fs` query and the category its rows belong to.</summary>
public sealed record MarketFilter(string Fs, ContractType Type);

/// <summary>
/// US instrument categories, read from f19.
///
/// For US rows f19 genuinely encodes the instrument type, and the m:105/106/107
/// codes are exchanges (NASDAQ / NYSE / AMEX) rather than categories — ETFs list
/// on all three, so tagging from the filter mislabels ~300 of them as 股票.
/// This is not the f19 that fails elsewhere: that problem is about the MARKET
/// (Beijing shares Shenzhen's market number, and Korean rows report f19=0).
/// </summary>
public static class UsSecurityTypes
{
    public static ContractType From(int f19) => f19 switch
    {
        1 => ContractType.Stock,
        2 => ContractType.PreferredStock,
        3 => ContractType.Adr,
        4 => ContractType.Fund,
        5 => ContractType.Etf,
        6 => ContractType.Bond,
        8 => ContractType.Warrant,
        9 => ContractType.Unit,
        _ => ContractType.Stock,
    };
}

/// <summary>
/// Per-market metadata: how to fetch it from EastMoney, and which clock decides
/// its trading date.
/// </summary>
public sealed record MarketInfo
{
    public required Market Market { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>IANA id. .NET resolves these on Windows via ICU.</summary>
    public required string TimeZoneId { get; init; }

    /// <summary>
    /// Boards and ETFs are separate queries, so each market has several.
    ///
    /// Each is fetched with its own explicit filter and tagged from that filter —
    /// never from the response's market number. EastMoney reports Beijing rows as
    /// market 0, the same as Shenzhen, and Tencent needs bj*/sz* correct or the
    /// row silently vanishes from the quote response.
    /// </summary>
    public required IReadOnlyList<MarketFilter> Filters { get; init; }

    private static readonly IReadOnlyDictionary<Market, MarketInfo> All =
        new Dictionary<Market, MarketInfo>
        {
            [Market.SH] = new()
            {
                Market = Market.SH,
                DisplayName = "沪A",
                TimeZoneId = "Asia/Shanghai",
                Filters = new MarketFilter[]
                {
                    new("m:1+t:2", ContractType.MainBoard),
                    new("m:1+t:23", ContractType.Star),
                    new("m:1+b:MK0021,m:1+b:MK0022,m:1+b:MK0023,m:1+b:MK0024", ContractType.Etf),
                },
            },
            [Market.SZ] = new()
            {
                Market = Market.SZ,
                DisplayName = "深A",
                TimeZoneId = "Asia/Shanghai",
                Filters = new MarketFilter[]
                {
                    new("m:0+t:6", ContractType.MainBoard),
                    new("m:0+t:80", ContractType.ChiNext),
                    new("m:0+b:MK0021,m:0+b:MK0022,m:0+b:MK0023,m:0+b:MK0024", ContractType.Etf),
                },
            },
            [Market.BJ] = new()
            {
                Market = Market.BJ,
                DisplayName = "北交所",
                TimeZoneId = "Asia/Shanghai",
                Filters = new MarketFilter[]
                {
                    new("m:0+t:81+s:2048", ContractType.BeijingBoard),
                },
            },
            [Market.HK] = new()
            {
                Market = Market.HK,
                DisplayName = "港股",
                TimeZoneId = "Asia/Hong_Kong",
                Filters = new MarketFilter[]
                {
                    new("m:128+t:3", ContractType.MainBoard),
                    new("m:128+t:4", ContractType.ChiNext),
                    // t:2 is RMB sovereign paper ("PRC B3106-R"), not equity.
                    new("m:128+t:2", ContractType.Bond),
                    // t:1 mixes REITs/trusts and RMB-counter lines.
                    new("m:128+t:1", ContractType.Other),
                },
            },
            [Market.US] = new()
            {
                Market = Market.US,
                DisplayName = "美股",
                TimeZoneId = "America/New_York",
                // m:105/106/107 are exchanges, not categories; the type comes
                // from f19 via UsSecurityTypes.
                Filters = new MarketFilter[]
                {
                    new("m:105", ContractType.Stock),
                    new("m:106", ContractType.Stock),
                    new("m:107", ContractType.Stock),
                },
            },
            [Market.KR] = new()
            {
                Market = Market.KR,
                DisplayName = "韩股",
                TimeZoneId = "Asia/Seoul",
                Filters = new MarketFilter[]
                {
                    new("m:177", ContractType.Stock),
                },
            },
        };

    public static IReadOnlyList<Market> AllMarkets { get; } =
        Enum.GetValues<Market>().ToArray();

    public static MarketInfo Of(Market market) => All[market];
}
