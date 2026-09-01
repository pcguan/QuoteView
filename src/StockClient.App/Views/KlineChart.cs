using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.App.ViewModels;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Candlestick + volume chart, drawn directly in OnRender.
///
/// Hand-drawn rather than a charting package: the app already builds its UI this
/// way (see StealthWindow), a candlestick with a shared-x volume subchart and a
/// crosshair is a tight fixed spec.
///
/// Two axes stacked on ONE shared x, never a dual-y overlay: price and volume
/// have unrelated scales. Price gets the top ~72%, volume the bottom.
///
/// Level-of-detail: the screen is only ~1000px wide, so drawing more candles than
/// there are pixel columns is both invisible and slow. When the visible window
/// exceeds the column budget, candles are aggregated into per-column "display
/// bars" (open = first, close = last, high/low = extremes, volume = sum) and
/// everything — candles, MAs, price range, hit-testing — runs off those. Drawing
/// cost is then bounded by screen width, not by how many years are on screen, so
/// zooming out to the full history stays smooth instead of pushing tens of
/// thousands of primitives per repaint.
/// </summary>
public sealed class KlineChart : FrameworkElement
{
    // Chinese market convention: red = up, green = down — the opposite of the
    // West. Verified as a colourblind-safe pair (deutan ΔE 11.6).
    private static readonly Brush UpBrush = Frozen("#EF5350");
    private static readonly Brush DownBrush = Frozen("#26A69A");

    // Candle outlines: only two, and a repaint draws one per bar (up to ~600 when
    // zoomed out). Frozen and shared, because an unfrozen Pen forces WPF to rebuild
    // its render resource on every frame.
    private static readonly Pen UpPen = FrozenPen(UpBrush, 1);
    private static readonly Pen DownPen = FrozenPen(DownBrush, 1);

    private static readonly Pen GridPen = FrozenPen("#222A38", 1);
    private static readonly Pen CrosshairPen = FrozenPen("#8B93A3", 1, dashed: true);
    private static readonly Brush AxisText = Frozen("#8B93A3");
    private static readonly Brush ReadoutBg = Frozen("#111722");
    private static readonly Brush ReadoutBorder = Frozen("#33405C");
    private static readonly Pen ReadoutBorderPen = FrozenPen(ReadoutBorder, 1);

    // MA5/10/20/60, in KlineViewModel.MaWindows order. Validated categorical set.
    private static readonly Brush[] MaBrushes =
    {
        Frozen("#3987e5"), Frozen("#c98500"), Frozen("#d55181"), Frozen("#9085e9"),
    };

    private static readonly Pen[] MaPens = MaBrushes.Select(b => FrozenPen(b, 1.4)).ToArray();

    /// <summary>Dimmed legend colour for a hidden MA, so its toggle is still visible.</summary>
    private static readonly Brush LegendOff = Frozen("#4A5160");

    private const double PadLeft = 8;
    private const double PadRight = 62;   // right-hand price axis labels
    private const double PadTop = 26;     // MA legend row
    private const double PadBottom = 22;  // date axis
    private const double VolumeFraction = 0.26;
    private const double GapFraction = 0.04;

    /// <summary>Candles shown by default, and the tightest/loosest the wheel allows.</summary>
    private const int DefaultView = 80;
    private const int MinView = 20;

    /// <summary>
    /// Minimum pixels per drawn bar. Below this, candles are indistinguishable, so
    /// the visible window is aggregated down to at most (plotWidth / this) bars.
    /// </summary>
    private const double MinSlotPx = 3;

    private static readonly Typeface Mono =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>One drawn unit: a single candle, or several aggregated into one column.</summary>
    private readonly struct Bar
    {
        public Bar(double open, double close, double high, double low, double volume,
            string date, string firstDate, bool aggregated, double?[] ma)
        {
            Open = open;
            Close = close;
            High = high;
            Low = low;
            Volume = volume;
            Date = date;
            FirstDate = firstDate;
            Aggregated = aggregated;
            Ma = ma;
        }

        public double Open { get; }
        public double Close { get; }
        public double High { get; }
        public double Low { get; }
        public double Volume { get; }
        public string Date { get; }        // representative (last) date
        public string FirstDate { get; }   // first date in an aggregated bar
        public bool Aggregated { get; }
        public double?[] Ma { get; }        // one entry per MA window; null until it fills
        public bool IsUp => Close >= Open;
    }

