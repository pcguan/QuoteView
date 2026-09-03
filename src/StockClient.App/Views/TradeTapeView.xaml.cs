using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// A 成交明细 (逐笔) tape: 时间 · 价 · 量(手) in chronological order (earliest at
/// top, newest at bottom), coloured by active side (红买 / 绿卖 / 灰中), with 大单
/// (成交额 ≥ the 万元 threshold) given an amber wash and bold. Virtualized, so a
/// full running day (a few thousand rows) refreshes cheaply.
///
/// Shared by the live K-line window and the historical replay in 历史分时对比. The
/// live tape sticks to the newest row unless the reader has scrolled up to study
/// earlier trades — so a 5s refresh never yanks the viewport; the historical
/// replay starts at the open.
/// </summary>
public partial class TradeTapeView : UserControl
{
    private ScrollViewer? _scroll;

    public TradeTapeView() => InitializeComponent();

    private ScrollViewer? Scroll => _scroll ??= List.Template?.FindName("TapeScroll", List) as ScrollViewer;

    /// <param name="bigTradeWan">成交额 万元 threshold for the 大单 highlight; 0 disables.</param>
    /// <param name="stickToNewest">Live tape: keep the newest (bottom) row in view
    /// across refreshes unless the reader scrolled up. False = historical replay,
    /// which parks at the open (top).</param>
    public void SetTicks(IReadOnlyList<TradeTick> ticks, int decimals, int bigTradeWan,
        bool stickToNewest = true)
    {
        var sv = Scroll;
        var oldOffset = sv?.VerticalOffset ?? 0;
        var wasAtBottom = sv is null || sv.ScrollableHeight <= 0
            || sv.VerticalOffset >= sv.ScrollableHeight - 4;

        var rows = new List<Row>(ticks.Count);
        foreach (var t in ticks)   // chronological — earliest first
        {
            var big = TradeColors.IsBig(t, bigTradeWan);
            rows.Add(new Row(
                t.Time,
                t.Price.ToString("F" + decimals),
                t.Volume.ToString(),
                TradeColors.For(t.Side, big),
                big ? FontWeights.SemiBold : FontWeights.Normal));
        }
        // Replacing ItemsSource resets the viewport to the top, so restore the
        // reader's place AFTER the new rows lay out. Chronological order means
        // new rows only ever append at the bottom, so earlier rows keep their
        // index — restoring the old offset holds the same trades in view.
        List.ItemsSource = rows;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var s = Scroll;
            if (s is null) return;
            if (!stickToNewest) s.ScrollToTop();          // historical replay parks at the open
            else if (wasAtBottom) s.ScrollToBottom();     // live: follow the newest
            else s.ScrollToVerticalOffset(oldOffset);     // live: reader scrolled up — stay put
        }), DispatcherPriority.Background);
    }

    public void Clear() => List.ItemsSource = null;

    /// <summary>One tape line, pre-shaped for the virtualized item template.</summary>
    private sealed record Row(string Time, string Price, string Volume, Brush Fg, FontWeight Weight);
}
