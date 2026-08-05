using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StockClient.Core.Brief;

namespace StockClient.App.Views;

/// <summary>
/// Renders the daily brief produced by the `brief/` pipeline.
///
/// A viewer, strictly. It reads a JSON file and lays it out — no fetching, no
/// summing, no re-ranking. That matters: the pipeline's whole guarantee is that
/// every figure traces to a file it wrote, and a client that "helpfully"
/// computed a total would put a number on screen that exists nowhere upstream.
///
/// Everything with a source shows its source. Anything the pipeline marked as
/// failed is reported rather than quietly omitted, because a missing section and
/// a section that legitimately had nothing in it look identical otherwise.
/// </summary>
public partial class BriefView : UserControl
{
    private static readonly Brush Up = Frozen("#EF5350");
    private static readonly Brush Down = Frozen("#26A69A");
    private static readonly Brush Muted = Frozen("#8B93A3");
    private static readonly Brush Faint = Frozen("#5F6672");
    private static readonly Brush Text = Frozen("#EDF1F7");
    private static readonly Brush Warn = Frozen("#FFC107");

    private readonly BriefStore _store = new();
    private bool _loading;

    public BriefView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>One entry in the date picker: the raw key, and what's shown.</summary>
    private sealed record DayOption(string Key, string Label);

