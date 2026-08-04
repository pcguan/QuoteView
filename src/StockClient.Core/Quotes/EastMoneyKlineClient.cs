using System.Text.Json;
using System.Text.Json.Serialization;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// K-line history from EastMoney's push2his endpoint.
///
/// Chosen over Tencent's fqkline deliberately. Measured on all six markets:
/// Tencent's web.ifzq endpoint returns full history only for SH/SZ/HK and just
/// the current day for BJ/US/KR — and it silently ignores an adjustment request
/// for the non-A markets (returns the "day" key instead of "qfqday"), so asking
/// for front-adjusted HK/US/KR yields an empty array with no error. EastMoney
/// serves complete history for every market from one code path, and it is the
/// same source the contract lists already come from.
///
///   https://push2his.eastmoney.com/api/qt/stock/kline/get?...&secid=1.600519
///
/// The row is CSV: date,open,close,high,low,volume,amount — close is the THIRD
/// column, not the second. See <see cref="Kline"/>.
/// </summary>
public sealed class EastMoneyKlineClient
{
    // fields2 selects the row columns, in this order:
    // f51 date, f52 open, f53 close, f54 high, f55 low, f56 volume, f57 amount.
    private const string Fields2 = "f51,f52,f53,f54,f55,f56,f57";

    private const string Host = "push2his.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";

    private readonly HttpClient _http;

    public EastMoneyKlineClient(HttpClient http) => _http = http;

    public async Task<KlineSeries> FetchAsync(
        Contract contract, KlinePeriod period, KlineAdjust adjust,
        int count, CancellationToken cancellationToken)
    {
        // Two shapes, chosen by count:
        //   count > 0  → end + lmt: the most recent N candles. With BOTH beg and
        //     end present the endpoint ignores lmt, so a bounded request must omit
        //     beg. Used by the smoke test to stay small.
        //   count <= 0 → beg + end: the entire listed history in one response. No
        //     paging — measured 5962 rows for 茅台, 10545 for 苹果, ~0.5s each. This
        //     is what the chart uses, so panning/zooming reaches the listing day
        //     with a single request per open.
        var range = count > 0
            ? $"&end=20500101&lmt={count}"
            : "&beg=19700101&end=20500101";

        var url =
            $"/api/qt/stock/kline/get?fields1=f1,f2,f3,f4,f5,f6&fields2={Fields2}" +
            "&ut=7eea3edcaed734bea9cbfc24409ed989" +
            $"&klt={PeriodCode(period)}&fqt={AdjustCode(adjust)}" +
            $"&secid={Uri.EscapeDataString(contract.EastMoneySecId)}" +
            range;

        var response = await GetAsync(url, cancellationToken);

        var candles = (response?.Data?.Klines ?? new List<string>())
            .Select(ParseRow)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToArray();

        return new KlineSeries
        {
            Code = contract.Code,
            // The endpoint echoes the resolved name; fall back to what we already
            // know so the window title is never blank.
            Name = string.IsNullOrWhiteSpace(response?.Data?.Name) ? contract.Name : response!.Data!.Name!,
            Period = period,
            Adjust = adjust,
            Candles = candles,
        };
    }

    /// <summary>One CSV row -> Kline. Null when a row is malformed, so it drops out.</summary>
    private static Kline? ParseRow(string row)
    {
        var c = row.Split(',');
        if (c.Length < 5) return null;

        return new Kline
        {
            Date = c[0],
            Open = Kline.Parse(c[1]),
            Close = Kline.Parse(c[2]),
            High = Kline.Parse(c[3]),
            Low = Kline.Parse(c[4]),
            Volume = c.Length > 5 ? Kline.Parse(c[5]) : 0,
            Amount = c.Length > 6 ? Kline.Parse(c[6]) : 0,
        };
    }

    private static int PeriodCode(KlinePeriod period) => period switch
    {
        KlinePeriod.Week => 102,
        KlinePeriod.Month => 103,
        _ => 101,
    };

    private static int AdjustCode(KlineAdjust adjust) => adjust switch
    {
        KlineAdjust.Qfq => 1,
        KlineAdjust.Hfq => 2,
        _ => 0,
    };

    private async Task<KlineResponse?> GetAsync(string path, CancellationToken cancellationToken)
    {
        // A few retries to ride out EastMoney's frequent transient drops
        // (connection reset / response ended prematurely). When it throttles the
        // kline path outright, all attempts fail and the caller falls back to
        // Tencent — retries only help the ordinary flaky case.
        const int maxAttempts = 3;
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}{path}");
                request.Headers.Referrer = new Uri(Referer);

                using var response = await _http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<KlineResponse>(json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (attempt < maxAttempts) await Task.Delay(300 * attempt, cancellationToken);
            }
        }

        if (last is not null) throw last;
        return null;
    }

    private sealed record KlineResponse
    {
        [JsonPropertyName("data")]
        public KlineData? Data { get; init; }
    }

    private sealed record KlineData
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("klines")]
        public List<string>? Klines { get; init; }
    }
}
