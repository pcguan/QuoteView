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
        var window = new StealthSettingsWindow(config, () => { }, () => { });
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
