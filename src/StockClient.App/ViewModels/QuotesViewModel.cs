using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using StockClient.Core;
using StockClient.Core.Contracts;
using StockClient.Core.Groups;
using StockClient.Core.Quotes;

namespace StockClient.App.ViewModels;

// Probe lives in the root namespace.

/// <summary>One row in the quote grid, updated in place.</summary>
public sealed class QuoteRow : ObservableObject
{
    private Quote _quote;
    private int _flash;
    private string _industry = "";
    private string _region = "";
    private string _concepts = "";

    public QuoteRow(string code) => _quote = Quote.Missing(code);

    public string Code => _quote.Code;

    /// <summary>
    /// 板块归属字段: 行业(A股/港股/美股) / 地区(A股) / 概念(A股)。都是每只合约的静态
    /// 归类,来自缓存的合约列表而非每秒行情——行情接口不带这些。对应市场没有时为空。
    /// </summary>
    public string Industry
    {
        get => _industry;
        private set => Set(ref _industry, value);
    }

    public string Region
    {
        get => _region;
        private set => Set(ref _region, value);
    }

    public string Concepts
    {
        get => _concepts;
        private set => Set(ref _concepts, value);
    }

    /// <summary>
    /// True once the row has been matched to a loaded contract. Stops the per-tick
    /// self-heal from re-querying forever for markets that legitimately carry no
    /// concepts/region (HK/US/KR), while still retrying until a contract is found.
    /// </summary>
    public bool MetaResolved { get; private set; }

    public void SetMeta(string? industry, string? region, string? concepts)
    {
        Industry = industry ?? "";
        Region = region ?? "";
        Concepts = concepts ?? "";
        MetaResolved = true;
    }

    public Quote Quote
    {
        get => _quote;
        private set => Set(ref _quote, value);
    }

    public string Name => _quote.IsMissing ? "无效代码" : _quote.Name;
    public double Now => _quote.Now;
    public double Yesterday => _quote.Yesterday;
    public double Open => _quote.Open;
    public double High => _quote.High;
    public double Low => _quote.Low;
    public double Change => _quote.Change;
    public double Percent => _quote.Percent;
    public string Time => _quote.Time;
    public bool IsMissing => _quote.IsMissing;
    public IReadOnlyList<QuoteField> Extras => _quote.Extras;

    /// <summary>Order book from the same 1s quote — no separate request.</summary>
    public QuoteDepth Depth => _quote.Depth;

    /// <summary>
    /// Decimals the price ladder should use. Prices come from the feed already
    /// rounded, so this reads the quote back rather than guessing per market:
    /// 1341.67 and 466.400 both need to render the way the feed sent them.
    /// </summary>
    public int PriceDecimals
    {
        get
        {
            var text = _quote.Now.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            var dot = text.IndexOf('.');
            return dot < 0 ? 2 : text.Length - dot - 1;
        }
    }

    // Per-market extras as structured numbers (null = not reported by the market),
    // for the optional grid columns. The grid shows these scaled (311.01万); the
    // detail badges below keep the raw values.
    public double? Volume => _quote.Volume;
    public double? Amount => _quote.Amount;
    public double? TurnoverRate => _quote.TurnoverRate;
    public double? VolumeRatio => _quote.VolumeRatio;
    public double? Amplitude => _quote.Amplitude;
    public double? AvgPrice => _quote.AvgPrice;
    public double? PeTtm => _quote.PeTtm;
    public double? Pb => _quote.Pb;
    public double? FloatCap => _quote.FloatCap;
    public double? TotalCap => _quote.TotalCap;
    public double? LimitUp => _quote.LimitUp;
    public double? LimitDown => _quote.LimitDown;
    public double? Week52High => _quote.Week52High;
    public double? Week52Low => _quote.Week52Low;
    public double? DividendYield => _quote.DividendYield;
    public double? OuterVolume => _quote.OuterVolume;
    public double? InnerVolume => _quote.InnerVolume;

    // Baselines for the period returns, refreshed once per trading day. The
    // percentages themselves are derived here against the live price, so they move
    // with the quote instead of with the fetch.
    private ReturnBaselines? _baselines;

