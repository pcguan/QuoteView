using System.Windows;
using System.Windows.Controls;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.App.Services;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// 历史分时查询: group → contract → date, reading the snapshots the after-close
/// sweep (and ordinary chart viewing) persisted to <see cref="TrendCache"/>.
/// Purely a disk reader — this page never makes a request.
/// </summary>
public partial class TrendHistoryView : UserControl
{
    private QuotesViewModel? _vm;
    private TrendCache? _cache;
    private ContractRepository? _contracts;
    private AccountSession? _session;

    public TrendHistoryView() => InitializeComponent();

    /// <summary>One row of the contract dropdown: code plus resolved name.</summary>
    private sealed record CodeItem(string Code, string Name)
    {
        public override string ToString() => Name.Length > 0 ? $"{Code}  {Name}" : Code;
    }

    public void Init(QuotesViewModel vm, TrendCache cache, ContractRepository contracts,
        AccountSession session)
    {
        _vm = vm;
        _cache = cache;
        _contracts = contracts;
        _session = session;

        // ObservableCollection: groups added/renamed later show up on their own.
        GroupBox.ItemsSource = vm.Groups;
        if (vm.Groups.Count > 0) GroupBox.SelectedIndex = 0;
        else ShowEmpty("还没有分组");
    }

    // Rebuilt on open rather than kept in sync: membership changes in the main
    // tab and there is no change event per group to hang a refresh on.
    private void GroupBox_DropDownOpened(object? sender, EventArgs e)
    {
        // ItemsSource is the live collection; nothing to do — the handler exists
        // so a stale-looking dropdown has an obvious place to fix if it ever is.
    }

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => FillCodes();

    private void FillCodes()
    {
        if (GroupBox.SelectedItem is not GroupRow group || _contracts is null)
        {
            CodeBox.ItemsSource = null;
            ShowEmpty("还没有分组");
            return;
        }

        var items = group.Model.Codes
            .Where(c => c.StartsWith("SH", StringComparison.OrdinalIgnoreCase)
                        || c.StartsWith("SZ", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => new CodeItem(c, _contracts.Find(c)?.Name ?? ""))
            .ToArray();

        CodeBox.ItemsSource = items;

        if (items.Length > 0) CodeBox.SelectedIndex = 0;
        else ShowEmpty("该分组没有沪深合约（快照仅覆盖沪深交易所）");
    }

    private void CodeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _ = FillDatesAsync();

    private void DateBox_DropDownOpened(object? sender, EventArgs e) =>
        _ = FillDatesAsync(keepSelection: true);

    /// <summary>
    /// Guards against a slow /dates answer landing after the user has already
    /// switched contracts: only the newest request may touch the dropdown.
    /// </summary>
    private int _datesRequest;

    private async Task FillDatesAsync(bool keepSelection = false)
    {
        if (CodeBox.SelectedItem is not CodeItem item || _cache is null || _session is null)
        {
            DateBox.ItemsSource = null;
            return;
        }

        var request = ++_datesRequest;
        var previous = keepSelection ? DateBox.SelectedItem as DateOnly? : null;

        // Local first so the list is usable immediately; the server's answer is
        // merged in when (and if) it arrives — offline just means local-only.
        var local = _cache.Dates(item.Code);
        var remote = await _session.DatesAsync(item.Code);
        if (request != _datesRequest) return;

        var dates = local.Concat(remote).Distinct().OrderByDescending(d => d).ToArray();
        DateBox.ItemsSource = dates;

        if (dates.Length == 0)
        {
            ShowEmpty(_session.IsSignedIn
                ? "该合约还没有分时快照（收盘后由服务端统一拉取）"
                : "本地无快照；登录后可查询服务端归档（右上角「登录」）");
            return;
        }

        var restored = previous is { } p ? Array.IndexOf(dates, p) : -1;
        DateBox.SelectedIndex = restored >= 0 ? restored : 0;
    }

    private void DateBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _ = LoadSelectedAsync();

    private int _loadRequest;

    private async Task LoadSelectedAsync()
    {
        if (CodeBox.SelectedItem is not CodeItem item || DateBox.SelectedItem is not DateOnly date
            || _cache is null || _session is null)
            return;

        var request = ++_loadRequest;

        // Local cache first — every series fetched from the server is written
        // back into it, so each snapshot crosses the network once per machine.
        var series = _cache.TryLoad(item.Code, date);
        if (series is null)
        {
            ShowEmpty("加载中…");
            series = await _session.TrendAsync(item.Code, date);
            if (request != _loadRequest) return;

            if (series is not null) _cache.Save(series, date);
        }

        if (series is null)
        {
            ShowEmpty(_session.IsSignedIn ? "该日快照获取失败(服务端不可达或缺失)" : "本地无此日快照；登录后可从服务端获取");
            return;
        }

        Empty.Visibility = Visibility.Collapsed;
        Chart.Visibility = Visibility.Visible;
        Chart.SetSeries(series);
    }

    private void ShowEmpty(string message)
    {
        Chart.Visibility = Visibility.Collapsed;
        Empty.Text = message;
        Empty.Visibility = Visibility.Visible;
    }
}
