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
}

public sealed class QuotesViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _http;
    private readonly GroupStore _store;
    private readonly GroupConfig _config;
    private readonly QuotePoller _poller;
    private readonly ContractRepository _contracts;
    private readonly DispatcherTimer _flashTimer;

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
            SetActive(Groups.Count == 0 ? null : Groups[Math.Min(index, Groups.Count - 1)]);
        else
            Save();
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

            _stealthIndex = 0;
            SetActive(Groups[i]);
            return;
        }
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

    public void RemoveCode(QuoteRow? row)
    {
        if (row is null || _activeGroup is null) return;

        _activeGroup.Model.Codes.RemoveAll(c =>
            string.Equals(c, row.Code, StringComparison.OrdinalIgnoreCase));
        _activeGroup.RefreshCount();
        Save();
        RebuildRows();
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
            return;
        }

        foreach (var code in _activeGroup.Model.Codes.Where(CodeMapper.IsValid))
        {
            var row = new QuoteRow(code);
            FillMeta(row);
            _rows[code] = row;
            Quotes.Add(row);
        }

        Status = Quotes.Count == 0 ? "该分组还没有合约" : "等待行情…";
        _stealthIndex = 0;
        StealthTick?.Invoke(StealthRows());
        _poller.SetTarget(_activeGroup.Id, _activeGroup.Model.Codes);
    }

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

            Error = "";
            Status = $"{tick.At:HH:mm:ss} · 腾讯批量 · {tick.LatencyMs}ms · {tick.Quotes.Count} 个合约/请求";
            StealthTick?.Invoke(StealthRows());
            OnPropertyChanged(nameof(HasSelection));

            _flashTimer.Stop();
            _flashTimer.Start();
        });
    }

    private void OnFailed(string message) => _dispatcher.InvokeAsync(() => Error = message);

    public void Pause() => _poller.Stop();

    public void Resume() => _poller.Resume();

    private void Save() => _store.Save(_config);

    public async ValueTask DisposeAsync()
    {
        _flashTimer.Stop();
        Save();
        await _poller.DisposeAsync();
        _http.Dispose();
    }
}
