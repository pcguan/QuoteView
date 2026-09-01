using StockClient.Core;
using Xunit;

namespace StockClient.Tests;

public class CodeMapperTests
{
    [Theory]
    [InlineData("SH600519", "SH", "600519")]
    [InlineData("usAAPL", "US", "AAPL")]
    [InlineData("kr005930", "KR", "005930")]
    public void Parses_market_prefixed_codes(string code, string market, string number)
    {
        Assert.True(CodeMapper.TryParse(code, out var m, out var n));
        Assert.Equal(market, m);
        Assert.Equal(number, n);
    }

    [Theory]
    [InlineData("600519")]
    [InlineData("")]
    [InlineData("XX123")]
    public void Rejects_unprefixed_or_unknown(string code) =>
        Assert.False(CodeMapper.IsValid(code));
}
