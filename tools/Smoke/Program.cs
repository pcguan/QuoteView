using System.Text;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

// Verifies the quote layer against live Tencent data on real Windows:
// batching, per-market field maps, poll cadence, and missing codes.

Console.OutputEncoding = Encoding.UTF8;

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockClient/1.0)");
var client = new TencentQuoteClient(http);

var codes = new[] { "SH600519", "SZ000651", "BJ920992", "HK00700", "USAAPL", "KR005930" };

Console.WriteLine("【1】批量拉取（6 个代码，应为 1 个 HTTP 请求）");
var sw = System.Diagnostics.Stopwatch.StartNew();
var quotes = await client.GetQuotesAsync(codes, default);
Console.WriteLine($"  {quotes.Count} 条，用时 {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"\n  {"代码",-10}{"名称",-22}{"现价",12}{"涨跌幅",10}{"最高",12}{"最低",12}");
foreach (var q in quotes)
    Console.WriteLine($"  {q.Code,-10}{q.Name,-22}{q.Now,12:0.###}{q.Percent,9:+0.00;-0.00;0.00}%{q.High,12:0.###}{q.Low,12:0.###}");

Console.WriteLine("\n【2】交叉校验：现价/昨收 算出的涨跌幅 应与 [32] 字段一致");
foreach (var q in quotes.Where(x => !x.IsMissing && x.Yesterday > 0))
{
    var derived = (q.Now / q.Yesterday - 1) * 100;
    var ok = Math.Abs(derived - q.Percent) < 0.05;
    Console.WriteLine($"  {q.Code,-10} 算出={derived,7:0.00}%  字段[32]={q.Percent,7:0.00}%  {(ok ? "OK" : "不一致 ← 字段位可能错")}");
}

Console.WriteLine("\n【3】每市场返回的字段（有啥显示啥，不共用字段表）");
foreach (var q in quotes.Where(x => !x.IsMissing))
{
    Console.WriteLine($"\n  {q.Code} {q.Name}  时间={q.Time}  额外字段 {q.Extras.Count} 个:");
    foreach (var f in q.Extras) Console.WriteLine($"      {f.Label,-14} = {f.Value}");
}

Console.WriteLine("\n【4】无效代码不抛错，降级为占位");
var bad = await client.GetQuotesAsync(new[] { "SH600519", "SH999999" }, default);
Console.WriteLine($"  SH999999 -> IsMissing={bad[1].IsMissing} (期望 True)，SH600519 仍为 {bad[0].Name}");

Console.WriteLine("\n【5】轮询 5 秒（期望约 5 跳，每跳 1 个批量请求）");
var poller = new QuotePoller(client);
var ticks = 0;
poller.Tick += t => { ticks++; Console.WriteLine($"  tick#{ticks} 延迟={t.LatencyMs}ms {t.Quotes[0].Name}={t.Quotes[0].Now}"); };
poller.Failed += m => Console.WriteLine($"  失败: {m}");
poller.SetTarget("g1", codes);
await Task.Delay(5200);
await poller.DisposeAsync();
Console.WriteLine(ticks is >= 4 and <= 7 ? $"  -> OK {ticks} 跳" : $"  -> 异常: {ticks} 跳");

Console.WriteLine("\n【6】K 线：六市场都要有完整历史（东财 push2his，前复权，日线）");
var klineClient = new EastMoneyKlineClient(http);

// secid prefixes carried explicitly here (MarketNumber), since Smoke doesn't
// load the contract cache. US AAPL is 105 (NASDAQ), verified live.
var klineTargets = new[]
{
    new Contract { Code = "SH600519", Name = "贵州茅台", MarketNumber = 1 },
    new Contract { Code = "SZ000651", Name = "格力电器", MarketNumber = 0 },
    new Contract { Code = "BJ920418", Name = "苏轴股份", MarketNumber = 0 },
    new Contract { Code = "HK00700", Name = "腾讯控股", MarketNumber = 116 },
    new Contract { Code = "USAAPL", Name = "苹果", MarketNumber = 105 },
    new Contract { Code = "KR005930", Name = "三星电子", MarketNumber = 177 },
};

foreach (var target in klineTargets)
{
    var series = await klineClient.FetchAsync(target, KlinePeriod.Day, KlineAdjust.Qfq, 120, default);
    var candles = series.Candles;

    if (candles.Count == 0)
    {
        Console.WriteLine($"  {target.Code,-10} -> 0 根 K 线 ← 失败（secid={target.EastMoneySecId}）");
        continue;
    }

    // Every candle must satisfy high >= max(open,close) and low <= min(open,close).
    // A close/high column swap (the OHLC-order trap) breaks this on the first
    // green candle where high would land below close.
    var oob = candles.Count(k => k.High + 1e-6 < Math.Max(k.Open, k.Close)
                                 || k.Low - 1e-6 > Math.Min(k.Open, k.Close));
    var last = candles[^1];

    // A close/high column swap breaks this on nearly every up candle, so a high
    // out-of-bounds ratio means the columns are wrong. A stray one or two is just
    // dirty upstream data (KR has a couple), which the chart clamps when drawing.
    var verdict = oob == 0 ? "OK"
        : (double)oob / candles.Count < 0.02 ? $"OK（上游脏数据 {oob} 根）"
        : "← OHLC 列序错";

    // lmt must now be honoured: 120 requested, so never far above 120.
    var within = candles.Count <= 130 ? "" : $"  ← lmt 未生效({candles.Count} 根)";

    Console.WriteLine(
        $"  {target.Code,-10}{series.Name,-12} {candles.Count,4} 根  " +
        $"{candles[0].Date}..{last.Date}  收={last.Close,10:0.###}  " +
        $"越界={oob} {verdict}{within}");
}

Console.WriteLine("\n【7】涨跌幅基准：K 线最后两根算出的涨跌幅，应等于腾讯实时的涨跌幅");
Console.WriteLine("     （相对昨收 close/prevClose，不是相对今开 close/open）");
foreach (var target in klineTargets.Take(3))
{
    var day = await klineClient.FetchAsync(target, KlinePeriod.Day, KlineAdjust.Qfq, 3, default);
    if (day.Candles.Count < 2) { Console.WriteLine($"  {target.Code} 不足两根，跳过"); continue; }

    var last = day.Candles[^1];
    var prev = day.Candles[^2];
    var vsPrevClose = (last.Close / prev.Close - 1) * 100;   // correct basis
    var vsOpen = (last.Close / last.Open - 1) * 100;         // the old, wrong basis

    var live = await client.GetQuotesAsync(new[] { target.Code }, default);
    var livePct = live[0].Percent;

    var ok = Math.Abs(vsPrevClose - livePct) < 0.1;
    Console.WriteLine(
        $"  {target.Code,-10} 相对昨收={vsPrevClose,7:+0.00;-0.00}%  " +
        $"相对今开={vsOpen,7:+0.00;-0.00}%  腾讯实时={livePct,7:+0.00;-0.00}%  " +
        $"{(ok ? "OK" : "← 不一致")}");
}

Console.WriteLine("\n  周/月线（沪A 贵州茅台）：");
foreach (var period in new[] { KlinePeriod.Week, KlinePeriod.Month })
{
    var s = await klineClient.FetchAsync(klineTargets[0], period, KlineAdjust.Qfq, 60, default);
    Console.WriteLine($"    {period,-6} {s.Candles.Count,3} 根  最新 {s.Candles[^1].Date} 收={s.Candles[^1].Close:0.##}");
}

Console.WriteLine("\n完成。");
