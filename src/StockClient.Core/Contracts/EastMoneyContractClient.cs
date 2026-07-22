using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyPinyin;

namespace StockClient.Core.Contracts;

public interface IContractListClient
{
    Task<IReadOnlyList<Contract>> FetchAsync(Market market, CancellationToken cancellationToken);
}

/// <summary>
/// Pulls full contract lists from EastMoney's clist endpoint.
///
/// The server caps pz at 100 regardless of what is asked for, so every market is
/// paged. Requests are issued sequentially: the measured cost of the largest
/// market (US, 137 pages) is ~1.1s that way, with no throttling, and a daily
/// refresh does not justify hammering with concurrency.
/// </summary>
public sealed class EastMoneyContractClient : IContractListClient
{
    private const int PageSize = 100;
    private const string Host = "push2delay.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";

    /// <summary>Guards against a bad `total` spinning the pager forever.</summary>
    private const int MaxPages = 400;

    private readonly HttpClient _http;

    public EastMoneyContractClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<Contract>> FetchAsync(
        Market market, CancellationToken cancellationToken)
    {
        var info = MarketInfo.Of(market);
        var byCode = new Dictionary<string, Contract>(StringComparer.OrdinalIgnoreCase);

        foreach (var filter in info.Filters)
        {
            foreach (var row in await FetchFilterAsync(filter.Fs, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name))
                    continue;

                // Market and type both come from the filter, never from the
                // response: EastMoney returns Beijing rows as market 0 (same as
                // Shenzhen), and f19 is 0 for every Korean row.
                var contract = Build(market, filter.Type, row);
                byCode[contract.Code] = contract;
            }
        }

