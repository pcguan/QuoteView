using System.Net.Http;
using System.Windows.Threading;
using StockClient.Core;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.ViewModels;

/// <summary>
/// Backs one K-line window: fetches candles for the chosen period/adjust and
/// exposes them plus the moving averages the chart overlays.
/// </summary>
public sealed class KlineViewModel : ObservableObject
{
    /// <summary>MA windows drawn over the candles. Order fixes their colours.</summary>
    public static readonly int[] MaWindows = { 5, 10, 20, 60 };

    // 0 = the full listed history, fetched in one request (EastMoney returns it
    // all, no paging, ~0.5s). The chart shows a recent window and zooms/pans back
    // over the rest, so this never caps how far back you can look, and every
    // visible window has hundreds of candles of run-up for MA60 to be continuous.
    private const int CandleCount = 0;

    /// <summary>Intraday re-poll cadence. The trend is minute-grained, so seconds is plenty.</summary>
    private static readonly TimeSpan TrendInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Candle re-poll cadence. Candles don't move — the running one isn't drawn —
    /// so this exists for one transition: a window left open across the close
    /// picks up the day's finished candle on its own. Cheap, because every poll
    /// before that is answered from cache without a request.
    /// </summary>
    private static readonly TimeSpan KlineInterval = TimeSpan.FromSeconds(30);

    /// <summary>Cap on the live 成交明细 pull — large enough for the whole running
    /// day (details is a few thousand rows even for the busiest names), so the
    /// tape shows open-to-now, not just a tail. The view virtualizes the rows.</summary>
    private const int TapeMaxRows = 100_000;

    private readonly Contract _contract;
    private readonly KlineRepository _repo;
    private readonly TrendRepository _trends;
    private readonly TencentQuoteClient? _quotes;
    private readonly EastMoneyDetailsClient? _details;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _trendTimer;
    private readonly DispatcherTimer _klineTimer;

    private CancellationTokenSource? _cts;

    private KlinePeriod _period = KlinePeriod.Day;
    private KlineAdjust _adjust = KlineAdjust.Qfq;
    private IReadOnlyList<Kline> _candles = Array.Empty<Kline>();
    private IReadOnlyDictionary<int, IReadOnlyList<double?>> _movingAverages =
        new Dictionary<int, IReadOnlyList<double?>>();
    private bool _isTrend;
    private TrendSeries? _trend;
    private Quote? _live;
    private string _source = "东财";
    private bool _loading;
    private string _error = "";

    public KlineViewModel(
        Contract contract, KlineRepository repo,
        TrendRepository trends, Dispatcher dispatcher, TencentQuoteClient? quotes = null,
        EastMoneyDetailsClient? details = null)
    {
        _contract = contract;
        _repo = repo;
        _trends = trends;
        _quotes = quotes;
        _details = details;
        _dispatcher = dispatcher;

        _trendTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TrendInterval,
        };
        _trendTimer.Tick += (_, _) => _ = LoadTrendAsync();

