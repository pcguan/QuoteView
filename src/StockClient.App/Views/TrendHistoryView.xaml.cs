using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        RefreshCompareItems();
    }

    private bool _refreshingCompare;

    /// <summary>
    /// Compare choices: "(无)" + every archived date EXCEPT the picked main
    /// day — comparing a day with itself is meaningless, so it simply isn't
    /// offered. If the current compare selection just became the main day,
    /// it falls back to "(无)".
    /// </summary>
    private void RefreshCompareItems()
    {
        var dates = DateBox.ItemsSource as DateOnly[] ?? Array.Empty<DateOnly>();
        var main = DateBox.SelectedItem as DateOnly?;
        var previous = CompareBox.SelectedItem as DateOnly?;

        var items = new List<object> { "（无）" };
        items.AddRange(dates.Where(d => main is null || d != main.Value).Cast<object>());

        _refreshingCompare = true;
        CompareBox.ItemsSource = items;
        var keep = previous is { } pc ? items.IndexOf(pc) : -1;
        CompareBox.SelectedIndex = keep >= 0 ? keep : 0;
        _refreshingCompare = false;
    }

    private void DateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshCompareItems();
        _ = LoadSelectedAsync();
    }

    private void CompareBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingCompare) _ = LoadSelectedAsync();
    }

    // What the newest load is about to render. A ComboBox raises no
    // SelectionChanged when the user re-picks the item it already shows — so
    // after a failed fetch, re-choosing the same date looked like a dead
    // control. DropDownClosed compares the selection against this instead:
    // any mismatch (failure reset it to null) reloads, and a plain
    // open-and-close stays a no-op.
    private DateOnly? _pendingMain, _pendingCompare;

    private void DateBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (DateBox.SelectedItem is DateOnly want && want != _pendingMain)
            _ = LoadSelectedAsync();
    }

    private void CompareBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (CompareBox.SelectedItem as DateOnly? != _pendingCompare)
            _ = LoadSelectedAsync();
    }

    private int _loadRequest;

    private async Task LoadSelectedAsync()
    {
        if (CodeBox.SelectedItem is not CodeItem item || DateBox.SelectedItem is not DateOnly date
            || _cache is null || _session is null)
            return;

        var request = ++_loadRequest;
        var compareDate = CompareBox.SelectedItem as DateOnly?;
        _pendingMain = date;
        _pendingCompare = compareDate;

        var series = await LoadDayAsync(item.Code, date);
        if (request != _loadRequest) return;

        if (series is null)
        {
            // Null the pending marks so re-picking the very same dates retries.
            _pendingMain = null;
            _pendingCompare = null;
            ShowEmpty(_session.IsSignedIn ? "该日快照获取失败(服务端不可达或缺失)" : "本地无此日快照；登录后可从服务端获取");
            return;
        }

        TrendSeries? compare = null;
        if (compareDate is { } cd && cd != date)
        {
            compare = await LoadDayAsync(item.Code, cd);
            if (request != _loadRequest) return;
        }

        Empty.Visibility = Visibility.Collapsed;
        Chart.Visibility = Visibility.Visible;
        Chart.SetSeries(series);
        Chart.SetCompare(compare);

        FillStats(StatsMain, date, series, MainAccent);

        if (compareDate is { } wanted && wanted != date && compare is null)
        {
            // The pick failed (server unreachable / day missing). Silently
            // clearing the overlay read as "the compare chart won't refresh" —
            // say so instead, and let re-picking the same date retry.
            _pendingCompare = null;
            FillStatsError(StatsCompare, wanted, CompareAccent,
                _session.IsSignedIn ? "该日快照获取失败，重选日期可重试" : "本地无此日快照，登录后可从服务端获取");
        }
        else
        {
            FillStats(StatsCompare, compareDate ?? default, compare, CompareAccent);
        }
    }

    /// <summary>Local cache first — every server fetch is written back, so each
    /// snapshot crosses the network once per machine.</summary>
    private async Task<TrendSeries?> LoadDayAsync(string code, DateOnly date)
    {
        if (_cache is null || _session is null) return null;

        var series = _cache.TryLoad(code, date);
        // Re-fetch when the cached copy predates the server's summary enrichment.
        if (series is not null && series.Summary is not null) return series;

        var fresh = await _session.TrendAsync(code, date);
        if (fresh is not null)
        {
            _cache.Save(fresh, date);
            return fresh;
        }

        return series;
    }

    // Accents match each day's line colour in the chart below.
    private static readonly Brush MainAccent = Frozen("#DCE4EE");
    private static readonly Brush CompareAccent = Frozen("#4C8DFF");
    private static readonly Brush LabelBrush = Frozen("#5F6672");
    private static readonly Brush Flat = Frozen("#DCE4EE");
    private static readonly Brush Up = Frozen("#EF5350");
    private static readonly Brush Down = Frozen("#26A69A");

    /// <summary>
    /// The day's closing stats as a boxed grid — 收盘/涨跌/振幅, 今开/最高/最低/
    /// 昨收, 量/额/外盘/内盘. Both days' boxes share the exact cell layout so a
    /// comparison is a straight horizontal glance. Prices come from the minute
    /// series itself; turnover fields need the server's after-close summary and
    /// show "--" on snapshots from before that existed.
    /// </summary>
    private static void FillStats(Border box, DateOnly date, TrendSeries? series, Brush accent)
    {
        if (series is null || series.Points.Count == 0)
        {
            box.Visibility = Visibility.Collapsed;
            return;
        }

        var pre = series.PreClose;
        var open = series.Points[0].Price;
        var close = series.Points[^1].Price;
        var high = series.Points.Max(p => p.Price);
        var low = series.Points.Min(p => p.Price);
        var s = series.Summary;
        var pct = s?.Percent ?? (pre > 0 ? (close / pre - 1) * 100 : 0);

        Brush VsPre(double v) => v > pre ? Up : v < pre ? Down : Flat;
        var pctBrush = pct > 0 ? Up : pct < 0 ? Down : Flat;

        var cells = new (string Label, string Value, Brush Colour)[]
        {
            ("收盘", Px(close), VsPre(close)),
            ("涨跌幅", pct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%", pctBrush),
            ("涨跌额", (close - pre).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture), pctBrush),
            ("振幅", pre > 0 ? ((high - low) / pre * 100).ToString("0.00") + "%" : "--", Flat),
            ("今开", Px(open), VsPre(open)),
            ("最高", Px(high), VsPre(high)),
            ("最低", Px(low), VsPre(low)),
            ("昨收", Px(pre), Flat),
            ("成交量", s is null ? "--" : Big(s.Volume) + "手", Flat),
            ("成交额", s is null ? "--" : Big(s.Amount), Flat),
            ("外盘", s is null ? "--" : Big(s.Outer), Up),
            ("内盘", s is null ? "--" : Big(s.Inner), Down),
        };

        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        header.Children.Add(new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(2),
            Background = accent, VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{date:yyyy-MM-dd}", Margin = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var (label, value, colour) in cells)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 2, 18, 2) };
            cell.Children.Add(new TextBlock
            {
                Text = label, FontSize = 10.5, Foreground = LabelBrush,
            });
            cell.Children.Add(new TextBlock
            {
                Text = value, FontSize = 12.5, FontFamily = new FontFamily("Consolas"),
                Foreground = colour,
            });
            grid.Children.Add(cell);
        }
        panel.Children.Add(grid);

        box.Child = panel;
        box.Visibility = Visibility.Visible;
    }

    /// <summary>The compare box's failure face: date header + why, visible —
    /// the one thing a silent collapse never told the user.</summary>
    private static void FillStatsError(Border box, DateOnly date, Brush accent, string message)
    {
        var panel = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        header.Children.Add(new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(2),
            Background = accent, VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{date:yyyy-MM-dd}", Margin = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = message, FontSize = 11.5, Foreground = LabelBrush,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
        });

        box.Child = panel;
        box.Visibility = Visibility.Visible;
    }

    private static string Px(double v) =>
        v.ToString(v < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>进位显示, same units the rest of the app uses (万/亿/万亿).</summary>
    private static string Big(double v)
    {
        var a = Math.Abs(v);
        return a >= 1e12 ? (v / 1e12).ToString("0.00", CultureInfo.InvariantCulture) + "万亿"
            : a >= 1e8 ? (v / 1e8).ToString("0.00", CultureInfo.InvariantCulture) + "亿"
            : a >= 1e4 ? (v / 1e4).ToString("0.00", CultureInfo.InvariantCulture) + "万"
            : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void ShowEmpty(string message)
    {
        Chart.Visibility = Visibility.Collapsed;
        StatsMain.Visibility = Visibility.Collapsed;
        StatsCompare.Visibility = Visibility.Collapsed;
        Empty.Text = message;
        Empty.Visibility = Visibility.Visible;
    }
}
