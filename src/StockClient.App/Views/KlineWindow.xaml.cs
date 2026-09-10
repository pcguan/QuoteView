using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// A standalone chart window for one contract, opened by double-clicking a row in
/// either grid; several can be open at once. Shows today's intraday trend or the
/// day/week/month candles, switched by the period buttons.
/// </summary>
public partial class KlineWindow : Window
{
    private static readonly (KlinePeriod Period, string Label)[] Periods =
    {
        (KlinePeriod.Day, "日K"),
        (KlinePeriod.Week, "周K"),
        (KlinePeriod.Month, "月K"),
    };

    private static readonly (KlineAdjust Adjust, string Label)[] Adjusts =
    {
        (KlineAdjust.Qfq, "前复权"),
        (KlineAdjust.None, "不复权"),
        (KlineAdjust.Hfq, "后复权"),
    };

    private KlineViewModel _vm;
    private readonly Func<Contract, KlineViewModel> _factory;
    private readonly ContractGroup[] _groups;
    private ContractItem[] _contractItems = Array.Empty<ContractItem>();
    private System.ComponentModel.ICollectionView? _contractView;
    private bool _syncing;   // programmatic group/contract/text change in progress
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;

    /// <summary>The current contract + view, for persisting/reopening the window.</summary>
    public string Code => _vm.Contract.Code;
    public bool IsTrendView => _vm.IsTrend;
    public KlinePeriod PeriodValue => _vm.Period;
    public KlineAdjust AdjustValue => _vm.Adjust;

