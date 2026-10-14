using StockClient.Core.Boards;

namespace StockClient.Core.MarketWatch;

public sealed record BoardMarketQuote(Board Board, double? Price, double? Percent, double? Amount,
    double? MainInflow, int? UpCount, int? DownCount, DateTimeOffset? QuoteTime)
{
    public string Code => Board.Code;
    public string Name => Board.Name;
    public string AmountDisplay => MarketValueFormat.Format(Amount, nameof(MarketQuote.Amount));
    public string InflowDisplay => MarketValueFormat.Format(MainInflow, nameof(MarketQuote.MainInflow));
    public string TimeDisplay => QuoteTime?.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss") ?? "—";
}

public sealed record BoardMarketSnapshot(IReadOnlyList<BoardMarketQuote> Rows, DateTimeOffset CompletedAt, string Host)
{
    public bool Delayed => Host.Contains("delay", StringComparison.Ordinal);
}

public static class BoardRanking
{
    public static IReadOnlyList<BoardMarketQuote> Filter(IEnumerable<BoardMarketQuote> rows, BoardKind kind, string keyword) =>
        rows.Where(r => r.Board.Kind == kind && (r.Name.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)
            || r.Code.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();

    public static IReadOnlyList<BoardMarketQuote> Gainers(IEnumerable<BoardMarketQuote> rows) =>
        rows.Where(r => r.Percent > 0).OrderByDescending(r => r.Percent).ThenBy(r => r.Code, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<BoardMarketQuote> Losers(IEnumerable<BoardMarketQuote> rows) =>
        rows.Where(r => r.Percent < 0).OrderBy(r => r.Percent).ThenBy(r => r.Code, StringComparer.Ordinal).ToArray();
}
