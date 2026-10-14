using System.Globalization;
using System.Text.Json;
using StockClient.Core.Boards;

namespace StockClient.Core.MarketWatch;

/// <summary>Current board quotes, with stable-code paging; never publishes a partial ranking.</summary>
public sealed class EastMoneyBoardMarketClient
{
    private static readonly string[] Hosts = { "push2.eastmoney.com", "82.push2.eastmoney.com", "push2delay.eastmoney.com" };
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task> _pace;
    private int _hostIndex;
    private DateTimeOffset _retryPrimary;

    public EastMoneyBoardMarketClient(HttpClient http, Func<CancellationToken, Task>? pace = null)
    { _http = http; _pace = pace ?? (ct => Task.Delay(200, ct)); }

    public async Task<BoardMarketSnapshot> FetchAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_hostIndex > 0 && DateTimeOffset.UtcNow >= _retryPrimary) _hostIndex = 0;
        var host = Hosts[_hostIndex];
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        try
        {
            var rows = new List<BoardMarketQuote>();
            var seen = new HashSet<string>();
            var requests = 0;
            foreach (var (kind, type, name) in new[] { (BoardKind.Industry, 2, "行业"), (BoardKind.Concept, 3, "概念"), (BoardKind.Region, 1, "地区") })
            {
                int total = 0, received = 0, pages = 1;
                for (var page = 1; page <= pages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (requests++ > 0) await _pace(ct).ConfigureAwait(false);
                    var url = $"https://{host}/api/qt/clist/get?pn={page}&pz=100&po=0&np=1&fltt=2&invt=2&fid=f12&fs=m:90+t:{type}"
                        + "&fields=f12,f14,f2,f3,f6,f62,f104,f105,f124,f134";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
                    using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                        || !data.TryGetProperty("total", out var size) || !size.TryGetInt32(out var count) || count <= 0 || count > 4000
                        || !data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("板块行情接口返回无效数据");
                    if (page == 1) { total = count; pages = (total + 99) / 100; }
                    if (count != total || diff.GetArrayLength() != Math.Min(100, total - received))
                        throw new InvalidOperationException("板块分页不完整或数量变更");
                    foreach (var item in diff.EnumerateArray())
                    {
                        var code = Text(item, "f12"); var title = Text(item, "f14");
                        if (!ValidCode(code) || title.Length == 0 || !seen.Add(code))
                            throw new InvalidOperationException("板块代码无效或分页重复");
                        var stamp = Num(item, "f124");
                        rows.Add(new BoardMarketQuote(new Board { Code = code, Name = title, Kind = kind,
                                MemberCount = Count(item, "f134") ?? 0 },
                            Num(item, "f2"), Num(item, "f3"), Num(item, "f6") / 1e8, Num(item, "f62") / 1e4,
                            Count(item, "f104"), Count(item, "f105"),
                            stamp is > 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds((long)stamp.Value) : null));
                        received++;
                    }
                    progress?.Report($"正在刷新{name}板块 {page}/{pages} 页");
                }
            }
            ct.ThrowIfCancellationRequested();
            return new BoardMarketSnapshot(rows, DateTimeOffset.UtcNow, host);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (_hostIndex == 0) _retryPrimary = DateTimeOffset.UtcNow.AddMinutes(15);
            _hostIndex = (_hostIndex + 1) % Hosts.Length;
            throw;
        }
    }

    public static bool ValidCode(string code) => code.Length is >= 6 and <= 8
        && code.StartsWith("BK", StringComparison.Ordinal) && code.AsSpan(2).ToArray().All(char.IsAsciiDigit);
    private static string Text(JsonElement row, string key) => row.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        && v.ToString() != "-" ? v.ToString().Trim() : "";
    private static double? Num(JsonElement row, string key) => double.TryParse(Text(row, key), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    private static int? Count(JsonElement row, string key) => Num(row, key) is { } n && n >= 0 && n <= int.MaxValue ? (int)n : null;
}
