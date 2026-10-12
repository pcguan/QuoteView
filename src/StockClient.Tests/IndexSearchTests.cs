using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;
using Xunit;

namespace StockClient.Tests;

public sealed class IndexSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qv-index-" + Guid.NewGuid());
    private readonly MarketClock _clock = new(() => DateTimeOffset.Parse("2026-10-08T08:00:00Z"));
    private ContractCache Cache => new(_root);
    private ContractRepository Repo(IContractListClient? client = null) =>
        new(client ?? new ListClient(fail: true), Cache, _clock);

    [Theory]
    [InlineData("上证指数", "SH000001")]
    [InlineData("沪指", "SH000001")]
    [InlineData("深证指数", "SZ399001")]
    [InlineData("创业板指数", "SZ399006")]
    [InlineData("szcz", "SZ399001")]
    [InlineData("shangzhengzhishu", "SH000001")]
    [InlineData("sh000001", "SH000001")]
    [InlineData("399006", "SZ399006")]
    public void Names_aliases_pinyin_and_codes_work_before_network_loading(string query, string expected)
    {
        Assert.Equal(expected, Repo().Search(query)[0].Code);
        Assert.True(IndexCatalog.MatchesExactly(query));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Current_and_stale_caches_gain_indices_without_losing_stocks(int days)
    {
        Cache.Save(Market.SZ, _clock.TradingDate(Market.SZ).AddDays(days), new[] { Stock });
        var repo = Repo();
        var result = await repo.EnsureMarketAsync(Market.SZ);
        Assert.True(result.FromCache);
        Assert.Equal(days < 0, result.Stale);
        Assert.Equivalent(Stock, repo.Find(Stock.Code));
        Assert.Equal(ContractType.Index, repo.Find("SZ399006")!.Type);
        Assert.Equal(repo.Loaded(Market.SZ).Count, result.Count);
        await repo.EnsureMarketAsync(Market.SZ);
        Assert.Equal(repo.Loaded(Market.SZ).Count,
            repo.Loaded(Market.SZ).Select(c => c.Code).Distinct().Count());
    }

    [Fact]
    public async Task Failed_first_load_keeps_indices_searchable_and_can_retry()
    {
        var client = new ListClient(fail: true);
        var repo = Repo(client);
        Assert.NotNull((await repo.EnsureMarketAsync(Market.SZ)).Error);
        Assert.Equal("SZ399006", Assert.Single(repo.Search("创业板指数")).Code);
        client.Fail = false;
        Assert.Null((await repo.EnsureMarketAsync(Market.SZ)).Error);
        Assert.Equivalent(Stock, repo.Find(Stock.Code));
    }

    [Fact]
    public async Task Duplicate_index_is_normalized_and_same_bare_code_stock_remains_distinct()
    {
        Cache.Save(Market.SH, _clock.TradingDate(Market.SH), new[]
        {
            new Contract { Code = "SH000001", Name = "旧名称" },
        });
        Cache.Save(Market.SZ, _clock.TradingDate(Market.SZ), new[] { Stock });
        var repo = Repo();
        await repo.EnsureMarketAsync(Market.SH);
        await repo.EnsureMarketAsync(Market.SZ);
        Assert.Equal(new[] { "SH000001", "SZ000001" }, repo.Search("000001").Select(c => c.Code));
        Assert.Equal("上证指数", Assert.Single(repo.Search("000001", type: ContractType.Index)).Name);
        Assert.Equivalent(Stock, Assert.Single(repo.Search("000001", market: Market.SZ)));
        Assert.Equal("1.000001", repo.Find("SH000001")!.EastMoneySecId);
        Assert.Equal("0.399006", repo.Find("SZ399006")!.EastMoneySecId);
    }

    [Fact]
    public void Listing_board_exclusions_do_not_hide_benchmarks()
    {
        var indices = Repo().Browse(type: ContractType.Index);
        Assert.Equal(IndexCatalog.All.Count, indices.Count);
        Assert.All(indices, c => Assert.True(BoardSearch.Includes(c, ListingBoards.None)));
    }

    [Fact]
    public void Index_quotes_keep_prices_but_omit_upstream_negative_limit_sentinels()
    {
        var fields = Enumerable.Repeat("", 62).ToArray();
        fields[1] = "上证指数";
        fields[3] = "3808.84";
        fields[47] = fields[48] = "-1";
        fields[61] = "ZS";
        var quote = Assert.Single(TencentQuoteClient.Parse(new[] { "SH000001" },
            new[] { "sh000001" }, "v_sh000001=\"" + string.Join("~", fields) + "\";\n"));
        Assert.Equal(3808.84, quote.Now);
        Assert.Null(quote.LimitUp);
        Assert.Null(quote.LimitDown);
        Assert.DoesNotContain(quote.Extras, f => f.Label is "涨停价" or "跌停价");
    }

    private static readonly Contract Stock = new()
        { Code = "SZ000001", Name = "平安银行", Type = ContractType.MainBoard };

    private sealed class ListClient(bool fail) : IContractListClient
    {
        public bool Fail { get; set; } = fail;
        public Task<IReadOnlyList<Contract>> FetchAsync(Market market, CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("offline")
                : Task.FromResult<IReadOnlyList<Contract>>(new[] { Stock });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
