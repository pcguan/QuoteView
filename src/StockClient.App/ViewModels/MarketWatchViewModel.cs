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
    private readonly LiveMarketClient _liveClient;
    private CancellationTokenSource? _extensionRequest;
    private DateTimeOffset _extensionsDue;
    private readonly Dictionary<string, MarketQuote> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _extensionUpdated = new HashSet<string>();
    private bool _extensionsAttempted;
    private string _extensionStatus = "扩展待更新", _errorDetail = "", _extensionError = "";
    private readonly Board? _board;
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

    public MarketWatchViewModel(Dispatcher dispatcher, Board? board = null, LiveMarketClient? liveClient = null)
    {
        _board = board;
        if (board is not null)
        {
            Markets = new[] { new MarketOption(null, "全部市场"), new MarketOption(Market.SH, "沪市"),
                new MarketOption(Market.SZ, "深市"), new MarketOption(Market.BJ, "北交所") };
            _market = Markets[0];
            _status = "展开后加载板块成分股行情";
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; QuoteView/1.0)");
        _client = new EastMoneyMarketClient(_http);
        _liveClient = liveClient ?? new LiveMarketClient(_http);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => CanRefresh);
        AddConditionCommand = new RelayCommand(_ => AddCondition());
        RemoveConditionCommand = new RelayCommand(c => { if (c is MarketCondition condition) Conditions.Remove(condition); });
        ClearCommand = new RelayCommand(_ => Clear());
        Conditions.CollectionChanged += (_, _) => Filter();
    }

    public string Title => _board is null ? "沪深全市场行情" : $"{_board.Name} · 成分股行情";
    public bool SupportsBeijing => _board is not null;
    private bool _beijing = true;
    public bool IncludeBeijing { get => _beijing; set { if (Set(ref _beijing, value)) Filter(); } }

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
    public string EmptyMessage => Busy ? "正在加载行情，请稍候…"
        : _snapshot is null ? Status : "暂无符合条件的行情";
    public string SnapshotInfo => _snapshot is null ? "尚未取得行情"
        : _board is null ? $"腾讯 · 本轮 {_snapshot.UpdatedCount ?? 0:N0}/{_snapshot.Rows.Count:N0} · {_extensionStatus}" + (_snapshot.DirectoryUnconfirmed ? " · 目录覆盖未确认" : "")
        : $"{(_snapshot.Delayed ? "东方财富 · 延迟备用源" : "东方财富")} · 完成 {_snapshot.CompletedAt.ToLocalTime():MM-dd HH:mm:ss} · 耗时 {(_snapshot.CompletedAt - _snapshot.StartedAt).TotalSeconds:0.0}s";
    public string ErrorDetail => string.Join("\n", new[] { _errorDetail, _extensionError }.Where(s => s.Length > 0));
    public string DataDetails => _board is not null ? "东方财富板块成分股行情；各合约上游时间见行情时间列。" : string.Join("\n", new[] { _snapshot?.DirectoryInfo ?? "", ErrorDetail,
        "价格等基础字段来自腾讯；动态市盈率、涨速、资金流及行业/概念等来自东方财富。扩展字段约5分钟更新，独立于基础报价；空值表示未取得数据。每行分别显示行情时间和扩展采集时间。" });
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
        else { _timer.Stop(); _request?.Cancel(); _extensionRequest?.Cancel(); Status = "已暂停刷新"; }
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
        if (_board is null && _snapshot is not null && _extensionRequest is null && DateTimeOffset.UtcNow >= _extensionsDue)
            _ = RefreshExtensionsAsync();
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
        Status = _snapshot is null ? "正在获取行情…" : "正在刷新，显示已取得的行情";
        var progress = new Progress<(int Page, int Total)>(p =>
        {
            if (ReferenceEquals(_request, cts) && !cts.IsCancellationRequested)
                Status = $"正在刷新 {p.Page}/{p.Total} 页" + (_snapshot is null ? "" : " · 显示上一轮行情");
        });
        try
        {
            var partial = new Progress<MarketSnapshot>(value =>
            {
                if (!ReferenceEquals(_request, cts) || cts.IsCancellationRequested || !_active) return;
                Publish(value);
                Status = $"基础行情已取得 {value.UpdatedCount:N0}/{value.Rows.Count:N0} · 正在继续刷新";
            });
            var snapshot = _board is null ? await _liveClient.FetchAsync(progress, cts.Token, partial)
                : await _client.FetchBoardAsync(_board.Code, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            Publish(snapshot);
            Probe.Log($"market watch updated: rows={snapshot.Rows.Count} pages={snapshot.Pages} host={snapshot.Host} updated={snapshot.UpdatedCount} errors={snapshot.ErrorDetail}");
            if (snapshot.UpdatedCount is null or > 0) _failures = 0;
            _nextDue = started + (MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? MarketRefreshPolicy.Interval : MarketRefreshPolicy.ClosedInterval);
            // A very slow refresh must not immediately start another whole crawl.
            if (_nextDue <= DateTimeOffset.UtcNow) _nextDue = DateTimeOffset.UtcNow + MarketRefreshPolicy.Interval;
            _allowedAt = MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? _nextDue : started + MarketRefreshPolicy.Interval;
            Status = snapshot.Delayed ? "已更新 · 当前使用延迟备用行情" : "已更新";
            if (_board is null)
            {
                if (snapshot.UpdatedCount == 0)
                {
                    var delay = MarketRefreshPolicy.FailureDelay(++_failures);
                    _allowedAt = _nextDue = DateTimeOffset.UtcNow + delay;
                    Status = $"基础行情更新失败，{delay.TotalMinutes:0} 分钟后重试 · 保留已有值";
                }
                else if (snapshot.UpdatedCount < snapshot.Rows.Count)
                    Status = $"部分更新 {snapshot.UpdatedCount:N0}/{snapshot.Rows.Count:N0} · 未成功项保留旧值";
                if (snapshot.ErrorDetail.Length > 0) Status += " · 详情见提示";
                if (_extensionRequest is null && DateTimeOffset.UtcNow >= _extensionsDue) _ = RefreshExtensionsAsync();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var delay = MarketRefreshPolicy.FailureDelay(++_failures);
            _allowedAt = _nextDue = DateTimeOffset.UtcNow + delay;
            Status = $"刷新失败，{delay.TotalMinutes:0} 分钟后重试" + (_snapshot is null ? "" : " · 保留上一轮行情");
            _errorDetail = MarketDataErrors.Describe(ex);
            OnPropertyChanged(nameof(ErrorDetail)); OnPropertyChanged(nameof(DataDetails));
            Probe.Log($"market watch refresh failed: {ex}");
        }
        finally
        {
            _request = null;
            Busy = false;
            OnPropertyChanged(nameof(Schedule));
        }
    }

    private void Publish(MarketSnapshot snapshot)
    {
        _snapshot = snapshot;
        _errorDetail = snapshot.ErrorDetail;
        OnPropertyChanged(nameof(SnapshotInfo)); OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ErrorDetail)); OnPropertyChanged(nameof(DataDetails));
        Filter();
    }

    private async Task RefreshExtensionsAsync()
    {
        if (_disposed || !_active || _extensionRequest is not null) return;
        using var cts = new CancellationTokenSource();
        _extensionRequest = cts;
        _extensionStatus = "扩展更新中";
        OnPropertyChanged(nameof(SnapshotInfo));
        try
        {
            var result = await _client.FetchExtensionsAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            foreach (var row in result.Rows) _extensions[row.Code] = row;
            _extensionUpdated = result.UpdatedCodes;
            _extensionsAttempted = true;
            _extensionError = result.ErrorDetail.Length > 0 ? "扩展字段：" + result.ErrorDetail : "";
            _extensionStatus = result.Rows.Count == 0 ? "扩展更新失败"
                : result.ErrorDetail.Length > 0 ? $"扩展部分更新 {result.Rows.Count:N0} 只" : "扩展已更新";
            if (_snapshot is not null)
            {
                var known = _snapshot.Rows.Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _extensionStatus += $" · {result.UpdatedCodes.Count(known.Contains):N0}/{known.Count:N0}";
            }
            Probe.Log($"market extensions updated: rows={result.Rows.Count} host={result.Host} errors={result.ErrorDetail}");
            Filter();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { _extensionStatus = "扩展已暂停"; }
        catch (Exception ex)
        {
            _extensionUpdated = new HashSet<string>(); _extensionsAttempted = true;
            _extensionError = "扩展字段：" + MarketDataErrors.Describe(ex); _extensionStatus = "扩展更新失败";
            Probe.Log($"market extensions failed: {ex}"); Filter();
        }
        finally
        {
            _extensionsDue = DateTimeOffset.UtcNow.AddMinutes(MarketRefreshPolicy.IsTradingSession(DateTimeOffset.UtcNow) ? 5 : 15);
            _extensionRequest = null;
            OnPropertyChanged(nameof(SnapshotInfo)); OnPropertyChanged(nameof(ErrorDetail)); OnPropertyChanged(nameof(DataDetails));
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
                | (IncludeStar ? ListingBoards.Star : 0) | (SupportsBeijing && IncludeBeijing ? ListingBoards.Beijing : 0), Conditions = Conditions.ToArray(),
        };
        ResultsChanging?.Invoke();
        var rows = _snapshot?.Rows ?? Array.Empty<MarketQuote>();
        Rows = query.Apply(_board is null ? rows.Select(r => MarketExtensionMerge.Apply(r, _extensions.GetValueOrDefault(r.Code),
            _extensionsAttempted && !_extensionUpdated.Contains(r.Code))) : rows, SortKey, Descending);
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
        _market = Markets[0]; _keyword = ""; _main = _chiNext = _star = _beijing = true;
        _excludeSt = _quotedOnly = false; SortKey = nameof(MarketQuote.Code); Descending = false;
        ConditionValue = ""; FilterError = "";
        foreach (var property in new[] { nameof(SelectedMarket), nameof(Keyword), nameof(IncludeMain), nameof(IncludeChiNext),
            nameof(IncludeStar), nameof(IncludeBeijing), nameof(ExcludeSt), nameof(QuotedOnly) }) OnPropertyChanged(property);
        Conditions.Clear();
    }

    public void Dispose()
    { _disposed = true; _active = false; _timer.Stop(); _request?.Cancel(); _extensionRequest?.Cancel(); _http.Dispose(); }
}
