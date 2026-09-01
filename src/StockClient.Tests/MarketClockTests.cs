using StockClient.Core.Contracts;
using Xunit;

namespace StockClient.Tests;

/// <summary>Trading-date and session arithmetic in exchange-local time — the
/// timezone edges that made US rows go stale or refresh twice.</summary>
public class MarketClockTests
{
    private static MarketClock At(string utc) =>
        new(() => DateTimeOffset.Parse(utc + "Z"));

    [Fact]
    public void Beijing_midnight_is_still_new_yorks_previous_day()
    {
        // Beijing 2026-09-01 00:30 = UTC 2026-08-31 16:30 = New York 12:30 (EDT),
        // mid-session of the PREVIOUS calendar day.
        var clock = At("2026-08-31T16:30:00");
        Assert.Equal(new DateOnly(2026, 9, 1), clock.TradingDate(Market.SH));
        Assert.Equal(new DateOnly(2026, 8, 31), clock.TradingDate(Market.US));
    }

    [Fact]
    public void After_close_needs_the_settle_margin()
    {
        // SH closes 15:00; 15:10 local is inside the settle margin, 15:25 past it.
        Assert.False(At("2026-08-31T07:10:00").IsAfterClose(Market.SH, DateTimeOffset.Parse("2026-08-31T07:10:00Z")));
        Assert.True(At("2026-08-31T07:25:00").IsAfterClose(Market.SH, DateTimeOffset.Parse("2026-08-31T07:25:00Z")));
    }
}
