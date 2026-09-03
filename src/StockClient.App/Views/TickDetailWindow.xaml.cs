using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Standalone window listing a contract's WHOLE 逐笔成交 for the running/settled
/// day, opened from the trend panel's 更多 button. Filters by single-print size
/// (全部 / ≥100 / ≥1000 / ≥10000 手) and pages through the result; colouring
/// follows the shared 成交明细 scheme. Fetches its own copy (client-direct to
/// EastMoney, 沪深), so it stays live-independent of the panel; 刷新 re-pulls.
/// </summary>
public partial class TickDetailWindow : Window
{
    private const int PageSize = 500;

    private static readonly (string Label, long Min)[] FilterDefs =
    {
        ("全部", 0), ("≥100手", 100), ("≥1000手", 1000), ("≥10000手", 10000),
    };

    private readonly Contract _contract;
    private readonly EastMoneyDetailsClient _details;
    private readonly int _bigTradeWan;

    private int _decimals;
    private IReadOnlyList<TradeTick> _all = Array.Empty<TradeTick>();
    private List<TickRow> _filtered = new();
    private long _minVolume;
    private int _page;

    public TickDetailWindow(Contract contract, EastMoneyDetailsClient details, int decimals, int bigTradeWan)
    {
        InitializeComponent();

        _contract = contract;
        _details = details;
        _decimals = decimals > 0 ? decimals : 2;
        _bigTradeWan = bigTradeWan;

        Title = $"成交明细 · {contract.Name} {contract.Code}";
        TitleText.Text = $"{contract.Name}  {contract.Code}";

        BuildFilters();
        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    private void BuildFilters()
    {
        foreach (var (label, min) in FilterDefs)
        {
            var button = new ToggleButton
            {
                Content = label,
                Tag = min,
                FontSize = 12,
                Padding = new Thickness(11, 3, 11, 3),
                Margin = new Thickness(FilterButtons.Children.Count == 0 ? 0 : 6, 0, 0, 0),
                IsChecked = min == _minVolume,
            };
            button.Click += (_, _) => SetFilter((long)button.Tag);
            FilterButtons.Children.Add(button);
        }
    }

    private void SetFilter(long min)
    {
        _minVolume = min;
        foreach (var c in FilterButtons.Children)
            if (c is ToggleButton t) t.IsChecked = (long)t.Tag == min;
        _page = 0;
        ApplyFilter();
    }

    private async Task LoadAsync()
    {
        CountText.Text = "加载中…";
        RefreshButton.IsEnabled = false;
        try
        {
            var snap = await _details.FetchAsync(_contract, 100_000, CancellationToken.None);
            if (snap is not null)
            {
                _all = snap.Ticks;
                if (snap.Decimals > 0) _decimals = snap.Decimals;
            }
            else
            {
                _all = Array.Empty<TradeTick>();
            }
        }
        catch (Exception)
        {
            _all = Array.Empty<TradeTick>();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // Newest first (matches the reference tape), filtered by single-print 手.
        _filtered = new List<TickRow>();
        for (var i = _all.Count - 1; i >= 0; i--)
        {
            var t = _all[i];
            if (t.Volume < _minVolume) continue;
            var big = TradeColors.IsBig(t, _bigTradeWan);
            _filtered.Add(new TickRow(
                t.Time,
                t.Price.ToString("F" + _decimals),
                t.Volume.ToString(),
                t.Side switch { TradeSide.Buy => "买", TradeSide.Sell => "卖", _ => "中" },
                TradeColors.For(t.Side, big),
                big ? FontWeights.SemiBold : FontWeights.Normal));
        }
        RenderPage();
    }

    private void RenderPage()
    {
        var total = _filtered.Count;
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);

        Grid.ItemsSource = _filtered.GetRange(_page * PageSize, Math.Min(PageSize, total - _page * PageSize));

        CountText.Text = _all.Count == 0
            ? "无数据（仅沪深；非交易时段可能为空）"
            : $"共 {_all.Count} 笔 · 筛选 {total} 笔";
        PageText.Text = $"第 {_page + 1}/{pages} 页";

        FirstButton.IsEnabled = PrevButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = LastButton.IsEnabled = _page < pages - 1;
    }

    private void First_Click(object sender, RoutedEventArgs e) { _page = 0; RenderPage(); }
    private void Prev_Click(object sender, RoutedEventArgs e) { _page--; RenderPage(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _page++; RenderPage(); }
    private void Last_Click(object sender, RoutedEventArgs e) { _page = int.MaxValue; RenderPage(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    /// <summary>One detail row, shaped for the DataGrid columns.</summary>
    public sealed record TickRow(
        string Time, string Price, string Volume, string SideText, Brush Fg, FontWeight Weight);
}
