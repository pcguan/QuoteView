using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

public class GroupIndexCalcTests
{
    private static GroupIndexCalc.Member M(double pct, double cap = 0) => new(pct, cap);

    [Fact]
    public void Empty_is_null() =>
        Assert.Null(GroupIndexCalc.Compute(System.Array.Empty<GroupIndexCalc.Member>(), false));

    [Fact]
    public void Equal_weight_is_plain_average() =>
        Assert.Equal(1.0, GroupIndexCalc.Compute(new[] { M(0), M(2) }, equalWeight: true)!.Value, 9);

    [Fact]
    public void No_caps_fall_back_to_equal_weight() =>
        Assert.Equal(1.0, GroupIndexCalc.Compute(new[] { M(0), M(2) }, equalWeight: false)!.Value, 9);

    [Fact]
    public void Cap_weighted_uses_previous_close_cap()
    {
        // Two members: +0% with live cap 100, +10% with live cap 110 (prev-close
        // cap also 100). Prev-close weights are equal → index = (0+10)/2 = 5,
        // NOT tilted toward the riser as live-cap weighting (0*100+10*110)/210 would.
        var idx = GroupIndexCalc.Compute(new[] { M(0, 100), M(10, 110) }, equalWeight: false)!.Value;
        Assert.Equal(5.0, idx, 9);
    }

    [Fact]
    public void Missing_cap_member_rides_the_average_weight()
    {
        // One capped member and one cap-less: the cap-less one still contributes
        // (at the known average weight), it doesn't vanish.
        var idx = GroupIndexCalc.Compute(new[] { M(4, 100), M(0, 0) }, equalWeight: false)!.Value;
        Assert.Equal(2.0, idx, 9);   // equal prev-close weights → (4+0)/2
    }
}
