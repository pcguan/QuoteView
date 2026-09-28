using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    // A stable collection bound once: refreshes ADD only the new prints instead of
    // replacing ItemsSource, so existing rows (and the reader's scroll) aren't torn
    // down and rebuilt every poll — that wholesale rebuild was the flicker.
    private readonly ObservableCollection<Row> _obs = new();
    private int _rendered;        // ticks already turned into rows
    private double _lastPrice;    // newest rendered price, to chain the ↑↓ arrow onto appends
    private string? _lastTime;    // newest rendered time, to recognise a plain append
    private int _dec = -1;
    private int _big = -1;
    private double _pre = double.NaN;
    private bool _nf = true;

    public TradeTapeView()
    {
        InitializeComponent();
        List.ItemsSource = _obs;
    }

    private ScrollViewer? Scroll => _scroll ??= List.Template?.FindName("TapeScroll", List) as ScrollViewer;

    /// <param name="bigTradeWan">成交额 万元 threshold for the 大单 highlight; 0 disables.</param>
    /// <param name="newestFirst">Live tape: newest print at the top, holding the
    /// reader's place as new prints arrive. False = historical replay, chronological
    /// and parked at the open (top).</param>
    public void SetTicks(IReadOnlyList<TradeTick> ticks, int decimals, int bigTradeWan,
        double prePrice = 0, bool newestFirst = true)
    {
        var paramsSame = decimals == _dec && bigTradeWan == _big
                         && prePrice.Equals(_pre) && newestFirst == _nf;

        // Nothing new (the feed is 3s-sampled, so most polls repeat) → don't touch the
        // list at all. This is what stops the tape flickering on every refresh.
        if (paramsSame && ticks.Count == _rendered
            && (ticks.Count == 0 || ticks[^1].Time == _lastTime))
            return;

        // Append fast-path: same shape, only grew, old tail still matches → render just
        // the new prints and slide them in; existing rows stay put.
        var append = paramsSame && ticks.Count > _rendered && _rendered > 0
                     && ticks[_rendered - 1].Time == _lastTime;

        if (!append)
        {
            _obs.Clear();
            _rendered = 0;
            _lastPrice = prePrice;
            _dec = decimals; _big = bigTradeWan; _pre = prePrice; _nf = newestFirst;
        }

        var sv = Scroll;
        var oldOffset = sv?.VerticalOffset ?? 0;
        var wasAtTop = oldOffset <= 4;

        // 成交价 colour is vs 昨收 (prePrice); the ↑↓ arrow is vs the previous print —
        // chained onto _lastPrice so an append picks up where the last row left off.
        var prev = _lastPrice;
        var fresh = new List<Row>(ticks.Count - _rendered);
        for (var i = _rendered; i < ticks.Count; i++)
        {
            var t = ticks[i];
            var (priceFg, arrow) = TradeColors.PriceLook(t.Price, prePrice, prev);
            prev = t.Price;
            fresh.Add(new Row(
                t.Time,
                t.Price.ToString("F" + decimals) + arrow,
                t.Volume.ToString(),
                priceFg,
                TradeColors.Volume(t.Side, TradeColors.IsBig(t, bigTradeWan))));
        }

        if (newestFirst)
            for (var k = 0; k < fresh.Count; k++) _obs.Insert(k, fresh[fresh.Count - 1 - k]);
        else
            foreach (var r in fresh) _obs.Add(r);

        _rendered = ticks.Count;
        if (ticks.Count > 0) { _lastPrice = ticks[^1].Price; _lastTime = ticks[^1].Time; }

        // Hold the reader's place. Newest-first: new prints came in at the top, so a
        // reader who'd scrolled into history shifts down by that many; a reader at the
        // top keeps following the latest. Chronological replay just parks at the open.
        if (sv is not null)
        {
            if (!newestFirst) sv.ScrollToTop();
            else if (wasAtTop) sv.ScrollToTop();
            else sv.ScrollToVerticalOffset(Math.Max(0, oldOffset + fresh.Count));
        }
    }

    public void Clear()
    {
        _obs.Clear();
        _rendered = 0;
        _lastTime = null;
        _dec = _big = -1;
        _pre = double.NaN;
    }

    /// <summary>Snap back to the top — the newest print on a live (newest-first)
    /// tape. After this the tape is "at top" again, so it resumes following the
    /// newest as fresh prints arrive.</summary>
    public void ScrollToTop() => Scroll?.ScrollToTop();

    /// <summary>One tape line, pre-shaped for the virtualized item template.</summary>
    private sealed record Row(string Time, string Price, string Volume, Brush PriceFg, Brush VolFg);
}
