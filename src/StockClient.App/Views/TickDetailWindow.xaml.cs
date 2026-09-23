using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.App.Services;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Standalone window listing a contract's WHOLE 逐笔成交 for the day, opened from
/// the trend panel's 更多 button, styled to the reference: a 当日行情 header
/// (拿不到的字段填 -), 大单 filters by single-print 手数, 倒序 toggle, and paging.
/// 成交价 is 红涨/绿跌 vs 昨收; 手数 is a soft neutral except 大单 (外盘紫 / 内盘青),
/// the one scheme shared with the panel tape and the historical replay.
///
/// Live mode reads the chart panel's already-polled in-memory tape
/// (<see cref="KlineViewModel.Ticks"/>) rather than fetching its own copy — the
/// panel is the single 逐笔 poller, so a second request here was redundant and an
/// extra hit on the rate-limited details source. It AUTO-refreshes on every panel
/// poll (~1s, no button, scroll preserved), so it stays as live as the panel tape.
/// Pinned to the vm it opened on, so a later contract switch in the chart leaves
/// this window on its own contract. Historical mode instead loads an archived day
/// from the server and switches days via the date dropdown (static, no auto-poll).
/// </summary>
public partial class TickDetailWindow : Window
{
    private const int PageSize = 1000;   // shown across two columns (500 each) → half the page turns

    private static readonly (string Label, long Min)[] FilterDefs =
    {
        ("全部", 0), ("≥100", 100), ("≥200", 200), ("≥500", 500),
        ("≥1000", 1000), ("≥2000", 2000), ("≥5000", 5000), ("≥10000", 10000),
    };

    private readonly KlineViewModel? _vm;             // live mode: in-memory tape source
    private readonly AccountSession? _session;        // historical mode: server tape source
    private readonly bool _historical;
    private readonly Contract _contract;
    private readonly int _bigTradeWan;
    private readonly DateOnly _initialDate;
    private readonly string _emptyHint;

    private int _decimals;
    private int _ticksRequest;                        // guards historical async loads
    private IReadOnlyList<TradeTick> _all = Array.Empty<TradeTick>();
    private double _prePrice;
    private List<TickRow> _rows = new();     // all, chronological, coloured
    private List<TickRow> _view = new();     // filtered + ordered
    private long _minVolume;
    private int _page;

