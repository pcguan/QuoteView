using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Standalone window listing a contract's WHOLE 逐笔成交 for the day, opened from
/// the trend panel's 更多 button, styled to the reference: a 当日行情 header
/// (拿不到的字段填 -), 大单 filters by single-print 手数, 倒序 toggle, and paging.
/// 成交价 is 红涨/绿跌 with ↑↓; 手数 is a soft neutral except 大单 (外盘紫 / 内盘青),
/// the one scheme shared with the panel tape and the historical replay.
/// Fetches its own copy (client-direct, 沪深); 刷新 re-pulls the ticks.
/// </summary>
public partial class TickDetailWindow : Window
{
    private const int PageSize = 500;

    private static readonly (string Label, long Min)[] FilterDefs =
    {
        ("全部", 0), ("≥100", 100), ("≥200", 200), ("≥500", 500),
        ("≥1000", 1000), ("≥2000", 2000), ("≥5000", 5000), ("≥10000", 10000),
    };

    private readonly Contract _contract;
    private readonly Quote? _quote;
    private readonly EastMoneyDetailsClient _details;
    private readonly int _bigTradeWan;

    private int _decimals;
    private IReadOnlyList<TradeTick> _all = Array.Empty<TradeTick>();
    private double _prePrice;
    private List<TickRow> _rows = new();     // all, chronological, coloured
    private List<TickRow> _view = new();     // filtered + ordered
    private long _minVolume;
    private int _page;

    public TickDetailWindow(Contract contract, Quote? quote, EastMoneyDetailsClient details,
        int decimals, int bigTradeWan)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);

        _contract = contract;
        _quote = quote;
        _details = details;
        _decimals = decimals > 0 ? decimals : 2;
        _bigTradeWan = bigTradeWan;

        Title = $"成交明细 · {contract.Name} {contract.Code}";
        TitleText.Text = $"{contract.Name}  {contract.Code}";

        RenderStats(quote);
        BuildFilters();
        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    // --- 当日行情 header -------------------------------------------------------

    private void RenderStats(Quote? q)
    {
        if (q is null)
        {
            PriceText.Text = "-";
            ChangeText.Text = PercentText.Text = "";
            foreach (var t in new[] { OpenText, PrevText, HighText, LowText, LimitUpText, LimitDownText,
                         TurnoverText, VolRatioText, VolumeText, AmountText, PeText, PbText,
                         TotalCapText, FloatCapText })
                t.Text = "-";
            return;
        }

        var dec = _decimals;
        var moodBrush = q.Percent > 0 ? TradeColors.Up : q.Percent < 0 ? TradeColors.Down : TradeColors.Flat;

        PriceText.Text = q.Now > 0 ? q.Now.ToString("F" + dec) : "-";
        PriceText.Foreground = moodBrush;
        ChangeText.Text = Signed(q.Change, dec);
        PercentText.Text = Signed(q.Percent, 2) + "%";
        ChangeText.Foreground = PercentText.Foreground = moodBrush;

        OpenText.Text = Price(q.Open, dec);
        PrevText.Text = Price(q.Yesterday, dec);
        HighText.Text = Price(q.High, dec);
        LowText.Text = Price(q.Low, dec);
        LimitUpText.Text = Price(q.LimitUp, dec);
        LimitDownText.Text = Price(q.LimitDown, dec);
        TurnoverText.Text = q.TurnoverRate is { } tr ? tr.ToString("F2") + "%" : "-";
        VolRatioText.Text = q.VolumeRatio is { } vr ? vr.ToString("F2") : "-";
        VolumeText.Text = q.Volume is { } v and > 0 ? Compact(v) + "手" : "-";
        AmountText.Text = q.Amount is { } a and > 0 ? Compact(a) : "-";
        PeText.Text = q.PeTtm is { } pe ? pe.ToString("F2") : "-";
        PbText.Text = q.Pb is { } pb ? pb.ToString("F2") : "-";
        TotalCapText.Text = q.TotalCap is { } tc and > 0 ? Compact(tc) : "-";
        FloatCapText.Text = q.FloatCap is { } fc and > 0 ? Compact(fc) : "-";
    }

    private static string Price(double? v, int dec) => v is { } x and > 0 ? x.ToString("F" + dec) : "-";
    private static string Signed(double v, int dec) => (v >= 0 ? "+" : "") + v.ToString("F" + dec);

    private static string Compact(double v) =>
        v >= 1e8 ? $"{v / 1e8:F2}亿" : v >= 1e4 ? $"{v / 1e4:F2}万" : $"{v:F0}";

    // --- filters + ordering ----------------------------------------------------

    private void BuildFilters()
    {
        foreach (var (label, min) in FilterDefs)
        {
            var button = new ToggleButton
            {
                Content = label,
                Tag = min,
                FontSize = 12,
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(FilterButtons.Children.Count == 0 ? 0 : 5, 0, 0, 0),
                IsChecked = min == _minVolume,
                ToolTip = min == 0 ? "全部成交" : $"单笔 ≥ {min} 手",
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

    private void Reverse_Click(object sender, RoutedEventArgs e) { _page = 0; ApplyFilter(); }

    // --- data ------------------------------------------------------------------

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
                _prePrice = snap.PrePrice;
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

        BuildRows();
        ApplyFilter();
    }

    /// <summary>Builds every row once, chronological, with price direction and 大单 colour.</summary>
    private void BuildRows()
    {
        _rows = new List<TickRow>(_all.Count);
        var carry = TradeColors.Flat;
        var prev = _prePrice;
        foreach (var t in _all)
        {
            var (priceFg, arrow) = TradeColors.PriceLook(t.Price, prev, ref carry);
            prev = t.Price;
            var big = TradeColors.IsBig(t, _bigTradeWan);
            _rows.Add(new TickRow(
                t.Time,
                t.Price.ToString("F" + _decimals) + arrow,
                t.Volume.ToString(),
                t.Volume,
                priceFg,
                TradeColors.Volume(t.Side, big)));
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<TickRow> q = _minVolume > 0 ? _rows.Where(r => r.Vol >= _minVolume) : _rows;
        _view = ReverseBox.IsChecked == true ? q.Reverse().ToList() : q.ToList();
        RenderPage();
    }

    private void RenderPage()
    {
        var total = _view.Count;
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);

        Grid.ItemsSource = _view.GetRange(_page * PageSize, Math.Min(PageSize, total - _page * PageSize));

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

    /// <summary>One detail row. <see cref="Vol"/> backs filtering (not shown).</summary>
    public sealed record TickRow(
        string Time, string Price, string Volume, long Vol, Brush PriceFg, Brush VolFg);
}
