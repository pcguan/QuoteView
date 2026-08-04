using System.Globalization;
using System.Text.Json;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Today's intraday trend from Tencent — the fallback for when EastMoney's
/// trends2 path is throttled. (Measured on corp-win: trends2 answers with a
/// connection reset while the kline path on the same host, and Tencent, stay up.
/// Same throttling pattern the K-line fallback exists for.)
///
///   https://web.ifzq.gtimg.cn/appstock/app/minute/query?code=sh600519
///
/// Rows are `HHmm price cumulativeVolume [cumulativeAmount]`, under
/// data.{code}.data.data. Two differences from EastMoney worth knowing:
///
///   - volume is CUMULATIVE for the day, not the minute's own, so it is
///     differenced here — feeding the running total to a volume histogram draws
///     a staircase instead of bars.
///   - there is no average-price column; it is derived from amount/volume where
///     the amount column exists (A-shares and HK), and left at 0 for US/KR,
///     which return three columns only. Consumers must tolerate 0.
///
/// Coverage, measured: A-shares and HK give the full minute series; US and KR
/// return a single point (the last/closing minute). Best-effort backup that
/// keeps a line on screen, not a replacement.
/// </summary>
public sealed class TencentTrendClient
{
    private readonly HttpClient _http;

    public TencentTrendClient(HttpClient http) => _http = http;

    public async Task<TrendSeries> FetchAsync(Contract contract, CancellationToken cancellationToken)
    {
        var api = TencentQuoteClient.ToApiCode(contract.Code);

        using var response = await _http.GetAsync(
            $"https://web.ifzq.gtimg.cn/appstock/app/minute/query?code={api}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        // A-share volumes are in 手 (verified: price × volume × 100 == amount),
        // HK in 股 — so the average price needs the market's own multiplier.
        CodeMapper.TryParse(contract.Code, out var market, out _);
        var lot = market is "SH" or "SZ" or "BJ" ? 100 : 1;

        return Parse(json, api, contract, lot);
    }

    private static TrendSeries Parse(string json, string api, Contract contract, int lot)
    {
        var points = Array.Empty<TrendPoint>();
        var preClose = 0.0;

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty(api, out var node))
            {
                if (node.TryGetProperty("data", out var inner)
                    && inner.TryGetProperty("data", out var rows)
                    && rows.ValueKind == JsonValueKind.Array)
                {
                    points = ParseRows(rows, lot);
                }

                // Previous close comes from the quote block bundled in the same
                // response ([4], same layout as the batch quote endpoint).
                if (node.TryGetProperty("qt", out var qt)
                    && qt.TryGetProperty(api, out var fields)
                    && fields.ValueKind == JsonValueKind.Array
                    && fields.GetArrayLength() > 4)
                {
                    preClose = Kline.Parse(fields[4].GetString() ?? "");
                }
            }
        }
        catch (JsonException)
        {
            // Malformed body reads as "no data", same as an empty series; the
            // repository then keeps whatever it had.
        }

        return new TrendSeries
        {
            Code = contract.Code,
            Name = contract.Name,
            PreClose = preClose,
            Points = points,
        };
    }

    private static TrendPoint[] ParseRows(JsonElement rows, int lot)
    {
        var points = new List<TrendPoint>(rows.GetArrayLength());
        var previousCumulative = 0.0;

        foreach (var row in rows.EnumerateArray())
        {
            var c = (row.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (c.Length < 3) continue;

            var price = Kline.Parse(c[1]);
            if (price <= 0) continue;

            var cumulative = Kline.Parse(c[2]);
            var amount = c.Length > 3 ? Kline.Parse(c[3]) : 0;

            points.Add(new TrendPoint
            {
                Time = Time(c[0]),
                Price = price,
                // Cumulative → this minute's own. Guarded against a total that
                // goes backwards (a corrected row) so a bar is never negative.
                Volume = Math.Max(0, cumulative - previousCumulative),
                AvgPrice = amount > 0 && cumulative > 0 ? amount / (cumulative * lot) : 0,
            });

            previousCumulative = cumulative;
        }

        return points.ToArray();
    }

    /// <summary>`0930` → `09:30`, to match what EastMoney's rows carry.</summary>
    private static string Time(string raw) =>
        raw.Length == 4
            ? $"{raw[..2]}:{raw[2..]}"
            : raw;
}
