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
    public void Weekend_kline_day_rolls_back_to_friday_and_reads_settled()
    {
        // Saturday 2026-08-29 10:00 Beijing = UTC Friday 2026-08-28 02:00.
        var (date, settled) = At("2026-08-29T02:00:00").KlineDay(Market.SH);
        Assert.Equal(new DateOnly(2026, 8, 28), date);   // Friday
        Assert.True(settled);                             // that session is over

        // Sunday too — the same Friday, so a whole weekend reads one cache entry.
        var (sunday, sundaySettled) = At("2026-08-30T02:00:00").KlineDay(Market.SH);
        Assert.Equal(new DateOnly(2026, 8, 28), sunday);
        Assert.True(sundaySettled);
    }

    [Fact]
    public void Weekday_kline_day_keeps_the_intraday_then_settled_transition()
    {
        // Beijing Monday 11:00 (UTC 03:00): today, not settled — the running
        // candle must still be dropped.
        var (day, settled) = At("2026-08-31T03:00:00").KlineDay(Market.SH);
        Assert.Equal(new DateOnly(2026, 8, 31), day);
        Assert.False(settled);

        // Same day 15:25 Beijing (UTC 07:25): past close + settle margin.
        Assert.True(At("2026-08-31T07:25:00").KlineDay(Market.SH).Settled);
    }

    [Fact]
    public void IsLive_tracks_each_markets_own_session()
    {
        // Beijing Monday 10:00 (UTC 02:00): Shanghai trading, New York asleep.
        var trading = At("2026-08-31T02:00:00");
        Assert.True(trading.IsLive(Market.SH));
        Assert.False(trading.IsLive(Market.US));

        // Beijing Monday 22:00 (UTC 14:00) = New York 10:00 — the mirror image.
        var newYork = At("2026-08-31T14:00:00");
        Assert.False(newYork.IsLive(Market.SH));
        Assert.True(newYork.IsLive(Market.US));

        // Saturday: nobody trades.
        Assert.False(At("2026-08-29T02:00:00").IsLive(Market.SH));
    }

    [Fact]
    public void Dated_after_close_answers_across_the_calendar()
    {
        var clock = At("2026-08-31T00:00:00");
        var friday = new DateOnly(2026, 8, 28);

        // Saturday 09:00 Beijing IS after Friday's close, even though 09:00 is
        // before any close on its own clock face — the whole point of the dated
        // overload, and what keeps a weekend reading its cache.
        Assert.True(clock.IsAfterClose(Market.SH, friday,
            DateTimeOffset.Parse("2026-08-29T01:00:00Z")));

        // Friday 11:00 Beijing is NOT: a cache written mid-session still lacks
        // that day's closing candle and must be refetched.
        Assert.False(clock.IsAfterClose(Market.SH, friday,
            DateTimeOffset.Parse("2026-08-28T03:00:00Z")));

        // Friday 15:25 Beijing — past close plus settle margin.
        Assert.True(clock.IsAfterClose(Market.SH, friday,
            DateTimeOffset.Parse("2026-08-28T07:25:00Z")));

        // An instant BEFORE the judged date never counts.
        Assert.False(clock.IsAfterClose(Market.SH, friday,
            DateTimeOffset.Parse("2026-08-27T08:00:00Z")));
    }

    [Fact]
    public void After_close_needs_the_settle_margin()
    {
        // SH closes 15:00; 15:10 local is inside the settle margin, 15:25 past it.
        Assert.False(At("2026-08-31T07:10:00").IsAfterClose(Market.SH, DateTimeOffset.Parse("2026-08-31T07:10:00Z")));
        Assert.True(At("2026-08-31T07:25:00").IsAfterClose(Market.SH, DateTimeOffset.Parse("2026-08-31T07:25:00Z")));
    }
}
