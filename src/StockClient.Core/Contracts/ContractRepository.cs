namespace StockClient.Core.Contracts;

public sealed record MarketLoadResult
{
    public required Market Market { get; init; }
    public required DateOnly TradingDate { get; init; }
    public required int Count { get; init; }
    public required bool FromCache { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
    public bool Stale { get; init; }
}

/// <summary>
/// Owns the contract lists: loads each market once per its own trading day,
/// serves them from cache afterwards, and searches them locally.
/// </summary>
public sealed class ContractRepository
{
    private readonly IContractListClient _client;
    private readonly ContractCache _cache;
    private readonly IMarketClock _clock;

    private readonly Dictionary<Market, IReadOnlyList<Contract>> _loaded = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ContractRepository(
        IContractListClient client, ContractCache cache, IMarketClock clock)
    {
        _client = client;
        _cache = cache;
        _clock = clock;
    }

    public IReadOnlyList<Contract> Loaded(Market market) =>
        _loaded.TryGetValue(market, out var list) ? list : Array.Empty<Contract>();

    public int TotalCount => _loaded.Values.Sum(list => list.Count);

    /// <summary>
    /// Ensures every market is loaded for its current trading date. Markets are
    /// done one at a time; a failure in one must not block the others.
    /// </summary>
    public async Task<IReadOnlyList<MarketLoadResult>> EnsureLoadedAsync(
        IProgress<MarketLoadResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MarketLoadResult>();

        foreach (var market in MarketInfo.AllMarkets)
        {
            var result = await EnsureMarketAsync(market, cancellationToken);
            results.Add(result);
            progress?.Report(result);
        }

        return results;
    }

    public async Task<MarketLoadResult> EnsureMarketAsync(
        Market market, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var date = _clock.TradingDate(market);
            var started = System.Diagnostics.Stopwatch.StartNew();

            // Already in memory for this market's current trading date?
            if (_loaded.TryGetValue(market, out var inMemory) &&
                _cache.TryLoad(market, date) is not null)
            {
                return Ok(market, date, inMemory.Count, fromCache: true, started.ElapsedMilliseconds);
            }

            var cached = _cache.TryLoad(market, date);
            if (cached is not null)
            {
                _loaded[market] = cached.Symbols;
                return Ok(market, date, cached.Symbols.Count, true, started.ElapsedMilliseconds);
            }

            try
            {
                var fetched = await _client.FetchAsync(market, cancellationToken);
                if (fetched.Count == 0) throw new InvalidOperationException("接口返回 0 条");

                _cache.Save(market, date, fetched);
                _cache.Prune(market);
                _loaded[market] = fetched;

                return Ok(market, date, fetched.Count, false, started.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Falling back to the most recent earlier day beats showing
                // nothing: a day-old list is still overwhelmingly correct.
                var fallback = MostRecent(market, date);
                if (fallback is not null)
                {
                    _loaded[market] = fallback.Symbols;
                    return new MarketLoadResult
                    {
                        Market = market,
                        TradingDate = DateOnly.ParseExact(fallback.TradingDate, "yyyy-MM-dd"),
                        Count = fallback.Symbols.Count,
                        FromCache = true,
                        Stale = true,
                        ElapsedMs = started.ElapsedMilliseconds,
                        Error = ex.Message,
                    };
                }

                return new MarketLoadResult
                {
                    Market = market,
                    TradingDate = date,
                    Count = 0,
                    FromCache = false,
                    ElapsedMs = started.ElapsedMilliseconds,
                    Error = ex.Message,
                };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MarketLoadResult Ok(
        Market market, DateOnly date, int count, bool fromCache, long ms) =>
        new()
        {
            Market = market,
            TradingDate = date,
            Count = count,
            FromCache = fromCache,
            ElapsedMs = ms,
        };

    /// <summary>Newest retained cache older than <paramref name="before"/>.</summary>
    private SymbolFile? MostRecent(Market market, DateOnly before)
    {
        for (var back = 1; back <= ContractCache.RetainDays; back++)
        {
            var file = _cache.TryLoad(market, before.AddDays(-back));
            if (file is not null) return file;
        }

        return null;
    }

    /// <summary>Every contract in scope, unfiltered. Used when no keyword is typed.</summary>
    /// <summary>
    /// Exact lookup by normalized code, e.g. SH600519. Used to resolve a live
    /// quote row (which carries only a code) to its full contract, so opening a
    /// K-line has the market number for the secid. Narrows to the code's own
    /// market first. Null when the code isn't in the loaded lists — a delisted or
    /// hand-typed code — in which case the caller falls back to a bare contract.
    /// </summary>
    public Contract? Find(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var pool = code.Length >= 2 && Enum.TryParse<Market>(code[..2], out var market)
            ? Loaded(market)
            : _loaded.Values.SelectMany(list => list);

        return pool.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<Contract> Browse(Market? market = null, ContractType? type = null)
    {
        var pool = market is null
            ? _loaded.Values.SelectMany(list => list)
            : Loaded(market.Value);

        if (type is not null) pool = pool.Where(c => c.Type == type.Value);

        return pool as IReadOnlyList<Contract> ?? pool.ToArray();
    }

    /// <summary>
    /// Local search over the loaded lists, ordered by relevance.
    ///
    /// Matches code, Chinese name, or pinyin (full and initials). Pinyin is a
    /// matching aid only — it is never surfaced in the UI.
    ///
    /// Returns every match rather than a fixed page: the grid virtualizes, and
    /// truncating would silently hide instruments the user asked for.
    /// </summary>
    public IReadOnlyList<Contract> Search(
        string keyword, Market? market = null, ContractType? type = null)
    {
        var query = (keyword ?? "").Trim();
        if (query.Length == 0) return Browse(market, type);

        var lower = query.ToLowerInvariant();

        return Browse(market, type)
            .Select(c => (Contract: c, Rank: Rank(c, query, lower)))
            .Where(x => x.Rank < int.MaxValue)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Contract.Code, StringComparer.Ordinal)
            .Select(x => x.Contract)
            .ToArray();
    }

    /// <summary>Lower ranks sort first; int.MaxValue means no match.</summary>
    private static int Rank(Contract c, string query, string lower)
    {
        if (c.Code.Equals(lower, StringComparison.OrdinalIgnoreCase)) return 0;
        if (c.Name.Equals(query, StringComparison.Ordinal)) return 1;

        // Bare code: SH600519 should be reachable by typing 600519.
        var bare = c.Code.Length > 2 ? c.Code[2..] : c.Code;
        if (bare.Equals(lower, StringComparison.OrdinalIgnoreCase)) return 2;
        if (bare.StartsWith(lower, StringComparison.OrdinalIgnoreCase)) return 3;

        if (c.Name.StartsWith(query, StringComparison.Ordinal)) return 4;
        if (c.PinyinInitials.Length > 0 &&
            c.PinyinInitials.StartsWith(lower, StringComparison.Ordinal)) return 5;
        if (c.Name.Contains(query, StringComparison.Ordinal)) return 6;
        // Concepts are searchable too: "半导体" should surface concept members.
        if (c.Concepts is not null &&
            c.Concepts.Contains(query, StringComparison.Ordinal)) return 6;
        if (c.Pinyin.Length > 0 &&
            c.Pinyin.StartsWith(lower, StringComparison.Ordinal)) return 7;
        if (c.Code.Contains(lower, StringComparison.OrdinalIgnoreCase)) return 8;

        return int.MaxValue;
    }
}