    private IReadOnlyList<Kline> _candles = Array.Empty<Kline>();
    private IReadOnlyDictionary<int, IReadOnlyList<double?>> _mas =
        new Dictionary<int, IReadOnlyList<double?>>();

    // The visible window into _candles: [_viewStart, _viewStart + _viewCount).
    // Zoom/pan operate here, in original-candle units; aggregation happens only
    // when drawing.
    private int _viewStart;
    private int _viewCount;

    // The drawn units for the current frame, rebuilt each OnRender. _hoverIndex is
    // an index into THIS list, not into _candles.
    private readonly List<Bar> _bars = new();
    private int _hoverIndex = -1;

    private bool _dragging;
    private double _dragStartX;
    private int _dragStartView;

    // Refreshed once per repaint instead of once per label: a readout frame builds
    // ~20 FormattedText, and each GetDpi walks up the visual tree. Re-read every
    // frame rather than cached for good, so dragging the window to a monitor with
    // a different scale still lays text out correctly.
    private double _pixelsPerDip = 1;

    // Per-MA visibility, toggled by clicking its legend entry; and each entry's
    // clickable rect, filled in while the legend is drawn.
    private readonly bool[] _maVisible;
    private readonly Rect[] _maLegendHit;

    public KlineChart()
    {
        Background = Frozen("#0F1420");
        ClipToBounds = true;

        _maVisible = Enumerable.Repeat(true, KlineViewModel.MaWindows.Length).ToArray();
        _maLegendHit = new Rect[KlineViewModel.MaWindows.Length];
    }

    /// <summary>Painted behind the plot; also makes the whole surface hit-testable.</summary>
    public Brush Background { get; }

    public void SetSeries(
        IReadOnlyList<Kline> candles,
        IReadOnlyDictionary<int, IReadOnlyList<double?>> movingAverages)
    {
        _candles = candles;
        _mas = movingAverages;
        _hoverIndex = -1;
        _dragging = false;

        // Reset to the most recent window on every load (period/adjust change).
        // MA visibility is NOT reset here — a user who hid MA60 keeps it hidden
        // across period switches; only ResetView restores it.
        ResetViewWindow();
        InvalidateVisual();
    }

    /// <summary>
    /// Swaps in refreshed data while keeping the current zoom and pan — used by
    /// the intraday re-poll, where resetting the window every interval would yank
    /// the chart back to the right edge under anyone reading through history.
    ///
    /// A view sitting at the right edge stays pinned there, so a chart left alone
    /// still follows the newest candle.
    /// </summary>
    public void UpdateSeries(
        IReadOnlyList<Kline> candles,
        IReadOnlyDictionary<int, IReadOnlyList<double?>> movingAverages)
    {
        // Mid-pan: drop this refresh rather than shift the data under the drag.
        // The next poll picks it up.
        if (_dragging) return;

        if (_candles.Count == 0)
        {
            SetSeries(candles, movingAverages);
            return;
        }

        var pinned = ViewEnd >= _candles.Count;

        _candles = candles;
        _mas = movingAverages;
        _hoverIndex = -1;

        _viewCount = Math.Clamp(_viewCount, Math.Min(MinView, _candles.Count), _candles.Count);
        var last = Math.Max(0, _candles.Count - _viewCount);
        _viewStart = pinned ? last : Math.Clamp(_viewStart, 0, last);

        InvalidateVisual();
    }

