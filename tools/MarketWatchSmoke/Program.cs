using System.IO;
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
            try { PanelMouseGestureChecks.Run(); TestLayouts(); await TestPolling(dispatcher); await TestExtensionIsolation(dispatcher); await TestBoards(dispatcher); await TapeScrollChecks.Run(); await OverseasAddChecks.Run(dispatcher); Console.WriteLine("PASS: layout persistence/sync/reset, header alignment, shared refresh/cancel/cooldown; no desktop windows opened"); }
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
        var gainers = Grid(); var losers = Grid(); var otherGainers = Grid();
        using (var gains = new MarketColumnLayout(gainers, "board-gainers"))
        using (var losses = new MarketColumnLayout(losers, "board-losers"))
        using (var synced = new MarketColumnLayout(otherGainers, "board-gainers"))
        {
            gainers.Columns[1].Header = "自定义金额";
            gainers.Columns[1].Width = 222;
            gainers.Columns[1].DisplayIndex = 0;
            gainers.Columns[0].Visibility = Visibility.Collapsed;
            gains.Flush();
            Check(otherGainers.Columns[1].Header as string == "自定义金额" && otherGainers.Columns[1].Width.Value == 222
                && otherGainers.Columns[1].DisplayIndex == 0 && otherGainers.Columns[0].Visibility == Visibility.Collapsed, "board layouts must sync across windows");
            Check(losers.Columns[1].Header as string == "金额" && losers.Columns[1].Width.Value == 120
                && AppPrefs.GetColumns("market").Single(c => c.Key == "Amount").Width == 237, "separate ranking/market layout scopes");
            gains.Reset();
            Check(otherGainers.Columns[1].Header as string == "金额" && otherGainers.Columns[0].Visibility == Visibility.Visible, "rename reset must sync");
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
        Check(AppPrefs.GetColumns("market").Single(c => c.Key == "Code").Width == 199, "close must flush");
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
        var root = Path.Combine(Path.GetTempPath(), "qv-market-vm-" + Guid.NewGuid());
        using var http = DirectHttp.Create(TimeSpan.FromSeconds(10));
        using var vm = new MarketWatchViewModel(dispatcher, liveClient: new StockClient.Core.MarketWatch.LiveMarketClient(http, root));
        var tab = new object(); var window = new object();
        vm.SetActive(tab, true);
        vm.SetActive(window, true);
        vm.SetActive(tab, false);
        Check(handler.Calls == 1 && vm.Busy && handler.Canceled == 0, "second view must keep the single shared request alive");
        Check(vm.EmptyMessage.Contains("加载"), "loading must not look like an empty result");
        handler.Ready.SetResult(true);
        await Idle(vm);
        Check(vm.Rows.Count == 1 && vm.Rows[0].Price == 12.34, "first snapshot must populate rows");
        var completedCalls = handler.Calls;
        vm.SetActive(window, false);
        Check(vm.Schedule == "已暂停", "last view must stop polling");
        vm.SetActive(tab, true);
        Check(handler.Calls == completedCalls && vm.Rows.Count == 1 && !vm.CanRefresh, "reopening must preserve snapshot and cooldown");
        vm.SetActive(tab, false);

        handler = DirectHttp.Handler = new PendingHandler();
        using var cancelHttp = DirectHttp.Create(TimeSpan.FromSeconds(10));
        using var canceled = new MarketWatchViewModel(dispatcher, liveClient: new StockClient.Core.MarketWatch.LiveMarketClient(cancelHttp, root + "-cancel"));
        canceled.SetActive(tab, true);
        canceled.SetActive(tab, false);
        await Idle(canceled);
        Check(handler.Canceled == 1 && canceled.Schedule == "已暂停" && !canceled.Status.Contains("失败"), "closing last view must cancel without backoff failure");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static async Task TestExtensionIsolation(Dispatcher dispatcher)
    {
        // Hold EastMoney indefinitely while two independent Tencent polls complete.
        var extensions = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = DirectHttp.Handler = new PendingHandler { ResponseAsync = (_, ct) => extensions.Task.WaitAsync(ct) };
        handler.Ready.SetResult(true);
        var calls = 0;
        var contract = new StockClient.Core.Contracts.Contract { Code = "SH600000", Name = "测试", Type = StockClient.Core.Contracts.ContractType.MainBoard };
        var live = new StockClient.Core.MarketWatch.LiveMarketClient((_, _) => Task.FromResult(
            new StockClient.Core.MarketWatch.MarketUniverse(new[] { contract }, new DateOnly(2026, 10, 22), "test")), (codes, _) =>
            Task.FromResult<IReadOnlyList<StockClient.Core.Quotes.Quote>>(new[] { new StockClient.Core.Quotes.Quote
                { Code = codes[0], Name = "测试", Now = 10 + ++calls } }));
        using var vm = new MarketWatchViewModel(dispatcher, liveClient: live);
        var view = new object(); vm.SetActive(view, true); await Idle(vm);
        Check(vm.Rows[0].Price == 11 && vm.SnapshotInfo.Contains("扩展更新中"), "slow enrichment must not hold base quotes hostage");
        // Advance only the guard, avoiding a real minute wait in this isolated harness.
        typeof(MarketWatchViewModel).GetField("_allowedAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(vm, DateTimeOffset.MinValue);
        vm.RefreshCommand.Execute(null); await Idle(vm);
        Check(vm.Rows[0].Price == 12 && handler.Calls == 1, "base refresh must continue without starting another enrichment");
        extensions.SetException(new System.Net.Http.HttpRequestException("extension connection closed"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.SnapshotInfo.Contains("扩展更新失败") && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(vm.Rows[0].Price == 12 && vm.ErrorDetail.Contains("extension connection closed")
            && vm.Rows[0].ExtensionStatus.Contains("失败") && vm.Status.Contains("已更新"), "extension failure must be visible without failing base refresh");
        vm.SetActive(view, false);
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
        var thisView = new object();
        rankings.SetActive(thisView, true);
        var secondView = new object();
        rankings.SetActive(secondView, true);
        rankings.SetActive(thisView, false);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rankings.Busy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(!rankings.Busy && rankings.Gainers.Count == 1 && rankings.Losers.Count == 1 && rankings.Neutral.Count == 1, "board rankings populated incorrectly");
        rankings.SelectedKind = rankings.Kinds[1];
        Check(rankings.Gainers.Single().Code == "BK0004" && rankings.Losers.Count == 0 && handler.Calls == 3, "category switch should use same complete snapshot");
        rankings.Keyword = "不存在";
        Check(rankings.Gainers.Count == 0, "board keyword filtering");
        rankings.SetActive(secondView, false);
        Check(rankings.Schedule == "已暂停", "board timer must stop when hidden");

        var sharedBoard = new StockClient.Core.Boards.Board { Code = "BK1234", Name = "共享", Kind = StockClient.Core.Boards.BoardKind.Industry };
        var firstMembers = rankings.GetMembers(thisView, sharedBoard);
        Check(ReferenceEquals(firstMembers, rankings.GetMembers(secondView, sharedBoard)), "two views of one board must share member polling");
        for (var i = 0; i < 12; i++)
            rankings.GetMembers(thisView, sharedBoard with { Code = "BK" + (2000 + i) });
        Check(ReferenceEquals(firstMembers, rankings.GetMembers(secondView, sharedBoard)), "LRU must retain a hidden view's bound model");
        rankings.ReleaseMembers(thisView);
        rankings.ReleaseMembers(secondView);

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
