using System.Net;
using System.Text.Json;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

public sealed class OverseasIndexKlineTests
{
    private sealed class Handler(Action<Uri> check) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var uri = request.RequestUri!;
            check(uri);
            var api = uri.Query.Split('=')[1].Split(',')[0];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                data = new Dictionary<string, object> { [api] = new { day = new[] {
                    new[] { "2026-10-16", "10", "11", "12", "9", "100" },
                    new[] { "2026-10-19", "11", "12", "13", "10", "200" } } } },
            })) });
        }
    }

    [Theory]
    [InlineData("US.NDX", KlineAdjust.None)]
    [InlineData("US.NDX", KlineAdjust.Qfq)]
    [InlineData("US.INX", KlineAdjust.Hfq)]
    [InlineData("US.IXIC", KlineAdjust.None)]
    public async Task Dedicated_index_endpoint_has_no_stock_exchange_suffix(string code, KlineAdjust adjust)
    {
        using var http = new HttpClient(new Handler(uri =>
        {
            Assert.Equal("/appstock/app/usfqkline/get", uri.AbsolutePath);
            Assert.Contains("param=us" + code[2..] + ",day,,,30,qfq", uri.Query);
            Assert.DoesNotContain(".OQ", uri.Query);
            Assert.DoesNotContain(".N,", uri.Query);
        }));
        var bare = new Contract { Code = code, Name = code };
        var series = await new TencentKlineClient(http).FetchAsync(bare, KlinePeriod.Day, adjust, 30, default);
        Assert.Equal(2, series.Candles.Count);
        Assert.Equal(12, series.Candles[^1].Close);
        Assert.Equal(adjust, series.Adjust);
    }

    [Fact]
    public async Task Repository_uses_confirmed_index_route_before_stock_proxy_and_caches_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "index-kline-" + Guid.NewGuid());
        try
        {
            var handler = new Handler(uri => Assert.Equal("web.ifzq.gtimg.cn", uri.Host));
            using var http = new HttpClient(handler);
            var clock = new MarketClock(() => DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
            var repo = new KlineRepository(new EastMoneyKlineClient(http), new TencentKlineClient(http), new KlineCache(root), clock,
                server: (_, _, _, _, _) => throw new Exception("overseas index should not wait for stock proxy"));
            var index = IndexCatalog.All.Single(c => c.Code == "US.NDX");
            var (first, source) = await repo.GetAsync(index, KlinePeriod.Day, KlineAdjust.None, 30, default);
            Assert.Equal(2, first.Candles.Count);
            Assert.Equal("腾讯", source);
            await repo.GetAsync(index, KlinePeriod.Day, KlineAdjust.None, 30, default);
            Assert.Equal(1, handler.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
