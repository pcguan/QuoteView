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
    private readonly IMarketClock _clock;
    private readonly DispatcherTimer _rolloverTimer;

    private CancellationTokenSource? _searchCts;
    private MarketOption _selectedMarket;
    private TypeOption _selectedType;
    private string _keyword = "";
    private string _status = "正在加载合约列表…";
    private bool _loading = true;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockClient/1.0)");

        _clock = new MarketClock();
        _repo = new ContractRepository(
            new EastMoneyContractClient(_http), new ContractCache(), _clock);
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
        Loading ? "" : $"{_results.Count} 条";

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
         || _sortField != SortField.None);

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

        OnPropertyChanged(nameof(Keyword));
        OnPropertyChanged(nameof(SelectedMarket));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(CanClear));

        RunSearch();
    }

    private void RunSearch()
    {
        // Empty keyword is a browse, not an empty result: selecting a market
        // should show everything in it.
        var hits = _repo.Search(_keyword, _selectedMarket.Market, _selectedType.Type);
        Results = Sort(hits);
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
