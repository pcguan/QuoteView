using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.App.ViewModels;
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

    private readonly KlineViewModel _vm;

    public KlineWindow(KlineViewModel vm)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);

        _vm = vm;
        TitleText.Text = _vm.Title;
        Title = _vm.Title;

        BuildPeriodButtons();
        BuildToggles(AdjustButtons, Adjusts.Select(a => (object)a.Adjust).ToArray(),
            Adjusts.Select(a => a.Label).ToArray(), () => _vm.Adjust, a => _vm.Adjust = (KlineAdjust)a);

        _vm.Loaded += OnKlineLoaded;
        _vm.LiveUpdated += OnLiveUpdated;
        _vm.Refreshed += OnKlineRefreshed;
        _vm.TrendLoaded += OnTrendLoaded;
        _vm.TicksUpdated += OnTicksLoaded;
        InitTape();
        _vm.PropertyChanged += (_, e) => Dispatcher.Invoke(() =>
        {
            if (e.PropertyName == nameof(KlineViewModel.IsTrend)) ApplyMode();
            else if (e.PropertyName is nameof(KlineViewModel.Loading) or nameof(KlineViewModel.Error))
                UpdateStatus();
        });

        ApplyMode();
        Loaded += async (_, _) => await _vm.ReloadAsync();
        Closed += (_, _) => _vm.Dispose();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Chart.ResetView();

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
        if (trend) RenderDepth();

        // 成交明细 rides alongside the book, and only where EastMoney serves it (沪深).
        var tape = trend && _vm.HasTape;
        TapeHeader.Visibility = tape ? Visibility.Visible : Visibility.Collapsed;
        Tape.Visibility = tape ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = tape ? Visibility.Visible : Visibility.Collapsed;
        if (tape) RenderTape();

        // Adjustment doesn't apply to an intraday line; the hint changes to match.
        AdjustButtons.IsEnabled = !trend;
        HintText.Text = trend ? "分时 · 每 5 秒刷新" : "滚轮缩放 · 拖动平移";

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
