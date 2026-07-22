using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Boards;

public interface IBoardListClient
{
    Task<IReadOnlyList<Board>> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Pulls the whole board catalog from EastMoney's clist endpoint — the same
/// endpoint the contract lists use, but with an m:90 board filter instead of a
/// market one, so the rows are boards rather than stocks.
///
/// Three universes (行业/概念/地区), each paged like the contract lists (pz is
/// capped at 100 server-side). ~11 requests total, so it is fetched sequentially
/// alongside the daily contract refresh without any throttling concern.
/// </summary>
public sealed class EastMoneyBoardClient : IBoardListClient
{
    private const int PageSize = 100;
    private const string Host = "push2delay.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";
    private const int MaxPages = 40;

    /// <summary>The three board universes under market 90.</summary>
    private static readonly (string Fs, BoardKind Kind)[] Universes =
    {
        ("m:90+t:2", BoardKind.Industry),
        ("m:90+t:3", BoardKind.Concept),
        ("m:90+t:1", BoardKind.Region),
    };

    private readonly HttpClient _http;

    public EastMoneyBoardClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<Board>> FetchAsync(CancellationToken cancellationToken)
    {
        var byCode = new Dictionary<string, Board>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fs, kind) in Universes)
        {
            foreach (var row in await FetchUniverseAsync(fs, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name))
                    continue;

                var board = new Board
                {
                    Code = row.Code!.Trim(),
                    Name = row.Name!.Trim(),
                    Kind = kind,
                    MemberCount = ParseInt(row.MemberCount),
                };
                byCode[board.Code] = board;
            }
        }

        return byCode.Values.ToArray();
    }

    private static int ParseInt(object? value) =>
        int.TryParse(value?.ToString()?.Trim() ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;

    private async Task<List<BoardRow>> FetchUniverseAsync(
        string fs, CancellationToken cancellationToken)
    {
        var rows = new List<BoardRow>();

        var first = await FetchPageAsync(fs, 1, cancellationToken);
        if (first?.Data is null) return rows;

        rows.AddRange(first.Data.Diff ?? new List<BoardRow>());

        var pages = Math.Min((int)Math.Ceiling(first.Data.Total / (double)PageSize), MaxPages);
        for (var page = 2; page <= pages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = await FetchPageAsync(fs, page, cancellationToken);
            var diff = next?.Data?.Diff;
            if (diff is null || diff.Count == 0) break;

            rows.AddRange(diff);
        }

        return rows;
    }

    private async Task<BoardResponse?> FetchPageAsync(
        string fs, int page, CancellationToken cancellationToken)
    {
        // f12 board code, f14 board name, f134 成分股数.
        var url =
            $"https://{Host}/api/qt/clist/get?pn={page}&pz={PageSize}&po=1&np=1" +
            $"&fltt=2&invt=2&fid=f3&fs={fs}&fields=f12,f13,f14,f134";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(Referer);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<BoardResponse>(json);
    }

    private sealed record BoardResponse
    {
        [JsonPropertyName("data")]
        public BoardData? Data { get; init; }
    }

    private sealed record BoardData
    {
        [JsonPropertyName("total")]
        public int Total { get; init; }

        [JsonPropertyName("diff")]
        public List<BoardRow>? Diff { get; init; }
    }

    private sealed record BoardRow
    {
        [JsonPropertyName("f12")]
        public string? Code { get; init; }

        [JsonPropertyName("f14")]
        public string? Name { get; init; }

        [JsonPropertyName("f134")]
        public object? MemberCount { get; init; }
    }
}
