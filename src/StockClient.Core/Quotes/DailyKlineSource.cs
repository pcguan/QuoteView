using System.Text.Json;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// The daily candles behind ALL the period returns (昨日/3日/…/年初, see
/// <see cref="PeriodReturns"/>).
///
/// Deliberately NOT <see cref="KlineRepository"/>, even though both fetch day
/// candles. Three differences are the point:
/// <list type="bullet">
/// <item>bounded lmt (<see cref="CandleCount"/>) instead of the full listed history;</item>
/// <item>NO shared-cache write — storing a truncated series there would become
/// what the chart draws for the rest of the day;</item>
/// <item>Tencent FIRST for SH/SZ/HK, so 昨收 comes from the same vendor as the
/// quote poll and matches to the tick.</item>
/// </list>
///
/// Upstream access is injected as delegates so the account session (a UI-layer
/// type) stays out of Core and the routing stays testable.
/// </summary>
public sealed class DailyKlineSource
{
    /// <summary>270 candles cover 60日 and a full year of sessions for 年初至今.</summary>
    public const int CandleCount = 270;

    private readonly EastMoneyKlineClient _east;
    private readonly TencentKlineClient _tencent;
    private readonly Func<bool> _isSignedIn;
    private readonly Func<string, int, int, int, CancellationToken, Task<string?>> _serverKline;
    private readonly Func<string, CancellationToken, Task<string?>> _krDaily;
    private readonly Func<DateTimeOffset> _now;

    // Circuit breaker for the EastMoney kline host: it throttles with connection
    // resets, and each dead call burns its full timeout — with a whole group
    // crawled sequentially that added up to minutes of blank 昨日涨幅. One
    // failure skips the host for a while.
    private DateTimeOffset _eastDownUntil = DateTimeOffset.MinValue;

    /// <param name="serverKlineJson">
    /// secid/klt/fqt/lmt → the snapshot server's /kline proxy, returning the
    /// EastMoney response body verbatim (null = unavailable).
    /// </param>
    /// <param name="krDailyJson">
    /// code → the server's own archive of Korean session closes (null = unavailable).
    /// </param>
    public DailyKlineSource(
        EastMoneyKlineClient east,
        TencentKlineClient tencent,
        Func<bool> isSignedIn,
        Func<string, int, int, int, CancellationToken, Task<string?>> serverKlineJson,
        Func<string, CancellationToken, Task<string?>> krDailyJson,
        Func<DateTimeOffset>? now = null)
    {
        _east = east;
        _tencent = tencent;
        _isSignedIn = isSignedIn;
        _serverKline = serverKlineJson;
        _krDaily = krDailyJson;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public async Task<KlineSeries?> FetchAsync(Contract contract, CancellationToken cancellationToken)
    {
        if (contract.Market == Market.KR) return await KoreaAsync(contract, cancellationToken);

        // SH/SZ/HK/US: Tencent FIRST — full history, same vendor as the quote
        // poll (so 昨收 matches to the tick), and immune to the EastMoney kline
        // host's routine outages; EastMoney only as its backup. US joined this
        // list once the exchange-suffix form was found (see
        // TencentKlineClient.ToKlineApiCode) — a whole evening of blank US
        // returns during a push2his outage is what surfaced it.
        if (contract.Market is Market.SH or Market.SZ or Market.HK or Market.US)
        {
            try
            {
                var series = await _tencent.FetchAsync(
                    contract, KlinePeriod.Day, KlineAdjust.Qfq, CandleCount, cancellationToken);
                if (Usable(series)) return Trim(series);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to EastMoney.
            }
            if (cancellationToken.IsCancellationRequested) return null;
            return await EastAsync(contract, cancellationToken);
        }

        // BJ: EastMoney only — Tencent has no Beijing klines in any symbol form.
        return await EastAsync(contract, cancellationToken);
    }

    /// <summary>
    /// A series that can't yield even 昨日涨幅 is no data, not partial data.
    /// Returning it would let the caller stamp the day as fetched and stop
    /// retrying — exactly how a one-row upstream stub once blanked every US
    /// return for a whole session.
    /// </summary>
    private static bool Usable(KlineSeries? series) => series is { Candles.Count: >= 2 };

    /// <summary>
    /// Korea has no queryable daily history upstream (EastMoney's period fields
    /// are broken there, Tencent has no KR klines in any symbol form) — the SERVER
    /// archives each session's close itself and serves the pairs back.
    /// </summary>
    private async Task<KlineSeries?> KoreaAsync(Contract contract, CancellationToken cancellationToken)
    {
        if (!_isSignedIn()) return null;
        var body = await _krDaily(contract.Code, cancellationToken);
        if (body is null) return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candles", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;

        var candles = new List<Kline>();
        foreach (var c in arr.EnumerateArray())
        {
            var close = c.TryGetProperty("close", out var cl) ? cl.GetDouble() : 0;
            if (close <= 0) continue;
            var date = c.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
            candles.Add(new Kline
            {
                Date = date, Open = close, Close = close,
                High = close, Low = close,
            });
        }
        return candles.Count == 0 ? null : new KlineSeries
        {
            Code = contract.Code, Name = contract.Name,
            Period = KlinePeriod.Day, Adjust = KlineAdjust.None,
            Candles = candles,
        };
    }

    private async Task<KlineSeries?> EastAsync(Contract contract, CancellationToken cancellationToken)
    {
        if (_now() < _eastDownUntil) return null;

        if (_isSignedIn())
        {
            try
            {
                var json = await _serverKline(
                    contract.EastMoneySecId,
                    EastMoneyKlineClient.PeriodCode(KlinePeriod.Day),
                    EastMoneyKlineClient.AdjustCode(KlineAdjust.Qfq),
                    CandleCount, cancellationToken);
                if (json is not null)
                {
                    var fromServer = EastMoneyKlineClient.ParseSeries(
                        json, contract, KlinePeriod.Day, KlineAdjust.Qfq);
                    if (Usable(fromServer)) return fromServer;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to the direct fetch.
            }
        }

        try
        {
            var east = await _east.FetchAsync(
                contract, KlinePeriod.Day, KlineAdjust.Qfq, CandleCount, cancellationToken);
            if (Usable(east)) return east;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Routine throttling (connection resets); trip below.
        }

        // A cancelled fetch says nothing about the host. Counting it tripped the
        // breaker on every group switch made mid-crawl, and BJ (no Tencent
        // history) were then left with no daily source at all for five minutes.
        if (cancellationToken.IsCancellationRequested) return null;

        _eastDownUntil = _now() + TimeSpan.FromMinutes(5);
        return null;
    }

    private static KlineSeries Trim(KlineSeries series) =>
        series.Candles.Count <= CandleCount
            ? series
            : series with { Candles = series.Candles.TakeLast(CandleCount).ToArray() };
}