    public double? PrevDayPercent => _baselines?.PrevDayPercent;
    public double? Return3 => Ret(_baselines?.Day3);
    public double? Return5 => Ret(_baselines?.Day5);
    public double? Return10 => Ret(_baselines?.Day10);
    public double? Return20 => Ret(_baselines?.Day20);
    public double? Return60 => Ret(_baselines?.Day60);
    public double? ReturnYtd => Ret(_baselines?.YearStart);

    private double? Ret(double? baseline) =>
        baseline is { } b ? ReturnBaselines.Percent(Now, b) : null;

    /// <summary>Attaches (or replaces) the day's baselines for this contract.</summary>
    public void SetBaselines(ReturnBaselines? baselines)
    {
        _baselines = baselines;

        foreach (var name in new[]
                 {
                     nameof(PrevDayPercent), nameof(Return3), nameof(Return5),
                     nameof(Return10), nameof(Return20), nameof(Return60), nameof(ReturnYtd),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // From the secondary EastMoney poll (A-shares only), null until it runs.
    private QuoteExtra? _extra;
    public double? Speed => _extra?.Speed;
    public double? MainInflow => _extra?.MainInflow;
    public double? SuperInflow => _extra?.SuperInflow;
    public double? BigInflow => _extra?.BigInflow;
    public double? MidInflow => _extra?.MidInflow;
    public double? SmallInflow => _extra?.SmallInflow;
    public double? MainInflowPct => _extra?.MainInflowPct;

    /// <summary>Merges the EastMoney fund-flow extras. Distinct source from Update().</summary>
    public void UpdateExtra(QuoteExtra extra)
    {
        _extra = extra;
        foreach (var name in new[]
                 {
                     nameof(Speed), nameof(MainInflow), nameof(SuperInflow), nameof(BigInflow),
                     nameof(MidInflow), nameof(SmallInflow), nameof(MainInflowPct),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private string _note = "";

    /// <summary>
    /// User's own note for this contract. Set from the shared store, so the same
    /// contract shows the same text in every group.
    /// </summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value ?? "");
    }

    /// <summary>1 up, -1 down, 0 none — drives the row flash after a price move.</summary>
    public int Flash
    {
        get => _flash;
        private set => Set(ref _flash, value);
    }

    public void Update(Quote next)
    {
        var previous = _quote.Now;
        Quote = next;

        // Empty string = "all properties changed" to WPF. With ~30 passthrough
        // props now, a hand-kept name list is a bug farm.
        OnPropertyChanged(string.Empty);

        if (!next.IsMissing && previous > 0 && Math.Abs(next.Now - previous) > 1e-9)
            Flash = next.Now > previous ? 1 : -1;
    }

    public void ClearFlash() => Flash = 0;
}

public sealed class GroupRow : ObservableObject
{
    private string _name;
    private int _count;
    private bool _isActive;
    private bool _isEditing;

    public GroupRow(Group model)
    {
        Model = model;
        _name = model.Name;
        _count = model.Codes.Count;
    }

    public Group Model { get; }
    public string Id => Model.Id;

    public string Name
    {
        get => _name;
        set
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !Set(ref _name, trimmed)) return;
            Model.Name = trimmed;
        }
    }

    public int Count
    {
        get => _count;
        private set => Set(ref _count, value);
    }

    /// <summary>
    /// Whether this group is cycled by the stealth panel (切分组). Persisted on the
    /// model; the view saves after a toggle. Default on.
    /// </summary>
    public bool InPanel
    {
        get => Model.InPanel;
        set
        {
            if (Model.InPanel == value) return;
            Model.InPanel = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Exactly one group carries this at a time; only it is polled.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>
    /// Toggles the inline rename box. Renaming happens in place rather than in a
    /// dialog — a modal prompt for one short string is both slower and the only
    /// thing on screen that isn't Fluent-styled.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }

    public void RefreshCount() => Count = Model.Codes.Count;

    private double? _indexPercent;
    private string _indexText = "";
    private int _indexSign;

    /// <summary>
    /// The group's aggregate move (整体涨跌幅), recomputed by the view model on
    /// every poll tick. Null until the first tick covering this group lands.
    /// </summary>
    public double? IndexPercent
    {
        get => _indexPercent;
        set
        {
            if (!Set(ref _indexPercent, value)) return;
            IndexText = value is { } v
                ? v.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture) + "%"
                : "";
            IndexSign = value is { } x ? Math.Sign(Math.Round(x, 2)) : 0;
        }
    }

    public string IndexText
    {
        get => _indexText;
        private set => Set(ref _indexText, value);
    }

    /// <summary>1 up / -1 down / 0 flat-or-unknown, for the list's colour triggers.</summary>
    public int IndexSign
    {
        get => _indexSign;
        private set => Set(ref _indexSign, value);
    }
}

public sealed class QuotesViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _http;
    private readonly GroupStore _store;
    private readonly GroupConfig _config;
    private readonly QuotePoller _poller;
    private readonly EastMoneyExtraPoller _extraPoller;
    private readonly ReturnBaselineRepository _returns;
    private readonly ContractRepository _contracts;
    private readonly DispatcherTimer _flashTimer;

    /// <summary>Whether any fund-flow/涨速 column is on, so the secondary poll should run.</summary>
    private bool _fundFlowActive;

    private readonly Dictionary<string, QuoteRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    private GroupRow? _activeGroup;
    private QuoteRow? _selectedRow;
    private string _newCode = "";
    private string _status = "等待行情…";
    private string _error = "";

    public QuotesViewModel(Dispatcher dispatcher, ContractRepository contracts)
    {
        _dispatcher = dispatcher;
        _contracts = contracts;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockClient/1.0)");

        _store = new GroupStore();
        _config = _store.Load();

        _poller = new QuotePoller(new TencentQuoteClient(_http));
        _poller.Tick += OnTick;
        _poller.Failed += OnFailed;

        _extraPoller = new EastMoneyExtraPoller(new EastMoneyQuoteClient(_http));

        // Period-return baselines: one batch request per trading day, then the
        // percentages are computed locally against each tick's price.
        _returns = new ReturnBaselineRepository(
            new ReturnBaselineClient(_http), new ReturnBaselineCache(), new MarketClock());
        _extraPoller.Tick += OnExtraTick;

        Groups = new ObservableCollection<GroupRow>(_config.Groups.Select(g => new GroupRow(g)));

        _flashTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            foreach (var row in Quotes) row.ClearFlash();
        };

        SetActive(Groups.FirstOrDefault(g => g.Id == _config.ActiveGroupId) ?? Groups.FirstOrDefault());
    }

    /// <summary>Stealth panel settings, persisted alongside the groups.</summary>
    public StealthConfig Stealth => _config.Stealth;

    /// <summary>Live-quote column layout, persisted alongside the groups.</summary>
    public List<QuoteColumnState> QuoteColumns => _config.QuoteColumns;

    public void SaveConfig() => Save();

    public ObservableCollection<GroupRow> Groups { get; }

    public ObservableCollection<QuoteRow> Quotes { get; } = new();

    /// <summary>Candidates for the add-contract box, from the cached contract lists.</summary>
    public ObservableCollection<Contract> Suggestions { get; } = new();

    public GroupRow? ActiveGroup
    {
        get => _activeGroup;
        private set
        {
            if (Set(ref _activeGroup, value)) OnPropertyChanged(nameof(HasGroup));
        }
    }

    public bool HasGroup => _activeGroup is not null;

    public QuoteRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value)) OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedRow is not null && !_selectedRow.IsMissing;

    public string NewCode
    {
        get => _newCode;
        set
        {
            if (!Set(ref _newCode, value)) return;
            OnPropertyChanged(nameof(CanAddCode));
            RefreshSuggestions();
        }
    }

    /// <summary>
    /// A raw normalized code is accepted directly; otherwise the text has to
    /// resolve against the contract lists, so a typo can't be added silently.
    /// </summary>
    public bool CanAddCode =>
        _activeGroup is not null &&
        (CodeMapper.IsValid(_newCode.Trim()) || Suggestions.Count > 0);

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>Activates a group. Exactly one is active, and only it is polled.</summary>
    public void SetActive(GroupRow? group)
    {
        foreach (var row in Groups) row.IsActive = ReferenceEquals(row, group);

        ActiveGroup = group;
        _config.ActiveGroupId = group?.Id;
        Save();
        RebuildRows();
    }

    public void AddGroup()
    {
        var group = new Group { Id = Guid.NewGuid().ToString("N"), Name = NextName() };
        _config.Groups.Add(group);

        var row = new GroupRow(group);
        Groups.Add(row);
        Save();
        SetActive(row);
    }

    private string NextName()
    {
        for (var i = 1; ; i++)
        {
            var name = $"分组 {i}";
            if (Groups.All(g => g.Name != name)) return name;
        }
    }

    public void RemoveGroup(GroupRow? group)
    {
        if (group is null) return;

        var index = Groups.IndexOf(group);
        _config.Groups.Remove(group.Model);
        Groups.Remove(group);

        if (ReferenceEquals(_activeGroup, group))
        {
            SetActive(Groups.Count == 0 ? null : Groups[Math.Min(index, Groups.Count - 1)]);
        }
        else
        {
            Save();
            // Its codes must leave the merged poll too — nothing else rebuilds
            // the target when a background group goes away.
            RefreshPollTarget();
        }
    }

    public void CommitRename() => Save();

    /// <summary>Reorders a group by drag. Groups and _config.Groups are 1:1, so indices map directly.</summary>
    public void MoveGroup(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Groups.Count || to >= Groups.Count) return;

        Groups.Move(from, to);

        var model = _config.Groups[from];
        _config.Groups.RemoveAt(from);
        _config.Groups.Insert(to, model);

        Save();
    }

    /// <summary>
    /// Reorders a contract within the active group by drag. Quotes is a filtered
    /// view of Codes (invalid codes are dropped), so the codes list is rewritten
    /// from the new Quotes order rather than moved by index; any invalid codes are
    /// kept, appended at the end.
    /// </summary>
    public void MoveCode(int from, int to)
    {
        if (_activeGroup is null) return;
        if (from == to || from < 0 || to < 0 || from >= Quotes.Count || to >= Quotes.Count) return;

        Quotes.Move(from, to);

        var codes = _activeGroup.Model.Codes;
        var invalid = codes.Where(c => !CodeMapper.IsValid(c)).ToList();

        codes.Clear();
        codes.AddRange(Quotes.Select(q => q.Code));
        codes.AddRange(invalid);

        Save();
    }

    /// <summary>Writes all groups to a file the user chose.</summary>
    public void ExportGroups(string path) =>
        GroupStore.ExportGroups(path, _config.Groups, _config.ActiveGroupId);

    /// <summary>Replaces all groups with an imported file's, then rebuilds the list.</summary>
    public void ImportGroups(string path)
    {
        var payload = GroupStore.ImportGroups(path);

        _config.Groups = payload.Groups;
        _config.ActiveGroupId = payload.ActiveGroupId;
        Save();

        Groups.Clear();
        foreach (var group in _config.Groups) Groups.Add(new GroupRow(group));

        var active = Groups.FirstOrDefault(g => g.Id == _config.ActiveGroupId) ?? Groups.FirstOrDefault();
        SetActive(active);
    }

    /// <summary>
    /// Pushes the stealth panel's rows. The panel rides the same 1s poll — it must
    /// not open a second one. It's a list because the panel can show several
    /// contracts at once (configurable row count).
    /// </summary>
    public event Action<IReadOnlyList<QuoteRow>>? StealthTick;

    private int _stealthIndex;

    /// <summary>The display name of a quoted row, for labelling a chart opened from it.</summary>
    public string? RowName(string code) =>
        _rows.TryGetValue(code, out var row) ? row.Name : null;

    /// <summary>Resolves a code to its full contract (for a secid, e.g. the panel's trend).</summary>
    public Contract? FindContract(string code) => _contracts.Find(code);

    /// <summary>
    /// The contracts the panel shows: up to <see cref="StealthConfig.Rows"/> of
    /// them, starting from the current one and wrapping. One row is the original
    /// single-line behaviour.
    /// </summary>
    public IReadOnlyList<QuoteRow> StealthRows()
    {
        if (Quotes.Count == 0) return Array.Empty<QuoteRow>();

        var count = Math.Clamp(_config.Stealth.Rows, 1, Quotes.Count);
        var start = ((_stealthIndex % Quotes.Count) + Quotes.Count) % Quotes.Count;

        var rows = new List<QuoteRow>(count);
        for (var i = 0; i < count; i++) rows.Add(Quotes[(start + i) % Quotes.Count]);
        return rows;
    }

    /// <summary>Steps through the active group's contracts, wrapping at the ends.</summary>
    public void StealthStep(int delta)
    {
        if (Quotes.Count == 0) return;

        _stealthIndex = ((_stealthIndex + delta) % Quotes.Count + Quotes.Count) % Quotes.Count;
        StealthTick?.Invoke(StealthRows());
    }

    /// <summary>Re-pushes the current rows, e.g. after the row count or fields change.</summary>
    public void StealthRefresh() => StealthTick?.Invoke(StealthRows());

    /// <summary>
    /// Steps to another group, which also re-points the poller: only one group is
    /// ever active, so the stealth panel and the main table always agree.
    /// </summary>
    public void StealthStepGroup(int delta)
    {
        if (Groups.Count == 0 || delta == 0) return;

        // Only cycle groups the user opted into the panel; step one such group per
        // call, in the given direction, and stay put if there is no other.
        var step = Math.Sign(delta);
        var i = Groups.IndexOf(_activeGroup!);
        if (i < 0) i = 0;

        for (var n = 0; n < Groups.Count; n++)
        {
            i = ((i + step) % Groups.Count + Groups.Count) % Groups.Count;
            if (!Groups[i].InPanel) continue;

            StealthSelectGroup(Groups[i]);
            return;
        }
    }

    /// <summary>
    /// Activates a specific group from the panel — what the panel's right-click
    /// list does, versus the PageUp/PageDown rotation above.
    ///
    /// <see cref="GroupRow.InPanel"/> is deliberately NOT consulted: it governs
    /// which groups the rotation stops at, and an explicit pick is not the
    /// rotation. Landing on the first contract mirrors stepping, so the panel
    /// always opens a group at its top.
    /// </summary>
    public void StealthSelectGroup(GroupRow? group)
    {
        if (group is null || ReferenceEquals(group, _activeGroup)) return;

        _stealthIndex = 0;
        SetActive(group);
    }

    /// <summary>Raised so the view can open/close the suggestion popup.</summary>
    public event Action<bool>? SuggestionsChanged;

    private void RefreshSuggestions()
    {
        Suggestions.Clear();

        var query = _newCode.Trim();
        if (query.Length >= 1)
        {
            // Same repository search the 合约查询 tab uses, same ranking.
            foreach (var hit in _contracts.Search(query).Take(10)) Suggestions.Add(hit);
        }

        Probe.Log($"RefreshSuggestions query='{query}' -> {Suggestions.Count}");
        SuggestionsChanged?.Invoke(Suggestions.Count > 0);
    }

    public void AddCode(string? code = null)
    {
        if (_activeGroup is null) return;

        var raw = (code ?? _newCode).Trim().ToUpperInvariant();
        if (!CodeMapper.IsValid(raw))
        {
            // Fall back to the best search hit so typing a name also works.
            var hit = Suggestions.FirstOrDefault();
            if (hit is null)
            {
                Error = $"无法识别：{raw}（试试 SH600519 或输入名称搜索）";
                return;
            }

            raw = hit.Code;
        }

        if (_activeGroup.Model.Codes.Contains(raw, StringComparer.OrdinalIgnoreCase))
        {
            Error = $"{raw} 已在该分组中";
            NewCode = "";
            return;
        }

        _activeGroup.Model.Codes.Add(raw);
        _activeGroup.RefreshCount();
        NewCode = "";
        Error = "";
        Save();
        RebuildRows();
    }

    /// <summary>Moves contracts out of the active group into another one.</summary>
    public void MoveCodesTo(IReadOnlyList<QuoteRow> rows, GroupRow? target) =>
        TransferCodes(rows, target, move: true);

    /// <summary>Adds contracts to another group, leaving them in this one too.</summary>
    public void CopyCodesTo(IReadOnlyList<QuoteRow> rows, GroupRow? target) =>
        TransferCodes(rows, target, move: false);

    /// <summary>
    /// Shared by move and copy: the only difference is whether the source keeps
    /// its copies. A target that already holds a code is not a failure — a move
    /// still takes it out of the source, which is what "move it there" means when
    /// it is already there.
    ///
    /// The whole batch is one save and one rebuild; doing it per contract made the
    /// list flash once per selected row.
    /// </summary>
    private void TransferCodes(IReadOnlyList<QuoteRow> rows, GroupRow? target, bool move)
    {
        if (rows.Count == 0 || target is null || _activeGroup is null) return;
        if (ReferenceEquals(target, _activeGroup)) return;

        foreach (var row in rows)
        {
            if (target.Model.Codes.Contains(row.Code, StringComparer.OrdinalIgnoreCase)) continue;
            target.Model.Codes.Add(row.Code);
        }

        target.RefreshCount();

        if (move)
        {
            var codes = rows.Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _activeGroup.Model.Codes.RemoveAll(codes.Contains);
            _activeGroup.RefreshCount();
        }

        Save();

        // Only a move changes what this group shows; a copy would just make the
        // list flash for nothing. Both change group membership, so the merged
        // poll target is refreshed either way (RebuildRows does it for the move).
        if (move) RebuildRows();
        else RefreshPollTarget();
    }

    /// <summary>This contract's note, empty when it has none.</summary>
    public string GetNote(string code) =>
        _config.Notes.TryGetValue(code, out var note) ? note : "";

    /// <summary>
    /// Stores a note against the contract code, so every group showing that
    /// contract picks it up. An empty note is removed rather than stored blank,
    /// which keeps groups.json from accumulating dead keys.
    /// </summary>
    public void SetNote(string code, string? note)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        var text = (note ?? "").Trim();

        if (text.Length == 0) _config.Notes.Remove(code);
        else _config.Notes[code] = text;

        if (_rows.TryGetValue(code, out var row)) row.Note = text;

        Save();
    }

    /// <summary>Removes contracts from the active group.</summary>
    public void RemoveCodes(IReadOnlyList<QuoteRow> rows)
    {
        if (rows.Count == 0 || _activeGroup is null) return;

        var codes = rows.Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _activeGroup.Model.Codes.RemoveAll(codes.Contains);
        _activeGroup.RefreshCount();
        Save();
        RebuildRows();
    }

    public void RemoveCode(QuoteRow? row)
    {
        if (row is not null) RemoveCodes(new[] { row });
    }

    private void RebuildRows()
    {
        Quotes.Clear();
        _rows.Clear();
        Error = "";
        SelectedRow = null;

        if (_activeGroup is null)
        {
            Status = "请先创建分组";
            _poller.SetTarget(null, null);
            _extraPoller.SetTarget(Array.Empty<(string, string)>());
            return;
        }

        foreach (var code in _activeGroup.Model.Codes.Where(CodeMapper.IsValid))
        {
            var row = new QuoteRow(code);
            FillMeta(row);
            row.Note = GetNote(code);
            _rows[code] = row;
            Quotes.Add(row);
        }

        Status = Quotes.Count == 0 ? "该分组还没有合约" : "等待行情…";
        _stealthIndex = 0;
        StealthTick?.Invoke(StealthRows());
        RefreshPollTarget();
        RefreshExtraPolling();
        ApplyBaselines();
        _ = RefreshBaselinesAsync();
    }

    /// <summary>
    /// Hands each row whatever baselines are already known — instant, no request.
    /// </summary>
    private void ApplyBaselines()
    {
        var known = _returns.Current;
        foreach (var (code, row) in _rows)
            row.SetBaselines(known.TryGetValue(code, out var b) ? b : null);
    }

    /// <summary>
    /// Tops up the baselines for contracts whose own market has rolled over. Costs
    /// one batch request, at most once per contract per trading day; silent on
    /// failure, since the columns simply stay blank.
    /// </summary>
    private async Task RefreshBaselinesAsync()
    {
        if (_activeGroup is null) return;

        var contracts = _activeGroup.Model.Codes
            .Where(CodeMapper.IsValid)
            .Select(c => _contracts.Find(c.ToUpperInvariant()))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToArray();

        if (contracts.Length == 0) return;

        var changed = await _returns.RefreshAsync(contracts, CancellationToken.None);
        if (changed) _dispatcher.InvokeAsync(ApplyBaselines);
    }

    /// <summary>
    /// Turns the secondary EastMoney poll on/off. Called by the view when a
    /// fund-flow/涨速 column is shown or hidden — nobody looking at those columns
    /// means no extra request goes out at all.
    /// </summary>
    public void SetFundFlowActive(bool active)
    {
        if (_fundFlowActive == active) return;
        _fundFlowActive = active;
        RefreshExtraPolling();
    }

    /// <summary>
    /// Points the secondary poll at the active group's A-shares — but only while a
    /// fund-flow column is on. Non-A-share codes are excluded (those fields don't
    /// exist for them), and an all-non-A group polls nothing.
    /// </summary>
    /// <summary>
    /// Re-evaluates the demand-driven polls after the stealth panel's field set
    /// changes — turning a fund-flow field on there must start the secondary poll
    /// even while the grid's fund-flow columns are all hidden.
    /// </summary>
    public void StealthSettingsChanged() => RefreshExtraPolling();

    /// <summary>
    /// Whether the stealth panel is configured to show any fund-flow field. Kept
    /// simple on purpose: it counts even while the panel window is closed — one
    /// batched request per tick is a fair price for not tracking window state here.
    /// </summary>
    private bool StealthWantsFundFlow =>
        _config.Stealth?.Fields.Any(f => f.Visible && StealthFields.IsFundFlow(f.Field)) == true;

    private void RefreshExtraPolling()
    {
        if ((!_fundFlowActive && !StealthWantsFundFlow) || _activeGroup is null)
        {
            _extraPoller.SetTarget(Array.Empty<(string, string)>());
            return;
        }

        var targets = _activeGroup.Model.Codes
            .Where(CodeMapper.IsValid)
            .Select(c => c.ToUpperInvariant())
            .Select(code => (code, contract: _contracts.Find(code)))
            .Where(x => x.contract is { Market: Market.SH or Market.SZ or Market.BJ })
            .Select(x => (x.code, x.contract!.EastMoneySecId))
            .ToArray();

        _extraPoller.SetTarget(targets);
    }

    private void OnExtraTick(IReadOnlyDictionary<string, QuoteExtra> extras) =>
        _dispatcher.InvokeAsync(() =>
        {
            foreach (var (code, extra) in extras)
                if (_rows.TryGetValue(code, out var row)) row.UpdateExtra(extra);
        });

    /// <summary>
    /// Resolves a row's 行业/地区/概念 from the shared contract list. A no-op while
    /// the code isn't in the loaded lists yet (a failed market, or before load);
    /// once matched, <see cref="QuoteRow.MetaResolved"/> stops further attempts.
    /// </summary>
    private void FillMeta(QuoteRow row)
    {
        var contract = _contracts.Find(row.Code);
        if (contract is null) return;

        row.SetMeta(contract.Industry, contract.Region, contract.Concepts);
    }

    /// <summary>
    /// Latest usable quote per code across ALL groups — what the per-group
    /// aggregate is computed from. Codes only ever accumulate; a contract removed
    /// from every group just stops being requested and its stale entry stops
    /// being read, which is cheaper than reference-counting removals.
    /// </summary>
    private readonly Dictionary<string, (double Pct, double FloatCap)> _agg =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Points the poller at the active group's codes PLUS every other group's,
    /// deduplicated — the active group still drives the table, the rest feed the
    /// per-group aggregates. One batched request per second either way (the
    /// client chunks above 800 codes, the measured URL limit).
    /// </summary>
    private void RefreshPollTarget()
    {
        if (_activeGroup is null)
        {
            _poller.SetTarget(null, null);
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var code in _activeGroup.Model.Codes.Where(CodeMapper.IsValid))
            if (seen.Add(code)) merged.Add(code);

        foreach (var group in Groups)
        {
            if (ReferenceEquals(group, _activeGroup)) continue;
            foreach (var code in group.Model.Codes.Where(CodeMapper.IsValid))
                if (seen.Add(code)) merged.Add(code);
        }

        _poller.SetTarget(_activeGroup.Id, merged);
    }

    /// <summary>Sidebar width in pixels; clamped to the XAML column's own bounds.</summary>
    public double GroupPaneWidth
    {
        get => _config.GroupPaneWidth is >= 170 and <= 480 ? _config.GroupPaneWidth : 210;
        set
        {
            if (Math.Abs(_config.GroupPaneWidth - value) < 0.5) return;
            _config.GroupPaneWidth = value;
            Save();
        }
    }

    /// <summary>整体涨跌幅口径：false=流通市值加权（昨收口径）, true=等权平均。</summary>
    public bool AggEqualWeight
    {
        get => _config.AggEqualWeight;
        set
        {
            if (_config.AggEqualWeight == value) return;
            _config.AggEqualWeight = value;
            Save();
            RecomputeGroupIndices();
        }
    }

    /// <summary>
    /// One group's aggregate move over its members' latest quotes.
    ///
    /// Cap mode follows the CSI index method reduced to a single day: weight each
    /// contract by its float cap AT YESTERDAY'S CLOSE — today's cap divided back
    /// by (1+pct) — because weighting by the live cap lets a riser inflate its
    /// own weight. Suspended contracts ride along at 0% like real indices keep
    /// them at last price; contracts with no quote yet or no cap fall back to the
    /// members' average weight so one HK/US line doesn't vanish from the basket.
    /// </summary>
    private double? GroupIndex(GroupRow group)
    {
        var pcts = new List<double>();
        var caps = new List<double>();   // 0 = unknown, resolved to the average below

        foreach (var code in group.Model.Codes)
        {
            if (!_agg.TryGetValue(code, out var q)) continue;
            pcts.Add(q.Pct);
            caps.Add(q.FloatCap > 0 ? q.FloatCap / (1 + q.Pct / 100) : 0);
        }

        if (pcts.Count == 0) return null;
        if (AggEqualWeight) return pcts.Average();

        var known = caps.Where(c => c > 0).ToArray();
        if (known.Length == 0) return pcts.Average();   // no caps at all: equal weight

        var fallback = known.Average();
        double num = 0, den = 0;
        for (var i = 0; i < pcts.Count; i++)
        {
            var w = caps[i] > 0 ? caps[i] : fallback;
            num += w * pcts[i];
            den += w;
        }

        return den > 0 ? num / den : null;
    }

    private void RecomputeGroupIndices()
    {
        foreach (var group in Groups) group.IndexPercent = GroupIndex(group);
    }

    private void OnTick(QuoteTick tick)
    {
        // The poller runs off the UI thread; marshal before touching bound state.
        _dispatcher.InvokeAsync(() =>
        {
            if (_activeGroup?.Id != tick.GroupId) return;

            foreach (var quote in tick.Quotes)
            {
                if (!_rows.TryGetValue(quote.Code, out var row)) continue;
                row.Update(quote);

                // Self-heal: a market whose contract list failed at startup then
                // recovered (rollover refetch) can fill in the board fields it lacked.
                if (!row.MetaResolved) FillMeta(row);
            }

            foreach (var quote in tick.Quotes)
            {
                if (quote.IsMissing || quote.Now <= 0) continue;
                _agg[quote.Code] = (quote.Percent, quote.FloatCap ?? 0);
            }
            RecomputeGroupIndices();

            Error = "";
            Status = $"{tick.At:HH:mm:ss} · 腾讯批量 · {tick.LatencyMs}ms · {tick.Quotes.Count} 个合约（全分组）";
            StealthTick?.Invoke(StealthRows());
            OnPropertyChanged(nameof(HasSelection));

            _flashTimer.Stop();
            _flashTimer.Start();
        });
    }

    private void OnFailed(string message) => _dispatcher.InvokeAsync(() => Error = message);

    public void Pause()
    {
        _poller.Stop();
        _extraPoller.Stop();
    }

    public void Resume()
    {
        _poller.Resume();
        _extraPoller.Resume();
    }

    private void Save() => _store.Save(_config);

    public async ValueTask DisposeAsync()
    {
        _flashTimer.Stop();
        Save();
        await _poller.DisposeAsync();
        await _extraPoller.DisposeAsync();
        _http.Dispose();
    }
}
