using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StockClient.App;
using StockClient.App.Services;
using StockClient.App.ViewModels;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? error = null;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { TestLayouts(); await TestPolling(dispatcher); Console.WriteLine("PASS: layout persistence/sync/reset, header alignment, shared refresh/cancel/cooldown; no desktop windows opened"); }
            catch (Exception ex) { error = ex; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (error is not null) { Console.Error.WriteLine(error); return 1; }
        return 0;
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); }

    private static DataGrid Grid(string caption = "金额")
    {
        var grid = new DataGrid();
        grid.Columns.Add(new DataGridTextColumn { Header = "代码", SortMemberPath = "Code", Width = 100 });
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        grid.Columns.Add(new DataGridTextColumn { Header = caption, SortMemberPath = "Amount", Width = 120, ElementStyle = style });
        return grid;
    }

    private static void TestLayouts()
    {
        var first = Grid();
        using (var layout = new MarketColumnLayout(first))
        {
            var second = Grid();
            using var other = new MarketColumnLayout(second);
            first.Columns[1].Width = 237;
            first.Columns[1].DisplayIndex = 0;
            first.Columns[0].Visibility = Visibility.Collapsed;
            layout.Flush();
            Check(second.Columns[1].Width.Value == 237 && second.Columns[1].DisplayIndex == 0
                && second.Columns[0].Visibility == Visibility.Collapsed, "layout not shared");
            ColumnMenu.Attach(first);
            Check(first.Columns[1].HeaderStyle.Setters.OfType<Setter>().Any(s =>
                s.Property == Control.HorizontalContentAlignmentProperty && s.Value is HorizontalAlignment.Right), "header alignment");
        }
        var reopened = Grid("成交额");
        using var restored = new MarketColumnLayout(reopened);
        Check(reopened.Columns[1].Width.Value == 237 && reopened.Columns[1].DisplayIndex == 0
            && reopened.Columns[0].Visibility == Visibility.Collapsed, "stable keys must survive caption changes/reopen");
        restored.Reset();
        Check(reopened.Columns[1].Width.Value == 120 && reopened.Columns[1].DisplayIndex == 1
            && reopened.Columns.All(c => c.Visibility == Visibility.Visible), "default reset");
        reopened.Columns[0].Width = 199;
        restored.Dispose(); // A close before the debounce must still save.
        Check(AppPrefs.MarketColumns.Single(c => c.Key == "Code").Width == 199, "close must flush");
    }

    private static async Task Idle(MarketWatchViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.Busy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(!vm.Busy, "refresh did not finish");
    }

    private static async Task TestPolling(Dispatcher dispatcher)
    {
        var handler = DirectHttp.Handler = new PendingHandler();
        using var vm = new MarketWatchViewModel(dispatcher);
        var tab = new object(); var window = new object();
        vm.SetActive(tab, true);
        vm.SetActive(window, true);
        vm.SetActive(tab, false);
        Check(handler.Calls == 1 && vm.Busy && handler.Canceled == 0, "second view must keep the single shared request alive");
        Check(vm.EmptyMessage.Contains("加载"), "loading must not look like an empty result");
        handler.Ready.SetResult(true);
        await Idle(vm);
        Check(vm.Rows.Count == 1 && vm.Rows[0].Price == 12.34, "first snapshot must populate rows");
        vm.SetActive(window, false);
        Check(vm.Schedule == "已暂停", "last view must stop polling");
        vm.SetActive(tab, true);
        Check(handler.Calls == 1 && vm.Rows.Count == 1 && !vm.CanRefresh, "reopening must preserve snapshot and cooldown");
        vm.SetActive(tab, false);

        handler = DirectHttp.Handler = new PendingHandler();
        using var canceled = new MarketWatchViewModel(dispatcher);
        canceled.SetActive(tab, true);
        canceled.SetActive(tab, false);
        await Idle(canceled);
        Check(handler.Canceled == 1 && canceled.Schedule == "已暂停" && !canceled.Status.Contains("失败"), "closing last view must cancel without backoff failure");
    }
}
