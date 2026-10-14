using System.Net;
using System.Text.Json;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using StockClient.Core.MarketWatch;
using Xunit;

namespace StockClient.Tests;

public class BoardMarketTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { data })) };
    private static object Page(int total, IEnumerable<int> ids) => new { total, diff = ids.Select(i => new {
        f12 = "BK" + i.ToString("D4"), f14 = "板块" + i, f2 = 1234.5, f3 = -1.25, f6 = 2e8, f62 = -3e7, f104 = 10, f105 = 20, f134 = 32, f124 = 1791856815 }) };

    [Fact]
    public async Task RankingsFetchEveryCategoryAndPageInStableOrder()
    {
        var calls = new List<string>(); var pauses = 0;
        using var http = new HttpClient(new Handler(r =>
        {
            var q = r.RequestUri!.Query; calls.Add(q);
            return Json(q.Contains("t:2") ? q.Contains("pn=1&") ? Page(101, Enumerable.Range(0, 100)) : Page(101, new[] { 100 })
                : Page(1, new[] { q.Contains("t:3") ? 200 : 300 }));
        }));
        var snapshot = await new EastMoneyBoardMarketClient(http, _ => { pauses++; return Task.CompletedTask; }).FetchAsync(null, default);
        Assert.Equal(103, snapshot.Rows.Count);
        Assert.Equal(3, pauses);
        Assert.All(calls, q => { Assert.Contains("fid=f12", q); Assert.Contains("pz=100", q); });
        Assert.Equal(101, snapshot.Rows.Count(r => r.Board.Kind == BoardKind.Industry));
        var row = snapshot.Rows[0];
        Assert.Equal(2, row.Amount); Assert.Equal(-3000, row.MainInflow); Assert.Equal(10, row.UpCount);
        Assert.Equal(20, row.DownCount); Assert.Equal(32, row.Board.MemberCount); Assert.NotNull(row.QuoteTime);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("count")]
    [InlineData("http")]
    public async Task PartialOrUnstableRankingsAreRejected(string fault)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) return Json(Page(101, Enumerable.Range(0, 100)));
            return fault == "http" ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : Json(Page(fault == "count" ? 102 : 101, fault == "missing" ? Array.Empty<int>() : new[] { fault == "duplicate" ? 0 : 100 }));
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => new EastMoneyBoardMarketClient(http, _ => Task.CompletedTask).FetchAsync(null, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationDoesNotFetchAnotherCategory()
    {
        using var cts = new CancellationTokenSource(); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Json(Page(1, new[] { 1 })); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EastMoneyBoardMarketClient(http,
            ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }).FetchAsync(null, cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NextAttemptSwitchesHostAndMissingQuotesRemainMissing()
    {
        var hosts = new List<string>(); var code = 0;
        using var http = new HttpClient(new Handler(r =>
        {
            hosts.Add(r.RequestUri!.Host);
            if (hosts.Count == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Json(new { total = 1, diff = new[] { new { f12 = "BK" + (++code).ToString("D4"), f14 = "测试", f3 = "-", f62 = "-" } } });
        }));
        var client = new EastMoneyBoardMarketClient(http, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchAsync(null, default));
        var result = await client.FetchAsync(null, default);
        Assert.Equal("push2.eastmoney.com", hosts[0]);
        Assert.All(hosts.Skip(1), h => Assert.Equal("82.push2.eastmoney.com", h));
        Assert.All(result.Rows, r => { Assert.Null(r.Percent); Assert.Null(r.MainInflow); });
    }

    private static BoardMarketQuote Row(string code, double? percent, BoardKind kind = BoardKind.Industry) =>
        new(new Board { Code = code, Name = "测试板块", Kind = kind }, null, percent, null, null, null, null, null);

    [Fact]
    public void RankingsSeparateSignsAndKeepStableTiesWithoutTreatingMissingAsZero()
    {
        var rows = new[] { Row("BK0002", 2), Row("BK0001", 2), Row("BK0003", -5), Row("BK0004", -1), Row("BK0005", 0), Row("BK0006", null), Row("BK0007", 9, BoardKind.Concept) };
        var industry = BoardRanking.Filter(rows, BoardKind.Industry, "测试");
        Assert.Equal(new[] { "BK0001", "BK0002" }, BoardRanking.Gainers(industry).Select(r => r.Code));
        Assert.Equal(new[] { "BK0003", "BK0004" }, BoardRanking.Losers(industry).Select(r => r.Code));
        Assert.Single(BoardRanking.Filter(rows, BoardKind.Concept, "BK0007"));
    }

    [Fact]
    public async Task BoardDetailsIncludeBeijingAndEveryPageWithoutChangingWholeMarketScope()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(r =>
        {
            calls.Add(r.RequestUri!.Query);
            var ids = r.RequestUri.Query.Contains("pn=1&") ? Enumerable.Range(1, 100) : new[] { 101 };
            return Json(new { total = 101, diff = ids.Select(i => new { f12 = (920000 + i).ToString(), f13 = 0, f14 = "北交所" + i, f2 = 12.34 }) });
        }));
        var client = new EastMoneyMarketClient(http, _ => Task.CompletedTask);
        var snapshot = await client.FetchBoardAsync("BK1033", null, default);
        Assert.Equal(101, snapshot.Rows.Count);
        Assert.All(snapshot.Rows, r => { Assert.Equal(Market.BJ, r.Contract.Market); Assert.Equal("北交所", r.MarketName); });
        Assert.All(calls, q => Assert.Contains("fs=b:BK1033", q));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(null, default));
    }

    [Fact]
    public async Task EmptyBoardIsValidButEmptyWholeMarketIsNot()
    {
        using var http = new HttpClient(new Handler(_ => Json(new { total = 0 })));
        var client = new EastMoneyMarketClient(http);
        Assert.Empty((await client.FetchBoardAsync("BK1033", null, default)).Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchAsync(null, default));
    }

    [Theory]
    [InlineData("BK1033&fs=m:0")]
    [InlineData("BKabc1")]
    [InlineData("1033")]
    public void RejectsInvalidBoardCodesBeforeAnyNetwork(string code)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("must not request")));
        Assert.Throws<ArgumentException>(() => { _ = new EastMoneyMarketClient(http).FetchBoardAsync(code, null, default); });
    }
}
