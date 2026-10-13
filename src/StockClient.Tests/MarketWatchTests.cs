using System.Net;
using System.Text;
using System.Text.Json;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using StockClient.Core.MarketWatch;
using Xunit;

namespace StockClient.Tests;

public sealed class MarketWatchTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response(request));
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static string Page(int count, IEnumerable<int> ids) => JsonSerializer.Serialize(new
    {
        data = new { total = count, diff = ids.Select(i => new { f12 = (600000 + i).ToString(), f13 = 1,
            f14 = "测试" + i, f2 = 10 + i, f3 = i, f6 = 250000000, f5 = 35000, f20 = 5000000000L,
            f62 = -1500000, f184 = -3.5, f124 = 1791856815 }) }
    });

    [Fact]
    public async Task Loads_every_page_with_stable_sort_pacing_units_and_timestamps()
    {
        var calls = new List<string>(); var pauses = 0;
        using var http = new HttpClient(new Handler(req =>
        {
            calls.Add(req.RequestUri!.ToString());
            return Json(req.RequestUri.Query.Contains("pn=1&") ? Page(101, Enumerable.Range(0, 100)) : Page(101, new[] { 100 }));
        }));
        var snapshot = await new EastMoneyMarketClient(http, _ => { pauses++; return Task.CompletedTask; }).FetchAsync(null, default);
        Assert.Equal(101, snapshot.Rows.Count);
        Assert.Equal(2, snapshot.Pages); Assert.Equal(1, pauses);
        Assert.All(calls, url => { Assert.Contains("fid=f12", url); Assert.Contains("pz=100", url); Assert.Contains("f184", url); });
        var row = snapshot.Rows[0];
        Assert.Equal(2.5, row.Amount); Assert.Equal(3.5, row.Volume); Assert.Equal(50, row.TotalCap);
        Assert.Equal(-150, row.MainInflow); Assert.Equal(-3.5, row.MainInflowPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791856815), row.QuoteTime);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("count-changed")]
    [InlineData("http")]
    public async Task Rejects_partial_snapshots_and_does_not_retry_immediately(string fault)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1) return Json(Page(101, Enumerable.Range(0, 100)));
            return fault == "http" ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : Json(Page(fault == "count-changed" ? 102 : 101,
                    fault == "missing" ? Array.Empty<int>() : new[] { fault == "duplicate" ? 0 : 100 }));
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => new EastMoneyMarketClient(http, _ => Task.CompletedTask).FetchAsync(null, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_between_pages_stops_requests()
    {
        using var ct = new CancellationTokenSource(); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Json(Page(101, Enumerable.Range(0, 100))); }));
        var client = new EastMoneyMarketClient(http, token => { ct.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(null, ct.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Missing_values_remain_null_and_unknown_market_is_rejected()
    {
        using var http = new HttpClient(new Handler(_ => Json("""
            {"data":{"total":1,"diff":[{"f12":"301001","f13":0,"f14":"测试","f2":"-","f3":0,"f184":"-"}]}}
            """)));
        var row = Assert.Single((await new EastMoneyMarketClient(http).FetchAsync(null, default)).Rows);
        Assert.Null(row.Price); Assert.Equal(0, row.Percent); Assert.Null(row.MainInflowPercent);
        Assert.Equal(ContractType.ChiNext, row.Contract.Type);
        using var invalid = new HttpClient(new Handler(_ => Json("""
            {"data":{"total":1,"diff":[{"f12":"920001","f13":0,"f14":"测试"}]}}
            """)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EastMoneyMarketClient(invalid).FetchAsync(null, default));
    }

    [Fact]
    public async Task Next_attempt_rotates_host_without_mixing_pages()
    {
        var hosts = new List<string>();
        using var http = new HttpClient(new Handler(req =>
        {
            hosts.Add(req.RequestUri!.Host);
            return hosts.Count == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json(Page(1, new[] { 0 }));
        }));
        var client = new EastMoneyMarketClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync(null, default));
        var next = await client.FetchAsync(null, default);
        Assert.Equal(new[] { "push2.eastmoney.com", "82.push2.eastmoney.com" }, hosts);
        Assert.Equal(hosts[1], next.Host);
    }

    private static MarketQuote Row(string code, ContractType type, double? pct, double? amount, string name = "电池股") => new()
    {
        Contract = new Contract { Code = code, Name = name, Type = type, Industry = "电池", PinyinInitials = "dcg" },
        Percent = pct, Amount = amount, Price = pct is null ? null : 10,
    };

    [Fact]
    public void Filters_whole_universe_with_market_boards_keyword_and_multiple_conditions()
    {
        var rows = new[] { Row("SZ300001", ContractType.ChiNext, 4, 2), Row("SZ000001", ContractType.MainBoard, 5, 3),
            Row("SH688001", ContractType.Star, 6, 4), Row("SZ300002", ContractType.ChiNext, 7, .5),
            Row("SZ300003", ContractType.ChiNext, 8, 5, "ST测试") };
        var query = new MarketQuery { Market = Market.SZ, Boards = ListingBoards.ChiNext, Keyword = "dcg", ExcludeSt = true,
            Conditions = new[] { new MarketCondition(MarketFields.All.Single(f => f.Key == "Percent"), MarketComparison.Greater, 3),
                new MarketCondition(MarketFields.All.Single(f => f.Key == "Amount"), MarketComparison.GreaterOrEqual, 1) } };
        Assert.Equal("SZ300001", Assert.Single(query.Apply(rows, "Amount", true)).Code);
        Assert.Empty((query with { Boards = ListingBoards.None }).Apply(rows, "Code", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sorts_numeric_values_not_display_text_and_missing_is_last(bool descending)
    {
        var rows = new[] { Row("SH600001", ContractType.MainBoard, 10, 2), Row("SH600002", ContractType.MainBoard, 2, 1),
            Row("SH600003", ContractType.MainBoard, null, null), Row("SH600004", ContractType.MainBoard, -3, 0) };
        var sorted = new MarketQuery().Apply(rows, "Percent", descending);
        Assert.Null(sorted[^1].Percent);
        Assert.Equal(descending ? new double?[] { 10, 2, -3, null } : new double?[] { -3, 2, 10, null }, sorted.Select(r => r.Percent));
        Assert.Equal(3, new MarketQuery { QuotedOnly = true }.Apply(rows, "Name", false).Count);
        Assert.False(new MarketCondition(MarketFields.All[1], MarketComparison.LessOrEqual, 0).Matches(rows[2]));
    }

    [Theory]
    [InlineData("2026-10-12T01:10:00Z", false)]
    [InlineData("2026-10-12T01:15:00Z", true)]
    [InlineData("2026-10-12T03:40:00Z", false)]
    [InlineData("2026-10-12T05:00:00Z", true)]
    [InlineData("2026-10-12T07:10:00Z", false)]
    [InlineData("2026-10-10T02:00:00Z", false)]
    public void Refresh_slows_during_lunch_closed_hours_and_weekends(string now, bool live) =>
        Assert.Equal(live, MarketRefreshPolicy.IsTradingSession(DateTimeOffset.Parse(now)));

    [Fact]
    public void Refresh_policy_backs_off_to_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), MarketRefreshPolicy.Interval);
        Assert.Equal(new[] { 120d, 240d, 480d, 600d, 600d }, Enumerable.Range(1, 5).Select(i => MarketRefreshPolicy.FailureDelay(i).TotalSeconds));
    }
}
