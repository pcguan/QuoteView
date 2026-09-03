using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StockClient.App.Views;
using StockClient.Core.Groups;

namespace Preview;

// Headless layout check: builds the real controls with the real App.xaml
// resources (WPF UI's implicit styles included) and renders them to PNG with
// RenderTargetBitmap. No desktop session needed, which is the point — SSH lands
// in session 0 where nothing can be shown.
public sealed class FakeVm
{
    public System.Collections.ObjectModel.ObservableCollection<StockClient.App.ViewModels.GroupRow>? Groups { get; set; }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new StockClient.App.App();
        app.InitializeComponent();   // merges ThemesDictionary + ControlsDictionary + Theme.xaml

        var config = StealthConfig.CreateDefault();

        // 1) The settings window's own content, at the width the window gives it.
        var rootCfg = new StockClient.Core.Groups.GroupConfig { Stealth = config };
        rootCfg.StealthTemplates.Add(new StockClient.Core.Groups.NamedStealthTemplate
            { Name = "默认", Stealth = StealthConfigOps.Clone(config) });
        rootCfg.ActiveStealthTemplate = "默认";
        var view = new StealthSettingsView(rootCfg, () => { }, () => { });
        var content = (FrameworkElement)view;
        // Host it at the real client width WITH its margins inside the bitmap —
        // rendering the content element alone crops its own 18px margin and
        // looks like a clipping bug that isn't there.
        var host = new Border
        {
            Width = 414,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
            Child = content,
        };
        Render(host, @"C:\work\preview-settings.png");

        // 1b) The whole field list, unscrolled, at the width the ScrollViewer
        // gives it — the longest label (分组名（面板左侧）) is below the fold.
        var fieldsWin = new StockClient.App.Views.StealthFieldsWindow(
            StealthConfig.CreateDefault().Normalize(), () => { });
        var fieldsContent = (FrameworkElement)fieldsWin.Content;
        fieldsWin.Content = null;
        Render(new Border
        {
            Width = 1080,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
            Child = fieldsContent,
        }, @"C:\work\preview-fields.png");

