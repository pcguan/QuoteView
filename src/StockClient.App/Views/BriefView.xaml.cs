using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using StockClient.App.Services;
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
    private static readonly Brush Up = Frozen(Tones.UpHex);
    private static readonly Brush Down = Frozen(Tones.DownHex);
    private static readonly Brush Muted = Frozen(Tones.FlatHex);
    private static readonly Brush Faint = Frozen("#5F6672");
    private static readonly Brush Text = Frozen("#EDF1F7");
    private static readonly Brush Warn = Frozen("#FFC107");

    private readonly BriefStore _store = new();
    private readonly BriefClient _client;
    private bool _loading;

    public BriefView()
    {
        InitializeComponent();
        // Direct client: the brief source is the NAS (domestic); a stale
        // process-cached system proxy must not black-hole it (see DirectHttp).
        _client = new BriefClient(_store,
            Services.DirectHttp.Create(TimeSpan.FromSeconds(20)));
        Loaded += (_, _) => _ = ReloadAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    // ---- 关注动态: the per-account watch feed --------------------------------

    private AccountSession? _session;
    private JsonDocument? _news;
    private DispatcherTimer? _newsTimer;

    /// <summary>Wires the account session in; the feed is per-account.</summary>
    public void InitNews(AccountSession session)
    {
        _session = session;
        _ = LoadNewsAsync();
        _newsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _newsTimer.Tick += (_, _) => _ = LoadNewsAsync();
        _newsTimer.Start();
        session.Changed += () => Dispatcher.InvokeAsync(() => _ = LoadNewsAsync());
    }

    /// <summary>Server push said fresh items exist — refetch right away.</summary>
    public void NudgeNews() => _ = LoadNewsAsync();

    private async Task LoadNewsAsync()
    {
        if (_session is null) return;

        if (!_session.IsSignedIn)
        {
            WatchCard.Visibility = Visibility.Visible;
            WatchBody.Children.Clear();
            WatchHint.Text = "登录后，服务端会按你的分组与合约聚合公告、业绩与新闻，并做多空标注。";
            WatchHint.Visibility = Visibility.Visible;
            return;
        }

        var json = await _session.NewsJsonAsync();
        if (json is null)
        {
            if (_news is null)
            {
                WatchCard.Visibility = Visibility.Visible;
                WatchHint.Text = "关注动态暂时拉取失败，稍后自动重试。";
                WatchHint.Visibility = Visibility.Visible;
            }
            return;
        }

        try
        {
            _news?.Dispose();
            _news = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        RenderNews();
    }

    private void WatchFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderNews();

    private void WatchRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadNewsAsync();

    private void RenderNews()
    {
        if (_news is null) return;

        WatchCard.Visibility = Visibility.Visible;
        WatchHint.Visibility = Visibility.Collapsed;
        WatchBody.Children.Clear();

        var filter = (WatchFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "全部";
        var root = _news.RootElement;
        WatchUpdated.Text = root.TryGetProperty("updated", out var up)
            ? "更新于 " + up.GetString() : "";

        var any = false;
        if (root.TryGetProperty("groups", out var groups))
            foreach (var group in groups.EnumerateArray())
            {
                var rows = new List<JsonElement>();
                foreach (var item in group.GetProperty("items").EnumerateArray())
                {
                    var tone = item.TryGetProperty("tone", out var t) ? t.GetString() : "";
                    var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : "";
                    var keep = filter switch
                    {
                        "利多" => tone == "利多",
                        "利空" => tone == "利空",
                        "公告与业绩" => kind is "公告" or "业绩",
                        _ => true,
                    };
                    if (keep) rows.Add(item);
                }
                if (rows.Count == 0) continue;

                any = true;
                var header = new TextBlock
                {
                    Text = $"{group.GetProperty("name").GetString()}（{rows.Count}）",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Text,
                    Margin = new Thickness(0, 8, 0, 4),
                };
                WatchBody.Children.Add(header);

                foreach (var item in rows.Take(20))
                    WatchBody.Children.Add(NewsRow(item));
            }

        if (!any)
        {
            WatchHint.Text = filter == "全部"
                ? "暂无与你的分组相关的动态（服务端每小时轮询一批，新增会推送提醒）。"
                : $"没有「{filter}」条目，切回「全部」查看。";
            WatchHint.Visibility = Visibility.Visible;
        }
    }

    private UIElement NewsRow(JsonElement item)
    {
        string Get(string prop) =>
            item.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

        var tone = Get("tone");
        var kind = Get("kind");
        var toneBrush = tone == "利多" ? Up : tone == "利空" ? Down : Faint;

        var row = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 1, 0, 1),
        };
        var when = Get("time");
        row.Inlines.Add(new Run((when.Length > 5 ? when[5..] : when).PadRight(12))
            { Foreground = Faint, FontFamily = new FontFamily("Consolas") });
        row.Inlines.Add(new Run($"[{tone}] ") { Foreground = toneBrush });
        row.Inlines.Add(new Run($"[{kind}] ") { Foreground = kind == "业绩" ? Warn : Muted });
        var who = Get("name");
        if (who.Length > 0)
            row.Inlines.Add(new Run(who + "：") { Foreground = Muted });

        var link = new Hyperlink(new Run(Get("title")))
        {
            Foreground = Text,
            TextDecorations = null,
        };
        var url = Get("url");
        if (url.StartsWith("http"))
            link.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    // A broken registry handler must not crash the app.
                }
            };
        row.Inlines.Add(link);
        row.ToolTip = Get("title");
        return row;
    }

    /// <summary>One entry in the date picker: the raw key, and what's shown.</summary>
    private sealed record DayOption(string Key, string Label);

    private void DayPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DayPicker.SelectedValue is not string day) return;
        Show(_store.Load(day), day);
    }

    /// <summary>
    /// Shows what is cached immediately, then asks the server what else exists.
    ///
    /// Cache-first so the page is never blank while a request is in flight, and
    /// so a machine with no network still shows the last brief it read.
    /// </summary>
    private async Task ReloadAsync()
    {
        Render(_store.AvailableDays());

        var catalog = await _client.GetCatalogAsync(CancellationToken.None);
        if (catalog is null || catalog.Days.Count == 0) return;

        // The newest day is re-fetched every time: it is the one that gets
        // regenerated during the day, so a cached copy of it goes stale. Older
        // days are settled and come from cache.
        var newest = catalog.Days[0];
        await _client.GetAsync(newest, CancellationToken.None, refresh: true);

        foreach (var day in catalog.Days.Skip(1).Take(BriefStore.RetainDays - 1))
            await _client.GetAsync(day, CancellationToken.None);

        Render(_store.AvailableDays());
    }

    private void Render(IReadOnlyList<string> days)
    {

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
                "每个交易日 07:30 / 15:30 生成，客户端自动拉取。\n" +
                "现在没有内容，通常是还没到今天的生成时间，或者暂时连不上。\n\n" +
                "客户端只负责展示：不自己抓数据、不做计算。";
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
