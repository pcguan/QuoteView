using System.Net;
using System.Text.Json;
using StockClient.Core.Contracts;
using StockClient.Core.MarketWatch;
using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

public sealed class LiveMarketTests
{
    private static Contract Stock(int i) => new() { Code = "SH" + (600000 + i), Name = "测试" + i, Type = ContractType.MainBoard, MarketNumber = 1 };
    private static Task Pace(CancellationToken _) => Task.CompletedTask;
    private static MarketUniverse Universe(int size) => new(Enumerable.Range(0, size).Select(Stock).ToArray(), new DateOnly(2026, 10, 22), "测试");
    private sealed class Progress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(req)) });
    }

    [Fact]
    public async Task Batches_Continue_After_Failure_And_Retain_Only_Failed_Quotes()
    {
        var universe = Universe(1601);
        var round = 0; var calls = new List<int>(); var partials = new List<MarketSnapshot>();
        var client = new LiveMarketClient((_, _) => Task.FromResult(universe), (codes, _) =>
        {
            calls.Add(codes.Count);
            if (round == 1 && codes[0] == "SH600800") throw new HttpRequestException("middle batch failed");
            return Task.FromResult<IReadOnlyList<Quote>>(codes.Select(c => new Quote { Code = c, Name = c, Now = round + 10,
                Time = "20261022140000", Amount = 120000000, Volume = 23000, TotalCap = 5000000000, PeTtm = 20 }).ToArray());
        }, Pace);
        var first = await client.FetchAsync(null, default);
        round = 1;
        var second = await client.FetchAsync(null, default, new Progress<MarketSnapshot>(partials.Add));
        Assert.Equal(new[] { 800, 800, 1, 800, 800, 1 }, calls);
        Assert.Equal(801, second.UpdatedCount);
        Assert.Equal(1601, second.Rows.Count);
        Assert.Equal(11, second.Rows[0].Price); Assert.Equal(10, second.Rows[800].Price); Assert.Equal(11, second.Rows[1600].Price);
        Assert.True(second.Rows[800].RefreshFailed); Assert.False(second.Rows[1600].RefreshFailed);
        Assert.Equal(first.Rows[800].ReceivedAt, second.Rows[800].ReceivedAt);
        Assert.Equal(3, partials.Count); Assert.Equal(800, partials[0].UpdatedCount);
        Assert.True(partials[0].Rows[1600].RefreshFailed); // immutable copy, not changed by later batches
        Assert.Contains("middle batch failed", second.ErrorDetail);
        Assert.Equal(1.2, second.Rows[0].Amount); Assert.Equal(2.3, second.Rows[0].Volume);
        Assert.Equal(50, second.Rows[0].TotalCap); Assert.Null(second.Rows[0].Pe); Assert.Equal(20, second.Rows[0].PeTtm);
        Assert.Equal(TimeSpan.FromHours(8), second.Rows[0].QuoteTime!.Value.Offset);
    }

    [Fact]
    public async Task Missing_And_All_Failed_Quotes_Remain_Explicit_And_Recover()
    {
        var mode = 0;
        var client = new LiveMarketClient((_, _) => Task.FromResult(Universe(2)), (codes, _) =>
        {
            if (mode == 1) throw new HttpRequestException("offline");
            return Task.FromResult<IReadOnlyList<Quote>>(new[] { new Quote { Code = codes[0], Name = "测试", Now = 9 }, Quote.Missing(codes[1]) });
        }, Pace);
        var first = await client.FetchAsync(null, default);
        Assert.Equal(1, first.UpdatedCount); Assert.Null(first.Rows[1].Price); Assert.True(first.Rows[1].RefreshFailed);
        mode = 1; var failed = await client.FetchAsync(null, default);
        Assert.Equal(0, failed.UpdatedCount); Assert.Equal(9, failed.Rows[0].Price); Assert.All(failed.Rows, r => Assert.True(r.RefreshFailed));
        mode = 0; var recovered = await client.FetchAsync(null, default);
        Assert.False(recovered.Rows[0].RefreshFailed); Assert.Equal(1, recovered.UpdatedCount);
    }

    [Fact]
    public async Task Cancellation_Does_Not_Become_A_Failed_Batch()
    {
        using var cts = new CancellationTokenSource();
        var client = new LiveMarketClient((_, _) => Task.FromResult(Universe(2)), (_, _) =>
        { cts.Cancel(); throw new OperationCanceledException(cts.Token); }, Pace);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(null, cts.Token));
    }

    [Fact]
    public void Extension_Merge_Cannot_Overwrite_Fresh_Base_Quotes_Or_Ttm()
    {
        var basic = new MarketQuote { Contract = Stock(0), Price = 25, Percent = 3, PeTtm = 20, QuoteTime = DateTimeOffset.UtcNow };
        var old = new MarketQuote { Contract = Stock(0) with { Industry = "电池" }, Price = 10, Percent = -5, Pe = 22,
            MainInflow = 500, ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10), QuoteTime = DateTimeOffset.UtcNow.AddMinutes(-11), ExtensionSource = "东财延迟源" };
        var merged = MarketExtensionMerge.Apply(basic, old, true);
        Assert.Equal(25, merged.Price); Assert.Equal(3, merged.Percent); Assert.Equal(20, merged.PeTtm); Assert.Equal(22, merged.Pe);
        Assert.Equal(500, merged.MainInflow); Assert.Equal("电池", merged.Industry); Assert.Equal(basic.QuoteTime, merged.QuoteTime);
        Assert.Equal(old.QuoteTime, merged.ExtensionTime); Assert.Contains("更新失败", merged.ExtensionStatus); Assert.Contains("延迟", merged.ExtensionStatus);
        Assert.Null(MarketExtensionMerge.Apply(basic, null, true).MainInflow);
    }

    [Fact]
    public async Task Extensions_Keep_Good_Pages_Around_A_Bad_Page()
    {
        using var http = new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.Query.Contains("pn=2&")) throw new HttpRequestException("page two down");
            var ids = req.RequestUri.Query.Contains("pn=3&") ? new[] { 200 } : Enumerable.Range(0, 100);
            return JsonSerializer.Serialize(new { data = new { total = 201, diff = ids.Select(i => new { f12 = (600000 + i).ToString(), f13 = 1, f14 = "测试", f62 = 15000 }) } });
        }));
        var result = await new EastMoneyMarketClient(http, Pace).FetchExtensionsAsync(default);
        Assert.Equal(101, result.Rows.Count); Assert.Contains("SH600200", result.UpdatedCodes);
        Assert.Equal(1.5, result.Rows[0].MainInflow); Assert.NotNull(result.Rows[0].ReceivedAt); Assert.Contains("page two down", result.ErrorDetail);
    }

    [Fact]
    public async Task Directory_Validates_Pages_Caches_Identities_And_Falls_Back_With_Warning()
    {
        var root = Path.Combine(Path.GetTempPath(), "qv-universe-" + Guid.NewGuid());
        var now = new DateTimeOffset(2026, 10, 22, 2, 0, 0, TimeSpan.Zero); var calls = 0; var fail = false;
        using var http = new HttpClient(new Handler(req =>
        {
            calls++; if (fail) throw new HttpRequestException("directory offline");
            if (req.RequestUri!.AbsolutePath.EndsWith("Count")) return "\"3\"";
            return """[{"symbol":"bj920001","name":"北交"},{"symbol":"sh688001","name":"华兴源创"},{"symbol":"sz300001","name":"特锐德"}]""";
        }));
        try
        {
            var client = new MarketUniverseClient(http, root, () => now, Pace);
            var first = await client.GetAsync(null, default);
            Assert.Equal(2, first.Contracts.Count); Assert.Equal(ContractType.Star, first.Contracts[0].Type);
            Assert.Equal(ContractType.ChiNext, first.Contracts[1].Type); Assert.Equal("", first.Warning);
            var count = calls;
            await new MarketUniverseClient(http, root, () => now, Pace).GetAsync(null, default);
            Assert.Equal(count, calls); // persisted daily directory
            var text = File.ReadAllText(Path.Combine(root, "market-universe.json"));
            Assert.DoesNotContain("Price", text); Assert.DoesNotContain("MainInflow", text);
            fail = true; now = now.AddDays(1);
            var stale = await client.GetAsync(null, default);
            Assert.Equal(first.Date, stale.Date); Assert.Equal(2, stale.Contracts.Count); Assert.Contains("覆盖未确认", stale.Warning);
            count = calls; await client.GetAsync(null, default); Assert.Equal(count, calls); // failed directory has separate cooldown
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Directory_Includes_Cdr_From_Kcb_And_Deduplicates_Ordinary_Star_Stocks()
    {
        var root = Path.Combine(Path.GetTempPath(), "qv-universe-" + Guid.NewGuid());
        using var http = new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("Count")) return "\"2\"";
            return req.RequestUri.Query.Contains("node=kcb")
                ? """[{"symbol":"sh688001","name":"科创"},{"symbol":"sh689009","name":"九号公司"}]"""
                : """[{"symbol":"sh688001","name":"科创"},{"symbol":"sz300001","name":"创业"}]""";
        }));
        try
        {
            var result = await new MarketUniverseClient(http, root, pace: Pace).GetAsync(null, default);
            Assert.Equal(3, result.Contracts.Count); Assert.Equal("", result.Warning);
            Assert.Equal(ContractType.Star, result.Contracts.Single(c => c.Code == "SH689009").Type);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Directory_Repairs_Only_Empty_Pages_Once_And_Validates_Total()
    {
        var root = Path.Combine(Path.GetTempPath(), "qv-universe-" + Guid.NewGuid());
        var pageCalls = new Dictionary<int, int>();
        using var http = new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("Count")) return "\"81\"";
            var page = req.RequestUri.Query.Contains("page=2&") ? 2 : 1;
            pageCalls[page] = pageCalls.GetValueOrDefault(page) + 1;
            if (page == 2 && pageCalls[page] == 1) return "[]";
            var ids = page == 2 ? new[] { 80 } : Enumerable.Range(0, 80);
            return JsonSerializer.Serialize(ids.Select(i => new { symbol = "sh" + (600000 + i), name = "测试" }));
        }));
        try
        {
            var result = await new MarketUniverseClient(http, root, pace: Pace).GetAsync(null, default);
            Assert.Equal(81, result.Contracts.Count); Assert.Equal("", result.Warning);
            Assert.Equal(2, pageCalls[1]); Assert.Equal(3, pageCalls[2]);
            Assert.True(File.Exists(Path.Combine(root, "market-universe.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Incomplete_Directory_Is_Usable_But_Never_Persisted_As_Complete()
    {
        var root = Path.Combine(Path.GetTempPath(), "qv-universe-" + Guid.NewGuid());
        using var http = new HttpClient(new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("Count")) return "\"161\"";
            if (req.RequestUri.Query.Contains("page=2&")) return "[]";
            var ids = req.RequestUri.Query.Contains("page=3&") ? new[] { 160 } : Enumerable.Range(0, 80);
            return JsonSerializer.Serialize(ids.Select(i => new { symbol = "sh" + (600000 + i), name = "测试" }));
        }));
        var result = await new MarketUniverseClient(http, root, pace: Pace).GetAsync(null, default);
        Assert.Equal(81, result.Contracts.Count); Assert.Contains("覆盖未确认", result.Warning);
        Assert.Contains(result.Contracts, c => c.Code == "SH600160"); Assert.False(File.Exists(Path.Combine(root, "market-universe.json")));
    }
}