    private void DayPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DayPicker.SelectedValue is not string day) return;
        Show(_store.Load(day), day);
    }

    private void Reload()
    {
        var days = _store.AvailableDays();

        // Shown as 2026-08-05 rather than 20260805 — the raw form is both harder
        // to read and wider than it looks in a fixed-width box.
        _loading = true;
        DayPicker.ItemsSource = days.Select(d => new DayOption(d, Pretty(d))).ToArray();
        DayPicker.SelectedValue = days.FirstOrDefault();
        _loading = false;

        DayPicker.Visibility = days.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (days.Count == 0)
        {
            Show(null, "");
            return;
        }

        Show(_store.Load(days[0]), days[0]);
    }

    private void Show(DailyBrief? brief, string day)
    {
        Sections.Children.Clear();

        if (brief is null)
        {
            DayText.Text = "无数据";
            SubtitleText.Text = "";
            EmptyPane.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Collapsed;
            EmptyHint.Text =
                $"简报由 brief/ 管道每个交易日生成后放到：\n{_store.Root}\n\n" +
                "这里只负责展示，不联网、不自己生成 —— 没有文件就是没有。";
            return;
        }

        EmptyPane.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;

        DayText.Text = Pretty(brief.TradingDay.Length > 0 ? brief.TradingDay : day);
        SubtitleText.Text = brief.GeneratedAt is { Length: > 0 } at
            ? $"生成于 {at} · 仅信息聚合，不含判断、预测或建议"
            : "仅信息聚合，不含判断、预测或建议";

        // Failed sources first: what's missing shapes how you read the rest.
        if (brief.FailedSources.Count > 0)
        {
            var bad = string.Join("、", brief.FailedSources.Select(s => $"{s.Key}={s.Value}"));
            Sections.Children.Add(Card("数据源异常", new[]
            {
                Line($"以下数据源本次未取到内容：{bad}。相关条目按 N/A 处理，未用其它来源替代。", Warn),
            }));
        }

        AddMarket(brief.Market);
        AddItems("利多线索", brief.Bullish, Up);
        AddItems("利空线索", brief.Bearish, Down);
        AddItems("未证实信息", brief.Unverified, Warn,
            "以下为传闻或未经官方确认的消息，单独隔离，不与上面两节混同。");

        if (brief.Counterpoint.Count > 0)
        {
            var lines = brief.Counterpoint
                .Select(c => (UIElement)Line("· " + c, Text))
                .Prepend(Line("对当日主流叙事的反面证据，用于对冲确认偏误。", Faint))
                .ToArray();
            Sections.Children.Add(Card("反方视角", lines));
        }
    }

    private void AddMarket(BriefMarket market)
    {
        var rows = new List<UIElement>();

        if (market.Indices.Count > 0)
        {
            var grid = NewGrid("指数", "点位", "涨跌幅", "成交额");
            foreach (var index in market.Indices)
            {
                AddRow(grid,
                    (index.Label, Text, false),
                    (Num(index.Price), Text, true),
                    (Pct(index.Pct), Sign(index.Pct), true),
                    (Money(index.Amount), Muted, true));
            }
            rows.Add(grid);
        }

        if (market.Breadth is { } breadth)
        {
            var parts = new List<string>();
            if (breadth.Advancing is { } up && breadth.Declining is { } down)
                parts.Add($"上涨 {up} / 下跌 {down}");
            if (breadth.Total is { } total) parts.Add($"全市场 {total} 只");
            if (breadth.LimitUp is { } lu) parts.Add($"涨停 {lu}");
            if (breadth.LimitDown is { } ld) parts.Add($"跌停 {ld}");
            if (breadth.TotalAmount is { } amount) parts.Add($"两市成交 {Money(amount)}");

            if (parts.Count > 0) rows.Add(Line(string.Join("　·　", parts), Text));
        }

        if (market.TopBoards.Count > 0 || market.BottomBoards.Count > 0)
        {
            var grid = NewGrid("板块", "涨跌幅", "涨/跌家数", "");
            foreach (var board in market.TopBoards.Take(8).Concat(market.BottomBoards.Take(8)))
            {
                AddRow(grid,
                    (board.Name, Text, false),
                    (Pct(board.Pct), Sign(board.Pct), true),
                    ($"{board.Up?.ToString() ?? "-"}/{board.Down?.ToString() ?? "-"}", Muted, true),
                    ("", Muted, true));
            }
            rows.Add(grid);
        }

        if (market.Watchlist.Count > 0)
        {
            var grid = NewGrid("关注池", "最新", "涨跌幅", "标签");
            foreach (var item in market.Watchlist)
            {
                AddRow(grid,
                    ($"{item.Name} {item.Code}", Text, false),
                    (Num(item.Price), Text, true),
                    (Pct(item.Pct), Sign(item.Pct), true),
                    (item.Tag, Muted, true));
            }
            rows.Add(grid);
        }

        if (rows.Count == 0) rows.Add(Line("N/A（本次未取到行情数据）", Warn));

        Sections.Children.Add(Card("交易详情", rows));
    }

    private void AddItems(string title, IReadOnlyList<BriefItem> items, Brush accent,
                          string? note = null)
    {
        var rows = new List<UIElement>();
        if (note is not null) rows.Add(Line(note, Faint));

        if (items.Count == 0)
        {
            rows.Add(Line("本次无条目。", Faint));
        }
        else
        {
            foreach (var item in items)
            {
                var block = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };

                block.Children.Add(new TextBlock
                {
                    Text = item.Text,
                    Foreground = Text,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19,
                });

                // Provenance. The pipeline requires every classified line to name
                // the raw file it came from; showing it is what makes that useful
                // rather than ceremonial.
                var meta = new List<string>();
                if (item.Time.Length > 0) meta.Add(item.Time);
                if (item.Source.Length > 0) meta.Add($"来源 {item.Source}");

                if (meta.Count > 0)
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = string.Join("　·　", meta),
                        Foreground = Faint,
                        FontSize = 10.5,
                        Margin = new Thickness(0, 3, 0, 0),
                    });
                }

                rows.Add(block);
            }
        }

        Sections.Children.Add(Card($"{title}（{items.Count}）", rows, accent));
    }

    // ------------------------------------------------------------- building

    private Border Card(string title, IEnumerable<UIElement> content, Brush? accent = null)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent ?? Muted,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (var child in content) panel.Children.Add(child);

        return new Border
        {
            Style = TryFindResource("Card") as Style,
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
    }

    private static TextBlock Line(string text, Brush brush) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
        LineHeight = 18,
    };

    private static Grid NewGrid(params string[] headers)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 1; i < headers.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = headers[i],
                Foreground = Faint,
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private static void AddRow(Grid grid, params (string Text, Brush Brush, bool Right)[] cells)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < cells.Length && i < grid.ColumnDefinitions.Count; i++)
        {
            var (text, brush, right) = cells[i];
            var cell = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 2),
                FontFamily = right ? new FontFamily("Consolas") : new FontFamily("Microsoft YaHei"),
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(cell, i);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }
    }

    // ------------------------------------------------------------ formatting
    // Formatting only — never arithmetic. A missing value renders as N/A rather
    // than 0, because 0 is a number someone might act on.

    private static Brush Sign(double? value) =>
        value is null ? Muted : value >= 0 ? Up : Down;

    private static string Num(double? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "N/A";

    private static string Pct(double? value) =>
        value?.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%" ?? "N/A";

    private static string Money(double? value)
    {
        if (value is not { } v) return "N/A";
        return v >= 1e8
            ? (v / 1e8).ToString("0.#", CultureInfo.InvariantCulture) + "亿"
            : v.ToString("0", CultureInfo.InvariantCulture);
    }

    private static string Pretty(string day) =>
        day.Length == 8 ? $"{day[..4]}-{day[4..6]}-{day[6..]}" : day;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
