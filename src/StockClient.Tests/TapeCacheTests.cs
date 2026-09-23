using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// The on-disk cache for today's live 逐笔 tape: it must round-trip the tape
/// (so a reopen / blocked request restores it), key by trading day (so a stale day
/// is never served as today), and prune old days.
/// </summary>
public class TapeCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "qv-tapecache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static TradeTickSnapshot Snap(params TradeTick[] ticks) =>
        new() { Code = "SH600519", PrePrice = 11.5, Decimals = 3, Ticks = ticks };

    private static TradeTick T(string time, double price, long vol, TradeSide side) =>
        new() { Time = time, Price = price, Volume = vol, Trades = 2, Side = side };

    [Fact]
    public void Round_trips_the_tape_including_side_and_preprice()
    {
        var cache = new TapeCache(_root);
        var date = new DateOnly(2026, 9, 22);
        cache.Save("SH600519", date, Snap(
            T("13:05:00", 11.58, 1500, TradeSide.Sell),
            T("13:05:07", 11.69, 22, TradeSide.Buy)));

        var back = cache.TryLoad("SH600519", date);

        Assert.NotNull(back);
        Assert.Equal(11.5, back!.PrePrice);
        Assert.Equal(3, back.Decimals);
        Assert.Equal(2, back.Ticks.Count);
        Assert.Equal("13:05:00", back.Ticks[0].Time);
        Assert.Equal(11.58, back.Ticks[0].Price);
        Assert.Equal(1500, back.Ticks[0].Volume);
        Assert.Equal(TradeSide.Sell, back.Ticks[0].Side);
        Assert.Equal(TradeSide.Buy, back.Ticks[1].Side);
    }

    [Fact]
    public void A_different_day_is_a_separate_file_and_not_served_as_today()
    {
        var cache = new TapeCache(_root);
        cache.Save("SH600519", new DateOnly(2026, 9, 21), Snap(T("14:00:00", 11.6, 10, TradeSide.Buy)));

        // Asking for a day we never wrote must miss — a stale session can't leak in.
        Assert.Null(cache.TryLoad("SH600519", new DateOnly(2026, 9, 22)));
        Assert.NotNull(cache.TryLoad("SH600519", new DateOnly(2026, 9, 21)));
    }

    [Fact]
    public void Empty_tape_is_treated_as_absent()
    {
        var cache = new TapeCache(_root);
        var date = new DateOnly(2026, 9, 22);
        cache.Save("SH600519", date, new TradeTickSnapshot
            { Code = "SH600519", Ticks = Array.Empty<TradeTick>() });

        Assert.Null(cache.TryLoad("SH600519", date));
    }

    [Fact]
    public void Prune_keeps_only_the_newest_retained_days()
    {
        var cache = new TapeCache(_root);
        for (var d = 1; d <= TapeCache.RetainDays + 3; d++)
            cache.Save("SH600519", new DateOnly(2026, 9, d),
                Snap(T("09:30:00", 11.6, 1, TradeSide.Buy)));

        // The oldest days are gone, the newest RetainDays remain.
        Assert.Null(cache.TryLoad("SH600519", new DateOnly(2026, 9, 1)));
        Assert.NotNull(cache.TryLoad("SH600519", new DateOnly(2026, 9, TapeCache.RetainDays + 3)));
    }
}
