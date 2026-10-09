using StockClient.Core.Contracts;

namespace StockClient.Core.Boards;

[Flags]
public enum ListingBoards { None = 0, Main = 1, ChiNext = 2, Star = 4, Beijing = 8, All = 15 }

public static class BoardSearch
{
    public static IReadOnlyList<Board> Match(IEnumerable<Board> boards, string keyword)
    {
        var query = keyword.Trim();
        if (query.EndsWith("板块", StringComparison.Ordinal)) query = query[..^2].Trim();
        if (query.Length == 0) return Array.Empty<Board>();
        return boards.Where(b => b.Kind is BoardKind.Industry or BoardKind.Concept)
            .Where(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || b.Code.Equals(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(b => !b.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(b => b.Kind).ThenBy(b => b.Name, StringComparer.CurrentCulture).ToArray();
    }

    public static bool Includes(Contract contract, ListingBoards boards)
    {
        if (contract.Market is not (Market.SH or Market.SZ or Market.BJ)) return true;
        var flag = contract.Type switch
        {
            ContractType.MainBoard => ListingBoards.Main,
            ContractType.ChiNext => ListingBoards.ChiNext,
            ContractType.Star => ListingBoards.Star,
            ContractType.BeijingBoard => ListingBoards.Beijing,
            _ => ListingBoards.None,
        };
        return flag == ListingBoards.None || (boards & flag) != 0;
    }

    // f13=0 is shared by Shenzhen and Beijing. Inspect the stock code as well.
    public static (Market Market, ContractType Type)? Classify(string code, int? number) =>
        number == 1 && code.StartsWith('6')
            ? (Market.SH, code.StartsWith("688") || code.StartsWith("689") ? ContractType.Star : ContractType.MainBoard)
        : number == 0 && (code.StartsWith('4') || code.StartsWith('8') || code.StartsWith('9'))
            ? (Market.BJ, ContractType.BeijingBoard)
        : number == 0 && code.StartsWith('3') ? (Market.SZ, ContractType.ChiNext)
        : number == 0 && code.StartsWith('0') ? (Market.SZ, ContractType.MainBoard)
        : null;
}

/// <summary>Only complete member sets are cached per trading day and board; unions reuse them.</summary>
public sealed class BoardMemberRepository
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Contract>>> _fetch;
    private readonly IMarketClock _clock;
    private readonly Dictionary<string, IReadOnlyList<Contract>> _cache = new();
    private DateOnly _date;

    public BoardMemberRepository(
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Contract>>> fetch, IMarketClock clock)
    { _fetch = fetch; _clock = clock; }

    public async Task<IReadOnlyList<Contract>> GetAsync(IEnumerable<Board> boards, CancellationToken ct)
    {
        var date = _clock.TradingDate(Market.SH);
        if (date != _date) { _cache.Clear(); _date = date; }
        var codes = boards.Select(b => b.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, Contract>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            if (!_cache.TryGetValue(code, out var rows))
            {
                rows = await _fetch(new[] { code }, ct);
                ct.ThrowIfCancellationRequested();
                if (_date == date) _cache[code] = rows;
            }
            foreach (var contract in rows) result[contract.Code] = contract;
        }
        return result.Values.ToArray();
    }
}