    public KlineWindow(KlineViewModel vm, IReadOnlyList<ContractGroup> groups,
        Func<Contract, KlineViewModel> factory,
        (bool Trend, KlinePeriod Period, KlineAdjust Adjust)? initialView = null)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);
        // Placement is per-contract, not one shared "kline" slot, so each chart
        // reopens where that contract's chart last sat instead of all stacking.
        // Restore for the contract we open with; save under whatever contract is
        // showing at close (an in-place switch leaves the window put and simply
        // re-homes the new contract here).
        WindowPlacement.Restore(this, PlaceKey(vm.Contract.Code));
        Closing += (_, _) => WindowPlacement.Save(this, PlaceKey(_vm.Contract.Code));
        WindowMinimizeGesture.Attach(this);

        _factory = factory;
        _groups = groups.ToArray();
        GroupBox.ItemsSource = _groups;
        _vm = vm;   // before BuildToggles / Bind, both of which read it
        // Restored view: set 复权 up front so BuildToggles builds the row checked
        // on the right one (it reads _vm.Adjust once, at build time). The setter
        // may fire a reload; the mode applied on Loaded supersedes it.
        if (initialView is { } iv0) _vm.Adjust = iv0.Adjust;

        BuildPeriodButtons();
        BuildToggles(AdjustButtons, Adjusts.Select(a => (object)a.Adjust).ToArray(),
            Adjusts.Select(a => a.Label).ToArray(), () => _vm.Adjust, a => _vm.Adjust = (KlineAdjust)a);
        InitTape();

        // Second-precision wall clock, shown top-right of the 分时 chart (handy for
        // eyeballing how far the data lags the current time). Ticks every second
        // regardless of the 3s data refresh.
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer.Start();

        Bind();
        if (initialView is { } iv)
        {
            // Reopen in the exact view the window was persisted with (复权 already
            // set above). ShowTrend/ShowKline supersede the default Day load.
            Loaded += (_, _) =>
            {
                if (iv.Trend) _vm.ShowTrend();
                else if (iv.Period != KlinePeriod.Day) _vm.ShowKline(iv.Period);
                else _ = _vm.ReloadAsync();
                RefreshPeriodStates();
            };
        }
        else
        {
            Loaded += async (_, _) => await _vm.ReloadAsync();
        }
        Closed += (_, _) => { _clockTimer.Stop(); Unbind(); };
    }

    /// <summary>Wires the current <see cref="_vm"/> to the view and reflects it in
    /// the contract picker / title.</summary>
    private void Bind()
    {
        _vm.Loaded += OnKlineLoaded;
        _vm.LiveUpdated += OnLiveUpdated;
        _vm.Refreshed += OnKlineRefreshed;
        _vm.TrendLoaded += OnTrendLoaded;
        _vm.TicksUpdated += OnTicksLoaded;
        _vm.PropertyChanged += OnVmPropertyChanged;

        Title = _vm.Title;
        SelectCurrent();
        ApplyMode();
    }

    private void Unbind()
    {
        _vm.Loaded -= OnKlineLoaded;
        _vm.LiveUpdated -= OnLiveUpdated;
        _vm.Refreshed -= OnKlineRefreshed;
        _vm.TrendLoaded -= OnTrendLoaded;
        _vm.TicksUpdated -= OnTicksLoaded;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Dispose();
    }

    /// <summary>Point the two pickers at <see cref="_vm"/>'s contract: its group,
    /// then the contract inside it. Guarded so neither programmatic selection
    /// re-triggers a switch or a filter.</summary>
    private void SelectCurrent()
    {
        _syncing = true;
        var group = _groups.FirstOrDefault(
                        g => g.Contracts.Any(c => SameCode(c.Code, _vm.Contract.Code)))
                    ?? _groups.FirstOrDefault();
        GroupBox.SelectedItem = group;
        PopulateContracts(group);
        ContractBox.SelectedItem = _contractItems.FirstOrDefault(
            i => SameCode(i.Contract.Code, _vm.Contract.Code));
        _syncing = false;
    }

    /// <summary>Refill the contract list for a group behind a fresh collection
    /// view, and clear the filter box so the new group shows in full.</summary>
    private void PopulateContracts(ContractGroup? group)
    {
        _contractItems = (group?.Contracts ?? Array.Empty<Contract>())
            .Select(c => new ContractItem(c)).ToArray();
        _contractView = System.Windows.Data.CollectionViewSource.GetDefaultView(_contractItems);
        ContractBox.ItemsSource = _contractView;
        FilterBox.Text = string.Empty;   // Filter_TextChanged no-ops under _syncing
    }

    private static bool SameCode(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-contract placement key, so each contract's chart remembers its
    /// own position/size independently.</summary>
    private static string PlaceKey(string code) => "kline:" + code.ToUpperInvariant();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.PropertyName == nameof(KlineViewModel.IsTrend)) ApplyMode();
        else if (e.PropertyName is nameof(KlineViewModel.Loading) or nameof(KlineViewModel.Error))
            UpdateStatus();
    });

    /// <summary>Pick a group: refill the contract list. If it holds the current
    /// contract, keep that selected; otherwise leave the box empty and drop it
    /// open to invite a pick —选分组再选合约, not "silently chart the first one".</summary>
    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || GroupBox.SelectedItem is not ContractGroup group) return;

        _syncing = true;
        PopulateContracts(group);
        var cur = _contractItems.FirstOrDefault(i => SameCode(i.Contract.Code, _vm.Contract.Code));
        ContractBox.SelectedItem = cur;
        _syncing = false;

        if (cur is null) ContractBox.IsDropDownOpen = true;   // invite a pick
    }

    /// <summary>Substring-filter the current group's contract list by name or code
    /// as the user types — so a big group doesn't mean scrolling forever. Empty
    /// text shows the whole group again.
    ///
    /// It deliberately does NOT open the dropdown: a ComboBox's dropdown grabs the
    /// keyboard, which fought the user still typing here. The filter just narrows
    /// the list; opening it (click, or ↓/Enter in this box) shows the narrowed
    /// result.</summary>
    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _contractView is null) return;

        var q = FilterBox.Text.Trim();
        _contractView.Filter = q.Length == 0
            ? null
            : o => o is ContractItem it
                   && (it.Contract.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || it.Contract.Code.Contains(q, StringComparison.OrdinalIgnoreCase));

        // Filtering the charted contract out of view blanks the box; when the
        // filter clears again, put it back so the box shows what's on screen.
        if (q.Length == 0)
        {
            var cur = _contractItems.FirstOrDefault(i => SameCode(i.Contract.Code, _vm.Contract.Code));
            if (!ReferenceEquals(ContractBox.SelectedItem, cur))
            {
                _syncing = true;
                ContractBox.SelectedItem = cur;
                _syncing = false;
            }
        }
    }

    /// <summary>Switch the charted contract in place — no need to go back to the
    /// panel and right-click another. Keeps the current mode (分时 vs 日/周/月K).</summary>
    private void Contract_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ContractBox.SelectedItem is not ContractItem item) return;
        if (SameCode(item.Contract.Code, _vm.Contract.Code)) return;

        var wasTrend = _vm.IsTrend;
        var period = _vm.Period;
        var adjust = _vm.Adjust;

        Unbind();
        _vm = _factory(item.Contract);
        _vm.Adjust = adjust;   // keeps the 复权 choice; ReloadAsync below supersedes any it fires
        Bind();                // Bind → SelectCurrent re-points both pickers, clearing the filter

        // Keep the same view. ShowKline(Day) on a fresh Day-default vm wouldn't
        // reload (no change), so the day case reloads directly.
        if (wasTrend) _vm.ShowTrend();
        else if (period != KlinePeriod.Day) _vm.ShowKline(period);
        else _ = _vm.ReloadAsync();
        RefreshPeriodStates();
    }

    /// <summary>One contract row in the picker.</summary>
    private sealed record ContractItem(Contract Contract)
    {
        public override string ToString() =>
            Contract.Name.Length > 0 ? $"{Contract.Name}  {Contract.Code}" : Contract.Code;
    }

    /// <summary>A group and the contracts under it, feeding the 分组→合约 pickers.</summary>
    public sealed record ContractGroup(string Name, IReadOnlyList<Contract> Contracts);

    private void Reset_Click(object sender, RoutedEventArgs e) => Chart.ResetView();

    private void TapeTop_Click(object sender, RoutedEventArgs e) => Tape.ScrollToTop();

    /// <summary>
    /// Window-level shortcuts. Handled in the PREVIEW pass so the arrow keys never
    /// reach the period/adjust toggles, where WPF would spend them on directional
    /// focus navigation instead of panning the chart. Chart keys are inert in
    /// intraday mode, which has no pan or zoom — only Esc and the period digits.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // While a text box has focus (the 大单 threshold input) these window
        // shortcuts must stay out of the way — otherwise typing "1"/"2" fires
        // 分时/日K and the view jumps out from under the cursor.
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;

        // Shift is deliberately allowed through: on a US layout "+" IS Shift+OemPlus,
        // so gating on "no modifier at all" would leave the zoom-in key dead.
        const ModifierKeys blocked = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;
        if (e.Handled || (Keyboard.Modifiers & blocked) != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.D1 or Key.NumPad1:
                _vm.ShowTrend();
                break;
            case Key.D2 or Key.NumPad2:
                _vm.ShowKline(KlinePeriod.Day);
                break;
            case Key.D3 or Key.NumPad3:
                _vm.ShowKline(KlinePeriod.Week);
                break;
            case Key.D4 or Key.NumPad4:
                _vm.ShowKline(KlinePeriod.Month);
                break;
            case Key.Left when !_vm.IsTrend:
                Chart.Pan(-1);
                break;
            case Key.Right when !_vm.IsTrend:
                Chart.Pan(1);
                break;
            case Key.PageUp when !_vm.IsTrend:
                Chart.PanPages(-1);
                break;
            case Key.PageDown when !_vm.IsTrend:
                Chart.PanPages(1);
                break;
            case Key.Home when !_vm.IsTrend:
                Chart.JumpToStart();
                break;
            case Key.End when !_vm.IsTrend:
                Chart.JumpToEnd();
                break;
            case (Key.OemPlus or Key.Add) when !_vm.IsTrend:
                Chart.Zoom(1);
                break;
            case (Key.OemMinus or Key.Subtract) when !_vm.IsTrend:
                Chart.Zoom(-1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnKlineLoaded() => Dispatcher.Invoke(() =>
    {
        Chart.SetSeries(_vm.Candles, _vm.MovingAverages);
        RefreshPeriodStates();
        UpdateStatus();
    });

    /// <summary>Silent re-poll: same data path, but the view stays where it is.</summary>
    private void OnKlineRefreshed() => Dispatcher.Invoke(() =>
    {
        Chart.UpdateSeries(_vm.Candles, _vm.MovingAverages);
        UpdateStatus();
    });

    /// <summary>Width of the book + stats + 成交明细 pane beside the intraday line.</summary>
    private const double DepthWidth = 280;

    private static readonly Brush UpBrush = Frozen(Tones.UpHex);
    private static readonly Brush DownBrush = Frozen(Tones.DownHex);
    private static readonly Brush NeutralBrush = Frozen("#DCE4EE");

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private DepthChart? _depth;

    private void OnLiveUpdated() => Dispatcher.Invoke(RenderDepth);

    /// <summary>
    /// Draws the book from the window's own 1s quote plus the day's stats
    /// (委比/委差, 涨跌停, 总手/金额/换手/量比, 外/内盘) — the full 分时 side panel.
    /// </summary>
    private void RenderDepth()
    {
        if (!_vm.IsTrend) return;

        _depth ??= new DepthChart { RowHeight = 16, FontSize = 12 };
        if (!ReferenceEquals(DepthHost.Child, _depth))
        {
            DepthHost.Child = _depth;
            DepthHost.Height = 16 * 10 + 1;
        }

        var live = _vm.Live;
        _depth.Set(
            live?.Depth ?? new StockClient.Core.Quotes.QuoteDepth(),
            live?.Yesterday ?? 0,
            Decimals(live));

        RenderStats(live);
        RenderTopStats(live);
    }

    /// <summary>Fills the numeric stat rows from the quote; "--" where a field
    /// isn't served (non-A markets carry no turnover/limit/盘 fields).</summary>
    private void RenderStats(Quote? q)
    {
        if (q is null)
        {
            foreach (var t in new[] { WeibiText, WeichaText, LimitUpText, LimitDownText,
                         TotalVolText, AmountText, TurnoverText, VolRatioText, OuterText, InnerText })
                t.Text = "--";
            WeibiText.Foreground = WeichaText.Foreground = NeutralBrush;
            return;
        }

        var dec = Decimals(q);
        var bid = q.Depth.Bids.Sum(b => b.Volume);
        var ask = q.Depth.Asks.Sum(a => a.Volume);
        var sum = bid + ask;
        if (sum > 0)
        {
            var ratio = (bid - ask) / sum * 100;
            var diff = bid - ask;
            WeibiText.Text = ratio.ToString("+0.00;-0.00;0.00") + "%";
            WeichaText.Text = diff.ToString("+#,##0;-#,##0;0");
            WeibiText.Foreground = WeichaText.Foreground = diff >= 0 ? UpBrush : DownBrush;
        }
        else
        {
            WeibiText.Text = WeichaText.Text = "--";
            WeibiText.Foreground = WeichaText.Foreground = NeutralBrush;
        }

        LimitUpText.Text = q.LimitUp is { } lu and > 0 ? lu.ToString("F" + dec) : "--";
        LimitDownText.Text = q.LimitDown is { } ld and > 0 ? ld.ToString("F" + dec) : "--";
        TotalVolText.Text = q.Volume is { } v and > 0 ? Compact(v) + "手" : "--";
        AmountText.Text = q.Amount is { } a and > 0 ? Compact(a) : "--";
        TurnoverText.Text = q.TurnoverRate is { } tr ? tr.ToString("F2") + "%" : "--";
        VolRatioText.Text = q.VolumeRatio is { } vr ? vr.ToString("F2") : "--";
        OuterText.Text = q.OuterVolume is { } o and > 0 ? Compact(o) + "手" : "--";
        InnerText.Text = q.InnerVolume is { } inn and > 0 ? Compact(inn) + "手" : "--";
    }

    /// <summary>Fills the top 每日实时信息 header (现价 + 今开/昨收/最高/最低/涨跌停/
    /// 换手/量比/成交量额/市盈净/市值/振幅/内外盘) from the quote; "-" where a field
    /// isn't served (non-A markets).</summary>
    private void RenderTopStats(Quote? q)
    {
        if (q is null)
        {
            TopPrice.Text = "-";
            TopChange.Text = TopPercent.Text = "";
            foreach (var t in new[] { TopOpen, TopPrev, TopHigh, TopLow, TopLimitUp, TopLimitDown,
                         TopTurnover, TopVolRatio, TopVolume, TopAmount, TopPe, TopPb,
                         TopTotalCap, TopFloatCap, TopAmplitude, TopInner, TopOuter })
                t.Text = "-";
            TopPrice.Foreground = TopChange.Foreground = TopPercent.Foreground =
                TopOpen.Foreground = TopHigh.Foreground = TopLow.Foreground = NeutralBrush;
            return;
        }

        var dec = Decimals(q);
        var mood = q.Percent > 0 ? UpBrush : q.Percent < 0 ? DownBrush : NeutralBrush;

        // 今开/最高/最低 colour by whether they're above or below 昨收, like the feed.
        Brush VsPrev(double v) => q.Yesterday > 0 && v > 0
            ? (v > q.Yesterday ? UpBrush : v < q.Yesterday ? DownBrush : NeutralBrush)
            : NeutralBrush;

        TopPrice.Text = q.Now > 0 ? q.Now.ToString("F" + dec) : "-";
        TopPrice.Foreground = mood;
        TopChange.Text = Signed(q.Change, dec);
        TopPercent.Text = Signed(q.Percent, 2) + "%";
        TopChange.Foreground = TopPercent.Foreground = mood;

        TopOpen.Text = Px(q.Open, dec);
        TopOpen.Foreground = VsPrev(q.Open);
        TopPrev.Text = Px(q.Yesterday, dec);
        TopHigh.Text = Px(q.High, dec);
        TopHigh.Foreground = VsPrev(q.High);
        TopLow.Text = Px(q.Low, dec);
        TopLow.Foreground = VsPrev(q.Low);
        TopLimitUp.Text = Px(q.LimitUp, dec);
        TopLimitDown.Text = Px(q.LimitDown, dec);
        TopTurnover.Text = q.TurnoverRate is { } tr ? tr.ToString("F2") + "%" : "-";
        TopVolRatio.Text = q.VolumeRatio is { } vr ? vr.ToString("F2") : "-";
        TopVolume.Text = q.Volume is { } v and > 0 ? Compact(v) + "手" : "-";
        TopAmount.Text = q.Amount is { } a and > 0 ? Compact(a) : "-";
        TopPe.Text = q.PeTtm is { } pe ? pe.ToString("F2") : "-";
        TopPb.Text = q.Pb is { } pb ? pb.ToString("F2") : "-";
        TopTotalCap.Text = q.TotalCap is { } tc and > 0 ? Compact(tc) : "-";
        TopFloatCap.Text = q.FloatCap is { } fc and > 0 ? Compact(fc) : "-";
        TopAmplitude.Text = q.High > 0 && q.Low > 0 && q.Yesterday > 0
            ? ((q.High - q.Low) / q.Yesterday * 100).ToString("F2") + "%" : "-";
        TopInner.Text = q.InnerVolume is { } inn2 and > 0 ? Compact(inn2) + "手" : "-";
        TopOuter.Text = q.OuterVolume is { } o and > 0 ? Compact(o) + "手" : "-";
    }

    private static string Px(double? v, int dec) => v is { } x and > 0 ? x.ToString("F" + dec) : "-";
    private static string Signed(double v, int dec) => (v >= 0 ? "+" : "") + v.ToString("F" + dec);

    /// <summary>万/亿 short form for 手 counts and 元 amounts.</summary>
    private static string Compact(double v) =>
        v >= 1e8 ? $"{v / 1e8:F2}亿" : v >= 1e4 ? $"{v / 1e4:F2}万" : $"{v:F0}";

    private static int Decimals(StockClient.Core.Quotes.Quote? quote) =>
        quote is null ? 2 : StockClient.Core.Quotes.PriceScale.Decimals(quote.Now, quote.Depth);

    // --- 成交明细 tape ---------------------------------------------------------

    private void InitTape()
    {
        BigTradeBox.Text = AppPrefs.BigTradeWan.ToString();
        BigTradeBox.LostKeyboardFocus += (_, _) => CommitBigTrade();
        BigTradeBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitBigTrade();
            Keyboard.ClearFocus();
            e.Handled = true;
        };
    }

    private void CommitBigTrade()
    {
        AppPrefs.BigTradeWan = int.TryParse(BigTradeBox.Text.Trim(), out var v) ? v : AppPrefs.BigTradeWan;
        BigTradeBox.Text = AppPrefs.BigTradeWan.ToString();   // reflect the clamp
        RenderTape();
    }

    private void OnTicksLoaded() => Dispatcher.Invoke(RenderTape);

    /// <summary>
    /// Refreshes the tape newest-first from the view model's tail, each 5s poll.
    /// The row rendering lives in <see cref="TradeTapeView"/>, shared with the
    /// historical replay.
    /// </summary>
    private void RenderTape()
    {
        if (!_vm.HasTape) return;
        Tape.SetTicks(_vm.Ticks, Decimals(_vm.Live), AppPrefs.BigTradeWan, _vm.TickPrePrice);
    }

    /// <summary>Opens the full-day 成交明细 in its own window (stats + filter + paging).</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Details is null) return;
        new TickDetailWindow(_vm.Contract, _vm.Live, _vm.Details, Decimals(_vm.Live), AppPrefs.BigTradeWan)
        { Owner = this }.Show();
    }

    private void OnTrendLoaded() => Dispatcher.Invoke(() =>
    {
        if (_vm.Trend is { } trend) Trend.SetSeries(trend);
        RefreshPeriodStates();
        UpdateStatus();
    });

    /// <summary>Shows the right chart for the current mode and adjusts the chrome.</summary>
    private void ApplyMode()
    {
        var trend = _vm.IsTrend;

        Chart.Visibility = trend ? Visibility.Collapsed : Visibility.Visible;
        Trend.Visibility = trend ? Visibility.Visible : Visibility.Collapsed;

        // Order book only alongside the intraday line — it is today's book, which
        // means nothing next to a year of candles. Collapsing the column (not just
        // the pane) gives the width back to the chart.
        DepthPane.Visibility = trend ? Visibility.Visible : Visibility.Collapsed;
        DepthColumn.Width = trend ? new GridLength(DepthWidth) : new GridLength(0);
        // 每日实时信息 header — only in 分时 (the 1s quote it reads is polled there).
        // The clock lives inside TopStats now, so its visibility rides along.
        TopStats.Visibility = trend ? Visibility.Visible : Visibility.Collapsed;
        if (trend) RenderDepth();

        // 成交明细 rides alongside the book, and only where EastMoney serves it (沪深).
        var tape = trend && _vm.HasTape;
        TapeHeader.Visibility = tape ? Visibility.Visible : Visibility.Collapsed;
        Tape.Visibility = tape ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = tape ? Visibility.Visible : Visibility.Collapsed;
        if (tape) RenderTape();

        // Adjustment doesn't apply to an intraday line; the hint changes to match.
        AdjustButtons.IsEnabled = !trend;
        HintText.Text = trend ? "分时 · 每 3 秒刷新" : "滚轮缩放 · 拖动平移";

        RefreshPeriodStates();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_vm.Loading)
        {
            Overlay.Text = "加载中…";
            Overlay.Visibility = Visibility.Visible;
            StatusText.Text = "加载中";
            return;
        }

        if (_vm.Error.Length > 0)
        {
            Overlay.Text = _vm.Error;
            Overlay.Visibility = Visibility.Visible;
            StatusText.Text = "加载失败";
            return;
        }

        if (_vm.IsTrend)
        {
            var n = _vm.Trend?.Points.Count ?? 0;
            Overlay.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"东财 · 分时 · {n} 点";
        }
        else
        {
            Overlay.Visibility = _vm.Candles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"{_vm.Source} · {_vm.Candles.Count} 根";
        }
    }

    /// <summary>分时 + 日K/周K/月K, mutually exclusive. Trend's tag is a string, candles' a KlinePeriod.</summary>
    private void BuildPeriodButtons()
    {
        AddPeriodButton("分时", "trend");
        foreach (var (period, label) in Periods) AddPeriodButton(label, period);
        RefreshPeriodStates();
    }

    private void AddPeriodButton(string label, object tag)
    {
        var button = new ToggleButton
        {
            Content = label,
            Tag = tag,
            FontSize = 12,
            Padding = new Thickness(11, 3, 11, 3),
            Margin = new Thickness(PeriodButtons.Children.Count == 0 ? 0 : 4, 0, 0, 0),
            MinWidth = 46,
        };

        button.Click += (_, _) =>
        {
            if (tag is KlinePeriod period) _vm.ShowKline(period);
            else _vm.ShowTrend();
        };

        PeriodButtons.Children.Add(button);
    }

    private void RefreshPeriodStates()
    {
        foreach (var child in PeriodButtons.Children)
            if (child is ToggleButton tb)
                tb.IsChecked = tb.Tag is KlinePeriod period
                    ? !_vm.IsTrend && Equals(period, _vm.Period)
                    : _vm.IsTrend;
    }

    /// <summary>
    /// A row of small segmented toggle buttons that reads/writes one enum on the
    /// view model. Used for the adjustment row.
    /// </summary>
    private void BuildToggles(
        Panel host, object[] values, string[] labels, Func<object> get, Action<object> set)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var button = new ToggleButton
            {
                Content = labels[i],
                Tag = value,
                FontSize = 12,
                Padding = new Thickness(11, 3, 11, 3),
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                MinWidth = 46,
            };

            button.Click += (_, _) =>
            {
                if (!Equals(get(), value)) set(value);
                foreach (var c in host.Children)
                    if (c is ToggleButton t) t.IsChecked = Equals(t.Tag, get());
            };

            host.Children.Add(button);
        }

        foreach (var c in host.Children)
            if (c is ToggleButton t) t.IsChecked = Equals(t.Tag, get());
    }
}
