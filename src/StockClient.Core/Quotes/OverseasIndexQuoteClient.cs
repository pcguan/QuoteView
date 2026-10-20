using System.Text.Json;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>Fixed delayed index feed through the domestic server, shared for 60s
/// per client. Failure retains and labels the last good values, never inventing
/// exchange timestamps from a request time or TradingView's daily bar time.</summary>
public sealed class OverseasIndexQuoteClient
{
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Quote> _last = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _next;
    private readonly string _url;
    public OverseasIndexQuoteClient(HttpClient http, Func<DateTimeOffset>? now = null, string? url = null)
    {
        _http = http; _now = now ?? (() => DateTimeOffset.UtcNow);
        _url = url ?? AccountClient.DefaultBase + "/overseas-indices";
    }

    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(IReadOnlyList<string> codes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_now() >= _next)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var json = await _http.GetStringAsync(_url, timeout.Token);
                    var fresh = Parse(json).ToDictionary(q => q.Code, StringComparer.OrdinalIgnoreCase);
                    foreach (var old in _last.Keys.ToArray())
                        _last[old] = _last[old] with { RefreshFailed = true };
                    foreach (var q in fresh.Values) _last[q.Code] = q;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    foreach (var old in _last.Keys.ToArray())
                        _last[old] = _last[old] with { RefreshFailed = true };
                }
                ct.ThrowIfCancellationRequested();
                _next = _now().AddSeconds(60);
            }
            return codes.Select(c => _last.GetValueOrDefault(c) ?? Quote.Missing(c)).ToArray();
        }
        finally { _gate.Release(); }
    }

    public static IReadOnlyList<Quote> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new FormatException("海外指数报价格式无效");
        var result = new Dictionary<string, Quote>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("s", out var symbol)) continue;
            var code = symbol.GetString() switch
            {
                "NASDAQ:SOX" => "US.SOX", "SP:S5INFT" => "US.S5INFT", "ICEUS:NYFANG" => "US.NYFANG", _ => null,
            };
            if (code is null || !row.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Array || d.GetArrayLength() < 7) continue;
            // Fixed scanner columns: close, change, change_abs, open, high, low, update_mode.
            var price = Num(d[0]); var pct = Num(d[1]); var change = Num(d[2]);
            var mode = d[6].ValueKind == JsonValueKind.String ? d[6].GetString() ?? "" : "";
            if (price is not > 0 || pct is null || change is null || !mode.StartsWith("delayed_streaming_", StringComparison.Ordinal)
                || !int.TryParse(mode[18..], out var seconds) || seconds <= 0) continue;
            var contract = IndexCatalog.All.Single(c => c.Code == code);
            result[code] = new Quote
            {
                Code = code, Name = contract.Name, Now = price.Value,
                Yesterday = price.Value - change.Value, Change = change.Value, Percent = pct.Value,
                Open = Num(d[3]) ?? 0, High = Num(d[4]) ?? 0, Low = Num(d[5]) ?? 0,
                Source = "TradingView", DelayMinutes = (int)Math.Ceiling(seconds / 60d),
                RefreshFailed = row.TryGetProperty("stale", out var stale) && stale.ValueKind == JsonValueKind.True,
                Extras = new[] { new QuoteField("数据源", "TradingView"),
                    new QuoteField("行情延迟", $"{(int)Math.Ceiling(seconds / 60d)}分钟"),
                    new QuoteField("行情时间", "上游未提供") },
            };
        }
        return result.Values.ToArray();
    }

    private static double? Num(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
}
