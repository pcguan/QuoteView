using System.Text.Json;
using System.Text.Json.Serialization;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Today's intraday trend from EastMoney trends2 — the minute-by-minute line, not
/// the historical candles. Same secid as the K-line client, one request per pull.
///
///   https://push2his.eastmoney.com/api/qt/stock/trends2/get?secid=1.600519&ndays=1
///
/// All six markets return their own session length (241 points for A-shares,
/// more for HK/US/KR, US including its night session), so the caller drives the
/// x-axis off the returned points rather than a fixed 9:30–15:00 window. `preClose`
/// is the baseline the intraday change is measured against; the day is live, so
/// the caller re-polls.
/// </summary>
public sealed class EastMoneyTrendClient
{
    // f51 time, f53 price, f56 volume, f58 average.
    // (f52/f54/f55 are the minute's open/high/low, unused by the line.)
    private const string Fields2 = "f51,f52,f53,f54,f55,f56,f57,f58";
    private const string Host = "push2his.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";

    private readonly HttpClient _http;

    public EastMoneyTrendClient(HttpClient http) => _http = http;

    public async Task<TrendSeries> FetchAsync(Contract contract, CancellationToken cancellationToken)
    {
        var url =
            "/api/qt/stock/trends2/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8" +
            $"&fields2={Fields2}&ut=fa5fd1943c7b386f172d6893dbfba10b&iscr=0&ndays=1" +
            $"&secid={Uri.EscapeDataString(contract.EastMoneySecId)}";

        var response = await GetAsync(url, cancellationToken);

        var points = (response?.Data?.Trends ?? new List<string>())
            .Select(ParseRow)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();

        return new TrendSeries
        {
            Code = contract.Code,
            Name = string.IsNullOrWhiteSpace(response?.Data?.Name) ? contract.Name : response!.Data!.Name!,
            PreClose = response?.Data?.PreClose ?? 0,
            Points = points,
        };
    }

    private static TrendPoint? ParseRow(string row)
    {
        var c = row.Split(',');
        if (c.Length < 8) return null;

        return new TrendPoint
        {
            Time = c[0],
            Price = Kline.Parse(c[2]),
            AvgPrice = Kline.Parse(c[7]),
            Volume = Kline.Parse(c[5]),
        };
    }

    private async Task<TrendResponse?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}{path}");
        request.Headers.Referrer = new Uri(Referer);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TrendResponse>(json);
    }

    private sealed record TrendResponse
    {
        [JsonPropertyName("data")]
        public TrendData? Data { get; init; }
    }

    private sealed record TrendData
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("preClose")]
        public double PreClose { get; init; }

        [JsonPropertyName("trends")]
        public List<string>? Trends { get; init; }
    }
}
