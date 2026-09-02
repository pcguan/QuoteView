using StockClient.Core.Contracts;
using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// The kline endpoint's symbol form. US without its exchange suffix answered
/// with a one-row stub for months and was written off as "no US history at
/// Tencent" — this pins the mapping so it can't silently regress to the bare form.
/// </summary>
public class TencentKlineClientTests
{
    [Theory]
    [InlineData("USASX", 106, "usASX.N")]      // NYSE
    [InlineData("USAAPL", 105, "usAAPL.OQ")]   // NASDAQ
    [InlineData("USIMO", 107, "usIMO.A")]      // NYSE American
    [InlineData("USAAPL", null, "usAAPL.OQ")]  // bare contract: NASDAQ, like EastMoneySecId
    public void Us_symbols_carry_the_exchange_suffix(string code, int? marketNumber, string expected) =>
        Assert.Equal(expected, TencentKlineClient.ToKlineApiCode(
            new Contract { Code = code, Name = code, MarketNumber = marketNumber }));

    [Theory]
    [InlineData("SH600519", "sh600519")]
    [InlineData("HK00700", "hk00700")]
    [InlineData("BJ430418", "bj430418")]
    public void Other_markets_keep_the_quote_form(string code, string expected) =>
        Assert.Equal(expected, TencentKlineClient.ToKlineApiCode(
            new Contract { Code = code, Name = code }));
}
