using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// Parsing the 逐笔成交 rows EastMoney details/get returns — the shape probed live
/// on 2026-09-02: <c>时间,成交价,量(手),笔数,方向</c>, 方向 1 买 / 2 卖 / 4 中性.
/// </summary>
public class EastMoneyDetailsClientTests
{
    [Theory]
    [InlineData("13:06:05,1295.14,45,23,2", 1295.14, 45L, 23, TradeSide.Sell)]
    [InlineData("13:06:11,1295.19,1,1,1", 1295.19, 1L, 1, TradeSide.Buy)]
    [InlineData("13:06:29,1295.20,7,3,4", 1295.20, 7L, 3, TradeSide.Neutral)]
    public void Parses_a_well_formed_row(string row, double price, long vol, int trades, TradeSide side)
    {
        var tick = EastMoneyDetailsClient.ParseRow(row);

        Assert.NotNull(tick);
        Assert.Equal("13", tick!.Time[..2]);
        Assert.Equal(price, tick.Price);
        Assert.Equal(vol, tick.Volume);
        Assert.Equal(trades, tick.Trades);
        Assert.Equal(side, tick.Side);
    }

    [Fact]
    public void Amount_uses_the_100_shares_per_hand_convention()
    {
        var tick = EastMoneyDetailsClient.ParseRow("13:06:05,10.00,50,1,1");
        Assert.NotNull(tick);
        Assert.Equal(50_000, tick!.Amount);   // 10 元 × 50 手 × 100 股
    }

    [Theory]
    [InlineData("")]
    [InlineData("13:06:05,1295.14,45")]          // too few columns
    [InlineData("13:06:05,1295.14,notanumber,1,1")]
    public void Rejects_malformed_rows(string row) =>
        Assert.Null(EastMoneyDetailsClient.ParseRow(row));

    [Fact]
    public void Unknown_direction_reads_as_neutral()
    {
        var tick = EastMoneyDetailsClient.ParseRow("13:06:05,1295.14,45,23,9");
        Assert.NotNull(tick);
        Assert.Equal(TradeSide.Neutral, tick!.Side);
    }
}
