namespace StockClient.Core.Quotes;

/// <summary>
/// The per-group intraday index — a market-cap-weighted (or equal-weighted)
/// average of members' 涨跌幅. Pure and money-relevant, so it lives here with
/// unit tests instead of inside the view-model.
///
/// A-shares only: HK trades a different session and US/KR quotes carry another
/// day's move, so mixing them into one intraday basket is meaningless. Weight
/// is by FLOAT CAP AT PREVIOUS CLOSE (cap ÷ (1+pct/100)) — weighting by the
/// live cap lets a riser inflate its own weight. Members with no cap fall back
/// to the known members' average weight, so one cap-less line doesn't vanish.
/// </summary>
public static class GroupIndexCalc
{
    /// <summary>One member's live figures: change % and float cap (0 = unknown).</summary>
    public readonly record struct Member(double Percent, double FloatCap);

    public static double? Compute(IReadOnlyList<Member> members, bool equalWeight)
    {
        if (members.Count == 0) return null;

        if (equalWeight) return members.Average(m => m.Percent);

        // Float cap at the previous close, so today's move doesn't reweight.
        var caps = members
            .Select(m => m.FloatCap > 0 ? m.FloatCap / (1 + m.Percent / 100) : 0.0)
            .ToArray();

        var known = caps.Where(c => c > 0).ToArray();
        if (known.Length == 0) return members.Average(m => m.Percent);

        var fallback = known.Average();
        double num = 0, den = 0;
        for (var i = 0; i < members.Count; i++)
        {
            var w = caps[i] > 0 ? caps[i] : fallback;
            num += w * members[i].Percent;
            den += w;
        }
        return den > 0 ? num / den : null;
    }
}