    /// <summary>
    /// One-click restore: the default recent window and every MA shown again.
    /// Panning, zooming and toggled-off MAs all revert.
    /// </summary>
    public void ResetView()
    {
        ResetViewWindow();
        Array.Fill(_maVisible, true);
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private void ResetViewWindow()
    {
        _viewCount = Math.Min(DefaultView, _candles.Count);
        _viewStart = Math.Max(0, _candles.Count - _viewCount);
    }

    /// <summary>
    /// Keyboard pan, in original-candle units like a drag. Positive moves toward
    /// the newest candle. The crosshair is dropped because its index points into
    /// the bar list that is about to be rebuilt.
    /// </summary>
    public void Pan(int candles)
    {
        if (_candles.Count == 0 || candles == 0) return;

        var start = Math.Clamp(
            _viewStart + candles, 0, Math.Max(0, _candles.Count - _viewCount));
        if (start == _viewStart) return;

        _viewStart = start;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    /// <summary>Pan by whole screens.</summary>
    public void PanPages(int pages) => Pan(pages * Math.Max(1, _viewCount));

    /// <summary>
    /// Keyboard zoom, same step as the wheel but anchored on the middle of the
    /// window — there is no cursor position to anchor on.
    /// </summary>
    public void Zoom(int direction)
    {
        if (_candles.Count == 0 || direction == 0) return;

        var anchor = _viewStart + _viewCount / 2.0;
        var factor = direction > 0 ? 0.85 : 1 / 0.85;
        var newCount = ClampView(_viewCount * factor);
        if (newCount == _viewCount) return;

        _viewCount = newCount;
        _viewStart = Math.Clamp(
            (int)Math.Round(anchor - newCount / 2.0), 0, Math.Max(0, _candles.Count - newCount));
        _hoverIndex = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Visible-candle count, clamped to something the series can actually
    /// satisfy. The floor has to yield to a SHORT series: a recent listing's
    /// week/month line can hold fewer than <see cref="MinView"/> candles, and
    /// Math.Clamp throws outright when its min exceeds its max.
    /// </summary>
    private int ClampView(double count) =>
        Math.Clamp((int)Math.Round(count), Math.Min(MinView, _candles.Count), _candles.Count);

    /// <summary>Jump to the oldest candles, keeping the current zoom.</summary>
    public void JumpToStart() => Pan(-_candles.Count);

    /// <summary>Jump back to the newest candles, keeping the current zoom.</summary>
    public void JumpToEnd() => Pan(_candles.Count);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_candles.Count == 0) return;
        var x = e.GetPosition(this).X;

        if (_dragging)
        {
            // Drag right to pull earlier history into view (content follows the
            // cursor), so the view start moves opposite to the drag distance.
            // Uses the original-candle step, so panning is unaffected by aggregation.
            var step = PlotWidth / Math.Max(1, _viewCount);
            var shift = (int)Math.Round((x - _dragStartX) / step);
            var start = Math.Clamp(_dragStartView - shift, 0, Math.Max(0, _candles.Count - _viewCount));
            if (start != _viewStart)
            {
                _viewStart = start;
                InvalidateVisual();
            }

            return;
        }

        var index = BarIndexAt(x);
        if (index == _hoverIndex) return;

        _hoverIndex = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_candles.Count == 0) return;

        var p = e.GetPosition(this);

        // A click on a MA legend entry toggles that line instead of starting a
        // drag, so the two gestures never fight.
        for (var w = 0; w < _maLegendHit.Length; w++)
        {
            if (_maLegendHit[w].Contains(p))
            {
                _maVisible[w] = !_maVisible[w];
                InvalidateVisual();
                return;
            }
        }

        _dragging = true;
        _dragStartX = p.X;
        _dragStartView = _viewStart;
        _hoverIndex = -1;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Wheel zooms the visible window, anchored on the candle under the cursor so
    /// it stays put while the rest expands or contracts around it. Zoom is in
    /// original-candle units; aggregation adjusts on the next paint.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_candles.Count == 0 || PlotWidth <= 0) return;

        var frac = Math.Clamp((e.GetPosition(this).X - PadLeft) / PlotWidth, 0, 1);
        var anchor = _viewStart + frac * _viewCount;

        var factor = e.Delta > 0 ? 0.85 : 1 / 0.85;
        var newCount = ClampView(_viewCount * factor);

        _viewCount = newCount;
        _viewStart = Math.Clamp(
            (int)Math.Round(anchor - frac * newCount), 0, Math.Max(0, _candles.Count - newCount));

        // Crosshair index is into the (about to be rebuilt) bar list; drop it and
        // let the next move set it rather than carry a stale index across a zoom.
        _hoverIndex = -1;
        InvalidateVisual();
    }

    /// <summary>Index into the CURRENT bar list under an x pixel, or -1.</summary>
    private int BarIndexAt(double x)
    {
        if (_bars.Count == 0) return -1;

        var slot = PlotWidth / _bars.Count;
        if (slot <= 0) return -1;

        var i = (int)((x - PadLeft) / slot);
        return Math.Clamp(i, 0, _bars.Count - 1);
    }

    private double PlotWidth => Math.Max(0, ActualWidth - PadLeft - PadRight);

    private int ViewEnd => _viewStart + _viewCount; // exclusive, in candle units

    private double BarX(int i, double slot) => PadLeft + slot * i + slot / 2;

