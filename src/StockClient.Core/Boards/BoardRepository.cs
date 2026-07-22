using StockClient.Core.Contracts;

namespace StockClient.Core.Boards;

public sealed record BoardLoadResult
{
    public required DateOnly TradingDate { get; init; }
    public required int Count { get; init; }
    public required bool FromCache { get; init; }
    public bool Stale { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Owns the board catalog: loads it once per A-share trading day, serves it from
/// cache afterwards. Mirrors <see cref="ContractRepository"/> but for the single
/// A-share-wide board list rather than per-market contract lists.
/// </summary>
public sealed class BoardRepository
{
    private readonly IBoardListClient _client;
    private readonly BoardCache _cache;
    private readonly IMarketClock _clock;

    private IReadOnlyList<Board> _loaded = Array.Empty<Board>();

    public BoardRepository(IBoardListClient client, BoardCache cache, IMarketClock clock)
    {
        _client = client;
        _cache = cache;
        _clock = clock;
    }

    /// <summary>The loaded catalog. Empty until <see cref="EnsureLoadedAsync"/> runs.</summary>
    public IReadOnlyList<Board> Loaded => _loaded;

    public IReadOnlyList<Board> OfKind(BoardKind kind) =>
        _loaded.Where(b => b.Kind == kind).ToArray();

    /// <summary>
    /// Ensures the catalog is loaded for the current A-share trading date. Boards
    /// exist only for A-shares, so the SH clock decides the date — the same daily
    /// rollover as the contract lists.
    /// </summary>
    public async Task<BoardLoadResult> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        var date = _clock.TradingDate(Market.SH);
        var started = System.Diagnostics.Stopwatch.StartNew();

        if (_loaded.Count > 0 && _cache.TryLoad(date) is not null)
            return Ok(date, _loaded.Count, fromCache: true, started.ElapsedMilliseconds);

        var cached = _cache.TryLoad(date);
        if (cached is not null)
        {
            _loaded = cached.Boards;
            return Ok(date, cached.Boards.Count, true, started.ElapsedMilliseconds);
        }

        try
        {
            var fetched = await _client.FetchAsync(cancellationToken);
            if (fetched.Count == 0) throw new InvalidOperationException("接口返回 0 个板块");

            _cache.Save(date, fetched);
            _cache.Prune();
            _loaded = fetched;

            return Ok(date, fetched.Count, false, started.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallback = MostRecent(date);
            if (fallback is not null)
            {
                _loaded = fallback.Boards;
                return new BoardLoadResult
                {
                    TradingDate = DateOnly.ParseExact(fallback.TradingDate, "yyyy-MM-dd"),
                    Count = fallback.Boards.Count,
                    FromCache = true,
                    Stale = true,
                    ElapsedMs = started.ElapsedMilliseconds,
                    Error = ex.Message,
                };
            }

            return new BoardLoadResult
            {
                TradingDate = date,
                Count = 0,
                FromCache = false,
                ElapsedMs = started.ElapsedMilliseconds,
                Error = ex.Message,
            };
        }
    }

    private static BoardLoadResult Ok(DateOnly date, int count, bool fromCache, long ms) =>
        new() { TradingDate = date, Count = count, FromCache = fromCache, ElapsedMs = ms };

    /// <summary>Newest retained catalog older than <paramref name="before"/>.</summary>
    private BoardFile? MostRecent(DateOnly before)
    {
        for (var back = 1; back <= BoardCache.RetainDays; back++)
        {
            var file = _cache.TryLoad(before.AddDays(-back));
            if (file is not null) return file;
        }

        return null;
    }
}