        return byCode.Values.ToArray();
    }

    private static Contract Build(Market market, ContractType type, ClistRow row)
    {
        var name = row.Name!.Trim();
        return new Contract
        {
            Code = market + NormalizeCode(market, row.Code!),
            Name = name,
            Pinyin = Romanize(name),
            PinyinInitials = Initials(name),
            Type = market == Market.US ? UsType(row.SecurityType) : type,
            ListedOn = ParseDate(row.ListedOn),
            Industry = Clean(row.Industry),
            Region = Clean(row.Region),
            Concepts = Clean(row.Concepts),
            TotalShares = ParseLong(row.TotalShares),
            FloatShares = ParseLong(row.FloatShares),
            MarketNumber = ParseInt(row.MarketNumber),
        };
    }

    /// <summary>
    /// f13 is the EastMoney market number, needed later as the K-line secid prefix.
    /// Parsed without Clean(): 0 is a real market number here (SZ and BJ), and
    /// Clean treats "0" as missing.
    /// </summary>
    private static int? ParseInt(object? value) =>
        int.TryParse(value?.ToString()?.Trim() ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Share counts arrive as floats — fltt=2 makes EastMoney render f38/f39 as
    /// "96730934.0", which long.TryParse rejects outright. f26 comes back as a
    /// plain int, which is why the listing date parsed fine while these did not.
    /// </summary>
    private static long? ParseLong(object? value) =>
        decimal.TryParse(
            Clean(value) ?? "",
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0
            ? (long)parsed
            : null;

    private static ContractType UsType(object? f19) =>
        int.TryParse(Clean(f19) ?? "", out var code)
            ? UsSecurityTypes.From(code)
            : ContractType.Stock;

    /// <summary>EastMoney sends 20210909; markets without the data send "-".</summary>
    private static string? ParseDate(object? value)
    {
        var raw = Clean(value);
        if (raw is null || raw.Length != 8) return null;

        return DateOnly.TryParseExact(raw, "yyyyMMdd", out var date)
            ? date.ToString("yyyy-MM-dd")
            : null;
    }

    /// <summary>Missing values come back as "-" or 0, not null.</summary>
    private static string? Clean(object? value)
    {
        var raw = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(raw) || raw == "-" || raw == "0" ? null : raw;
    }

    /// <summary>EastMoney gives bare codes; US symbols are already plain (AAPL).</summary>
    private static string NormalizeCode(Market market, string code) =>
        market == Market.US ? code.Trim().ToUpperInvariant() : code.Trim();

    private static string Romanize(string name) =>
        HasHan(name) ? PinyinHelper.GetPinyin(name, "").ToLowerInvariant() : "";

    private static string Initials(string name)
    {
        if (!HasHan(name)) return "";

        var chars = name
            .Where(IsHan)
            .Select(c => PinyinHelper.GetPinyin(c.ToString(), ""))
            .Where(p => p.Length > 0)
            .Select(p => char.ToLowerInvariant(p[0]));

        return string.Concat(chars);
    }

    private static bool HasHan(string value) => value.Any(IsHan);

    private static bool IsHan(char c) => c >= 0x4E00 && c <= 0x9FFF;

    private async Task<List<ClistRow>> FetchFilterAsync(
        string filter, CancellationToken cancellationToken)
    {
        var rows = new List<ClistRow>();

        var first = await FetchPageAsync(filter, 1, cancellationToken);
        if (first?.Data is null) return rows;

        rows.AddRange(first.Data.Diff ?? new List<ClistRow>());

        var pages = Math.Min((int)Math.Ceiling(first.Data.Total / (double)PageSize), MaxPages);
        for (var page = 2; page <= pages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = await FetchPageAsync(filter, page, cancellationToken);
            var diff = next?.Data?.Diff;
            if (diff is null || diff.Count == 0) break;

            rows.AddRange(diff);
        }

        return rows;
    }

    private async Task<ClistResponse?> FetchPageAsync(
        string filter, int page, CancellationToken cancellationToken)
    {
        // `fs` must keep its literal '+' separators; escaping them breaks the filter.
        var url =
            $"https://{Host}/api/qt/clist/get?pn={page}&pz={PageSize}&po=1&np=1" +
            $"&fltt=2&invt=2&fid=f12&fs={filter}&fields=f12,f13,f14,f19,f26,f38,f39,f100,f102,f103";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(Referer);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ClistResponse>(json);
    }

    private sealed record ClistResponse
    {
        [JsonPropertyName("data")]
        public ClistData? Data { get; init; }
    }

    private sealed record ClistData
    {
        [JsonPropertyName("total")]
        public int Total { get; init; }

        [JsonPropertyName("diff")]
        public List<ClistRow>? Diff { get; init; }
    }

    private sealed record ClistRow
    {
        [JsonPropertyName("f12")]
        public string? Code { get; init; }

        [JsonPropertyName("f14")]
        public string? Name { get; init; }

        /// <summary>
        /// Market number. Already requested in the fields list; kept here as the
        /// K-line secid prefix. NOT for deriving the exchange — EastMoney returns
        /// 0 for both SZ and BJ (README pitfall ①), so the board still comes from
        /// which `fs` filter fetched the row, not from this.
        /// </summary>
        [JsonPropertyName("f13")]
        public object? MarketNumber { get; init; }

        /// <summary>Instrument type code. Meaningful for US; 0 for KR.</summary>
        [JsonPropertyName("f19")]
        public object? SecurityType { get; init; }

        /// <summary>上市日期, e.g. 20210909. Absent for KR.</summary>
        [JsonPropertyName("f26")]
        public object? ListedOn { get; init; }

        /// <summary>所属行业. Absent for KR.</summary>
        [JsonPropertyName("f100")]
        public object? Industry { get; init; }

        /// <summary>所属地区. A-shares only.</summary>
        [JsonPropertyName("f102")]
        public object? Region { get; init; }

        /// <summary>概念板块. A-shares only.</summary>
        [JsonPropertyName("f103")]
        public object? Concepts { get; init; }

        /// <summary>总股本.</summary>
        [JsonPropertyName("f38")]
        public object? TotalShares { get; init; }

        /// <summary>流通股.</summary>
        [JsonPropertyName("f39")]
        public object? FloatShares { get; init; }
    }
}
