using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// The period-return calculator is where every 昨日涨幅 bug of 2026-08/09 lived
/// (rollover races, mispaired closes, gap windows) — these tests pin the
/// anchor-and-measure semantics that replaced all of it.
/// </summary>
public class PeriodReturnsTests
{
    private static Kline Candle(string date, double close) =>
        new() { Date = date, Open = close, Close = close, High = close, Low = close };

    // Ten sessions, closes 10..19, newest last (2026-01-16 = 19).
    private static IReadOnlyList<Kline> Ten() =>
        Enumerable.Range(0, 10)
            .Select(i => Candle($"2026-01-{5 + i:00}", 10 + i))
            .ToArray();

    [Fact]
    public void Anchor_matches_the_quotes_previous_close()
    {
        // The quote says 昨收 = 18 → yesterday is the second-to-last candle,
        // even though a newer one (19) exists — mirrors the pre-open window
        // where the feed still refers to the older session.
        Assert.Equal(8, PeriodReturns.YesterdayIndex(Ten(), 18));
        Assert.Equal(9, PeriodReturns.YesterdayIndex(Ten(), 19));
    }

    [Fact]
    public void Anchor_prefers_the_newest_of_equal_closes()
    {
        var candles = new[] { Candle("2026-01-05", 10), Candle("2026-01-06", 11), Candle("2026-01-07", 10) };
        Assert.Equal(2, PeriodReturns.YesterdayIndex(candles, 10));
    }

    [Fact]
    public void Anchor_tolerates_source_rounding_but_not_a_real_gap()
    {
        Assert.Equal(9, PeriodReturns.YesterdayIndex(Ten(), 19.001));
        Assert.Equal(-1, PeriodReturns.YesterdayIndex(Ten(), 19.5));
        Assert.Equal(-1, PeriodReturns.YesterdayIndex(Ten(), 0));
    }

    [Fact]
    public void Prev_day_is_anchor_over_its_predecessor()
    {
        // Yesterday closed 19, the day before 18 → 昨日涨幅 = 19/18-1.
        var pct = PeriodReturns.PrevDayPercent(Ten(), 9);
        Assert.NotNull(pct);
        Assert.Equal((19.0 / 18 - 1) * 100, pct!.Value, 10);

        // Anchor at the very first candle: no predecessor, no answer.
        Assert.Null(PeriodReturns.PrevDayPercent(Ten(), 0));
        Assert.Null(PeriodReturns.PrevDayPercent(Ten(), -1));
    }

    [Fact]
    public void Baseline_counts_sessions_back_from_today()
    {
        // 1 session ago IS the anchor; 3 sessions ago is two candles earlier.
        Assert.Equal(19, PeriodReturns.Baseline(Ten(), 9, 1));
        Assert.Equal(17, PeriodReturns.Baseline(Ten(), 9, 3));
        Assert.Equal(10, PeriodReturns.Baseline(Ten(), 9, 10));
        Assert.Null(PeriodReturns.Baseline(Ten(), 9, 11));   // beyond history
        Assert.Null(PeriodReturns.Baseline(Ten(), -1, 3));   // no anchor
    }

    [Fact]
    public void Year_start_takes_last_close_of_the_prior_year()
    {
        var candles = new[]
        {
            Candle("2025-12-30", 100), Candle("2025-12-31", 105),
            Candle("2026-01-02", 110), Candle("2026-01-05", 120),
        };
        Assert.Equal(105, PeriodReturns.YearStartBaseline(candles, 3, 2026));
    }

    [Fact]
    public void Year_start_in_early_january_uses_the_old_years_close_itself()
    {
        // Today is 2026-01-02 (thisYear=2026) but yesterday's candle is still
        // 2025-12-31 — 年初至今 must measure from THAT close, not 2024's.
        var candles = new[] { Candle("2025-12-30", 100), Candle("2025-12-31", 105) };
        Assert.Equal(105, PeriodReturns.YearStartBaseline(candles, 1, 2026));
    }

    [Fact]
    public void Year_start_is_null_when_history_stops_inside_this_year()
    {
        var candles = new[] { Candle("2026-03-02", 100), Candle("2026-03-03", 105) };
        Assert.Null(PeriodReturns.YearStartBaseline(candles, 1, 2026));
    }

    [Fact]
    public void Percent_is_guarded_against_junk()
    {
        Assert.Null(PeriodReturns.Percent(0, 10));
        Assert.Null(PeriodReturns.Percent(10, null));
        Assert.Null(PeriodReturns.Percent(10, 0));
        Assert.Equal(10.0, PeriodReturns.Percent(11, 10)!.Value, 10);
    }
}
