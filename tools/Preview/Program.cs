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
        var window = new StealthSettingsWindow(
            config, new List<StockClient.Core.Groups.NamedStealthTemplate>(), () => { }, () => { });
        var content = (FrameworkElement)window.Content;
        window.Content = null;   // detach before re-parenting
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
        var panel = (FrameworkElement)typeof(StealthSettingsWindow)
            .GetField("FieldsPanel", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(window)!;
        if (LogicalTreeHelper.GetParent(panel) is ContentControl owner) owner.Content = null;
        Render(new Border
        {
            Width = 372,
            Padding = new Thickness(0, 6, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
            Child = panel,
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
            var t = new DateTime(2026, 8, 21, 9, 30, 0).AddMinutes(i <= 120 ? i : i + 90);
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
                AvgPrice = 0, Volume = 3000,
            });
        }
        var cmpSeries = new StockClient.Core.Quotes.TrendSeries
            { Code = "SH600519", Name = "贵州茅台", PreClose = 102.2, Points = pts2 };

        var history3 = new StockClient.App.Views.TrendHistoryView { Width = 1050, Height = 470 };
        var t3 = typeof(StockClient.App.Views.TrendHistoryView);
        var chart3 = (StockClient.App.Views.TrendChart)t3.GetField("Chart", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history3)!;
        chart3.SetSeries(fakeSeries);
        chart3.SetCompare(cmpSeries);
        var sm = (System.Windows.Controls.TextBlock)t3.GetField("SummaryMain", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history3)!;
        var sc = (System.Windows.Controls.TextBlock)t3.GetField("SummaryCompare", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history3)!;
        sm.Text = "2026-08-24  涨跌幅 -6.07%  成交额 83.44亿  成交量 111.99万手  外盘 51.70万  内盘 60.29万";
        sm.Visibility = Visibility.Visible;
        sc.Text = "2026-08-21  涨跌幅 +1.20%  成交额 65.02亿  成交量 88.10万手  外盘 45.00万  内盘 43.10万";
        sc.Visibility = Visibility.Visible;
        Render(new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)), Child = history3 },
            @"C:\work\preview-compare.png");

        // 7) Settings window with the template bar.
        var config2 = StealthConfig.CreateDefault();
        var tmpls = new List<StockClient.Core.Groups.NamedStealthTemplate>
        {
            new() { Name = "白天高亮", Stealth = StealthConfigOps.Clone(config2) },
            new() { Name = "夜间低调", Stealth = StealthConfigOps.Clone(config2) },
        };
        var win2 = new StealthSettingsWindow(config2, tmpls, () => { }, () => { });
        var content2 = (FrameworkElement)win2.Content;
        win2.Content = null;
        Render(new Border { Width = 430, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)), Child = content2 },
            @"C:\work\preview-templates.png");

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
