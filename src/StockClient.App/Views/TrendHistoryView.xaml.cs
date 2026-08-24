using System.Windows;
using System.Windows.Controls;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
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

    public TrendHistoryView() => InitializeComponent();

    /// <summary>One row of the contract dropdown: code plus resolved name.</summary>
    private sealed record CodeItem(string Code, string Name)
    {
        public override string ToString() => Name.Length > 0 ? $"{Code}  {Name}" : Code;
    }

    public void Init(QuotesViewModel vm, TrendCache cache, ContractRepository contracts)
    {
        _vm = vm;
        _cache = cache;
        _contracts = contracts;

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

    private void CodeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => FillDates();

    private void DateBox_DropDownOpened(object? sender, EventArgs e) => FillDates(keepSelection: true);

    private void FillDates(bool keepSelection = false)
    {
        if (CodeBox.SelectedItem is not CodeItem item || _cache is null)
        {
            DateBox.ItemsSource = null;
            return;
        }

        var previous = keepSelection ? DateBox.SelectedItem as DateOnly? : null;
        var dates = _cache.Dates(item.Code);
        DateBox.ItemsSource = dates;

        if (dates.Count == 0)
        {
            ShowEmpty("该合约还没有分时快照（收盘后由后台自动拉取）");
            return;
        }

        var restored = previous is { } p ? dates.IndexOf(p) : -1;
        DateBox.SelectedIndex = restored >= 0 ? restored : 0;
    }

    private void DateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CodeBox.SelectedItem is not CodeItem item || DateBox.SelectedItem is not DateOnly date
            || _cache is null)
            return;

        var series = _cache.TryLoad(item.Code, date);
        if (series is null)
        {
            ShowEmpty("快照文件缺失或损坏");
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

internal static class DateListExtensions
{
    public static int IndexOf(this IReadOnlyList<DateOnly> list, DateOnly value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }
}
