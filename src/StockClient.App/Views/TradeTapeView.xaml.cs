using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Virtualized 成交明细. Live rows are newest first; new data follows only while
/// parked at the top. Otherwise the visible trade, rather than its old row index,
/// anchors the viewport across appends, corrections and historical gap fills.
/// </summary>
public partial class TradeTapeView : UserControl
{
    private ScrollViewer? _scroll;
    private readonly ObservableCollection<Row> _obs = new();
    private IReadOnlyList<TradeTick> _ticks = Array.Empty<TradeTick>();
    private int _dec = -1, _big = -1;
    private double _pre = double.NaN;
    private bool _nf = true;
    private string? _sourceKey;
    private ViewportAnchor? _pendingAnchor;
    private DispatcherOperation? _restore;

    public TradeTapeView()
    {
        InitializeComponent();
        List.ItemsSource = _obs;
        // A user's new navigation always wins over a queued refresh restore.
        PreviewMouseWheel += (_, _) => CancelRestore();
        PreviewMouseDown += (_, _) => CancelRestore();
        PreviewKeyDown += (_, _) => CancelRestore();
        PreviewTouchDown += (_, _) => CancelRestore();
        Unloaded += (_, _) => CancelRestore();
    }

    private ScrollViewer? Scroll => _scroll ??= List.Template?.FindName("TapeScroll", List) as ScrollViewer;

    /// <param name="sourceKey">Contract/session identity, used to reset to the start when switching sources.</param>
    public void SetTicks(IReadOnlyList<TradeTick> ticks, int decimals, int bigTradeWan,
        double prePrice = 0, bool newestFirst = true, string? sourceKey = null)
    {
        var sameSource = sourceKey == _sourceKey && newestFirst == _nf;
        var paramsSame = sameSource && decimals == _dec && bigTradeWan == _big && prePrice.Equals(_pre);
        if (paramsSame && ticks.SequenceEqual(_ticks)) return;

        // Capture BEFORE changing the collection. Logical scroll offsets are rows,
        // not pixels: even one row away from the live top is a deliberate browse.
        var anchor = sameSource ? _pendingAnchor ?? CaptureAnchor() : ViewportAnchor.Start;
        _restore?.Abort();
        _dec = decimals; _big = bigTradeWan; _pre = prePrice; _nf = newestFirst; _sourceKey = sourceKey;

        var append = paramsSame && _ticks.Count > 0 && ticks.Count > _ticks.Count
                     && ticks.Take(_ticks.Count).SequenceEqual(_ticks);
        var start = append ? _ticks.Count : 0;
        var desired = new List<Row>(ticks.Count - start);
        var previous = append ? _ticks[^1].Price : prePrice;
        for (var i = start; i < ticks.Count; i++)
        {
            var tick = ticks[i];
            var (priceFg, arrow) = TradeColors.PriceLook(tick.Price, prePrice, previous);
            previous = tick.Price;
            desired.Add(new Row(tick, tick.Time, tick.Price.ToString("F" + decimals) + arrow,
                tick.Volume.ToString(), priceFg, TradeColors.Volume(tick.Side, TradeColors.IsBig(tick, bigTradeWan))));
        }
        if (newestFirst) desired.Reverse();

        if (append)
        {
            if (newestFirst)
                for (var i = 0; i < desired.Count; i++) _obs.Insert(i, desired[i]);
            else foreach (var row in desired) _obs.Add(row);
        }
        else
        {
            // Preserve existing containers. Backfilled trades are inserted where they
            // belong; revised volume/side/price arrows replace only affected rows.
            var oldKeys = _obs.Select(r => r.Key).ToHashSet();
            if (!sameSource || !desired.Any(r => oldKeys.Contains(r.Key))) _obs.Clear();
            for (var i = 0; i < desired.Count; i++)
            {
                var row = desired[i];
                if (i < _obs.Count && _obs[i].Key == row.Key)
                {
                    if (_obs[i] != row) _obs[i] = row;
                    continue;
                }
                var existing = oldKeys.Contains(row.Key) ? FindRow(row.Key, i + 1) : -1;
                if (existing >= 0)
                {
                    _obs.Move(existing, i);
                    if (_obs[i] != row) _obs[i] = row;
                }
                else _obs.Insert(i, row);
            }
            while (_obs.Count > desired.Count) _obs.RemoveAt(_obs.Count - 1);
        }
        _ticks = ticks.ToArray();
        RestoreAfterLayout(anchor);
    }

    private int FindRow((string Time, double Price) key, int start = 0)
    {
        for (var i = start; i < _obs.Count; i++) if (_obs[i].Key == key) return i;
        return -1;
    }

    private ViewportAnchor CaptureAnchor()
    {
        var offset = Scroll?.VerticalOffset ?? 0;
        if (_obs.Count == 0 || offset <= 0.01) return ViewportAnchor.Start;
        var index = Math.Clamp((int)Math.Floor(offset), 0, _obs.Count - 1);
        return new ViewportAnchor(_obs[index].Key, offset - Math.Floor(offset), offset);
    }

    private void RestoreAfterLayout(ViewportAnchor anchor)
    {
        _pendingAnchor = anchor;
        _restore = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _restore = null;
            _pendingAnchor = null;
            var sv = Scroll;
            if (sv is null) return;
            var index = anchor.Key is { } key ? FindRow(key) : -1;
            // A source can correct a trade's price: prefer the same timestamp if
            // its original (time, price) key no longer exists.
            if (index < 0 && anchor.Key is { } missing)
                index = _obs.Select((r, i) => (r, i)).Where(p => p.r.Time == missing.Time)
                    .Select(p => p.i).DefaultIfEmpty(-1).First();
            var target = anchor.Key is null ? 0 : index >= 0 ? index + anchor.Fraction : anchor.Offset;
            sv.ScrollToVerticalOffset(Math.Clamp(target, 0, sv.ScrollableHeight));
        }));
    }

    private void CancelRestore()
    {
        _restore?.Abort();
        _restore = null;
        _pendingAnchor = null;
    }

    public void Clear()
    {
        CancelRestore();
        _obs.Clear();
        _ticks = Array.Empty<TradeTick>();
        _dec = _big = -1; _pre = double.NaN; _sourceKey = null;
        Scroll?.ScrollToTop();
    }

    /// <summary>Explicitly return to live following at the newest row.</summary>
    public void ScrollToTop()
    {
        CancelRestore();
        Scroll?.ScrollToTop();
    }

    private sealed record ViewportAnchor((string Time, double Price)? Key, double Fraction, double Offset)
    { public static readonly ViewportAnchor Start = new(null, 0, 0); }

    private sealed record Row(TradeTick Tick, string Time, string Price, string Volume, Brush PriceFg, Brush VolFg)
    { public (string Time, double Price) Key => (Tick.Time, Tick.Price); }
}
