using System.Globalization;
using System.Text.Json;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using TinyPinyin;

namespace StockClient.Core.MarketWatch;

public sealed record MarketUniverse(IReadOnlyList<Contract> Contracts, DateOnly Date, string Source, string Warning = "")
{
    public int Schema { get; init; }
    public string Display => $"目录 {Date:yyyy-MM-dd} · {Source}" + (Warning.Length == 0 ? "" : " · " + Warning);
}

/// <summary>Daily identities only. Quotes are never persisted in this cache.</summary>
public sealed class MarketUniverseClient
{
    private const string Endpoint = "https://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.";
    private readonly HttpClient _http;
    private readonly string _path;
    private readonly ContractCache _legacy;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<CancellationToken, Task> _pace;
    private MarketUniverse? _current;
    private DateTimeOffset _retryAt;

    public MarketUniverseClient(HttpClient http, string? root = null, Func<DateTimeOffset>? now = null,
        Func<CancellationToken, Task>? pace = null)
    {
        _http = http;
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StockClient");
        _path = Path.Combine(root, "market-universe.json");
        _legacy = new ContractCache(Path.Combine(root, "contracts"));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _pace = pace ?? (ct => Task.Delay(250, ct));
    }

    public async Task<MarketUniverse> GetAsync(IProgress<(int Page, int Total)>? progress, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_now().ToOffset(TimeSpan.FromHours(8)).DateTime);
        _current ??= Load();
        if (_current is not null && ((_current.Date == today && _current.Warning.Length == 0) || _now() < _retryAt)) return _current;
        var rows = new Dictionary<string, Contract>(StringComparer.OrdinalIgnoreCase);
        var metadata = Legacy(today, out var cachedDate);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            var token = budget.Token;
            var failures = new List<string>();
            // hs_a omits CDR instruments such as SH689009; kcb includes them.
            foreach (var node in new[] { "hs_a", "kcb" })
            {
                try { await ReadNode(node).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { failures.Add(node + "：" + MarketDataErrors.Describe(ex)); }
            }
            if (failures.Count > 0 || rows.Count == 0) throw new InvalidOperationException(string.Join("；", failures));

            async Task ReadNode(string node)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var total = await CountAsync(node, token).ConfigureAwait(false);
                var pages = (total + 79) / 80;
                var errors = new Dictionary<int, string>();
                var consecutiveFailures = 0;
                for (var page = 1; page <= pages; page++)
                {
                    try
                    {
                        if (page > 1) await _pace(token).ConfigureAwait(false);
                        await ReadPage(page).ConfigureAwait(false);
                        consecutiveFailures = 0;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        errors[page] = MarketDataErrors.Describe(ex);
                        if (++consecutiveFailures >= 3 || budget.IsCancellationRequested) break;
                    }
                    progress?.Report((page, pages));
                }
                // Daily directory only: one delayed repair per failed page, at most three requests.
                // Never refetch the whole universe just because a single page was empty.
                foreach (var page in errors.Keys.Take(3).ToArray())
                {
                    if (budget.IsCancellationRequested) break;
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        await ReadPage(page).ConfigureAwait(false);
                        errors.Remove(page);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { errors[page] = MarketDataErrors.Describe(ex); }
                }
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors.Select(e => $"第 {e.Key} 页：{e.Value}")));

                async Task ReadPage(int page)
                {
                    var url = Endpoint + $"getHQNodeData?page={page}&num=80&sort=symbol&asc=1&node={node}&symbol=&_s_r_a=page";
                    using var json = JsonDocument.Parse(await _http.GetStringAsync(url, token).ConfigureAwait(false));
                    if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != Math.Min(80, total - (page - 1) * 80))
                        throw new InvalidOperationException($"目录第 {page}/{pages} 页不完整");
                    var batchCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var batch = new List<Contract>();
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        var code = item.GetProperty("symbol").GetString()?.ToUpperInvariant() ?? "";
                        var name = item.GetProperty("name").GetString() ?? "";
                        if (code.Length != 8 || !code[2..].All(char.IsAsciiDigit) || name.Length == 0 || seen.Contains(code) || !batchCodes.Add(code))
                            throw new InvalidOperationException("目录合约身份无效或重复");
                        if (code.StartsWith("BJ", StringComparison.Ordinal)) continue;
                        var pair = BoardSearch.Classify(code[2..], code.StartsWith("SH", StringComparison.Ordinal) ? 1 : code.StartsWith("SZ", StringComparison.Ordinal) ? 0 : null);
                        if (pair is null || pair.Value.Market is not (Market.SH or Market.SZ))
                            throw new InvalidOperationException("目录包含非沪深 A 股");
                        var contract = metadata.GetValueOrDefault(code) ?? new Contract { Code = code, Name = name };
                        batch.Add(contract with { Name = name, Type = pair.Value.Type, MarketNumber = pair.Value.Market == Market.SH ? 1 : 0,
                            Pinyin = PinyinHelper.GetPinyin(name, "").ToLowerInvariant(),
                            PinyinInitials = string.Concat(name.Select(c => PinyinHelper.GetPinyin(c.ToString(), "")[0])).ToLowerInvariant() });
                    }
                    // Commit a page atomically, so retrying malformed data cannot create duplicates.
                    seen.UnionWith(batchCodes);
                    foreach (var row in batch) rows[row.Code] = row;
                }
                if (seen.Count != total || await CountAsync(node, token).ConfigureAwait(false) != total)
                    throw new InvalidOperationException("目录数量在采集期间变更");
            }
            ct.ThrowIfCancellationRequested();
            _current = new MarketUniverse(rows.Values.OrderBy(c => c.Code, StringComparer.Ordinal).ToArray(), today, "新浪 A股+科创板") { Schema = 1 };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temp, JsonSerializer.Serialize(_current)); File.Move(temp, _path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (IOException) { /* A read-only cache must not discard a valid directory. */ }
            catch (UnauthorizedAccessException) { }
            return _current;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _retryAt = _now().AddMinutes(15);
            // Keep known identities, including pages already obtained on a clean install.
            var fallback = (_current?.Contracts ?? metadata.Values.ToArray()).Concat(rows.Values)
                .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToArray();
            if (fallback.Length == 0) throw new InvalidOperationException("合约目录不可用：" + MarketDataErrors.Describe(ex), ex);
            _current = new MarketUniverse(fallback, _current?.Date ?? cachedDate ?? today, _current?.Source ?? "本地目录/新浪部分页",
                "目录更新失败，覆盖未确认：" + MarketDataErrors.Describe(ex));
            return _current;
        }
    }

    private async Task<int> CountAsync(string node, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await _http.GetStringAsync(Endpoint + "getHQNodeStockCount?node=" + node, ct).ConfigureAwait(false));
        if (!int.TryParse(json.RootElement.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total is < 1 or > 30000)
            throw new InvalidOperationException("目录总数无效");
        return total;
    }

    private MarketUniverse? Load()
    {
        try
        {
            var data = JsonSerializer.Deserialize<MarketUniverse>(File.ReadAllText(_path));
            return data is { Schema: 1, Contracts.Count: > 0, Warning.Length: 0 } && data.Contracts.All(IsStock)
                && data.Contracts.Select(c => c.Code).Distinct().Count() == data.Contracts.Count ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private Dictionary<string, Contract> Legacy(DateOnly today, out DateOnly? cachedDate)
    {
        cachedDate = null;
        var result = new Dictionary<string, Contract>(StringComparer.OrdinalIgnoreCase);
        foreach (var market in new[] { Market.SH, Market.SZ })
            for (var back = 0; back <= ContractCache.RetainDays; back++)
                if (_legacy.TryLoad(market, today.AddDays(-back)) is { } file)
                {
                    var date = today.AddDays(-back);
                    if (cachedDate is null || date < cachedDate) cachedDate = date;
                    foreach (var row in file.Symbols.Where(IsStock)) result[row.Code] = row;
                    break;
                }
        return result;
    }
    private static bool IsStock(Contract c) => c is not null && c.Code is not null && c.Code.Length == 8 && c.Code[2..].All(char.IsAsciiDigit)
        && (c.Code.StartsWith("SH6", StringComparison.Ordinal) || c.Code.StartsWith("SZ0", StringComparison.Ordinal) || c.Code.StartsWith("SZ3", StringComparison.Ordinal))
        && c.Type is ContractType.MainBoard or ContractType.ChiNext or ContractType.Star;
}

public static class MarketDataErrors
{
    public static string Describe(Exception ex)
    {
        var root = ex.GetBaseException();
        return root is OperationCanceledException ? "请求超时" : $"{root.GetType().Name}: {root.Message}";
    }
}
