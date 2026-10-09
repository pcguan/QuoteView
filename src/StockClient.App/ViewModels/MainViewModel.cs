using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using StockClient.Core.Boards;
using StockClient.Core.Contracts;

namespace StockClient.App.ViewModels;

public enum SortField
{
    None,
    Code,
    Name,
    Market,
    Type,
    Industry,
    ListedOn,
    Concepts,
    Region,
    TotalShares,
    FloatShares,
}

/// <summary>A market entry in the exchange dropdown; null Market means "all".</summary>
public sealed record MarketOption(Market? Market, string Display)
{
    public override string ToString() => Display;
}

/// <summary>A category entry in the type dropdown; null Type means "all".</summary>
public sealed record TypeOption(ContractType? Type, string Display)
{
    public override string ToString() => Display;
}

public sealed record BoardOption(string Key, string Display, IReadOnlyList<Board> Boards)
{
    public override string ToString() => Display;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Re-checked periodically so a long-running client still picks up a new
    /// trading day. Markets roll over at different moments, so this cannot be a
    /// single timer aimed at local midnight.
    /// </summary>
    private static readonly TimeSpan RolloverCheck = TimeSpan.FromMinutes(15);

    private const int SearchDebounceMs = 200;

    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _http;
    private readonly ContractRepository _repo;
    private readonly BoardRepository _boards;
    private readonly BoardMemberRepository _members;
    private readonly IMarketClock _clock;
    private readonly DispatcherTimer _rolloverTimer;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _queryCts;
    private string? _boardQuery;
    private string? _boardSelectionKeyword;
    private bool _updatingBoards;
    private bool _searching;
    private string _loadStatus = "";
    private bool _includeMain = true, _includeChiNext = true, _includeStar = true, _includeBeijing = true;
    private BoardOption _selectedBoard = new("-", "合约匹配", Array.Empty<Board>());
    private IReadOnlyList<BoardOption> _boardOptions = Array.Empty<BoardOption>();
    private MarketOption _selectedMarket;
    private TypeOption _selectedType;
    private string _keyword = "";
    private string _status = "正在加载合约列表…";
    private bool _loading = true;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _http = Services.DirectHttp.Create(TimeSpan.FromSeconds(20));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockClient/1.0)");

        _clock = new MarketClock();
        var client = new EastMoneyContractClient(_http);
        _repo = new ContractRepository(client, new ContractCache(), _clock);
        _members = new BoardMemberRepository(client.FetchBoardMembersAsync, _clock);
        RetrySearchCommand = new RelayCommand(_ => RunSearch());
        _boards = new BoardRepository(
            new EastMoneyBoardClient(_http), new BoardCache(), _clock);

        Markets = new ObservableCollection<MarketOption>(
            new[] { new MarketOption(null, "全部市场") }.Concat(
                MarketInfo.AllMarkets.Select(m =>
                    new MarketOption(m, MarketInfo.Of(m).DisplayName))));
        _selectedMarket = Markets[0];

        // Only categories that actually occur, so the list can't offer a filter
        // that yields nothing.
        Types = new ObservableCollection<TypeOption>(
            new[] { new TypeOption(null, "全部类型") }.Concat(
                new[]
                {
                    ContractType.MainBoard, ContractType.Star, ContractType.ChiNext,
                    ContractType.BeijingBoard, ContractType.Etf, ContractType.Stock,
                    ContractType.Bond, ContractType.Fund, ContractType.Adr,
                    ContractType.PreferredStock, ContractType.Warrant,
                    ContractType.Unit, ContractType.Other,
                }.Select(t => new TypeOption(t, t.Display()))));
        _selectedType = Types[0];

        _rolloverTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = RolloverCheck,
        };
        _rolloverTimer.Tick += (_, _) => _ = LoadAsync();
    }

    /// <summary>Shared with the quotes view so both use one cached contract set.</summary>
    public ContractRepository Repository => _repo;

    /// <summary>The daily board catalog, cached alongside the contract lists.</summary>
    public BoardRepository Boards => _boards;

    public ObservableCollection<MarketOption> Markets { get; }

    public ObservableCollection<TypeOption> Types { get; }

    public RelayCommand RetrySearchCommand { get; }
    public IReadOnlyList<BoardOption> BoardOptions => _boardOptions;
    public bool HasBoardMatches => _boardOptions.Any(o => o.Boards.Count > 0);
    public bool Searching
    {
        get => _searching;
        private set { if (Set(ref _searching, value)) OnPropertyChanged(nameof(ResultSummary)); }
    }
    public BoardOption SelectedBoard
    {
        get => _selectedBoard;
        set
        {
            if (value is null || _updatingBoards || !Set(ref _selectedBoard, value)) return;
            RunSearch();
        }
    }
    public bool IncludeMain { get => _includeMain; set { if (Set(ref _includeMain, value)) FilterChanged(); } }
    public bool IncludeChiNext { get => _includeChiNext; set { if (Set(ref _includeChiNext, value)) FilterChanged(); } }
    public bool IncludeStar { get => _includeStar; set { if (Set(ref _includeStar, value)) FilterChanged(); } }
    public bool IncludeBeijing { get => _includeBeijing; set { if (Set(ref _includeBeijing, value)) FilterChanged(); } }
    private void FilterChanged() { OnPropertyChanged(nameof(CanClear)); RunSearch(); }
    private ListingBoards ListingFilter =>
        (IncludeMain ? ListingBoards.Main : ListingBoards.None)
        | (IncludeChiNext ? ListingBoards.ChiNext : ListingBoards.None)
        | (IncludeStar ? ListingBoards.Star : ListingBoards.None)
        | (IncludeBeijing ? ListingBoards.Beijing : ListingBoards.None);

    public TypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (!Set(ref _selectedType, value)) return;
            OnPropertyChanged(nameof(CanClear));
            RunSearch();
        }
    }

    private IReadOnlyList<Contract> _results = Array.Empty<Contract>();

    /// <summary>
    /// Replaced wholesale rather than mutated: browsing a market can yield
    /// ~13k rows, and adding those one by one to an ObservableCollection fires
    /// one CollectionChanged per row.
    /// </summary>
    public IReadOnlyList<Contract> Results
    {
        get => _results;
        private set
        {
            if (Set(ref _results, value)) OnPropertyChanged(nameof(ResultSummary));
        }
    }

    public string ResultSummary =>
        Loading || Searching ? "…" : _results.Count.ToString("N0");

    private SortField _sortField = SortField.None;
    private bool _sortAscending = true;

    /// <summary>
    /// The sort state lives here, not on the grid's columns: replacing
    /// ItemsSource (which every sort does) makes the DataGrid reset
    /// DataGridColumn.SortDirection to null, so it cannot be used to work out
    /// which way the next click should go.
    /// </summary>
    public SortField SortField => _sortField;

    public bool SortAscending => _sortAscending;

    /// <summary>True when anything is actually filtering or sorting.</summary>
    public bool CanClear =>
        !Loading &&
        (_selectedMarket.Market is not null
         || _selectedType.Type is not null
         || !string.IsNullOrEmpty(_keyword)
         || _sortField != SortField.None || ListingFilter != ListingBoards.All);

    public ObservableCollection<MarketStatus> MarketStatuses { get; } = new();

    public MarketOption SelectedMarket
    {
        get => _selectedMarket;
        set
        {
            if (!Set(ref _selectedMarket, value)) return;
            OnPropertyChanged(nameof(CanClear));
            RunSearch();
        }
    }

    public string Keyword
    {
        get => _keyword;
        set
        {
            if (!Set(ref _keyword, value)) return;
            _queryCts?.Cancel();
            OnPropertyChanged(nameof(CanClear));
            _ = DebouncedSearchAsync();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (!Set(ref _loading, value)) return;
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(CanClear));
        }
    }

    /// <summary>Loads every market for its own current trading date.</summary>
    public async Task LoadAsync()
    {
        Loading = true;
        Status = "正在加载合约列表…";

        var progress = new Progress<MarketLoadResult>(r =>
        {
            var existing = MarketStatuses.FirstOrDefault(s => s.Market == r.Market);
            if (existing is not null) MarketStatuses.Remove(existing);
            MarketStatuses.Add(MarketStatus.From(r));
        });

        try
        {
            var results = await _repo.EnsureLoadedAsync(progress);
            var failed = results.Where(r => r.Error is not null && r.Count == 0).ToArray();
            var stale = results.Count(r => r.Stale);

            // Board catalog rides the same daily refresh. A-share-wide, ~11 requests,
            // and failure here must not affect the contract status the user sees.
            try
            {
                var boards = await _boards.EnsureLoadedAsync();
                Probe.Log($"boards loaded: {boards.Count} ({(boards.FromCache ? "缓存" : "网络")}) err={boards.Error}");
            }
            catch (Exception ex)
            {
                Probe.Log($"boards load failed: {ex.Message}");
            }

            Status = failed.Length > 0
                ? $"共 {_repo.TotalCount} 条 · {failed.Length} 个市场加载失败"
                : stale > 0
                    ? $"共 {_repo.TotalCount} 条 · {stale} 个市场用的是旧缓存"
                    : $"共 {_repo.TotalCount} 条合约";
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.Message;
        }
        finally
        {
            _loadStatus = Status;
            _boardQuery = null;
            Loading = false;
            _rolloverTimer.Start();
            RunSearch();
        }
    }

    private async Task DebouncedSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            // Search is local, but typing fast still shouldn't re-rank 28k rows
            // on every keystroke.
            await Task.Delay(SearchDebounceMs, cts.Token);
            RunSearch();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke won.
        }
    }

    /// <summary>Applied from the grid's column headers.</summary>
    public void SetSort(SortField field, bool ascending)
    {
        _sortField = field;
        _sortAscending = ascending;
        OnPropertyChanged(nameof(CanClear));
        RunSearch();
    }

    /// <summary>
    /// Resets market, type, keyword and sort back to defaults.
    ///
    /// The backing fields are assigned directly and RunSearch is called once at
    /// the end: going through the setters would re-rank all ~28k rows four times
    /// over for a single click.
    /// </summary>
    public void ClearAll()
    {
        _searchCts?.Cancel();

        _keyword = "";
        _selectedMarket = Markets[0];
        _selectedType = Types[0];
        _sortField = SortField.None;
        _sortAscending = true;
        _includeMain = _includeChiNext = _includeStar = _includeBeijing = true;
        _boardQuery = null;
        foreach (var property in new[] { nameof(IncludeMain), nameof(IncludeChiNext), nameof(IncludeStar), nameof(IncludeBeijing) })
            OnPropertyChanged(property);

        OnPropertyChanged(nameof(Keyword));
        OnPropertyChanged(nameof(SelectedMarket));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(CanClear));

        RunSearch();
    }

    private void UpdateBoardOptions()
    {
        var query = _keyword.Trim();
        if (_boardQuery == query) return;
        var selectedKey = _boardSelectionKeyword == query ? _selectedBoard.Key : null;
        _boardSelectionKeyword = query;
        _boardQuery = query;
        var matches = BoardSearch.Match(_boards.Loaded, query);
        var options = new List<BoardOption>();
        if (matches.Count > 0)
        {
            options.Add(new BoardOption("*", $"全部相关板块（{matches.Count}）", matches));
            options.AddRange(matches.Select(b => new BoardOption(b.Code,
                $"{b.Name} · {(b.Kind == BoardKind.Industry ? "行业" : "概念")}", new[] { b })));
        }
        options.Add(new BoardOption("-", "合约匹配", Array.Empty<Board>()));
        _updatingBoards = true;
        try
        {
            _boardOptions = options;
            _selectedBoard = options.FirstOrDefault(o => o.Key == selectedKey) ?? options[0];
            OnPropertyChanged(nameof(BoardOptions));
            OnPropertyChanged(nameof(SelectedBoard));
            OnPropertyChanged(nameof(HasBoardMatches));
        }
        finally { _updatingBoards = false; }
    }

    private void RunSearch() => _ = SearchAsync();

    private async Task SearchAsync()
    {
        if (Loading) return;
        _queryCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _queryCts = cts;
        UpdateBoardOptions();
        var boards = _selectedBoard.Boards;
        var market = _selectedMarket.Market;
        var type = _selectedType.Type;
        var listing = ListingFilter;
        try
        {
            IReadOnlyList<Contract> hits;
            if (boards.Count > 0)
            {
                Searching = true;
                Results = Array.Empty<Contract>();
                Status = "正在加载板块成分股…";
                var members = await _members.GetAsync(boards, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                hits = members.Where(c => (market is null || c.Market == market)
                    && (type is null || c.Type == type)).ToArray();
                Status = $"{_selectedBoard.Display} · 共 {members.Count} 只成分股";
            }
            else
            {
                hits = _repo.Search(_keyword, market, type);
                Status = _loadStatus;
            }
            Results = Sort(hits.Where(c => BoardSearch.Includes(c, listing)).ToArray());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                Results = Array.Empty<Contract>();
                Status = "板块成分股加载失败，请点击重试";
                Probe.Log($"board members failed: {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_queryCts, cts)) { Searching = false; _queryCts = null; }
        }
    }

    /// <summary>
    /// With no explicit sort, a browse reads best by code while a search must
    /// keep the relevance order Search() already produced.
    /// </summary>
    private IReadOnlyList<Contract> Sort(IReadOnlyList<Contract> hits)
    {
        var field = _sortField;
        if (field == SortField.None)
        {
            if (!string.IsNullOrWhiteSpace(_keyword)) return hits;
            field = SortField.Code;
        }

        IOrderedEnumerable<Contract> ordered = field switch
        {
            SortField.Name => _sortAscending
                ? hits.OrderBy(c => c.Name, StringComparer.CurrentCulture)
                : hits.OrderByDescending(c => c.Name, StringComparer.CurrentCulture),
            SortField.Market => _sortAscending
                ? hits.OrderBy(c => c.Code[..2], StringComparer.Ordinal)
                : hits.OrderByDescending(c => c.Code[..2], StringComparer.Ordinal),
            SortField.Type => _sortAscending
                ? hits.OrderBy(c => c.Type)
                : hits.OrderByDescending(c => c.Type),
            // Rows with no value sort last either way rather than clumping at
            // the top on a descending pass.
            SortField.Industry => _sortAscending
                ? hits.OrderBy(c => c.Industry is null).ThenBy(c => c.Industry, StringComparer.CurrentCulture)
                : hits.OrderBy(c => c.Industry is null).ThenByDescending(c => c.Industry, StringComparer.CurrentCulture),
            SortField.Concepts => _sortAscending
                ? hits.OrderBy(c => c.Concepts is null).ThenBy(c => c.Concepts, StringComparer.CurrentCulture)
                : hits.OrderBy(c => c.Concepts is null).ThenByDescending(c => c.Concepts, StringComparer.CurrentCulture),
            SortField.Region => _sortAscending
                ? hits.OrderBy(c => c.Region is null).ThenBy(c => c.Region, StringComparer.CurrentCulture)
                : hits.OrderBy(c => c.Region is null).ThenByDescending(c => c.Region, StringComparer.CurrentCulture),
            SortField.TotalShares => _sortAscending
                ? hits.OrderBy(c => c.TotalShares ?? long.MaxValue)
                : hits.OrderBy(c => c.TotalShares is null).ThenByDescending(c => c.TotalShares ?? 0),
            SortField.FloatShares => _sortAscending
                ? hits.OrderBy(c => c.FloatShares ?? long.MaxValue)
                : hits.OrderBy(c => c.FloatShares is null).ThenByDescending(c => c.FloatShares ?? 0),
            SortField.ListedOn => _sortAscending
                ? hits.OrderBy(c => c.ListedOn is null).ThenBy(c => c.ListedOn, StringComparer.Ordinal)
                : hits.OrderBy(c => c.ListedOn is null).ThenByDescending(c => c.ListedOn, StringComparer.Ordinal),
            _ => _sortAscending
                ? hits.OrderBy(c => c.Code, StringComparer.Ordinal)
                : hits.OrderByDescending(c => c.Code, StringComparer.Ordinal),
        };

        return ordered.ThenBy(c => c.Code, StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        _rolloverTimer.Stop();
        _searchCts?.Cancel();
        _queryCts?.Cancel();
        _http.Dispose();
    }
}

/// <summary>Per-market load state shown in the footer.</summary>
public sealed record MarketStatus
{
    public required Market Market { get; init; }
    public required string Display { get; init; }

    /// <summary>Kept apart from Detail so the badge can weight the number differently.</summary>
    public required string Count { get; init; }

    public required string Detail { get; init; }

    public static MarketStatus From(MarketLoadResult r)
    {
        var info = MarketInfo.Of(r.Market);
        var failed = r.Error is not null && r.Count == 0;

        return new MarketStatus
        {
            Market = r.Market,
            Display = info.DisplayName,
            Count = failed ? "—" : r.Count.ToString("N0"),
            Detail = failed
                ? "加载失败"
                : r.Stale
                    ? $"旧缓存 {r.TradingDate:MM-dd}"
                    : $"{r.TradingDate:MM-dd} · {(r.FromCache ? "缓存" : "网络")}",
        };
    }
}