        _klineTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = KlineInterval,
        };
        _klineTimer.Tick += (_, _) => _ = RefreshAsync();
        _klineTimer.Start();
    }

    public string Title => $"{_contract.Name}  {_contract.Code}";

    /// <summary>Current period; changed via <see cref="ShowKline"/> so the trend mode exits cleanly.</summary>
    public KlinePeriod Period => _period;

    /// <summary>True while the intraday trend is showing instead of candles.</summary>
    public bool IsTrend
    {
        get => _isTrend;
        private set => Set(ref _isTrend, value);
    }

    public TrendSeries? Trend
    {
        get => _trend;
        private set => Set(ref _trend, value);
    }

    /// <summary>
    /// The 1s quote for this contract, polled only while the intraday view is up —
    /// it exists for the order book beside the line, which comes free inside the
    /// same response. Null until the first tick, or if no client was supplied.
    /// </summary>
    public Quote? Live
    {
        get => _live;
        private set => Set(ref _live, value);
    }

    /// <summary>Raised after each live-quote tick so the book can redraw.</summary>
    public event Action? LiveUpdated;

    /// <summary>
    /// Newest-last 成交明细 tail for the running session (last <see cref="TapeRows"/>
    /// rows). Empty until the first poll, or for markets EastMoney details doesn't
    /// serve (only 沪深). Fed while the intraday line is showing.
    /// </summary>
    public IReadOnlyList<TradeTick> Ticks { get; private set; } = Array.Empty<TradeTick>();

    /// <summary>Whether a 成交明细 tape is available here — details is 沪深 only.</summary>
    public bool HasTape =>
        _details is not null && CodeMapper.MarketOf(_contract.Code) is "SH" or "SZ";

    /// <summary>Raised after each 成交明细 poll so the tape can redraw.</summary>
    public event Action? TicksUpdated;

    /// <summary>Which source served the current candles: "东财" or "腾讯(备用)".</summary>
    public string Source
    {
        get => _source;
        private set => Set(ref _source, value);
    }

    public KlineAdjust Adjust
    {
        get => _adjust;
        set
        {
            if (Set(ref _adjust, value)) _ = ReloadAsync();
        }
    }

    public IReadOnlyList<Kline> Candles
    {
        get => _candles;
        private set => Set(ref _candles, value);
    }

    /// <summary>MA window -> one value per candle, null until the window fills.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<double?>> MovingAverages
    {
        get => _movingAverages;
        private set => Set(ref _movingAverages, value);
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Raised after a successful candle load so the chart can redraw.</summary>
    public event Action? Loaded;

    /// <summary>Raised after each intraday poll so the trend chart can redraw.</summary>
    public event Action? TrendLoaded;

    /// <summary>
    /// Raised after a silent candle re-poll. Separate from <see cref="Loaded"/>
    /// because the chart must keep its zoom/pan here — only a real load resets
    /// the view.
    /// </summary>
    public event Action? Refreshed;

    /// <summary>Switch to a candle period, leaving intraday mode if it was active.</summary>
    public void ShowKline(KlinePeriod period)
    {
        var wasTrend = IsTrend;
        LeaveTrend();

        // Reload when the period actually changed, or when coming back from the
        // trend (where the period value may be unchanged but candles aren't shown).
        var changed = Set(ref _period, period);
        if (changed) OnPropertyChanged(nameof(Period));
        if (changed || wasTrend) _ = ReloadAsync();
    }

    /// <summary>Switch to today's intraday trend and start re-polling it.</summary>
    public void ShowTrend()
    {
        if (IsTrend) return;

        IsTrend = true;
        _trendTimer.Start();
        _ = LoadTrendAsync();
    }

    private void LeaveTrend()
    {
        if (!IsTrend) return;
        IsTrend = false;
        _trendTimer.Stop();
    }

    /// <summary>
    /// Pulls today's trend. The first pull shows the loading state; subsequent
    /// polls refresh silently so the chart doesn't flicker every few seconds.
    /// </summary>
    private async Task LoadTrendAsync()
    {
        if (!IsTrend) return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var first = Trend is null;
        if (first)
        {
            Loading = true;
            Error = "";
        }

        _ = PollLiveAsync();
        _ = PollDetailsAsync();

        try
        {
            // Through the repository, so this shares the panel's two sources
            // (EastMoney, then Tencent when it is throttled) and its 15s cache.
            // Null means neither source answered AND nothing was cached.
            var series = await _trends.GetAsync(_contract, cts.Token);
            if (cts.IsCancellationRequested || !IsTrend) return;

            if (series is not null) Trend = series;
            Error = Trend is null || Trend.Points.Count == 0 ? "没有分时数据" : "";
            TrendLoaded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a switch or a newer poll.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Error = "加载失败：" + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested && first) Loading = false;
        }
    }

    /// <summary>
    /// One quote for this contract, for the order book. Deliberately not a shared
    /// poller: a chart window can be open on a contract that is in no group, so it
    /// fetches its own — one request per intraday window per trend poll.
    /// </summary>
    private async Task PollLiveAsync()
    {
        if (_quotes is null || !IsTrend) return;

        try
        {
            var quotes = await _quotes.GetQuotesAsync(new[] { _contract.Code }, CancellationToken.None);
            if (!IsTrend || quotes.Count == 0 || quotes[0].IsMissing) return;

            Live = quotes[0];
            LiveUpdated?.Invoke();
        }
        catch (Exception)
        {
            // The book just keeps its last state; the line is the main event here.
        }
    }

    /// <summary>
    /// One 成交明细 poll for the tape beside the book, on the same trend cadence.
    /// 沪深 only (EastMoney details serves nothing else), best-effort like the
    /// book: a failed poll leaves the last tape in place.
    /// </summary>
    private async Task PollDetailsAsync()
    {
        if (!HasTape || !IsTrend) return;

        try
        {
            var snap = await _details!.FetchAsync(_contract, TapeMaxRows, CancellationToken.None);
            if (!IsTrend || snap is null) return;

            Ticks = snap.Ticks;
            TicksUpdated?.Invoke();
        }
        catch (Exception)
        {
            // Tape keeps its last state; not worth surfacing.
        }
    }

    public async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Loading = true;
        Error = "";

        try
        {
            // Cache-first inside the repository: today's cache, else EastMoney
            // (cached), else Tencent (not cached). At most one fetch per contract
            // per trading day.
            var (series, source) = await _repo.GetAsync(
                _contract, _period, _adjust, CandleCount, cts.Token);

            if (cts.IsCancellationRequested) return;

            Source = source;
            Candles = series.Candles;
            MovingAverages = ComputeMovingAverages(series.Candles);

            Error = series.Candles.Count == 0 ? "没有K线数据" : "";

            Loaded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Error = "加载失败：" + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) Loading = false;
        }
    }

    /// <summary>
    /// Silent re-poll of the candles: no loading state, no error surfaced, and
    /// the chart keeps its zoom/pan. The point is the close: a window opened
    /// during the session ends at the previous close, and this is what appends
    /// today's candle once it is final, without the user reopening anything.
    ///
    /// Skipped while a real load is in flight so the two can't race; the timer
    /// comes back around shortly.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (IsTrend || Loading || Candles.Count == 0) return;

        var period = _period;
        var adjust = _adjust;

        try
        {
            // Its own token: a background refresh must not cancel, or be cancelled
            // by, whatever the user is doing.
            var (series, source) = await _repo.GetAsync(
                _contract, period, adjust, CandleCount, CancellationToken.None);

            // A period/adjust switch (or the trend) may have landed meanwhile —
            // these candles are for the old view, so drop them.
            if (IsTrend || period != _period || adjust != _adjust) return;
            if (series.Candles.Count == 0) return;

            Source = source;
            Candles = series.Candles;
            MovingAverages = ComputeMovingAverages(series.Candles);

            Refreshed?.Invoke();
        }
        catch (Exception)
        {
            // Background refresh: the chart keeps the data it has, and the next
            // tick tries again. Surfacing this would flash an error over a chart
            // that is perfectly readable.
        }
    }

    /// <summary>
    /// Simple moving average of closes for each window. The first (window-1)
    /// entries are null — a 20-day MA has no value until 20 candles exist — so
    /// the line simply starts later rather than drawing a wrong early value.
    /// </summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<double?>> ComputeMovingAverages(
        IReadOnlyList<Kline> candles)
    {
        var result = new Dictionary<int, IReadOnlyList<double?>>();

        foreach (var window in MaWindows)
        {
            var line = new double?[candles.Count];
            var sum = 0.0;

            for (var i = 0; i < candles.Count; i++)
            {
                sum += candles[i].Close;
                if (i >= window) sum -= candles[i - window].Close;
                if (i >= window - 1) line[i] = sum / window;
            }

            result[window] = line;
        }

        return result;
    }

    public void Dispose()
    {
        _trendTimer.Stop();
        _klineTimer.Stop();
        _cts?.Cancel();
    }
}