        // 2) The picker popup, rendered straight from its child.
        var picker = new ColorPickerButton("#EF5350", _ => { }, "上涨颜色", 78);
        var build = typeof(ColorPickerButton).GetMethod("BuildPopup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var popup = (Popup)build.Invoke(picker, null)!;
        var body = (FrameworkElement)popup.Child;
        popup.Child = null;   // detach so it can be rendered on its own
        typeof(ColorPickerButton).GetMethod("SyncThumbs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(picker, null);
        Render(body, @"C:\work\preview-popup.png");

        // 3) The group sidebar with aggregate percents: two-line rows, one group
        // still waiting for its first tick (second line must collapse).
        var g1 = new StockClient.App.ViewModels.GroupRow(new StockClient.Core.Groups.Group
            { Id = "a", Name = "自选核心持仓监控", Codes = { "SH600519", "SZ000651" } }) { IndexPercent = 2.09 };
        var g2 = new StockClient.App.ViewModels.GroupRow(new StockClient.Core.Groups.Group
            { Id = "b", Name = "半导体", Codes = { "SH688981" } }) { IndexPercent = -1.37, IsActive = true };
        var g3 = new StockClient.App.ViewModels.GroupRow(new StockClient.Core.Groups.Group
            { Id = "c", Name = "港美观察" }) ;
        var quotesView = new StockClient.App.Views.QuotesView
        {
            DataContext = new FakeVm
            {
                Groups = new System.Collections.ObjectModel.ObservableCollection<StockClient.App.ViewModels.GroupRow> { g1, g2, g3 },
            },
            Width = 560, Height = 420,
        };
        Render(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)),
            Child = quotesView,
        }, @"C:\work\preview-groups.png");

        // 4) The history page: bar layout untouched (empty state), then the same
        // page with a series pushed in through its own internals.
        var history = new StockClient.App.Views.TrendHistoryView { Width = 980, Height = 430 };
        Render(new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)), Child = history },
            @"C:\work\preview-history-empty.png");

        var pts = new List<StockClient.Core.Quotes.TrendPoint>();
        for (var i = 0; i < 240; i++)
        {
            var t = new DateTime(2026, 8, 24, 9, 30, 0).AddMinutes(i <= 120 ? i : i + 90);
            var price = 100 + 3 * Math.Sin(i / 24.0) + i * 0.004;
            pts.Add(new StockClient.Core.Quotes.TrendPoint
            {
                Time = t.ToString("yyyy-MM-dd HH:mm"),
                Price = Math.Round(price, 2),
                AvgPrice = Math.Round(100 + 1.5 * Math.Sin(i / 40.0), 2),
                Volume = 5000 + 4000 * Math.Abs(Math.Sin(i / 9.0)),
            });
        }
        var fakeSeries = new StockClient.Core.Quotes.TrendSeries
            { Code = "SH600519", Name = "贵州茅台", PreClose = 100.5, Points = pts };

        var history2 = new StockClient.App.Views.TrendHistoryView { Width = 980, Height = 430 };
        var chartField = typeof(StockClient.App.Views.TrendHistoryView)
            .GetField("Chart", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((StockClient.App.Views.TrendChart)chartField.GetValue(history2)!).SetSeries(fakeSeries);
        Render(new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)), Child = history2 },
            @"C:\work\preview-history.png");

        // 5) The login dialog, form state.
        var session = new StockClient.App.Services.AccountSession(
            new StockClient.Core.Quotes.AccountClient(new System.Net.Http.HttpClient()),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qv-preview-account.json"));
        var login = new StockClient.App.Views.LoginWindow(session);
        var loginContent = (FrameworkElement)login.Content;
        login.Content = null;
        Render(new Border
        {
            Width = 340,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
            Child = loginContent,
        }, @"C:\work\preview-login.png");

        // 6) History page with a compare overlay + summary strip.
        var pts2 = new List<StockClient.Core.Quotes.TrendPoint>();
        for (var i = 0; i < 240; i++)
        {
            var t = new DateTime(2026, 8, 21, 9, 30, 0).AddMinutes(i <= 120 ? i : i + 90);
            pts2.Add(new StockClient.Core.Quotes.TrendPoint
            {
                Time = t.ToString("yyyy-MM-dd HH:mm"),
                Price = Math.Round(101 + 2.2 * Math.Cos(i / 30.0) - i * 0.006, 2),
                AvgPrice = Math.Round(101.6 - i * 0.004, 2), Volume = 3000,
            });
        }
        var cmpSeries = new StockClient.Core.Quotes.TrendSeries
            { Code = "SH600519", Name = "贵州茅台", PreClose = 102.2, Points = pts2 };

        var mainWithSummary = fakeSeries with
        {
            Summary = new StockClient.Core.Quotes.TrendDaySummary
                { Percent = -6.07, Amount = 83.44e8, Volume = 111.99e4, Outer = 51.70e4, Inner = 60.29e4 },
        };
        var cmpWithSummary = cmpSeries with
        {
            Summary = new StockClient.Core.Quotes.TrendDaySummary
                { Percent = 1.20, Amount = 65.02e8, Volume = 88.10e4, Outer = 45.00e4, Inner = 43.10e4 },
        };

        var history3 = new StockClient.App.Views.TrendHistoryView { Width = 1050, Height = 560 };
        var t3 = typeof(StockClient.App.Views.TrendHistoryView);
        var chart3 = (StockClient.App.Views.TrendChart)t3.GetField("Chart", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history3)!;
        chart3.SetSeries(mainWithSummary);
        chart3.SetCompare(cmpWithSummary);
        // Fake a crosshair hover so the twin per-day readout boxes render too.
        typeof(StockClient.App.Views.TrendChart)
            .GetField("_hoverIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(chart3, 150);
        var fill = t3.GetMethod("FillStats", BindingFlags.NonPublic | BindingFlags.Static)!;
        var accM = t3.GetField("MainAccent", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var accC = t3.GetField("CompareAccent", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var boxM = t3.GetField("StatsMain", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history3)!;
        var boxC = t3.GetField("StatsCompare", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history3)!;
        fill.Invoke(null, new[] { boxM, new DateOnly(2026, 8, 24), mainWithSummary, accM });
        fill.Invoke(null, new[] { boxC, new DateOnly(2026, 8, 21), cmpWithSummary, accC });
        Render(new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)), Child = history3 },
            @"C:\work\preview-compare.png");

        // 6b) KlineWindow in 分时 mode with a fake 成交明细 tape beside the book —
        // the P1 逐笔 feature. Trend + ticks are injected via reflection so nothing
        // is fetched; a low price makes the 大单 threshold (100万 default) split
        // into highlighted and plain rows.
        var http = new System.Net.Http.HttpClient();
        var clock = new StockClient.Core.Contracts.MarketClock();
        var kRepo = new StockClient.Core.Quotes.KlineRepository(
            new StockClient.Core.Quotes.EastMoneyKlineClient(http),
            new StockClient.Core.Quotes.TencentKlineClient(http),
            new StockClient.Core.Quotes.KlineCache(Path.Combine(Path.GetTempPath(), "qv-prev-kline")),
            clock);
        var tRepo = new StockClient.Core.Quotes.TrendRepository(
            new StockClient.Core.Quotes.EastMoneyTrendClient(http), clock);
        var kContract = new StockClient.Core.Contracts.Contract { Code = "SH600519", Name = "贵州茅台" };
        var kvm = new StockClient.App.ViewModels.KlineViewModel(kContract, kRepo, tRepo,
            System.Windows.Threading.Dispatcher.CurrentDispatcher, null,
            new StockClient.Core.Quotes.EastMoneyDetailsClient(http));

        var kvmT = typeof(StockClient.App.ViewModels.KlineViewModel);
        kvmT.GetField("_isTrend", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(kvm, true);
        var tape = new List<StockClient.Core.Quotes.TradeTick>();
        var rnd = new Random(7);
        for (var i = 0; i < 42; i++)
        {
            var side = i % 8 == 0 ? StockClient.Core.Quotes.TradeSide.Neutral
                : (rnd.Next(2) == 0 ? StockClient.Core.Quotes.TradeSide.Buy : StockClient.Core.Quotes.TradeSide.Sell);
            tape.Add(new StockClient.Core.Quotes.TradeTick
            {
                Time = $"13:{(5 + i / 3):00}:{(i * 7) % 60:00}",
                Price = Math.Round(11.6 + (rnd.NextDouble() - 0.5) * 0.2, 2),
                Volume = i % 9 == 0 ? 1500 : rnd.Next(1, 60),   // 1500 手 @ ~11.6 ≈ 174万 → 大单
                Trades = rnd.Next(1, 20),
                Side = side,
            });
        }
        kvmT.GetProperty("Ticks")!.SetValue(kvm, tape);

        var kwin = new StockClient.App.Views.KlineWindow(kvm);

        // Inject a fake quote so the stat rows (委比/委差, 涨跌停, 总手… 外/内盘) and
        // the 5-level book render populated instead of "--".
        StockClient.Core.Quotes.DepthLevel Bid(double p, double v) => new(p, v);
        var fakeQuote = new StockClient.Core.Quotes.Quote
        {
            Code = "SH600519", Name = "贵州茅台", Now = 11.60, Yesterday = 11.50,
            Open = 11.52, High = 11.71, Low = 11.48, Time = "14:59:57",
            Volume = 802500, Amount = 1.777e9, TurnoverRate = 17.74, VolumeRatio = 1.09,
            LimitUp = 12.65, LimitDown = 10.35, OuterVolume = 396600, InnerVolume = 405800,
            Depth = new StockClient.Core.Quotes.QuoteDepth
            {
                Asks = new[] { Bid(11.61, 230), Bid(11.62, 415), Bid(11.63, 152), Bid(11.64, 1671), Bid(11.65, 721) },
                Bids = new[] { Bid(11.60, 5439), Bid(11.59, 2406), Bid(11.58, 1094), Bid(11.57, 590), Bid(11.56, 190) },
            },
        };
        kvmT.GetField("_live", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(kvm, fakeQuote);
        typeof(StockClient.App.Views.KlineWindow)
            .GetMethod("RenderDepth", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(kwin, null);

        var kContent = (FrameworkElement)kwin.Content;
        kwin.Content = null;
        Render(new Border
        {
            Width = 920, Height = 560,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x14, 0x20)),
            Child = kContent,
        }, @"C:\work\preview-tape.png");

        // 6d) The full-day 成交明细 detail window (filter + paging). Ticks injected;
        // Loaded never fires (not shown) so no fetch happens.
        var detContract = new StockClient.Core.Contracts.Contract { Code = "SH600519", Name = "贵州茅台" };
        var detWin = new StockClient.App.Views.TickDetailWindow(
            detContract, new StockClient.Core.Quotes.EastMoneyDetailsClient(http), 2, 100);
        var detT = typeof(StockClient.App.Views.TickDetailWindow);
        detT.GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(detWin, tape);
        detT.GetMethod("ApplyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(detWin, null);
        var detContent = (FrameworkElement)detWin.Content;
        detWin.Content = null;
        Render(new Border
        {
            Width = 520, Height = 680,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x14, 0x20)),
            Child = detContent,
        }, @"C:\work\preview-tickdetail.png");

        // 6c) The history page with the 成交明细 side pane OPEN (P3 replay). A
        // fresh view (history3 is already parented above) with a chart series and
        // the tape injected via reflection.
        var history4 = new StockClient.App.Views.TrendHistoryView { Width = 1180, Height = 560 };
        FrameworkElement H4(string n) => (FrameworkElement)t3.GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history4)!;
        ((StockClient.App.Views.TrendChart)t3.GetField("Chart", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history4)!).SetSeries(mainWithSummary);
        ((System.Windows.Controls.ColumnDefinition)t3.GetField("TapeColumn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history4)!).Width = new GridLength(300);
        H4("TapePane").Visibility = Visibility.Visible;
        ((System.Windows.Controls.TextBlock)H4("TapeTitle")).Text = "成交明细 · 08-24 · 42 笔";
        ((StockClient.App.Views.TradeTapeView)t3.GetField("Tape", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history4)!).SetTicks(tape, 2, 100, stickToNewest: false);
        Render(new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)), Child = history4 },
            @"C:\work\preview-hist-tape.png");

        // 7) Settings window with the template bar.
        var config2 = StealthConfig.CreateDefault();
        var tmpls = new List<StockClient.Core.Groups.NamedStealthTemplate>
        {
            new() { Name = "白天高亮", Stealth = StealthConfigOps.Clone(config2) },
            new() { Name = "夜间低调", Stealth = StealthConfigOps.Clone(config2) },
        };
        var rootCfg2 = new StockClient.Core.Groups.GroupConfig { Stealth = config2 };
        foreach (var t in tmpls) rootCfg2.StealthTemplates.Add(t);
        rootCfg2.ActiveStealthTemplate = tmpls[0].Name;
        var content2 = new StealthSettingsView(rootCfg2, () => { }, () => { });
        Render(new Border { Width = 430, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)), Child = content2 },
            @"C:\work\preview-templates.png");

        // 7b) The new settings window, 系统 tab (auto-update switch + about).
        var sysCfg = new StockClient.Core.Groups.GroupConfig { Stealth = StealthConfig.CreateDefault() };
        sysCfg.StealthTemplates.Add(new StockClient.Core.Groups.NamedStealthTemplate
            { Name = "默认", Stealth = StealthConfigOps.Clone(sysCfg.Stealth) });
        sysCfg.ActiveStealthTemplate = "默认";
        var sysWin = new StockClient.App.Views.SystemSettingsWindow(sysCfg, () => { }, () => { }, 0);
        var sysContent = (FrameworkElement)sysWin.Content;
        sysWin.Content = null;
        Render(new Border { Width = 470, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)), Child = sysContent },
            @"C:\work\preview-syssettings.png");

        // 8) The reworked account dialog, three states via reflection.
        var s2 = new StockClient.App.Services.AccountSession(
            new StockClient.Core.Quotes.AccountClient(new System.Net.Http.HttpClient()),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qv-prev-acct2.json"));
        var lw = new StockClient.App.Views.LoginWindow(s2);
        var lt = typeof(StockClient.App.Views.LoginWindow);
        FrameworkElement F(string n) => (FrameworkElement)lt.GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lw)!;

        // a) signed-in with both sub-panels expanded (worst-case height)
        F("SignedInPanel").Visibility = Visibility.Visible;
        ((System.Windows.Controls.TextBlock)F("SignedInText")).Text = "已登录：admin";
        F("ChangePassPanel").Visibility = Visibility.Visible;
        F("FormPanel").Visibility = Visibility.Collapsed;
        F("SwitchPanel").Visibility = Visibility.Collapsed;
        var c1 = (FrameworkElement)lw.Content; lw.Content = null;
        Render(new Border { Width = 340, Background = new SolidColorBrush(Color.FromRgb(0x12,0x16,0x1F)), Child = c1 },
            @"C:\work\preview-login-signedin.png");

        // b) quick-switch list
        var lw2 = new StockClient.App.Views.LoginWindow(s2);
        FrameworkElement F2(string n) => (FrameworkElement)lt.GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lw2)!;
        F2("SignedInPanel").Visibility = Visibility.Collapsed;
        F2("FormPanel").Visibility = Visibility.Collapsed;
        var list = (System.Windows.Controls.StackPanel)F2("SavedList");
        foreach (var name in new[] { "admin", "trader02" })
            list.Children.Add(new System.Windows.Controls.Button
            {
                Content = name, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 4, 10, 4),
            });
        F2("SwitchPanel").Visibility = Visibility.Visible;
        var c2 = (FrameworkElement)lw2.Content; lw2.Content = null;
        Render(new Border { Width = 340, Background = new SolidColorBrush(Color.FromRgb(0x12,0x16,0x1F)), Child = c2 },
            @"C:\work\preview-login-switch.png");

        Console.WriteLine("OK");
    }

    private static void Render(FrameworkElement el, string path)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(new Point(), el.DesiredSize));
        el.UpdateLayout();

        var w = (int)Math.Ceiling(el.ActualWidth);
        var h = (int)Math.Ceiling(el.ActualHeight);
        var rtb = new RenderTargetBitmap(Math.Max(w, 1), Math.Max(h, 1), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"{path} {w}x{h}");
    }
}