    public TickDetailWindow(KlineViewModel vm, int decimals, int bigTradeWan)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);
        WindowPlacement.Attach(this, "tickdetail");
        WindowMinimizeGesture.Attach(this);   // double-click a blank area to minimize

        _vm = vm;
        _contract = vm.Contract;
        _decimals = decimals > 0 ? decimals : 2;
        _bigTradeWan = bigTradeWan;
        _emptyHint = "无数据（仅沪深；非交易时段可能为空）";

        Title = $"成交明细 · {_contract.Name} {_contract.Code}";
        TitleText.Text = $"{_contract.Name}  {_contract.Code}";

        BuildFilters();
        Reload();   // seed from the panel's already-polled tape — no own request

        // Auto-refresh on EVERY panel poll (~1s), same cadence as the panel tape —
        // still reads the in-memory cache (no extra request). Scroll position is
        // preserved across the refresh (see Reload) so it flows instead of freezing
        // until a manual click, which is what made it feel tens-of-seconds stale.
        _vm.TicksUpdated += OnVmTicks;
        Closed += (_, _) => _vm.TicksUpdated -= OnVmTicks;
    }

    private void OnVmTicks() => Dispatcher.Invoke(Reload);

    /// <summary>Historical variant: the same window, but reading one archived
    /// session's 成交明细 from the server, with a date dropdown to switch days.
    /// 沪深 only. Opened from 历史分时对比's 更多 button.</summary>
    public TickDetailWindow(Contract contract, AccountSession session, DateOnly initial, int bigTradeWan)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);
        WindowPlacement.Attach(this, "tickdetail-hist");
        WindowMinimizeGesture.Attach(this);

        _historical = true;
        _session = session;
        _contract = contract;
        _initialDate = initial;
        _decimals = 2;
        _bigTradeWan = bigTradeWan;
        _emptyHint = "该日无归档，或未登录";

        Title = $"历史成交明细 · {contract.Name} {contract.Code}";
        TitleText.Text = $"{contract.Name}  {contract.Code}";

        BuildFilters();
        DateBox.Visibility = Visibility.Visible;
        Loaded += async (_, _) => await LoadDatesAsync();
    }

    /// <summary>Fills the date dropdown from the server's archived-tape dates
    /// (<see cref="AccountSession.TickDatesAsync"/>) and selects the day the history
    /// view was on, else the newest.</summary>
    private async Task LoadDatesAsync()
    {
        if (_session is null) return;
        CountText.Text = "加载日期…";

        IReadOnlyList<DateOnly> dates;
        try { dates = await _session.TickDatesAsync(_contract.Code); }
        catch { dates = Array.Empty<DateOnly>(); }

        DateBox.ItemsSource = dates;
        var pick = dates.Contains(_initialDate) ? _initialDate
            : dates.Count > 0 ? dates[0] : (DateOnly?)null;
        if (pick is { } d) DateBox.SelectedItem = d;   // fires DateBox_SelectionChanged → load
        else CountText.Text = _session.IsSignedIn ? "无归档日期" : "登录后可取服务端归档";
    }

    private void DateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DateBox.SelectedItem is DateOnly d) _ = LoadDateAsync(d);
    }

    /// <summary>Loads one archived day's tape from the server, guarded so a slow
    /// answer for an old date can't overwrite a newer pick.</summary>
    private async Task LoadDateAsync(DateOnly date)
    {
        if (_session is null) return;
        var req = ++_ticksRequest;
        CountText.Text = "加载中…";

        TradeTickSnapshot? snap = null;
        try { snap = await _session.TicksAsync(_contract.Code, date); }
        catch { /* leave snap null → empty state below */ }

        if (req != _ticksRequest) return;   // superseded by a newer date pick

        if (snap is not null && snap.Ticks.Count > 0)
        {
            _all = snap.Ticks;
            _prePrice = snap.PrePrice;
            _decimals = snap.Decimals > 0 ? snap.Decimals : 2;
            RenderStats(QuoteFromTicks(_contract, snap));
        }
        else
        {
            _all = Array.Empty<TradeTick>();
            _prePrice = 0;
            RenderStats(null);
        }
        BuildRows();
        ApplyFilter();
    }

    /// <summary>A day-summary quote synthesized from the archived ticks so the
    /// header shows 今开/最高/最低/现价/涨跌/量额/内外盘 (fields the ticks can prove);
    /// the rest (涨停/换手/PE/市值…) have no tick source and stay "-".</summary>
    private static Quote QuoteFromTicks(Contract contract, TradeTickSnapshot snap)
    {
        var ticks = snap.Ticks;
        double open = ticks[0].Price, last = ticks[^1].Price, hi = open, lo = open;
        double vol = 0, amt = 0, outer = 0, inner = 0;
        foreach (var t in ticks)
        {
            if (t.Price > hi) hi = t.Price;
            if (t.Price < lo) lo = t.Price;
            vol += t.Volume;
            amt += t.Amount;
            if (t.Side == TradeSide.Buy) outer += t.Volume;
            else if (t.Side == TradeSide.Sell) inner += t.Volume;
        }
        var pre = snap.PrePrice;
        return new Quote
        {
            Code = contract.Code,
            Name = contract.Name,
            Now = last,
            Yesterday = pre,
            Open = open,
            High = hi,
            Low = lo,
            Change = pre > 0 ? last - pre : 0,
            Percent = pre > 0 ? (last / pre - 1) * 100 : 0,
            Volume = vol,
            Amount = amt,
            OuterVolume = outer,
            InnerVolume = inner,
            Time = ticks[^1].Time,
        };
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

    /// <summary>Re-reads the panel's in-memory tape and re-renders, preserving the
    /// reader's scroll position. No request — the chart panel keeps this tape warm
    /// on its own poll.</summary>
    private void Reload()
    {
        var (atL, offL) = SnapScroll(GridL);
        var (atR, offR) = SnapScroll(GridR);

        _all = _vm!.Ticks;
        _prePrice = _vm.TickPrePrice;
        RenderStats(_vm.Live);
        BuildRows();
        ApplyFilter();   // resets both grids' ItemsSource (and their scroll)

        // Newest-first at the top stays pinned to the newest print; a reader who
        // scrolled down into history holds their place instead of jumping.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreScroll(GridL, atL, offL);
            RestoreScroll(GridR, atR, offR);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static (bool AtTop, double Offset) SnapScroll(DependencyObject grid)
    {
        var sv = FindScroll(grid);
        return sv is null ? (true, 0) : (sv.VerticalOffset <= 4, sv.VerticalOffset);
    }

    private static void RestoreScroll(DependencyObject grid, bool atTop, double offset)
    {
        var sv = FindScroll(grid);
        if (sv is null) return;
        if (atTop) sv.ScrollToTop(); else sv.ScrollToVerticalOffset(offset);
    }

    /// <summary>A DataGrid's inner ScrollViewer, for preserving scroll on refresh.</summary>
    private static ScrollViewer? FindScroll(DependencyObject? d)
    {
        if (d is null) return null;
        if (d is ScrollViewer sv) return sv;
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++)
        {
            var r = FindScroll(VisualTreeHelper.GetChild(d, i));
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>Builds every row once, chronological, with price direction and 大单 colour.</summary>
    private void BuildRows()
    {
        _rows = new List<TickRow>(_all.Count);
        foreach (var t in _all)
        {
            var (priceFg, arrow) = TradeColors.PriceLook(t.Price, _prePrice);
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

        var start = _page * PageSize;
        var count = Math.Max(0, Math.Min(PageSize, total - start));
        var pageRows = _view.GetRange(start, count);
        var half = (pageRows.Count + 1) / 2;                       // left column takes the first half
        GridL.ItemsSource = pageRows.GetRange(0, half);
        GridR.ItemsSource = pageRows.GetRange(half, pageRows.Count - half);

        CountText.Text = _all.Count == 0
            ? _emptyHint
            : $"共 {_all.Count} 笔 · 筛选 {total} 笔";
        PageText.Text = $"第 {_page + 1}/{pages} 页";

        FirstButton.IsEnabled = PrevButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = LastButton.IsEnabled = _page < pages - 1;
    }

    private void First_Click(object sender, RoutedEventArgs e) { _page = 0; RenderPage(); }
    private void Prev_Click(object sender, RoutedEventArgs e) { _page--; RenderPage(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _page++; RenderPage(); }
    private void Last_Click(object sender, RoutedEventArgs e) { _page = int.MaxValue; RenderPage(); }

    /// <summary>One detail row. <see cref="Vol"/> backs filtering (not shown).</summary>
    public sealed record TickRow(
        string Time, string Price, string Volume, long Vol, Brush PriceFg, Brush VolFg);
}
