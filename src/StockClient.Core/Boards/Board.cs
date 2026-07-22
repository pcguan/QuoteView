using System.Text.Json.Serialization;

namespace StockClient.Core.Boards;

/// <summary>Which EastMoney board universe a board came from.</summary>
public enum BoardKind
{
    /// <summary>行业板块 (m:90+t:2). Contains both coarse (Ⅱ) and fine (Ⅲ) levels.</summary>
    Industry,

    /// <summary>概念板块 (m:90+t:3).</summary>
    Concept,

    /// <summary>地区板块 (m:90+t:1).</summary>
    Region,
}

/// <summary>
/// One EastMoney board (板块), e.g. BK1276 油气开采Ⅱ.
///
/// EastMoney has no separate "细分板块" universe: the 行业 list (t:2) already
/// carries the fine subdivisions, distinguished only by a Ⅱ/Ⅲ suffix on the name
/// — there is no parent-code field in the response, so that suffix is the only
/// level signal (see <see cref="Level"/>).
/// </summary>
public sealed record Board
{
    /// <summary>Board code, e.g. BK1276.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required BoardKind Kind { get; init; }

    /// <summary>成分股数量 (f134).</summary>
    [JsonPropertyName("n")]
    public int MemberCount { get; init; }

    /// <summary>
    /// EastMoney secid for the board's own index quote/K-line: market 90 + code.
    /// </summary>
    [JsonIgnore]
    public string EastMoneySecId => $"90.{Code}";

    /// <summary>
    /// Industry depth read from the name suffix: 3 for a 三级 board (…Ⅲ), 2 for a
    /// 二级 board (…Ⅱ), else 1. Concepts and regions have no levels, so they are 1.
    /// </summary>
    [JsonIgnore]
    public int Level =>
        Kind == BoardKind.Industry && Name.EndsWith('Ⅲ') ? 3
        : Kind == BoardKind.Industry && Name.EndsWith('Ⅱ') ? 2
        : 1;
}

/// <summary>The whole board catalog for one trading date, as cached on disk.</summary>
public sealed record BoardFile
{
    [JsonPropertyName("tradingDate")]
    public required string TradingDate { get; init; }

    [JsonPropertyName("fetchedAtUtc")]
    public required DateTimeOffset FetchedAtUtc { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("boards")]
    public required IReadOnlyList<Board> Boards { get; init; }
}
