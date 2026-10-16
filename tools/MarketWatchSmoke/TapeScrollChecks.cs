using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StockClient.App.Views;
using StockClient.Core.Quotes;

internal static class TapeScrollChecks
{
    private static TradeTick Tick(int second) => new()
    {
        Time = TimeOnly.FromTimeSpan(TimeSpan.FromHours(9.5) + TimeSpan.FromSeconds(second)).ToString("HH:mm:ss"),
        Price = 10 + second / 100.0, Volume = second + 1, Side = TradeSide.Buy,
    };
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); }
    private static async Task Settle(TradeTapeView tape)
    {
        for (var i = 0; i < 3; i++)
        {
            tape.Measure(new Size(260, 350));
            tape.Arrange(new Rect(0, 0, 260, 350));
            tape.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
    private static (ScrollViewer Scroll, ItemsControl List) Scroller(TradeTapeView tape)
    {
        var list = (ItemsControl)tape.FindName("List");
        list.ApplyTemplate();
        return ((ScrollViewer)list.Template.FindName("TapeScroll", list), list);
    }
    private static string Top(ScrollViewer scroll, ItemsControl list) =>
        list.Items[Math.Min(list.Items.Count - 1, (int)Math.Floor(scroll.VerticalOffset))].GetType()
            .GetProperty("Time")!.GetValue(list.Items[(int)Math.Floor(scroll.VerticalOffset)])!.ToString()!;

    public static async Task Run()
    {
        var tape = new TradeTapeView { Width = 260, Height = 350 };
        var ticks = Enumerable.Range(100, 200).Select(Tick).ToArray();
        tape.SetTicks(ticks, 2, 100, 10);
        await Settle(tape);
        var (scroll, list) = Scroller(tape);
        Check(scroll.ScrollableHeight > 150 && scroll.ViewportHeight > 0, "headless tape must have a virtualized scroll viewport");
        scroll.ScrollToVerticalOffset(10);
        await Settle(tape);
        var anchor = Top(scroll, list);
        var filled = Enumerable.Range(0, 300).Select(Tick).ToArray();
        tape.SetTicks(filled, 2, 100, 10);
        await Settle(tape);
        Check(Top(scroll, list) == anchor, $"late-history fill moved reader: {anchor} -> {Top(scroll, list)}, offset={scroll.VerticalOffset}");
        // Even a single logical row is browsing, not live-follow mode.
        scroll.ScrollToVerticalOffset(1);
        await Settle(tape);
        anchor = Top(scroll, list);
        var grown = Enumerable.Range(0, 301).Select(Tick).ToArray();
        tape.SetTicks(grown, 2, 100, 10);
        await Settle(tape);
        Check(Top(scroll, list) == anchor && scroll.VerticalOffset == 2, "one-row scroll must survive a live append");

        // Backfill NEWER than the anchor, not just the oldest tail.
        var sparse = Enumerable.Range(0, 300).Where(i => i != 285).Select(Tick).ToArray();
        tape.SetTicks(sparse, 2, 100, 10);
        await Settle(tape);
        scroll.ScrollToVerticalOffset(30);
        await Settle(tape);
        anchor = Top(scroll, list);
        tape.SetTicks(filled, 2, 100, 10);
        await Settle(tape);
        Check(Top(scroll, list) == anchor, "middle gap fill must preserve visible trade");
        tape.SetTicks(filled, 3, 200, 9.9);
        await Settle(tape);
        Check(Top(scroll, list) == anchor, "recolor/decimal/highlight changes must preserve visible trade");
        var revised = filled.Select(t => t.Time == anchor ? t with { Volume = 7777, Side = TradeSide.Sell } : t).ToArray();
        tape.SetTicks(revised, 3, 200, 9.9);
        await Settle(tape);
        Check(Top(scroll, list) == anchor && list.Items[(int)scroll.VerticalOffset].GetType().GetProperty("Volume")!
            .GetValue(list.Items[(int)scroll.VerticalOffset])!.ToString() == "7777", "same-count volume corrections must update in place");

        // Two refreshes before a dispatcher layout use the same trade anchor.
        tape.SetTicks(Enumerable.Range(0, 302).Select(Tick).ToArray(), 3, 200, 9.9);
        tape.SetTicks(Enumerable.Range(0, 303).Select(Tick).ToArray(), 3, 200, 9.9);
        await Settle(tape);
        Check(Top(scroll, list) == anchor, "queued refreshes must not accumulate incorrect offsets");

        scroll.ScrollToBottom();
        await Settle(tape);
        anchor = Top(scroll, list);
        tape.SetTicks(Enumerable.Range(0, 360).Select(Tick).ToArray(), 3, 200, 9.9);
        await Settle(tape);
        Check(Top(scroll, list) == anchor, "large append while browsing oldest trades must preserve the visible record");

        tape.ScrollToTop();
        await Settle(tape);
        tape.SetTicks(Enumerable.Range(0, 304).Select(Tick).ToArray(), 3, 200, 9.9);
        await Settle(tape);
        Check(scroll.VerticalOffset == 0 && Top(scroll, list) == Tick(303).Time, "return-to-latest must resume following");

        // A deliberate user scroll cancels a pending programmatic restore.
        scroll.ScrollToVerticalOffset(10);
        await Settle(tape);
        tape.SetTicks(Enumerable.Range(0, 305).Select(Tick).ToArray(), 3, 200, 9.9);
        tape.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        scroll.ScrollToVerticalOffset(40);
        await Settle(tape);
        Check(scroll.VerticalOffset == 40, "user navigation must win over a queued refresh");

        tape.SetTicks(filled, 2, 100, 10, sourceKey: "another-contract");
        await Settle(tape);
        Check(scroll.VerticalOffset == 0, "contract switch must reset to newest");
        tape.SetTicks(grown, 2, 100, 10, sourceKey: "another-contract");
        tape.Clear();
        await Settle(tape);
        Check(list.Items.Count == 0 && scroll.VerticalOffset == 0, "clear must cancel pending restores");

        tape.SetTicks(filled, 2, 100, 10, newestFirst: false);
        await Settle(tape);
        scroll.ScrollToVerticalOffset(20);
        await Settle(tape);
        anchor = Top(scroll, list);
        tape.SetTicks(grown, 2, 100, 10, newestFirst: false);
        await Settle(tape);
        Check(Top(scroll, list) == anchor, "chronological replay must preserve the reader's position");
        tape.Clear();
        Console.WriteLine("PASS: tape gap-fill/append/one-row browse/recolor/revision/queued refresh/navigation/clear/replay scroll retention; no window opened");
    }
}
