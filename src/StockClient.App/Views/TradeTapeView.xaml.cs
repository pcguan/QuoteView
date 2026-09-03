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
    /// <param name="newestFirst">Live tape: newest print at the top, holding the
    /// reader's place as new prints arrive. False = historical replay, chronological
    /// and parked at the open (top).</param>
    public void SetTicks(IReadOnlyList<TradeTick> ticks, int decimals, int bigTradeWan,
        double prePrice = 0, bool newestFirst = true)
    {
        var sv = Scroll;
        var oldOffset = sv?.VerticalOffset ?? 0;
        var oldCount = List.Items.Count;
        var wasAtTop = oldOffset <= 4;

        // Direction/colour are computed in TIME order (each print vs the one
        // before it), then the list is flipped for display if newest-first.
        var rows = new List<Row>(ticks.Count);
        var carry = TradeColors.Flat;
        var prev = prePrice;
        foreach (var t in ticks)   // chronological — earliest first
        {
            var (priceFg, arrow) = TradeColors.PriceLook(t.Price, prev, ref carry);
            prev = t.Price;
            var big = TradeColors.IsBig(t, bigTradeWan);
            rows.Add(new Row(
                t.Time,
                t.Price.ToString("F" + decimals) + arrow,
                t.Volume.ToString(),
                priceFg,
                TradeColors.Volume(t.Side, big)));
        }
        if (newestFirst) rows.Reverse();   // newest print at the top

        List.ItemsSource = rows;

        // Replacing ItemsSource resets the viewport to the top; restore the
        // reader's place after the new rows lay out.
        var delta = rows.Count - oldCount;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var s = Scroll;
            if (s is null) return;
            if (!newestFirst) { s.ScrollToTop(); return; }   // historical replay parks at the open
            // Newest-first live tape: sit at the top to follow the latest, or —
            // when the reader has scrolled down into history — hold their place
            // (new prints arrive at the top, so shift the offset by how many).
            if (wasAtTop) s.ScrollToTop();
            else s.ScrollToVerticalOffset(Math.Max(0, oldOffset + delta));
        }), DispatcherPriority.Background);
    }

    public void Clear() => List.ItemsSource = null;

    /// <summary>One tape line, pre-shaped for the virtualized item template.</summary>
    private sealed record Row(string Time, string Price, string Volume, Brush PriceFg, Brush VolFg);
}
