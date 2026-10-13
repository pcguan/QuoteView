using System.Globalization;
using StockClient.Core.MarketWatch;
using Xunit;

namespace StockClient.Tests;

public class MarketValueFormatTests
{
    [Theory]
    [InlineData(100, "100.00万元")]
    [InlineData(12345, "1.23亿元")]
    [InlineData(-1234567, "-123.46亿元")]
    [InlineData(123456789, "1.23万亿元")]
    [InlineData(9999.999, "1.00亿元")]
    [InlineData(0, "0.00元")]
    [InlineData(0.5, "5,000.00元")]
    public void InflowUsesAbsoluteMagnitudeAndPreservesSign(double value, string expected) =>
        Assert.Equal(expected, MarketValueFormat.Format(value, nameof(MarketQuote.MainInflow), CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(nameof(MarketQuote.Amount), 0.05, "500.00万元")]
    [InlineData(nameof(MarketQuote.TotalCap), 12345, "1.23万亿元")]
    [InlineData(nameof(MarketQuote.Volume), 12.34, "12.34万手")]
    [InlineData(nameof(MarketQuote.Volume), 12345, "1.23亿手")]
    [InlineData(nameof(MarketQuote.Price), 12345, "12,345.00")]
    public void SourceUnitsAreRespected(string key, double value, string expected) =>
        Assert.Equal(expected, MarketValueFormat.Format(value, key, CultureInfo.InvariantCulture));

    [Fact]
    public void MissingAndNonfiniteRemainMissing()
    {
        foreach (var value in new double?[] { null, double.NaN, double.PositiveInfinity })
            Assert.Equal("—", MarketValueFormat.Format(value, nameof(MarketQuote.MainInflow)));
    }
}
