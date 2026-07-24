using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockClient.Core.Quotes;

/// <summary>The A-share-only extras EastMoney reports that Tencent doesn't.</summary>
public sealed record QuoteExtra
{
    public required string Code { get; init; }

    /// <summary>涨速 % (f22): price move over the last minute.</summary>
    public double? Speed { get; init; }

    /// <summary>主力净流入, raw 元 (f62).</summary>
    public double? MainInflow { get; init; }

    /// <summary>超大单净 (f66).</summary>
    public double? SuperInflow { get; init; }

    /// <summary>大单净 (f72).</summary>
    public double? BigInflow { get; init; }

    /// <summary>中单净 (f78).</summary>
    public double? MidInflow { get; init; }

    /// <summary>小单净 (f84).</summary>
    public double? SmallInflow { get; init; }

    /// <summary>主力净占比 % (f184).</summary>
    public double? MainInflowPct { get; init; }
}

/// <summary>
/// EastMoney batch real-time (ulist.np) for the A-share fund-flow and 涨速 fields
/// Tencent doesn't carry. One batched request for all secids — a secondary,
/// slower poll beside the primary Tencent quote, only run when those columns are
/// on (see <see cref="EastMoneyExtraPoller"/>).
///
/// A-shares only: 涨速/资金流 don't exist for HK/US/KR. Rows are matched back by
/// secid (f13.f12), which disambiguates SZ from BJ — both report f13=0.
/// </summary>
public sealed class EastMoneyQuoteClient
{
    private const string Host = "push2.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";
    private const string Fields = "f12,f13,f22,f62,f66,f72,f78,f84,f184";

    private readonly HttpClient _http;

    public EastMoneyQuoteClient(HttpClient http) => _http = http;

    /// <param name="targets">(normalized code, EastMoney secid) for each A-share.</param>
    public async Task<IReadOnlyDictionary<string, QuoteExtra>> GetAsync(
        IReadOnlyList<(string Code, string SecId)> targets, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, QuoteExtra>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return result;

        var bySecId = targets.ToDictionary(t => t.SecId, t => t.Code, StringComparer.OrdinalIgnoreCase);

        var url =
            $"https://{Host}/api/qt/ulist.np/get?fltt=2&invt=2" +
            $"&fields={Fields}&secids={string.Join(",", targets.Select(t => t.SecId))}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(Referer);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var rows = JsonSerializer.Deserialize<UlistResponse>(json)?.Data?.Diff;
        if (rows is null) return result;

        foreach (var row in rows)
        {
            if (row.Code is null || row.Market is null) continue;
            var secid = $"{row.Market}.{row.Code}";
            if (!bySecId.TryGetValue(secid, out var code)) continue;

            result[code] = new QuoteExtra
            {
                Code = code,
                Speed = Opt(row.Speed),
                MainInflow = Opt(row.MainInflow),
                SuperInflow = Opt(row.SuperInflow),
                BigInflow = Opt(row.BigInflow),
                MidInflow = Opt(row.MidInflow),
                SmallInflow = Opt(row.SmallInflow),
                MainInflowPct = Opt(row.MainInflowPct),
            };
        }

        return result;
    }

    // EastMoney sends "-" for a value it doesn't have; that must be null, not 0.
    private static double? Opt(object? value) =>
        double.TryParse(value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    private sealed record UlistResponse
    {
        [JsonPropertyName("data")] public UlistData? Data { get; init; }
    }

    private sealed record UlistData
    {
        [JsonPropertyName("diff")] public List<UlistRow>? Diff { get; init; }
    }

    private sealed record UlistRow
    {
        [JsonPropertyName("f12")] public string? Code { get; init; }
        [JsonPropertyName("f13")] public object? Market { get; init; }
        [JsonPropertyName("f22")] public object? Speed { get; init; }
        [JsonPropertyName("f62")] public object? MainInflow { get; init; }
        [JsonPropertyName("f66")] public object? SuperInflow { get; init; }
        [JsonPropertyName("f72")] public object? BigInflow { get; init; }
        [JsonPropertyName("f78")] public object? MidInflow { get; init; }
        [JsonPropertyName("f84")] public object? SmallInflow { get; init; }
        [JsonPropertyName("f184")] public object? MainInflowPct { get; init; }
    }
}
