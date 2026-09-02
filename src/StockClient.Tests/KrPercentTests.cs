using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// KR 昨日涨幅取归档 pct 而非两收盘相除：唯一能在归档缺一个交易日时仍正确的路径。
/// </summary>
public class KrPercentTests
{
    private static Kline C(string date, double close, double? pct = null) =>
        new() { Date = date, Open = close, Close = close, High = close, Low = close, Percent = pct };

    [Fact]
    public void Archived_percent_wins_over_dividing_closes()
    {
        // 相邻日缺失：closes 相除会得 (1674000/1620000-1) 的错值，而归档 pct=1.27
        // 是那天用实时行情算的、跳过缺口仍正确。
        var candles = new[]
        {
            C("2026-08-27", 1620000),                 // 27 与 31 之间 28 的会话缺档
            C("2026-08-31", 1674000, pct: 1.27),
        };
        var pct = PeriodReturns.PrevDayPercent(candles, 1);
        Assert.NotNull(pct);
        Assert.Equal(1.27, pct!.Value, 6);
    }

    [Fact]
    public void Falls_back_to_close_ratio_when_no_percent()
    {
        var candles = new[] { C("2026-08-28", 100), C("2026-08-31", 110) };
        var pct = PeriodReturns.PrevDayPercent(candles, 1);
        Assert.Equal(10.0, pct!.Value, 6);
    }

    [Fact]
    public void Percent_at_anchor_zero_index_still_works()
    {
        // 只归档了一天（新韩股），但那天带 pct：昨日涨幅仍可得，无需前一根。
        var candles = new[] { C("2026-08-31", 1674000, pct: 1.27) };
        Assert.Equal(1.27, PeriodReturns.PrevDayPercent(candles, 0)!.Value, 6);
    }
}
