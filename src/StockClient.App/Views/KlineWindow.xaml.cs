using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>Width of the book pane beside the intraday line.</summary>
    private const double DepthWidth = 200;

    private DepthChart? _depth;

    private void OnLiveUpdated() => Dispatcher.Invoke(RenderDepth);

    /// <summary>
    /// Draws the book from the window's own 1s quote. Rows are taller and the type
    /// bigger than the stealth panel's copy — there is room here, and this is the
    /// view someone opens to actually study the book.
    /// </summary>
    private void RenderDepth()
    {
        if (!_vm.IsTrend) return;

        _depth ??= new DepthChart { RowHeight = 22, FontSize = 12 };
        if (!ReferenceEquals(DepthHost.Child, _depth))
        {
            DepthHost.Child = _depth;
            DepthHost.Height = 22 * 10 + 1;
        }

        var live = _vm.Live;
        _depth.Set(
            live?.Depth ?? new StockClient.Core.Quotes.QuoteDepth(),
            live?.Yesterday ?? 0,
            Decimals(live));

        DepthTime.Text = live is null ? "" : $"{live.Name}  {live.Now}  {live.Time}";
    }

    private static int Decimals(StockClient.Core.Quotes.Quote? quote) =>
        quote is null ? 2 : StockClient.Core.Quotes.PriceScale.Decimals(quote.Now, quote.Depth);

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
