using System.Text.Json.Serialization;

namespace StockClient.Core.Quotes;

/// <summary>
/// The closing prices a contract's multi-day returns are measured against.
///
/// These are the only part of a period return that is actually static: a "5-day
/// return" is <c>现价 ÷ 五个交易日前的收盘 − 1</c>, and the numerator moves every
/// second. So the baselines are fetched once per trading day and the percentages
/// are computed locally against the live price — which also makes them update at
/// the quote's own cadence instead of the fetch's.
///
/// EastMoney doesn't serve the baselines directly; it serves the percentages. They
/// are inverted back into prices (<c>基准 = 现价 ÷ (1 + 涨幅/100)</c>) at fetch
/// time. The percentages carry two decimals, so a baseline is accurate to about
/// 1e-4 relative — recomputing a return from it lands within 0.01%, i.e. inside
/// the source's own precision.
/// </summary>
public sealed record ReturnBaselines
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Trading date these were fetched for, in the contract's own market.</summary>
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    /// <summary>
    /// Previous close. Kept across days, because today's previous close divided by
    /// the PREVIOUS entry's previous close is exactly yesterday's move — the one
    /// period EastMoney has no field for.
    /// </summary>
    [JsonPropertyName("prev")]
    public double PrevClose { get; init; }

    /// <summary>The previous close carried by the entry this one replaced.</summary>
    [JsonPropertyName("prior")]
    public double PriorClose { get; init; }

    /// <summary>Trading date of that earlier entry, to tell whether it is adjacent.</summary>
    [JsonPropertyName("priorDate")]
    public string PriorDate { get; init; } = "";

    /// <summary>
    /// The last COMPLETED session's own previous close, reverse-derived from an
    /// IDLE feed snapshot (现价≈昨收 — evenings, weekends, holidays). In that
    /// state 昨收 is the finished session's close while 涨跌幅 still carries its
    /// move, so its baseline falls straight out: 上收 = 昨收 ÷ (1 + 涨跌幅).
    /// Zero when the snapshot was taken mid-session (nothing to derive).
    /// </summary>
    [JsonPropertyName("implied")]
    public double ImpliedPrior { get; init; }

    /// <summary>
    /// <see cref="PriorClose"/> came from (or was verified against) the feed's
    /// own idle snapshot — adjacent BY CONSTRUCTION, so the date-gap heuristic
    /// doesn't apply. Without this, an entry chained across an unobserved
    /// double rollover (app off from Thursday, first fetch Monday morning)
    /// pairs closes two sessions apart and labels the ratio "yesterday".
    /// </summary>
    [JsonPropertyName("feedPrior")]
    public bool PriorFromFeed { get; init; }

    /// <summary>
    /// An idle post-close snapshot has been merged for this previous-close
    /// value — nothing newer can appear until the NEXT session finishes, so
    /// the repository stops re-fetching until the baseline date advances.
    /// </summary>
    [JsonPropertyName("settled")]
    public bool Settled { get; init; }

    [JsonPropertyName("d3")]
    public double Day3 { get; init; }

    [JsonPropertyName("d5")]
    public double Day5 { get; init; }

    [JsonPropertyName("d10")]
    public double Day10 { get; init; }

    [JsonPropertyName("d20")]
    public double Day20 { get; init; }

    [JsonPropertyName("d60")]
    public double Day60 { get; init; }

    [JsonPropertyName("ytd")]
    public double YearStart { get; init; }

    /// <summary>
    /// True when the feed returned something usable. Korea comes back with every
    /// period carrying the SAME percentage (measured: all six were -3.35), which is
    /// not a real answer — such rows are rejected rather than displayed as if the
    /// stock had moved identically over 3 days and 60.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => Day3 > 0 && Day5 > 0
                            && Math.Abs(Day3 - Day60) > 1e-6;

    /// <summary>
    /// The last completed session's move, from two consecutive previous closes.
    ///
    /// A feed-verified pair (<see cref="PriorFromFeed"/>) is trusted outright.
    /// A pair chained from a stored predecessor must be genuinely adjacent:
    /// with no trading calendar to consult, "within 4 calendar days" is the
    /// test, which covers weekends (gap 0 is legal — a post-close re-fetch
    /// rolls the chain under the same baseline date). After a long offline
    /// stretch the first day shows nothing rather than labelling a week's move
    /// as yesterday's — wrong is worse than missing, and it corrects itself at
    /// the next session close.
    /// </summary>
    [JsonIgnore]
    public double? PrevDayPercent
    {
        get
        {
            if (PrevClose <= 0 || PriorClose <= 0) return null;

            if (!PriorFromFeed)
            {
                if (!DateOnly.TryParse(Date, out var day)) return null;
                if (!DateOnly.TryParse(PriorDate, out var prior)) return null;

                var gap = day.DayNumber - prior.DayNumber;
                if (gap is not (>= 0 and <= 4)) return null;
            }

            return (PrevClose / PriorClose - 1) * 100;
        }
    }

    /// <summary>Return from <paramref name="baseline"/> to <paramref name="price"/>, in %.</summary>
    public static double? Percent(double price, double baseline) =>
        price > 0 && baseline > 0 ? (price / baseline - 1) * 100 : null;
}

/// <summary>One trading day's baselines for every contract fetched that day.</summary>
public sealed record ReturnBaselineDay
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<ReturnBaselines> Items { get; init; }
}
