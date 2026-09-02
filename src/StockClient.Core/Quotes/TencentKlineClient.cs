using System.Text.Json;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// K-line from Tencent fqkline — the fallback for when EastMoney's kline endpoint
/// is throttled. (Measured: EastMoney rate-limits the kline path with connection
/// resets while its other paths, and Tencent, stay up.)
///
///   https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param=sh600519,day,,,2000,qfq
///
/// Coverage: full history for SH/SZ (front-adjusted), HK and US. US needs the
/// exchange suffix on THIS endpoint (usASX.N / usAAPL.OQ) — the quote endpoint
/// accepts the bare form, so the bare form got reused here for months and
/// Tencent answered it with a one-or-two-row stub, which read as "no US
/// history at Tencent". BJ and KR genuinely return nothing here in any form;
/// those stay EastMoney-only (BJ) or server-archived (KR).
///
/// Row order matches EastMoney: date, open, <b>close</b>, high, low, volume.
/// </summary>
public sealed class TencentKlineClient
{
    private const int Count = 2000;

    private readonly HttpClient _http;

    public TencentKlineClient(HttpClient http) => _http = http;

    public Task<KlineSeries> FetchAsync(
        Contract contract, KlinePeriod period, KlineAdjust adjust, CancellationToken cancellationToken) =>
        FetchAsync(contract, period, adjust, Count, cancellationToken);

    public async Task<KlineSeries> FetchAsync(
        Contract contract, KlinePeriod period, KlineAdjust adjust, int count,
        CancellationToken cancellationToken)
    {
        var api = ToKlineApiCode(contract);
        var span = Span(period);
        var (endpoint, adjustParam, prefix) = AdjustParts(adjust);

        var url = $"https://web.ifzq.gtimg.cn/appstock/app/{endpoint}" +
                  $"?param={api},{span},,,{Math.Max(1, count)}{adjustParam}";

        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var candles = Parse(json, api, prefix + span, span);

        return new KlineSeries
        {
            Code = contract.Code,
            Name = contract.Name,
            Period = period,
            Adjust = adjust,
            Candles = candles,
        };
    }

    /// <summary>
    /// The symbol form the kline endpoint wants. Unlike the quote endpoint it
    /// needs US exchange suffixes: .N NYSE, .OQ NASDAQ, .A NYSE American —
    /// keyed by EastMoney's market number, which the contract list carries
    /// (106/105/107). A bare US contract with no number defaults to NASDAQ,
    /// same as EastMoneySecId does; a miss there just falls through to
    /// EastMoney via the caller's chain.
    /// </summary>
    public static string ToKlineApiCode(Contract contract)
    {
        var api = TencentQuoteClient.ToApiCode(contract.Code);
        if (contract.Market != Market.US) return api;

        var suffix = (contract.MarketNumber ?? 105) switch
        {
            106 => ".N",
            107 => ".A",
            _ => ".OQ",
        };
        return api + suffix;
    }

    /// <summary>
    /// The rows live under data.{code}.{key}; the key is the adjusted one
    /// (qfqday…) for markets that honour adjustment and the plain one (day…) for
    /// those that ignore it, so both are tried.
    /// </summary>
    private static IReadOnlyList<Kline> Parse(string json, string api, string preferredKey, string plainKey)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty(api, out var node))
            return Array.Empty<Kline>();

        var rows = node.TryGetProperty(preferredKey, out var a) && a.ValueKind == JsonValueKind.Array ? a
            : node.TryGetProperty(plainKey, out var b) && b.ValueKind == JsonValueKind.Array ? b
            : default;

        if (rows.ValueKind != JsonValueKind.Array) return Array.Empty<Kline>();

        var list = new List<Kline>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;

            list.Add(new Kline
            {
                Date = row[0].GetString() ?? "",
                Open = Num(row[1]),
                Close = Num(row[2]),
                High = Num(row[3]),
                Low = Num(row[4]),
                Volume = Num(row[5]),
            });
        }

        return list;
    }

    private static double Num(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => Kline.Parse(e.GetString() ?? ""),
        JsonValueKind.Number => e.GetDouble(),
        _ => 0,
    };

    private static string Span(KlinePeriod period) => period switch
    {
        KlinePeriod.Week => "week",
        KlinePeriod.Month => "month",
        _ => "day",
    };

    private static (string Endpoint, string AdjustParam, string Prefix) AdjustParts(KlineAdjust adjust) => adjust switch
    {
        KlineAdjust.Qfq => ("fqkline/get", ",qfq", "qfq"),
        KlineAdjust.Hfq => ("fqkline/get", ",hfq", "hfq"),
        _ => ("kline/kline", "", ""),
    };
}
