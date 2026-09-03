using System.Text.Json;
using System.Text.Json.Serialization;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// 逐笔成交 (成交明细) from EastMoney details/get — the running tape of aggregated
/// ticks for TODAY only (there is no historical form; the server archives each
/// session's tape after close, see the sweep). One request returns the whole
/// running day; <c>pos=-N</c> caps it to the last N rows.
///
///   https://push2.eastmoney.com/api/qt/stock/details/get?secid=1.600519&pos=-100
///
/// Each <c>details</c> row is <c>时间,成交价,量(手),笔数,方向</c> with 方向
/// 1 主动买 / 2 主动卖 / 4 中性. Covers 沪深 (and 北交所); other markets return
/// empty. Same secid and retry discipline as <see cref="EastMoneyTrendClient"/>.
/// </summary>
public sealed class EastMoneyDetailsClient
{
    private const string Fields2 = "f51,f52,f53,f54,f55";
    private const string Host = "push2.eastmoney.com";
    private const string Referer = "https://quote.eastmoney.com/";

    private readonly HttpClient _http;

    public EastMoneyDetailsClient(HttpClient http) => _http = http;

    /// <param name="max">Cap on rows (the tape's tail). Use a large value for the
    /// whole session (the archive sweep), a small one for a live tape.</param>
    public async Task<TradeTickSnapshot?> FetchAsync(Contract contract, int max, CancellationToken cancellationToken)
    {
        var url =
            "/api/qt/stock/details/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8" +
            $"&fields2={Fields2}&ut=fa5fd1943c7b386f172d6893dbfba10b&pos=-{Math.Max(1, max)}" +
            $"&secid={Uri.EscapeDataString(contract.EastMoneySecId)}";

        var response = await GetAsync(url, cancellationToken);
        if (response?.Data is not { } data) return null;

        var ticks = (data.Details ?? new List<string>())
            .Select(ParseRow)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();

        return new TradeTickSnapshot
        {
            Code = contract.Code,
            PrePrice = data.PrePrice,
            Decimals = data.Decimal > 0 ? data.Decimal : 2,
            Ticks = ticks,
        };
    }

    /// <summary>Parses one <c>时间,价,量,笔数,方向</c> row; null when malformed.</summary>
    public static TradeTick? ParseRow(string row)
    {
        var c = row.Split(',');
        if (c.Length < 5) return null;
        if (!long.TryParse(c[2], out var volume)) return null;

        return new TradeTick
        {
            Time = c[0],
            Price = Kline.Parse(c[1]),
            Volume = volume,
            Trades = int.TryParse(c[3], out var n) ? n : 0,
            Side = c[4] switch { "1" => TradeSide.Buy, "2" => TradeSide.Sell, _ => TradeSide.Neutral },
        };
    }

    private async Task<DetailsResponse?> GetAsync(string path, CancellationToken cancellationToken)
    {
        // Same flaky host as the trend/kline paths; retry the ordinary drop.
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
                return JsonSerializer.Deserialize<DetailsResponse>(json);
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

    private sealed record DetailsResponse
    {
        [JsonPropertyName("data")]
        public DetailsData? Data { get; init; }
    }

    private sealed record DetailsData
    {
        [JsonPropertyName("prePrice")]
        public double PrePrice { get; init; }

        [JsonPropertyName("decimal")]
        public int Decimal { get; init; }

        [JsonPropertyName("details")]
        public List<string>? Details { get; init; }
    }
}
