using System.Globalization;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.Core.MarketWatch;

/// <summary>Independent, paced batches. Each failed symbol keeps its own last good value.</summary>
public sealed class LiveMarketClient
{
    private readonly Func<IProgress<(int Page, int Total)>?, CancellationToken, Task<MarketUniverse>> _universe;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Quote>>> _quotes;
    private readonly Func<CancellationToken, Task> _pace;
    private Dictionary<string, MarketQuote> _previous = new(StringComparer.OrdinalIgnoreCase);

    public LiveMarketClient(HttpClient http, string? cacheRoot = null)
        : this(new MarketUniverseClient(http, cacheRoot).GetAsync, new TencentQuoteClient(http).GetQuotesAsync) { }

    public LiveMarketClient(Func<IProgress<(int Page, int Total)>?, CancellationToken, Task<MarketUniverse>> universe,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Quote>>> quotes,
        Func<CancellationToken, Task>? pace = null)
    { _universe = universe; _quotes = quotes; _pace = pace ?? (ct => Task.Delay(250, ct)); }

    public async Task<MarketSnapshot> FetchAsync(IProgress<(int Page, int Total)>? progress,
        CancellationToken ct, IProgress<MarketSnapshot>? partial = null)
    {
        var started = DateTimeOffset.UtcNow;
        var directory = await _universe(progress, ct).ConfigureAwait(false);
        var rows = directory.Contracts.Select(c => _previous.TryGetValue(c.Code, out var row)
            ? row with { Contract = c, RefreshFailed = true }
            : new MarketQuote { Contract = c, RefreshFailed = true }).ToArray();
        int pages = (rows.Length + 799) / 800, updated = 0;
        var errors = new List<string>();
        for (var page = 0; page < pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (page > 0) await _pace(ct).ConfigureAwait(false);
            var offset = page * 800;
            var chunk = rows.Skip(offset).Take(800).Select(r => r.Code).ToArray();
            try
            {
                var quotes = await _quotes(chunk, ct).ConfigureAwait(false);
                var byCode = quotes.Where(q => !q.IsMissing).GroupBy(q => q.Code, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var received = DateTimeOffset.UtcNow;
                var missing = 0;
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (!byCode.TryGetValue(chunk[i], out var quote)) { missing++; continue; }
                    rows[offset + i] = Convert(rows[offset + i].Contract, quote, received);
                    updated++;
                }
                if (missing > 0) errors.Add($"第 {page + 1} 批缺少 {missing} 只报价");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { errors.Add($"第 {page + 1} 批：{MarketDataErrors.Describe(ex)}"); }
            ct.ThrowIfCancellationRequested();
            // Preserve successes even if the user closes the view before the remaining batches finish.
            _previous = rows.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
            partial?.Report(Snapshot(page + 1));
        }
        return Snapshot(pages);

        MarketSnapshot Snapshot(int completed) => new(rows.ToArray(), started, DateTimeOffset.UtcNow, "腾讯", completed)
        { UpdatedCount = updated, DirectoryUnconfirmed = directory.Warning.Length > 0, DirectoryInfo = directory.Display, ErrorDetail = string.Join("\n", errors.Prepend(directory.Warning).Where(s => s.Length > 0)) };
    }

    public static MarketQuote Convert(Contract contract, Quote q, DateTimeOffset received)
    {
        DateTimeOffset? time = DateTime.TryParseExact(q.Time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? new DateTimeOffset(date, TimeSpan.FromHours(8)) : null;
        return new MarketQuote
        {
            Contract = contract with { Name = q.Name }, Price = q.Now > 0 ? q.Now : null,
            Previous = q.Yesterday > 0 ? q.Yesterday : null, Open = q.Open > 0 ? q.Open : null,
            High = q.High > 0 ? q.High : null, Low = q.Low > 0 ? q.Low : null,
            Change = q.Now > 0 ? q.Change : null, Percent = q.Now > 0 ? q.Percent : null,
            Volume = q.Volume / 1e4, Amount = q.Amount / 1e8, Turnover = q.TurnoverRate,
            VolumeRatio = q.VolumeRatio, Amplitude = q.Amplitude, TotalCap = q.TotalCap / 1e8,
            FloatCap = q.FloatCap / 1e8, PeTtm = q.PeTtm, Pb = q.Pb, QuoteTime = time, ReceivedAt = received,
        };
    }
}

public sealed record MarketExtensions(IReadOnlyList<MarketQuote> Rows, IReadOnlySet<string> UpdatedCodes,
    string ErrorDetail, string Host);

public static class MarketExtensionMerge
{
    public static MarketQuote Apply(MarketQuote quote, MarketQuote? extra, bool failed)
    {
        if (extra is null) return quote with { ExtensionFailed = failed };
        return quote with
        {
            Contract = quote.Contract with { Industry = extra.Contract.Industry, Region = extra.Contract.Region,
                Concepts = extra.Contract.Concepts, ListedOn = extra.Contract.ListedOn },
            Pe = extra.Pe, Speed = extra.Speed, MainInflow = extra.MainInflow, MainInflowPercent = extra.MainInflowPercent,
            SuperInflow = extra.SuperInflow, BigInflow = extra.BigInflow, MidInflow = extra.MidInflow, SmallInflow = extra.SmallInflow,
            ExtensionTime = extra.QuoteTime, ExtensionReceivedAt = extra.ReceivedAt,
            ExtensionSource = extra.ExtensionSource, ExtensionFailed = failed,
        };
    }
}
