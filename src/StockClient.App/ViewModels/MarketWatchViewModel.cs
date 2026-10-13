using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Threading;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;
using StockClient.Core.MarketWatch;

namespace StockClient.App.ViewModels;

public sealed record ComparisonOption(MarketComparison Value)
{
    public override string ToString() => MarketCondition.Symbol(Value);
}

public sealed class MarketWatchViewModel : ObservableObject, IDisposable
{
    private readonly HttpClient _http = Services.DirectHttp.Create(TimeSpan.FromSeconds(15));
    private readonly EastMoneyMarketClient _client;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _request;
    private MarketSnapshot? _snapshot;
    private readonly HashSet<object> _activeViews = new();
    private bool _active, _busy, _disposed, _wasLive;
    private int _failures;
    private DateTimeOffset _nextDue, _allowedAt;
    private string _status = "打开页面后加载沪深全市场行情", _keyword = "", _conditionValue = "", _filterError = "";
    private bool _main = true, _chiNext = true, _star = true, _excludeSt, _quotedOnly;
    private MarketOption _market = new(null, "沪深全部");
    private IReadOnlyList<MarketQuote> _rows = Array.Empty<MarketQuote>();

    public MarketWatchViewModel(Dispatcher dispatcher)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; QuoteView/1.0)");
        _client = new EastMoneyMarketClient(_http);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => CanRefresh);
        AddConditionCommand = new RelayCommand(_ => AddCondition());
        RemoveConditionCommand = new RelayCommand(c => { if (c is MarketCondition condition) Conditions.Remove(condition); });
        ClearCommand = new RelayCommand(_ => Clear());
        Conditions.CollectionChanged += (_, _) => Filter();
    }

    public IReadOnlyList<MarketOption> Markets { get; } = new[]
        { new MarketOption(null, "沪深全部"), new MarketOption(Market.SH, "沪市"), new MarketOption(Market.SZ, "深市") };
    public IReadOnlyList<MarketField> Fields => MarketFields.All;
    public IReadOnlyList<ComparisonOption> Comparisons { get; } = Enum.GetValues<MarketComparison>().Select(c => new ComparisonOption(c)).ToArray();
    private MarketField _selectedField = MarketFields.All[1];
    private ComparisonOption _selectedComparison = new(MarketComparison.GreaterOrEqual);
    public MarketField SelectedField { get => _selectedField; set { if (value is not null) Set(ref _selectedField, value); } }
    public ComparisonOption SelectedComparison { get => _selectedComparison; set { if (value is not null) Set(ref _selectedComparison, value); } }
    public string ConditionValue { get => _conditionValue; set => Set(ref _conditionValue, value); }
    public string FilterError { get => _filterError; private set => Set(ref _filterError, value); }
    public ObservableCollection<MarketCondition> Conditions { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddConditionCommand { get; }
    public RelayCommand RemoveConditionCommand { get; }
    public RelayCommand ClearCommand { get; }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(EmptyMessage)); } }
    public bool CanRefresh => _active && !Busy && DateTimeOffset.UtcNow >= _allowedAt;
    public string Status { get => _status; private set { Set(ref _status, value); OnPropertyChanged(nameof(EmptyMessage)); } }
    public string EmptyMessage => Busy ? "正在加载全市场行情，请稍候…"
        : _snapshot is null ? Status : "暂无符合条件的行情";
    public string SnapshotInfo => _snapshot is null ? "尚未取得完整行情"
        : $"{(_snapshot.Delayed ? "东方财富 · 延迟备用源" : "东方财富")} · 完成 {_snapshot.CompletedAt.ToLocalTime():MM-dd HH:mm:ss} · 耗时 {(_snapshot.CompletedAt - _snapshot.StartedAt).TotalSeconds:0.0}s";
    public string ResultCount => $"{Rows.Count:N0} / {_snapshot?.Rows.Count ?? 0:N0}";
    public string Schedule => !_active ? "已暂停"
        : _nextDue == default ? "首次加载" : $"下次刷新 {_nextDue.ToLocalTime():HH:mm:ss} · "
            + (_failures > 0 ? $"失败退避 {MarketRefreshPolicy.FailureDelay(_failures).TotalMinutes:0} 分钟"
                : MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? "间隔 60 秒" : "非交易时段 15 分钟");
    public IReadOnlyList<MarketQuote> Rows { get => _rows; private set { Set(ref _rows, value); OnPropertyChanged(nameof(ResultCount)); } }
    public MarketOption SelectedMarket { get => _market; set { if (value is not null && Set(ref _market, value)) Filter(); } }
    public string Keyword { get => _keyword; set { if (Set(ref _keyword, value)) Filter(); } }
    public bool IncludeMain { get => _main; set { if (Set(ref _main, value)) Filter(); } }
    public bool IncludeChiNext { get => _chiNext; set { if (Set(ref _chiNext, value)) Filter(); } }
    public bool IncludeStar { get => _star; set { if (Set(ref _star, value)) Filter(); } }
    public bool ExcludeSt { get => _excludeSt; set { if (Set(ref _excludeSt, value)) Filter(); } }
    public bool QuotedOnly { get => _quotedOnly; set { if (Set(ref _quotedOnly, value)) Filter(); } }
    public string SortKey { get; private set; } = nameof(MarketQuote.Code);
    public bool Descending { get; private set; }
    public event Action? ResultsChanging;
    public event Action? ResultsChanged;

    public void SetActive(object view, bool active)
    {
        if (active) _activeViews.Add(view); else _activeViews.Remove(view);
        SetActive(_activeViews.Count > 0);
    }

    private void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active) { _timer.Start(); Tick(); }
        else { _timer.Stop(); _request?.Cancel(); Status = "已暂停刷新"; }
        OnPropertyChanged(nameof(Schedule));
        OnPropertyChanged(nameof(CanRefresh));
    }

    private void Tick()
    {
        if (!_active || _disposed) return;
        var live = MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow);
        if (live && !_wasLive) _nextDue = _allowedAt;
        _wasLive = live;
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(Schedule));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        if (!Busy && DateTimeOffset.UtcNow >= _nextDue) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!CanRefresh || _disposed) return;
        Busy = true;
        var started = DateTimeOffset.UtcNow;
        _allowedAt = started + MarketRefreshPolicy.Interval;
        _nextDue = _allowedAt;
        using var cts = new CancellationTokenSource();
        _request = cts;
        Status = _snapshot is null ? "正在获取全市场行情…" : "正在刷新，显示上一轮完整行情";
        var progress = new Progress<(int Page, int Total)>(p =>
        {
            if (ReferenceEquals(_request, cts) && !cts.IsCancellationRequested)
                Status = $"正在刷新 {p.Page}/{p.Total} 页" + (_snapshot is null ? "" : " · 显示上一轮行情");
        });
        try
        {
            var snapshot = await _client.FetchAsync(progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _snapshot = snapshot;
            Probe.Log($"market watch updated: rows={snapshot.Rows.Count} pages={snapshot.Pages} host={snapshot.Host}");
            _failures = 0;
            _nextDue = started + (MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? MarketRefreshPolicy.Interval : MarketRefreshPolicy.ClosedInterval);
            // A very slow refresh must not immediately start another whole crawl.
            if (_nextDue <= DateTimeOffset.UtcNow) _nextDue = DateTimeOffset.UtcNow + MarketRefreshPolicy.Interval;
            _allowedAt = MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? _nextDue : started + MarketRefreshPolicy.Interval;
            Status = snapshot.Delayed ? "已更新 · 当前使用延迟备用行情" : "已更新";
            OnPropertyChanged(nameof(SnapshotInfo));
            OnPropertyChanged(nameof(EmptyMessage));
            Filter();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var delay = MarketRefreshPolicy.FailureDelay(++_failures);
            _allowedAt = _nextDue = DateTimeOffset.UtcNow + delay;
            Status = $"刷新失败，{delay.TotalMinutes:0} 分钟后重试" + (_snapshot is null ? "" : " · 保留上一轮行情");
            Probe.Log($"market watch refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _request = null;
            Busy = false;
            OnPropertyChanged(nameof(Schedule));
        }
    }

    public void Sort(string key, bool descending)
    { SortKey = key; Descending = descending; Filter(); }

    private void Filter()
    {
        var query = new MarketQuery
        {
            Market = _market.Market, Keyword = Keyword, ExcludeSt = ExcludeSt, QuotedOnly = QuotedOnly,
            Boards = (IncludeMain ? ListingBoards.Main : 0) | (IncludeChiNext ? ListingBoards.ChiNext : 0)
                | (IncludeStar ? ListingBoards.Star : 0), Conditions = Conditions.ToArray(),
        };
        ResultsChanging?.Invoke();
        Rows = query.Apply(_snapshot?.Rows ?? Array.Empty<MarketQuote>(), SortKey, Descending);
        ResultsChanged?.Invoke();
    }

    private void AddCondition()
    {
        if (!double.TryParse(ConditionValue, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || !double.IsFinite(value)) { FilterError = "请输入有效数值，单位与字段名一致"; return; }
        FilterError = "";
        Conditions.Add(new MarketCondition(SelectedField, SelectedComparison.Value, value));
        ConditionValue = "";
    }

    private void Clear()
    {
        _market = Markets[0]; _keyword = ""; _main = _chiNext = _star = true;
        _excludeSt = _quotedOnly = false; SortKey = nameof(MarketQuote.Code); Descending = false;
        ConditionValue = ""; FilterError = "";
        foreach (var property in new[] { nameof(SelectedMarket), nameof(Keyword), nameof(IncludeMain), nameof(IncludeChiNext),
            nameof(IncludeStar), nameof(ExcludeSt), nameof(QuotedOnly) }) OnPropertyChanged(property);
        Conditions.Clear();
    }

    public void Dispose()
    { _disposed = true; _active = false; _timer.Stop(); _request?.Cancel(); _http.Dispose(); }
}
