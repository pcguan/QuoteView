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
            try { TestLayouts(); await TestPolling(dispatcher); await TestBoards(dispatcher); Console.WriteLine("PASS: layout persistence/sync/reset, header alignment, shared refresh/cancel/cooldown; no desktop windows opened"); }
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
    private static async Task TestBoards(Dispatcher dispatcher)
    {
        var handler = DirectHttp.Handler = new PendingHandler
        {
            Response = r => r.RequestUri!.Query.Contains("t:2")
                ? """{"data":{"total":3,"diff":[{"f12":"BK0001","f14":"上涨","f3":5},{"f12":"BK0002","f14":"下跌","f3":-3},{"f12":"BK0003","f14":"平盘","f3":0}]}}"""
                : r.RequestUri.Query.Contains("t:3")
                    ? """{"data":{"total":1,"diff":[{"f12":"BK0004","f14":"概念","f3":2}]}}"""
                    : """{"data":{"total":1,"diff":[{"f12":"BK0005","f14":"地区","f3":"-"}]}}""",
        };
        handler.Ready.SetResult(true);
        using var rankings = new BoardWatchViewModel(dispatcher);
        rankings.SetActive(true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rankings.Busy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(!rankings.Busy && rankings.Gainers.Count == 1 && rankings.Losers.Count == 1 && rankings.Neutral.Count == 1, "board rankings populated incorrectly");
        rankings.SelectedKind = rankings.Kinds[1];
        Check(rankings.Gainers.Single().Code == "BK0004" && rankings.Losers.Count == 0 && handler.Calls == 3, "category switch should use same complete snapshot");
        rankings.Keyword = "不存在";
        Check(rankings.Gainers.Count == 0, "board keyword filtering");
        rankings.SetActive(false);
        Check(rankings.Schedule == "已暂停", "board timer must stop when hidden");

        handler = DirectHttp.Handler = new PendingHandler
        {
            Response = _ => """{"data":{"total":1,"diff":[{"f12":"920001","f13":0,"f14":"北交所测试","f2":12.34}]}}""",
        };
        handler.Ready.SetResult(true);
        using var members = new MarketWatchViewModel(dispatcher, new StockClient.Core.Boards.Board
            { Code = "BK1033", Name = "电池", Kind = StockClient.Core.Boards.BoardKind.Industry });
        var view = new object();
        members.SetActive(view, true);
        await Idle(members);
        Check(members.Rows.Count == 1 && members.SupportsBeijing && members.Title.Contains("电池"), "board detail must show all markets by default");
        members.IncludeBeijing = false;
        Check(members.Rows.Count == 0, "Beijing filter");
        members.ClearCommand.Execute(null);
        Check(members.Rows.Count == 1 && members.IncludeBeijing, "clear must restore all board members");
        members.SetActive(view, false);

        var oldHandler = DirectHttp.Handler = new PendingHandler();
        using var oldBoard = new MarketWatchViewModel(dispatcher, new StockClient.Core.Boards.Board
            { Code = "BK0001", Name = "旧板块", Kind = StockClient.Core.Boards.BoardKind.Industry });
        oldBoard.SetActive(view, true);
        oldBoard.SetActive(view, false);
        var newHandler = DirectHttp.Handler = new PendingHandler();
        newHandler.Ready.SetResult(true);
        using var newBoard = new MarketWatchViewModel(dispatcher, new StockClient.Core.Boards.Board
            { Code = "BK0002", Name = "新板块", Kind = StockClient.Core.Boards.BoardKind.Industry });
        newBoard.SetActive(view, true);
        oldHandler.Ready.SetResult(true);
        await Idle(oldBoard);
        await Idle(newBoard);
        Check(oldHandler.Canceled == 1 && oldBoard.Rows.Count == 0 && newBoard.Rows.Count == 1
            && newBoard.Title.Contains("新板块"), "switching boards must cancel old request without mixing rows");
        newBoard.SetActive(view, false);
    }

}