    protected override void OnRender(DrawingContext dc)
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_viewCount == 0 || ActualWidth <= PadLeft + PadRight || ActualHeight <= PadTop + PadBottom)
            return;

        var plotWidth = PlotWidth;
        var plotTop = PadTop;
        var plotBottom = ActualHeight - PadBottom;
        var totalHeight = plotBottom - plotTop;

        var volumeHeight = totalHeight * VolumeFraction;
        var gap = totalHeight * GapFraction;
        var priceBottom = plotTop + totalHeight - volumeHeight - gap;
        var volumeTop = priceBottom + gap;

        BuildBars(plotWidth);
        if (_bars.Count == 0) return;

        var (priceMin, priceMax) = PriceRange();
        var volumeMax = VolumeMax();

        double PriceToY(double p) =>
            priceMax <= priceMin
                ? (plotTop + priceBottom) / 2
                : priceBottom - (p - priceMin) / (priceMax - priceMin) * (priceBottom - plotTop);

        double VolumeToY(double v) =>
            volumeMax <= 0 ? plotBottom : plotBottom - v / volumeMax * (plotBottom - volumeTop);

        var slot = plotWidth / _bars.Count;
        var bodyWidth = Math.Max(1, slot * 0.68);

        DrawPriceGrid(dc, priceMin, priceMax, plotTop, priceBottom, PriceToY);
        DrawCandles(dc, slot, bodyWidth, PriceToY, VolumeToY, plotBottom, volumeTop);
        DrawMovingAverages(dc, slot, PriceToY);
        DrawMaLegend(dc);
        DrawDateAxis(dc, slot, plotBottom);
        DrawCrosshair(dc, slot, plotTop, plotBottom, PriceToY);
    }

    /// <summary>
    /// Turns the visible candle window into the units to draw. One bar per candle
    /// when they fit; otherwise at most (plotWidth / MinSlotPx) aggregated bars, so
    /// drawing cost is bounded by screen width no matter how far out the zoom is.
    /// </summary>
    private void BuildBars(double plotWidth)
    {
        _bars.Clear();
        if (_viewCount <= 0) return;

        var maxBars = Math.Max(1, (int)(plotWidth / MinSlotPx));

        if (_viewCount <= maxBars)
        {
            for (var g = _viewStart; g < ViewEnd; g++) _bars.Add(MakeBar(g, g));
            return;
        }

        for (var b = 0; b < maxBars; b++)
        {
            var lo = _viewStart + (int)((long)b * _viewCount / maxBars);
            var hi = _viewStart + (int)((long)(b + 1) * _viewCount / maxBars);
            if (hi <= lo) hi = lo + 1;
            if (lo >= ViewEnd) break;
            hi = Math.Min(hi, ViewEnd);

            _bars.Add(MakeBar(lo, hi - 1));
        }
    }

    /// <summary>Aggregates candles [firstG, lastG] into one bar. Single candle when equal.</summary>
    private Bar MakeBar(int firstG, int lastG)
    {
        var high = double.MinValue;
        var low = double.MaxValue;
        var volume = 0.0;

        for (var g = firstG; g <= lastG; g++)
        {
            var c = _candles[g];
            // Clamp against dirty highs/lows (KR has a couple) via all four values.
            var h = Math.Max(Math.Max(c.Open, c.Close), Math.Max(c.High, c.Low));
            var l = Math.Min(Math.Min(c.Open, c.Close), Math.Min(c.High, c.Low));
            if (h > high) high = h;
            if (l < low) low = l;
            volume += c.Volume;
        }

        var windows = KlineViewModel.MaWindows;
        var ma = new double?[windows.Length];
        for (var w = 0; w < windows.Length; w++)
            if (_mas.TryGetValue(windows[w], out var line) && lastG < line.Count)
                ma[w] = line[lastG];

        return new Bar(
            _candles[firstG].Open, _candles[lastG].Close, high, low, volume,
            _candles[lastG].Date, _candles[firstG].Date, firstG != lastG, ma);
    }

    /// <summary>Price range over the drawn bars and every visible MA point in them.</summary>
    private (double Min, double Max) PriceRange()
    {
        var min = double.MaxValue;
        var max = double.MinValue;

        foreach (var bar in _bars)
        {
            if (bar.High > max) max = bar.High;
            if (bar.Low < min) min = bar.Low;
        }

        for (var w = 0; w < _maVisible.Length; w++)
        {
            if (!_maVisible[w]) continue;
            foreach (var bar in _bars)
                if (bar.Ma[w] is { } value)
                {
                    if (value > max) max = value;
                    if (value < min) min = value;
                }
        }

        if (min > max) return (0, 1);

        var padding = (max - min) * 0.04;
        if (padding <= 0) padding = max * 0.01 + 1;
        return (min - padding, max + padding);
    }

    private double VolumeMax()
    {
        var max = 0.0;
        foreach (var bar in _bars)
            if (bar.Volume > max) max = bar.Volume;
        return max;
    }

    private void DrawPriceGrid(
        DrawingContext dc, double min, double max, double top, double bottom,
        Func<double, double> priceToY)
    {
        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var price = min + (max - min) * i / lines;
            var y = priceToY(price);
            dc.DrawLine(GridPen, new Point(PadLeft, y), new Point(ActualWidth - PadRight, y));

            var text = Label(FormatPrice(price), AxisText);
            dc.DrawText(text, new Point(ActualWidth - PadRight + 5, y - text.Height / 2));
        }
    }

    private void DrawCandles(
        DrawingContext dc, double slot, double bodyWidth,
        Func<double, double> priceToY, Func<double, double> volumeToY,
        double volumeBottom, double volumeTop)
    {
        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var cx = BarX(i, slot);
            var brush = bar.IsUp ? UpBrush : DownBrush;
            var pen = bar.IsUp ? UpPen : DownPen;

            dc.DrawLine(pen, new Point(cx, priceToY(bar.High)), new Point(cx, priceToY(bar.Low)));

            var yOpen = priceToY(bar.Open);
            var yClose = priceToY(bar.Close);
            var top = Math.Min(yOpen, yClose);
            var height = Math.Max(1, Math.Abs(yClose - yOpen));
            var rect = new Rect(cx - bodyWidth / 2, top, bodyWidth, height);

            // Up hollow, down filled — the A-share convention, keeps colour from
            // being the only up/down cue.
            if (bar.IsUp) dc.DrawRectangle(null, pen, rect);
            else dc.DrawRectangle(brush, pen, rect);

            var volY = volumeToY(bar.Volume);
            var volRect = new Rect(cx - bodyWidth / 2, volY, bodyWidth, Math.Max(0, volumeBottom - volY));
            dc.DrawRectangle(brush, null, volRect);
        }
    }

    private void DrawMovingAverages(DrawingContext dc, double slot, Func<double, double> priceToY)
    {
        for (var w = 0; w < _maVisible.Length; w++)
        {
            if (!_maVisible[w]) continue;

            var pen = MaPens[w];
            Point? previous = null;

            for (var i = 0; i < _bars.Count; i++)
            {
                if (_bars[i].Ma[w] is not { } value)
                {
                    previous = null;
                    continue;
                }

                var point = new Point(BarX(i, slot), priceToY(value));
                if (previous is { } p) dc.DrawLine(pen, p, point);
                previous = point;
            }
        }
    }

    private void DrawMaLegend(DrawingContext dc)
    {
        var windows = KlineViewModel.MaWindows;
        var x = PadLeft + 2;

        for (var w = 0; w < windows.Length; w++)
        {
            // Reads the value at the newest visible candle, so it tracks the view.
            var latest = MaAt(windows[w], ViewEnd - 1);
            var label = latest is { } v ? $"MA{windows[w]} {FormatPrice(v)}" : $"MA{windows[w]} --";

            var text = Label(label, _maVisible[w] ? MaBrushes[w] : LegendOff);
            dc.DrawText(text, new Point(x, 6));

            _maLegendHit[w] = new Rect(x - 3, 3, text.Width + 6, text.Height + 6);
            x += text.WidthIncludingTrailingWhitespace + 14;
        }
    }

    /// <summary>MA value at global index g, walking left to the last one that exists.</summary>
    private double? MaAt(int window, int g)
    {
        if (!_mas.TryGetValue(window, out var line)) return null;
        for (var i = Math.Min(g, line.Count - 1); i >= 0; i--)
            if (line[i] is { } v) return v;
        return null;
    }

    private void DrawDateAxis(DrawingContext dc, double slot, double bottom)
    {
        const int ticks = 5;
        var prevYear = "";
        for (var t = 0; t <= ticks; t++)
        {
            var i = (int)Math.Round((double)(_bars.Count - 1) * t / ticks);
            i = Math.Clamp(i, 0, _bars.Count - 1);

            // Year context without stamping it on every tick: the first tick and
            // any tick where the year rolls over get the full date, the rest stay
            // MM-dd — charts spanning years were unreadable as bare MM-dd.
            var date = _bars[i].Date;
            var year = date.Length >= 4 ? date[..4] : "";
            var text = Label(year != prevYear ? date : ShortDate(date), AxisText);
            prevYear = year;

            var tx = Math.Clamp(BarX(i, slot) - text.Width / 2, 0, ActualWidth - text.Width);
            dc.DrawText(text, new Point(tx, bottom + 4));
        }
    }

    private void DrawCrosshair(
        DrawingContext dc, double slot, double plotTop, double plotBottom,
        Func<double, double> priceToY)
    {
        if (_hoverIndex < 0 || _hoverIndex >= _bars.Count) return;

        var bar = _bars[_hoverIndex];
        var cx = BarX(_hoverIndex, slot);

        dc.DrawLine(CrosshairPen, new Point(cx, plotTop), new Point(cx, plotBottom));
        var y = priceToY(bar.Close);
        dc.DrawLine(CrosshairPen, new Point(PadLeft, y), new Point(ActualWidth - PadRight, y));

        DrawReadout(dc, _hoverIndex);
    }

    /// <summary>OHLC + change box for the hovered bar, pinned top-left.</summary>
    private void DrawReadout(DrawingContext dc, int index)
    {
        var bar = _bars[index];

        // Change vs the PREVIOUS bar's close — the market's 涨跌幅 — not this bar's
        // own open (that is what colours the body, a different number). First bar
        // has no predecessor, so it falls back to its open.
        var prevClose = index > 0 ? _bars[index - 1].Close : bar.Open;
        var change = bar.Close - prevClose;
        var pct = prevClose > 0 ? change / prevClose * 100 : 0;
        var changeBrush = pct >= 0 ? UpBrush : DownBrush;

        // Aggregated bars span a date range; show it so the reading isn't misread
        // as a single day.
        var dateText = bar.Aggregated
            ? RangeText(bar.FirstDate, bar.Date)
            : bar.Date;

        var lines = new[]
        {
            ("日期", dateText, AxisText),
            ("开", FormatPrice(bar.Open), AxisText),
            ("高", FormatPrice(bar.High), AxisText),
            ("低", FormatPrice(bar.Low), AxisText),
            ("收", FormatPrice(bar.Close), changeBrush),
            ("涨跌额", Signed(change), changeBrush),
            ("涨跌幅", pct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%", changeBrush),
            ("量", FormatVolume(bar.Volume), AxisText),
        };

        var texts = lines
            .Select(l => (Key: Label(l.Item1, AxisText), Val: Label(l.Item2, l.Item3)))
            .ToArray();

        var rowHeight = texts[0].Key.Height + 3;
        var keyWidth = texts.Max(t => t.Key.Width);
        var valWidth = texts.Max(t => t.Val.Width);
        var boxWidth = keyWidth + valWidth + 22;
        var boxHeight = rowHeight * texts.Length + 10;

        var box = new Rect(PadLeft + 6, PadTop + 6, boxWidth, boxHeight);
        dc.DrawRectangle(ReadoutBg, ReadoutBorderPen, box);

        var y = box.Top + 5;
        foreach (var (key, val) in texts)
        {
            dc.DrawText(key, new Point(box.Left + 8, y));
            dc.DrawText(val, new Point(box.Right - 8 - val.Width, y));
            y += rowHeight;
        }
    }

    private static string FormatPrice(double v) =>
        v >= 10000 ? v.ToString("N0", CultureInfo.InvariantCulture)
        : v.ToString(v < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>Signed change, same magnitude rule as a price so the digits line up.</summary>
    private static string Signed(double v) =>
        (v >= 0 ? "+" : "-") + FormatPrice(Math.Abs(v));

    private static string FormatVolume(double v) =>
        v >= 1e8 ? (v / 1e8).ToString("0.##", CultureInfo.InvariantCulture) + "亿"
        : v >= 1e4 ? (v / 1e4).ToString("0.##", CultureInfo.InvariantCulture) + "万"
        : v.ToString("0", CultureInfo.InvariantCulture);

    private static string ShortDate(string date) =>
        date.Length >= 10 ? date[5..] : date;

    /// <summary>Aggregated-bar range; repeats the year only when it differs.</summary>
    private static string RangeText(string first, string last) =>
        first.Length >= 4 && last.Length >= 4 && first[..4] == last[..4]
            ? $"{first}~{ShortDate(last)}"
            : $"{first}~{last}";

    private FormattedText Label(string text, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11, brush,
            _pixelsPerDip);

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string hex, double thickness, bool dashed = false)
    {
        var pen = new Pen(Frozen(hex), thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        pen.Freeze();
        return pen;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
