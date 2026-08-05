using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Brief;

/// <summary>
/// One day's aggregated brief, produced by the `brief/` pipeline and read here
/// as-is.
///
/// The client is a VIEWER. It renders what the file says and computes nothing:
/// no summing, no ranking, no deriving. That rule exists on the producing side
/// too (the generator is forbidden to do arithmetic, so every figure is written
/// down by the fetch layer), and it only holds end to end if this side doesn't
/// quietly reintroduce a calculation.
///
/// Note what has no field here: no forecast, no target price, no recommendation.
/// There is deliberately nowhere to put one.
/// </summary>
public sealed record DailyBrief
{
    [JsonPropertyName("trading_day")]
    public string TradingDay { get; init; } = "";

    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; init; }

    [JsonPropertyName("market")]
    public BriefMarket Market { get; init; } = new();

    [JsonPropertyName("bullish")]
    public IReadOnlyList<BriefItem> Bullish { get; init; } = Array.Empty<BriefItem>();

    [JsonPropertyName("bearish")]
    public IReadOnlyList<BriefItem> Bearish { get; init; } = Array.Empty<BriefItem>();

    /// <summary>Rumour and unconfirmed reports, kept apart from the two lists above.</summary>
    [JsonPropertyName("unverified")]
    public IReadOnlyList<BriefItem> Unverified { get; init; } = Array.Empty<BriefItem>();

    /// <summary>Facts that cut against the day's dominant narrative. Never empty by design.</summary>
    [JsonPropertyName("counterpoint")]
    public IReadOnlyList<string> Counterpoint { get; init; } = Array.Empty<string>();

    /// <summary>Per-source state: ok / empty / error. Anything not ok is shown, not hidden.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyDictionary<string, string> Sources { get; init; } =
        new Dictionary<string, string>();

    [JsonIgnore]
    public IReadOnlyList<KeyValuePair<string, string>> FailedSources =>
        Sources.Where(s => s.Value != "ok").ToArray();

    public static DailyBrief? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DailyBrief>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record BriefMarket
{
    [JsonPropertyName("indices")]
    public IReadOnlyList<BriefIndex> Indices { get; init; } = Array.Empty<BriefIndex>();

    [JsonPropertyName("breadth")]
    public BriefBreadth? Breadth { get; init; }

    [JsonPropertyName("top_boards")]
    public IReadOnlyList<BriefBoard> TopBoards { get; init; } = Array.Empty<BriefBoard>();

    [JsonPropertyName("bottom_boards")]
    public IReadOnlyList<BriefBoard> BottomBoards { get; init; } = Array.Empty<BriefBoard>();

    [JsonPropertyName("watchlist")]
    public IReadOnlyList<BriefWatch> Watchlist { get; init; } = Array.Empty<BriefWatch>();
}

public sealed record BriefIndex
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("pct")]
    public double? Pct { get; init; }

    [JsonPropertyName("amount")]
    public double? Amount { get; init; }
}

public sealed record BriefBreadth
{
    [JsonPropertyName("total")]
    public int? Total { get; init; }

    [JsonPropertyName("advancing")]
    public int? Advancing { get; init; }

    [JsonPropertyName("declining")]
    public int? Declining { get; init; }

    [JsonPropertyName("limit_up")]
    public int? LimitUp { get; init; }

    [JsonPropertyName("limit_down")]
    public int? LimitDown { get; init; }

    [JsonPropertyName("total_amount")]
    public double? TotalAmount { get; init; }
}

public sealed record BriefBoard
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("pct")]
    public double? Pct { get; init; }

    [JsonPropertyName("up")]
    public int? Up { get; init; }

    [JsonPropertyName("down")]
    public int? Down { get; init; }
}

public sealed record BriefWatch
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("pct")]
    public double? Pct { get; init; }

    [JsonPropertyName("tag")]
    public string Tag { get; init; } = "";
}

/// <summary>
/// One classified line. <see cref="Source"/> names the file under the pipeline's
/// raw/ directory it came from — shown in the UI, because a claim you can't trace
/// is a claim you shouldn't act on.
/// </summary>
public sealed record BriefItem
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("time")]
    public string Time { get; init; } = "";
}
