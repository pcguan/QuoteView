using System.Net;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

public sealed class DelayedIndexQuoteTests
{
    private const string Body = """{"data":[{"s":"SP:S5INFT","d":[6992.61,1.187,82.03,7001.3,7062.02,6982.89,"delayed_streaming_600"]},{"s":"ICEUS:NYFANG","d":[18876.081,1.583,294.315,18581.766,19010.718,18581.766,"delayed_streaming_600"]},{"s":"NASDAQ:SOX","d":[11316.343,1.607,179.0563,11466.1215,11550.9228,11293.4539,"delayed_streaming_900"]}]}""";
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(Body) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        { Calls++; return Task.FromResult(Reply(req)); }
    }

    [Fact]
    public void Actual_scanner_schema_maps_identity_delay_and_values_without_inventing_exchange_time()
    {
        var quotes = OverseasIndexQuoteClient.Parse(Body);
        Assert.Equal(3, quotes.Count);
        var sox = quotes.Single(q => q.Code == "US.SOX");
        Assert.Equal(15, sox.DelayMinutes); Assert.Equal("费城半导体指数", sox.Name);
        Assert.Equal(11316.343 - 179.0563, sox.Yesterday, 8);
        Assert.Empty(sox.Time); Assert.Null(sox.Volume);
        Assert.All(quotes.Where(q => q.Code != sox.Code), q => Assert.Equal(10, q.DelayMinutes));
        Assert.All(quotes, q => Assert.False(q.RefreshFailed));
    }

    [Fact]
    public void Invalid_missing_or_unexpected_symbols_do_not_shift_quotes()
    {
        var bad = Body.Replace("SP:S5INFT", "NYSE:FAKE").Replace("11316.343", "0");
        Assert.Equal("US.NYFANG", Assert.Single(OverseasIndexQuoteClient.Parse(bad)).Code);
        Assert.Empty(OverseasIndexQuoteClient.Parse("""{"data":[{"s":"SP:S5INFT","d":[1,null,0,1,1,1,"delayed_streaming_600"]}]}"""));
    }

    [Fact]
    public async Task Concurrent_requests_cache_failure_backoff_recovery_and_last_good()
    {
        var now = DateTimeOffset.UtcNow; var h = new Handler(); using var http = new HttpClient(h);
        var client = new OverseasIndexQuoteClient(http, () => now);
        var codes = new[] { "US.SOX", "US.NYFANG", "US.S5INFT" };
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetQuotesAsync(codes, default)));
        Assert.Equal(1, h.Calls);
        now = now.AddSeconds(61); h.Reply = _ => throw new HttpRequestException();
        var old = await client.GetQuotesAsync(codes, default);
        Assert.All(old, q => { Assert.False(q.IsMissing); Assert.True(q.RefreshFailed); Assert.Contains("更新失败", q.DataNotice); });
        await client.GetQuotesAsync(codes, default); Assert.Equal(2, h.Calls);
        now = now.AddSeconds(61); h.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(Body) };
        Assert.All(await client.GetQuotesAsync(codes, default), q => Assert.False(q.RefreshFailed));
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task Cancellation_does_not_enter_failure_backoff()
    {
        var h = new Handler(); using var http = new HttpClient(h);
        var client = new OverseasIndexQuoteClient(http); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetQuotesAsync(new[] { "US.SOX" }, ct.Token));
        Assert.False((await client.GetQuotesAsync(new[] { "US.SOX" }, default))[0].IsMissing);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Supplementary_failure_does_not_break_regular_quotes_or_send_indices_to_Tencent()
    {
        var h = new Handler { Reply = req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("overseas-indices")) throw new HttpRequestException();
            Assert.DoesNotContain("SOX", req.RequestUri.OriginalString);
            var p = Enumerable.Repeat("", 62).ToArray(); p[1] = "Apple"; p[3] = "250";
            return new(HttpStatusCode.OK) { Content = new StringContent("v_usAAPL=\"" + string.Join("~", p) + "\";\n") };
        }};
        using var http = new HttpClient(h);
        var quotes = await new TencentQuoteClient(http).GetQuotesAsync(new[] { "US.SOX", "USAAPL" }, default);
        Assert.True(quotes[0].IsMissing); Assert.Equal("USAAPL", quotes[1].Code); Assert.Equal(250, quotes[1].Now);
    }

    [Theory]
    [InlineData("US.SOX")]
    [InlineData("US.S5INFT")]
    [InlineData("US.NYFANG")]
    public async Task Quote_only_history_fails_clearly_without_network_calls(string code)
    {
        var h = new Handler(); using var http = new HttpClient(h);
        var contract = IndexCatalog.All.Single(c => c.Code == code);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => new TencentKlineClient(http)
            .FetchAsync(contract, KlinePeriod.Day, KlineAdjust.None, 30, default));
        Assert.Contains("暂不提供K线", ex.Message); Assert.Equal(0, h.Calls);
        Assert.Null(await new TrendRepository(new EastMoneyTrendClient(http), new MarketClock()).GetAsync(contract, default));
        Assert.Equal(0, h.Calls);
        var root = Path.Combine(Path.GetTempPath(), "quote-only-" + Guid.NewGuid());
        try
        {
            var repo = new KlineRepository(new EastMoneyKlineClient(http), new TencentKlineClient(http),
                new KlineCache(root), new MarketClock(), server: (_, _, _, _, _) => throw new Exception("must skip stock proxy"));
            await Assert.ThrowsAsync<NotSupportedException>(() => repo.GetAsync(contract, KlinePeriod.Day, KlineAdjust.None, 30, default));
            var daily = new DailyKlineSource(new EastMoneyKlineClient(http), new TencentKlineClient(http), () => true,
                (_, _, _, _, _) => throw new Exception("must skip stock proxy"), (_, _) => throw new Exception("must skip KR proxy"));
            Assert.Null(await daily.FetchAsync(contract, default));
            Assert.Equal(0, h.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
