using System.Net;
using System.Text;
using System.Text.Json;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using Xunit;

namespace StockClient.Tests;

public class BoardSearchTests
{
    private static Board B(string code, string name, BoardKind kind = BoardKind.Industry) =>
        new() { Code = code, Name = name, Kind = kind };

    [Fact]
    public void Matching_includes_industry_and_concept_but_not_regions()
    {
        var boards = new[] { B("BK2", "固态电池", BoardKind.Concept), B("BK1", "电池"),
            B("BK3", "电池地区", BoardKind.Region), B("BK4", "银行") };
        var result = BoardSearch.Match(boards, " 电池板块 ");
        Assert.Equal(new[] { "BK1", "BK2" }, result.Select(b => b.Code));
        Assert.Empty(BoardSearch.Match(boards, ""));
        Assert.Single(BoardSearch.Match(boards, "bk1"));
    }

    [Theory]
    [InlineData("600519", 1, Market.SH, ContractType.MainBoard)]
    [InlineData("688001", 1, Market.SH, ContractType.Star)]
    [InlineData("689009", 1, Market.SH, ContractType.Star)]
    [InlineData("001258", 0, Market.SZ, ContractType.MainBoard)]
    [InlineData("300750", 0, Market.SZ, ContractType.ChiNext)]
    [InlineData("301001", 0, Market.SZ, ContractType.ChiNext)]
    [InlineData("920914", 0, Market.BJ, ContractType.BeijingBoard)]
    [InlineData("830799", 0, Market.BJ, ContractType.BeijingBoard)]
    [InlineData("430418", 0, Market.BJ, ContractType.BeijingBoard)]
    public void Classifies_listing_board_without_confusing_beijing_and_shenzhen(
        string code, int number, Market market, ContractType type)
    {
        Assert.Equal((market, type), BoardSearch.Classify(code, number)!.Value);
    }

    [Fact]
    public void Multi_board_filter_supports_exclusion_and_empty_selection()
    {
        var contracts = new[]
        {
            new Contract { Code="SH600001", Name="甲", Type=ContractType.MainBoard },
            new Contract { Code="SZ300001", Name="乙", Type=ContractType.ChiNext },
            new Contract { Code="SH688001", Name="丙", Type=ContractType.Star },
            new Contract { Code="BJ920001", Name="丁", Type=ContractType.BeijingBoard },
        };
        Assert.Equal(new[] { "SH600001", "BJ920001" }, contracts.Where(c =>
            BoardSearch.Includes(c, ListingBoards.Main | ListingBoards.Beijing)).Select(c => c.Code));
        Assert.Empty(contracts.Where(c => BoardSearch.Includes(c, ListingBoards.None)));
        Assert.All(contracts, c => Assert.True(BoardSearch.Includes(c, ListingBoards.All)));
    }

    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
    }
    private static string Page(int total, IEnumerable<int> indices) => JsonSerializer.Serialize(new
    {
        data = new { total, diff = indices.Select(i => new
            { f12 = (600000 + i).ToString(), f13 = 1, f14 = "电池成员" + i, f100 = "电池" }) }
    });

    [Fact]
    public async Task Fetches_full_union_with_stable_paging()
    {
        var urls = new List<string>();
        using var http = new HttpClient(new Handler(req =>
        {
            var url = req.RequestUri!.ToString(); urls.Add(url);
            return url.Contains("pn=1&") ? Page(101, Enumerable.Range(0, 100)) : Page(101, new[] { 100 });
        }));
        var result = await new EastMoneyContractClient(http).FetchBoardMembersAsync(new[] { "BK1033", "BK0968" }, default);
        Assert.Equal(101, result.Count);
        Assert.All(urls, url => { Assert.Contains("fid=f12", url); Assert.True(url.Contains("b:BK1033") || url.Contains("b:BK0968")); Assert.DoesNotContain(",b:", url); });
        Assert.All(result, c => Assert.Equal(ContractType.MainBoard, c.Type));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("total-changed")]
    public async Task Rejects_partial_or_shifted_member_lists(string fault)
    {
        using var http = new HttpClient(new Handler(req => req.RequestUri!.Query.Contains("pn=1&")
            ? Page(101, Enumerable.Range(0, 100))
            : fault == "missing" ? Page(101, Array.Empty<int>())
            : fault == "duplicate" ? Page(101, new[] { 0 }) : Page(102, new[] { 100 })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EastMoneyContractClient(http)
            .FetchBoardMembersAsync(new[] { "BK1033" }, default));
    }

    [Fact]
    public async Task Board_member_market_zero_can_be_beijing()
    {
        using var http = new HttpClient(new Handler(_ =>
            "{\"data\":{\"total\":1,\"diff\":[{\"f12\":\"920914\",\"f13\":0,\"f14\":\"远航精密\"}]}}"));
        var rows = await new EastMoneyContractClient(http).FetchBoardMembersAsync(new[] { "BK1033" }, default);
        Assert.Equal("BJ920914", Assert.Single(rows).Code);
    }

    [Fact]
    public async Task Cache_is_shared_for_reordered_union_and_expires_next_day()
    {
        var now = DateTimeOffset.Parse("2026-10-08T08:00:00Z");
        var calls = 0;
        var repo = new BoardMemberRepository((_, _) =>
        { calls++; return Task.FromResult<IReadOnlyList<Contract>>(Array.Empty<Contract>()); }, new MarketClock(() => now));
        var a = B("BK1", "电池"); var b = B("BK2", "固态电池");
        await repo.GetAsync(new[] { a, b }, default);
        await repo.GetAsync(new[] { b, a, a }, default);
        Assert.Equal(2, calls);
        now = now.AddDays(1);
        await repo.GetAsync(new[] { a, b }, default);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Failure_is_not_cached_and_retry_can_succeed()
    {
        var calls = 0;
        var repo = new BoardMemberRepository((_, _) =>
        {
            if (++calls == 1) throw new InvalidOperationException("partial response");
            return Task.FromResult<IReadOnlyList<Contract>>(Array.Empty<Contract>());
        }, new MarketClock());
        var boards = new[] { B("BK1", "电池") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.GetAsync(boards, default));
        await repo.GetAsync(boards, default);
        Assert.Equal(2, calls);
    }
}
