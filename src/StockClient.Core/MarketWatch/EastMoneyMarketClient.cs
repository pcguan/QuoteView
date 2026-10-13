using System.Globalization;
using System.Text.Json;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using TinyPinyin;

namespace StockClient.Core.MarketWatch;

/// <summary>A complete current snapshot, published only after every stable-code page validates.</summary>
public sealed class EastMoneyMarketClient
{
    private const int PageSize = 100;
    private const string Filter = "m:1+t:2,m:1+t:23,m:0+t:6,m:0+t:80";
    private const string Fields = "f12,f13,f14,f2,f3,f4,f5,f6,f7,f8,f9,f10,f15,f16,f17,f18,f20,f21,f22,f23,f26,f62,f66,f72,f78,f84,f100,f102,f103,f124,f184";
    private static readonly string[] Hosts = { "push2.eastmoney.com", "82.push2.eastmoney.com", "push2delay.eastmoney.com" };
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task> _pace;
    private int _hostIndex;
    private DateTimeOffset _retryPrimary;

    public EastMoneyMarketClient(HttpClient http, Func<CancellationToken, Task>? pace = null)
    { _http = http; _pace = pace ?? (ct => Task.Delay(200, ct)); }

    public async Task<MarketSnapshot> FetchAsync(IProgress<(int Page, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        if (_hostIndex > 0 && DateTimeOffset.UtcNow >= _retryPrimary) _hostIndex = 0;
        var host = Hosts[_hostIndex];
        var started = DateTimeOffset.UtcNow;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        try
        {
            var rows = new List<MarketQuote>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0, pages = 1;
            for (var page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();
                if (page > 1) await _pace(ct).ConfigureAwait(false);
                var url = $"https://{host}/api/qt/clist/get?pn={page}&pz={PageSize}&po=0&np=1&fltt=2&invt=2&fid=f12&fs={Filter}&fields={Fields}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("total", out var size) || !size.TryGetInt32(out var count)
                    || count <= 0 || count > 30000 || !data.TryGetProperty("diff", out var diff)
                    || diff.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("全市场行情接口返回无效数据");
                if (page == 1) { total = count; pages = (total + PageSize - 1) / PageSize; }
                if (count != total || diff.GetArrayLength() != Math.Min(PageSize, total - rows.Count))
                    throw new InvalidOperationException("行情分页不完整或合约数量变更");
                foreach (var item in diff.EnumerateArray())
                {
                    var row = Parse(item);
                    if (!seen.Add(row.Code)) throw new InvalidOperationException("行情分页含重复合约");
                    rows.Add(row);
                }
                progress?.Report((page, pages));
            }
            ct.ThrowIfCancellationRequested();
            if (rows.Count != total) throw new InvalidOperationException("全市场行情尚未完整");
            return new MarketSnapshot(rows, started, DateTimeOffset.UtcNow, host, pages);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No immediate retry storm or mixing realtime/delayed pages. The poller
            // backs off before the next whole-snapshot attempt on another host.
            if (_hostIndex == 0) _retryPrimary = DateTimeOffset.UtcNow.AddMinutes(15);
            _hostIndex = (_hostIndex + 1) % Hosts.Length;
            throw;
        }
    }

    private static MarketQuote Parse(JsonElement row)
    {
        var code = Text(row, "f12");
        var name = Text(row, "f14");
        var number = Num(row, "f13");
        var listing = BoardSearch.Classify(code, number is 0 or 1 ? (int)number.Value : null);
        if (code.Length != 6 || !code.All(char.IsAsciiDigit) || name.Length == 0
            || listing is not { } pair || pair.Market is not (Market.SH or Market.SZ))
            throw new InvalidOperationException("行情合约身份字段缺失或市场不匹配");
        var date = Text(row, "f26");
        var stamp = Num(row, "f124");
        return new MarketQuote
        {
            Contract = new Contract
            {
                Code = pair.Market + code, Name = name, Type = pair.Type, MarketNumber = (int)number!.Value,
                Industry = Text(row, "f100"), Region = Text(row, "f102"), Concepts = Text(row, "f103"),
                ListedOn = DateOnly.TryParseExact(date, "yyyyMMdd", out var listed) ? listed.ToString("yyyy-MM-dd") : null,
                Pinyin = PinyinHelper.GetPinyin(name, "").ToLowerInvariant(),
                PinyinInitials = string.Concat(name.Select(c => PinyinHelper.GetPinyin(c.ToString(), "")[0])).ToLowerInvariant(),
            },
            Price = Num(row, "f2"), Percent = Num(row, "f3"), Change = Num(row, "f4"),
            Open = Num(row, "f17"), Previous = Num(row, "f18"), High = Num(row, "f15"), Low = Num(row, "f16"),
            Volume = Num(row, "f5") / 1e4, Amount = Num(row, "f6") / 1e8,
            Turnover = Num(row, "f8"), VolumeRatio = Num(row, "f10"), Amplitude = Num(row, "f7"),
            Speed = Num(row, "f22"), TotalCap = Num(row, "f20") / 1e8, FloatCap = Num(row, "f21") / 1e8,
            Pe = Num(row, "f9"), Pb = Num(row, "f23"), MainInflow = Num(row, "f62") / 1e4,
            MainInflowPercent = Num(row, "f184"), SuperInflow = Num(row, "f66") / 1e4,
            BigInflow = Num(row, "f72") / 1e4, MidInflow = Num(row, "f78") / 1e4, SmallInflow = Num(row, "f84") / 1e4,
            QuoteTime = stamp is > 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds((long)stamp.Value) : null,
        };
    }

    private static string Text(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && value.ToString() != "-" ? value.ToString().Trim() : "";
    private static double? Num(JsonElement row, string key) =>
        double.TryParse(Text(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? number : null;
}
