using System.Text.Json;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Fetches multi-period returns from EastMoney's batch list endpoint and inverts
/// them into the baseline closing prices they were measured from.
///
///   https://push2.eastmoney.com/api/qt/ulist.np/get?secids=1.600519,0.000651&fields=...
///
/// One request covers the whole group, and it is needed once per trading day —
/// the baselines don't move, only the live price does.
///
/// <b>Field numbers verified against computed returns on two contracts</b>
/// (2026-08-05, 茅台 and 格力, every field matching to the cent):
///
///   f3   今日涨跌幅        f127  3日
///   f109 5日               f160  10日
///   f110 20日              f24   60日
///   f25  年初至今          f18   昨收
///
/// f127 is the trap this codebase already documents: the SAME number means 市净率
/// in `clist` and 细分行业 in `stock/get`. These readings hold for `ulist.np` only.
///
/// Coverage measured across markets: SH/SZ/BJ, HK and US all return real values;
/// <b>KR returns the same percentage for every period</b> and is rejected by
/// <see cref="ReturnBaselines.IsUsable"/>.
/// </summary>
public sealed class ReturnBaselineClient
{
    private const string Fields = "f12,f13,f2,f3,f18,f24,f25,f109,f110,f127,f160";
    private const string Host = "push2.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";

    /// <summary>secids per request — the same batching the fund-flow poll uses.</summary>
    private const int BatchSize = 100;

    private readonly HttpClient _http;

    public ReturnBaselineClient(HttpClient http) => _http = http;

    /// <summary>
    /// Baselines keyed by contract code. Contracts the feed had nothing usable for
    /// are simply absent — the caller shows blanks rather than zeros.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ReturnBaselines>> GetAsync(
        IReadOnlyList<(string Code, string SecId, DateOnly Date)> targets,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ReturnBaselines>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return result;

        for (var offset = 0; offset < targets.Count; offset += BatchSize)
        {
            var batch = targets.Skip(offset).Take(BatchSize).ToArray();
            var secids = string.Join(",", batch.Select(t => t.SecId));

            var url = $"https://{Host}/api/qt/ulist.np/get?fltt=2&secids={Uri.EscapeDataString(secids)}" +
                      $"&fields={Fields}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(Referer);

            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            Parse(json, batch, result);
        }

        return result;
    }

    private static void Parse(
        string json, IReadOnlyList<(string Code, string SecId, DateOnly Date)> batch,
        Dictionary<string, ReturnBaselines> into)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("diff", out var diff)
            || diff.ValueKind != JsonValueKind.Array)
            return;

        // Rows come back keyed by secid parts, same as the fund-flow poll: f13.f12
        // disambiguates Shenzhen from Beijing, which share market number 0.
        var bySecId = batch.ToDictionary(
            t => t.SecId, t => (t.Code, t.Date), StringComparer.OrdinalIgnoreCase);

        foreach (var row in diff.EnumerateArray())
        {
            var market = Int(row, "f13");
            var symbol = Str(row, "f12");
            if (symbol.Length == 0) continue;

            if (!bySecId.TryGetValue($"{market}.{symbol}", out var target)) continue;

            var price = Num(row, "f2");
            if (price is not > 0) continue;

            var baselines = new ReturnBaselines
            {
                Code = target.Code,
                Date = target.Date.ToString("yyyy-MM-dd"),
                // Previous close is served directly, no inversion needed.
                PrevClose = Num(row, "f18") ?? 0,
                Day3 = Baseline(price.Value, Num(row, "f127")),
                Day5 = Baseline(price.Value, Num(row, "f109")),
                Day10 = Baseline(price.Value, Num(row, "f160")),
                Day20 = Baseline(price.Value, Num(row, "f110")),
                Day60 = Baseline(price.Value, Num(row, "f24")),
                YearStart = Baseline(price.Value, Num(row, "f25")),
            };

            if (baselines.IsUsable) into[target.Code] = baselines;
        }
    }

    /// <summary>
    /// The close a percentage was measured from. 0 when the feed gave no usable
    /// percentage, or when the maths would divide by zero (a -100% move).
    /// </summary>
    private static double Baseline(double price, double? percent)
    {
        if (percent is null) return 0;

        var factor = 1 + percent.Value / 100;
        return factor > 1e-6 ? price / factor : 0;
    }

    private static double? Num(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static int Int(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : -1;

    private static string Str(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
