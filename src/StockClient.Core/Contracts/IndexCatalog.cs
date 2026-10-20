using TinyPinyin;

namespace StockClient.Core.Contracts;

/// <summary>
/// Common benchmarks omitted by the stock/ETF list filters. Only static
/// identities live here; prices and charts use the normal live data clients.
/// Merged in memory so old caches and an unavailable list endpoint cannot hide them.
/// </summary>
public static class IndexCatalog
{
    public static IReadOnlyList<Contract> All { get; } = Array.AsReadOnly(new[]
    {
        Create("SH000001", "上证指数", "上证综指", "上证综合指数", "沪指"),
        Create("SZ399001", "深证成指", "深证指数", "深圳指数", "深成指", "深证成份指数"),
        Create("SZ399006", "创业板指", "创业板指数"),
        Create("SH000016", "上证50", "上证50指数"),
        Create("SH000300", "沪深300", "沪深300指数"),
        Create("SH000905", "中证500", "中证500指数"),
        Create("SH000852", "中证1000", "中证1000指数"),
        Create("SH000688", "科创50", "科创50指数", "上证科创板50成份指数"),
        Create("SZ399106", "深证综指", "深证综合指数"),
        Create("SZ399330", "深证100", "深证100指数"),
        CreateUs("US.NDX", "纳斯达克100", "NDX100", "NDX", "NDX100", "USNDX", "纳斯达克100指数", "纳指100"),
        CreateUs("US.INX", "标普500", "SPX", "SPX", "INX", "USSPX", "USINX", "标普500指数", "标准普尔500指数"),
        CreateUs("US.IXIC", "纳斯达克综合指数", "NDX", "IXIC", "COMP", "USIXIC", "纳斯达克", "纳指", "纳斯达克综合"),
    });

    public static bool IsOverseasIndex(string code) => All.Any(c => c.Market == Market.US
        && c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    private static Contract CreateUs(string code, string name, string eastMoneySymbol, params string[] aliases) =>
        Create(code, name, aliases) with { MarketNumber = 100, EastMoneySymbol = eastMoneySymbol };

    public static bool MatchesExactly(string keyword)
    {
        var query = keyword.Trim();
        return query.Length > 0 && All.Any(c =>
            c.Code.Equals(query, StringComparison.OrdinalIgnoreCase)
            || c.Code[2..].Equals(query, StringComparison.Ordinal)
            || c.Name.Equals(query, StringComparison.Ordinal)
            || c.Pinyin.Equals(query, StringComparison.OrdinalIgnoreCase)
            || c.PinyinInitials.Equals(query, StringComparison.OrdinalIgnoreCase)
            || c.SearchAliases.Any(a => a.Equals(query, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<Contract> Merge(Market market, IEnumerable<Contract> contracts) =>
        All.Where(c => c.Market == market).Concat(contracts)
            .DistinctBy(c => c.Code, StringComparer.OrdinalIgnoreCase).ToArray();

    private static Contract Create(string code, string name, params string[] aliases) => new()
    {
        Code = code,
        Name = name,
        Type = ContractType.Index,
        MarketNumber = code.StartsWith("SH", StringComparison.Ordinal) ? 1 : 0,
        Pinyin = PinyinHelper.GetPinyin(name, "").ToLowerInvariant(),
        PinyinInitials = string.Concat(name.Select(c =>
            PinyinHelper.GetPinyin(c.ToString(), "")[0])).ToLowerInvariant(),
        SearchAliases = Array.AsReadOnly(aliases),
    };
}
