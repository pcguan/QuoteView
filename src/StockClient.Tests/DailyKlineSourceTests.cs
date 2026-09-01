using System.Net;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

/// <summary>
/// Source routing for the period returns: which upstream answers for which
/// market, and how the EastMoney circuit breaker behaves. The breaker is why
/// the class takes a clock — five minutes of it would otherwise be untestable.
/// </summary>
public class DailyKlineSourceTests
{
    /// <summary>Every direct HTTP leg fails, so only the injected delegates answer.</summary>
    private static HttpClient DeadHttp() =>
        new(new StubHandler(_ => throw new HttpRequestException("upstream down")));

    // Contract.Market is derived from the code prefix, so the code alone picks
    // the routing branch under test.
    private static Contract Of(string code) =>
        new() { Code = code, Name = code };

    private static string KrBody(params double[] closes) =>
        "{\"candles\":[" + string.Join(",",
            closes.Select((c, i) => $"{{\"date\":\"2026-08-{20 + i:00}\",\"close\":{c}}}")) + "]}";

    private static DailyKlineSource Source(
        Func<string, int, int, int, CancellationToken, Task<string?>>? server = null,
        Func<string, CancellationToken, Task<string?>>? kr = null,
        bool signedIn = true,
        Func<DateTimeOffset>? now = null) =>
        new(new EastMoneyKlineClient(DeadHttp()),
            new TencentKlineClient(DeadHttp()),
            () => signedIn,
            server ?? ((_, _, _, _, _) => Task.FromResult<string?>(null)),
            kr ?? ((_, _) => Task.FromResult<string?>(null)),
            now);

    [Fact]
    public async Task Korea_reads_the_servers_own_archive()
    {
        var asked = "";
        var source = Source(kr: (code, _) =>
        {
            asked = code;
            return Task.FromResult<string?>(KrBody(1653000, 1674000));
        });

        var series = await source.FetchAsync(Of("KR000660"), default);

        Assert.Equal("KR000660", asked);
        Assert.NotNull(series);
        Assert.Equal(2, series!.Candles.Count);
        Assert.Equal(1674000, series.Candles[^1].Close);
    }

    [Fact]
    public async Task Korea_needs_a_session_and_never_touches_the_direct_chain()
    {
        var called = false;
        var source = Source(
            kr: (_, _) => { called = true; return Task.FromResult<string?>(KrBody(1)); },
            signedIn: false);

        // Signed out there is no archive to read, and no upstream serves KR
        // history — the row keeps showing "-" rather than a wrong number.
        Assert.Null(await source.FetchAsync(Of("KR000660"), default));
        Assert.False(called);
    }

    [Fact]
    public async Task Us_falls_back_to_the_server_proxy_when_the_direct_host_is_down()
    {
        var hits = 0;
        var source = Source(server: (secid, klt, fqt, lmt, _) =>
        {
            hits++;
            Assert.Equal("105.AAPL", secid);
            Assert.Equal(101, klt);      // day
            Assert.Equal(1, fqt);        // qfq
            Assert.Equal(DailyKlineSource.CandleCount, lmt);
            return Task.FromResult<string?>(
                "{\"data\":{\"code\":\"AAPL\",\"klines\":[\"2026-08-28,10,11,12,9,100,0\"]}}");
        });

        var series = await source.FetchAsync(Of("USAAPL"), default);

        Assert.Equal(1, hits);
        Assert.NotNull(series);
        Assert.Single(series!.Candles);
    }

    [Fact]
    public async Task A_failed_east_leg_trips_the_breaker_and_it_lifts_on_its_own()
    {
        var clock = DateTimeOffset.Parse("2026-09-01T10:00:00+08:00");
        var hits = 0;
        var source = Source(
            server: (_, _, _, _, _) => { hits++; return Task.FromResult<string?>(null); },
            now: () => clock);

        var us = Of("USAAPL");

        Assert.Null(await source.FetchAsync(us, default));
        Assert.Equal(1, hits);

        // Tripped: the next contracts skip the host outright instead of each
        // burning its full timeout — that is what made a whole group's 昨日涨幅
        // sit blank for minutes.
        Assert.Null(await source.FetchAsync(us, default));
        Assert.Equal(1, hits);

        clock = clock.AddMinutes(6);
        Assert.Null(await source.FetchAsync(us, default));
        Assert.Equal(2, hits);
    }

    [Fact]
    public async Task Cancellation_does_not_trip_the_breaker()
    {
        var clock = DateTimeOffset.Parse("2026-09-01T10:00:00+08:00");
        var hits = 0;
        using var cts = new CancellationTokenSource();

        var source = Source(
            server: (_, _, _, _, _) =>
            {
                hits++;
                cts.Cancel();      // a group switch lands mid-flight
                throw new OperationCanceledException(cts.Token);
            },
            now: () => clock);

        var us = Of("USAAPL");
        try { await source.FetchAsync(us, cts.Token); }
        catch (OperationCanceledException) { /* propagates by design */ }

        // A cancelled fetch says nothing about the host: counting it left BJ/US
        // (which have no Tencent history) with no daily source for 5 minutes
        // every time the user switched groups mid-crawl.
        var hitsAfter = hits;
        await source.FetchAsync(us, default);
        Assert.Equal(hitsAfter + 1, hits);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
