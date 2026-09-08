using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// The tape accumulator merges CDN-edge-inconsistent 逐笔 snapshots so no print is
/// lost or reordered — the fix for "33s → 39s dropping the 36s row" and the
/// backward flicker.
/// </summary>
public class TapeAccumulatorTests
{
    private static TradeTick T(string time, double price, long vol = 1) =>
        new() { Time = time, Price = price, Volume = vol, Side = TradeSide.Buy };

    private static string[] Times(IReadOnlyList<TradeTick> ticks) =>
        ticks.Select(t => t.Time).ToArray();

    [Fact]
    public void A_middle_print_a_later_edge_reports_is_recovered_not_lost()
    {
        var acc = new TapeAccumulator();
        // One edge jumped ahead to :39 without the :36 row.
        acc.Add(new[] { T("10:00:33", 10.0), T("10:00:39", 10.2) });
        // A later edge finally carries :36 — it must slot in, not be dropped.
        var r = acc.Add(new[] { T("10:00:33", 10.0), T("10:00:36", 10.1), T("10:00:39", 10.2) });

        Assert.Equal(new[] { "10:00:33", "10:00:36", "10:00:39" }, Times(r));
    }

    [Fact]
    public void A_lagging_edge_never_removes_a_newer_print()
    {
        var acc = new TapeAccumulator();
        acc.Add(new[] { T("10:00:33", 10.0), T("10:00:36", 10.1), T("10:00:39", 10.2) });
        // Edge lagging a few seconds behind — missing :39. It must NOT vanish.
        var r = acc.Add(new[] { T("10:00:33", 10.0), T("10:00:36", 10.1) });

        Assert.Equal(new[] { "10:00:33", "10:00:36", "10:00:39" }, Times(r));
    }

    [Fact]
    public void The_current_second_keeps_its_largest_reported_volume()
    {
        var acc = new TapeAccumulator();
        acc.Add(new[] { T("10:00:01", 10.0, 5) });
        acc.Add(new[] { T("10:00:01", 10.0, 8) });   // more of the second filled in
        var r = acc.Add(new[] { T("10:00:01", 10.0, 3) });   // a staler edge — must not shrink it

        Assert.Single(r);
        Assert.Equal(8, r[0].Volume);
    }

    [Fact]
    public void A_big_backward_jump_resets_rather_than_merging_two_sessions()
    {
        var acc = new TapeAccumulator();
        acc.Add(new[] { T("15:29:50", 10.0) });
        var r = acc.Add(new[] { T("09:15:03", 11.0) });   // next session's open, hours earlier

        Assert.Single(r);
        Assert.Equal("09:15:03", r[0].Time);
    }

    [Fact]
    public void A_minutes_stale_snapshot_merges_instead_of_blanking_the_tape()
    {
        var acc = new TapeAccumulator();
        acc.Add(new[] { T("13:05:00", 10.0), T("13:05:03", 10.1) });
        // A CDN edge briefly serves a snapshot a few minutes old. It must NOT reset
        // the tape back to those minutes-old prints (the "jumps to a few minutes
        // ago, then recovers" flicker) — merge it, keeping the newer prints.
        var r = acc.Add(new[] { T("13:02:10", 9.9) });

        Assert.Equal(new[] { "13:02:10", "13:05:00", "13:05:03" }, Times(r));
    }

    [Fact]
    public void Same_second_prints_keep_first_seen_order()
    {
        var acc = new TapeAccumulator();
        var r = acc.Add(new[] { T("10:00:05", 10.1), T("10:00:05", 10.2), T("10:00:05", 10.0) });

        Assert.Equal(new[] { 10.1, 10.2, 10.0 }, r.Select(t => t.Price).ToArray());
    }
}
