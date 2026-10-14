using System.Net.Http;
using System.Windows.Threading;
using StockClient.Core.Boards;
using StockClient.Core.MarketWatch;

namespace StockClient.App.ViewModels;

public sealed record BoardKindOption(BoardKind Kind, string Name)
{ public override string ToString() => Name; }

public sealed class BoardWatchViewModel : ObservableObject, IDisposable
{
    private readonly HttpClient _http = Services.DirectHttp.Create(TimeSpan.FromSeconds(15));
    private readonly EastMoneyBoardMarketClient _client;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _request;
    private BoardMarketSnapshot? _snapshot;
    private readonly HashSet<object> _activeViews = new();
    private readonly Dictionary<string, MarketWatchViewModel> _members = new();
    private readonly Dictionary<object, string> _memberViews = new();
    private readonly LinkedList<string> _recent = new();
    private readonly Dispatcher _dispatcher;
    private bool _active, _busy, _disposed, _wasLive;
    private int _failures;
    private DateTimeOffset _nextDue, _allowedAt;
    private string _status = "打开页面后加载板块行情", _keyword = "";
    private BoardKindOption _kind;

    public BoardWatchViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _kind = Kinds[0];
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; QuoteView/1.0)");
        _client = new EastMoneyBoardMarketClient(_http);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => _active && !Busy && DateTimeOffset.UtcNow >= _allowedAt);
    }

    public IReadOnlyList<BoardKindOption> Kinds { get; } = new[]
        { new BoardKindOption(BoardKind.Industry, "行业板块"), new BoardKindOption(BoardKind.Concept, "概念板块"), new BoardKindOption(BoardKind.Region, "地区板块") };
    public BoardKindOption SelectedKind { get => _kind; set { if (value is not null && Set(ref _kind, value)) Filter(); } }
    public string Keyword { get => _keyword; set { if (Set(ref _keyword, value)) Filter(); } }
    public RelayCommand RefreshCommand { get; }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); NotifyStatus(); } }
    public string Status { get => _status; private set { Set(ref _status, value); NotifyStatus(); } }
    public string EmptyMessage => Busy && _snapshot is null ? "正在加载板块行情…" : _snapshot is null ? Status : Neutral.Count > 0 ? "暂无上涨或下跌板块，可从上方查看平盘/无报价板块" : "暂无符合条件的板块";
    public string SnapshotInfo => _snapshot is null ? "" : $"{(_snapshot.Delayed ? "延迟备用行情" : "东方财富")} · 完成 {_snapshot.CompletedAt.ToLocalTime():HH:mm:ss}";
    public string Schedule => !_active ? "已暂停" : $"下次刷新 {_nextDue.ToLocalTime():HH:mm:ss} · "
        + (_failures > 0 ? $"失败退避 {MarketRefreshPolicy.FailureDelay(_failures).TotalMinutes:0} 分钟"
            : MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? "间隔 60 秒" : "非交易时段 15 分钟");
    public string Counts { get; private set; } = "";
    public IReadOnlyList<BoardMarketQuote> Gainers { get; private set; } = Array.Empty<BoardMarketQuote>();
    public IReadOnlyList<BoardMarketQuote> Losers { get; private set; } = Array.Empty<BoardMarketQuote>();
    public IReadOnlyList<BoardMarketQuote> Neutral { get; private set; } = Array.Empty<BoardMarketQuote>();
    public event Action? RankingsChanging;
    public event Action? RankingsChanged;

    private void NotifyStatus()
    { OnPropertyChanged(nameof(EmptyMessage)); OnPropertyChanged(nameof(Schedule)); }

    private void Filter()
    {
        RankingsChanging?.Invoke();
        var rows = BoardRanking.Filter(_snapshot?.Rows ?? Array.Empty<BoardMarketQuote>(), _kind.Kind, Keyword);
        Gainers = BoardRanking.Gainers(rows);
        Losers = BoardRanking.Losers(rows);
        Neutral = rows.Where(r => r.Percent is null or 0).OrderBy(r => r.Code).ToArray();
        Counts = $"{rows.Count} 个板块 · 上涨 {Gainers.Count} · 下跌 {Losers.Count} · 平盘/无报价 {Neutral.Count}";
        foreach (var p in new[] { nameof(Gainers), nameof(Losers), nameof(Neutral), nameof(Counts), nameof(EmptyMessage) }) OnPropertyChanged(p);
        RankingsChanged?.Invoke();
    }

    public MarketWatchViewModel GetMembers(object view, Board board)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_members.TryGetValue(board.Code, out var model))
            _members.Add(board.Code, model = new MarketWatchViewModel(_dispatcher, board));
        _memberViews[view] = board.Code;
        _recent.Remove(board.Code);
        _recent.AddLast(board.Code);
        TrimMembers();
        return model;
    }

    public void ReleaseMembers(object view)
    { _memberViews.Remove(view); TrimMembers(); }

    private void TrimMembers()
    {
        // Hidden views can reopen still-bound models. Keep those alive too.
        foreach (var code in _recent.ToArray())
        {
            if (_members.Count <= 8) break;
            if (_memberViews.ContainsValue(code)) continue;
            _members[code].Dispose(); _members.Remove(code); _recent.Remove(code);
        }
    }

    public void SetActive(object view, bool active)
    {
        if (_disposed) return;
        if (active) _activeViews.Add(view); else _activeViews.Remove(view);
        SetActive(_activeViews.Count > 0);
    }

    private void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active) { _timer.Start(); Tick(); }
        else { _timer.Stop(); _request?.Cancel(); Status = "已暂停刷新"; }
        NotifyStatus();
    }

    private void Tick()
    {
        if (!_active || _disposed) return;
        var live = MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow);
        if (live && !_wasLive) _nextDue = _allowedAt;
        _wasLive = live;
        OnPropertyChanged(nameof(Schedule));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        if (!Busy && DateTimeOffset.UtcNow >= _nextDue && DateTimeOffset.UtcNow >= _allowedAt) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!_active || _disposed || Busy || DateTimeOffset.UtcNow < _allowedAt) return;
        Busy = true;
        var started = DateTimeOffset.UtcNow;
        _allowedAt = _nextDue = started + MarketRefreshPolicy.Interval;
        using var cts = new CancellationTokenSource();
        _request = cts;
        Status = "正在获取板块行情…";
        try
        {
            var progress = new Progress<string>(s => { if (ReferenceEquals(_request, cts) && !cts.IsCancellationRequested) Status = s; });
            var snapshot = await _client.FetchAsync(progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _snapshot = snapshot;
            _failures = 0;
            _nextDue = started + (MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? MarketRefreshPolicy.Interval : MarketRefreshPolicy.ClosedInterval);
            if (_nextDue <= DateTimeOffset.UtcNow) _nextDue = DateTimeOffset.UtcNow + MarketRefreshPolicy.Interval;
            Status = snapshot.Delayed ? "已更新 · 当前使用延迟备用行情" : "已更新";
            OnPropertyChanged(nameof(SnapshotInfo));
            Filter();
            Probe.Log($"board watch updated: rows={snapshot.Rows.Count} host={snapshot.Host}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var delay = MarketRefreshPolicy.FailureDelay(++_failures);
            _allowedAt = _nextDue = DateTimeOffset.UtcNow + delay;
            Status = $"板块行情刷新失败，{delay.TotalMinutes:0} 分钟后重试" + (_snapshot is null ? "" : " · 保留上轮行情");
            Probe.Log($"board watch failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _request = null; Busy = false; }
    }

    public void Dispose()
    {
        _disposed = true; _active = false; _timer.Stop(); _request?.Cancel(); _http.Dispose();
        foreach (var member in _members.Values) member.Dispose();
        _members.Clear(); _memberViews.Clear(); _recent.Clear(); _activeViews.Clear();
    }
}
