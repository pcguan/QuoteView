using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Intraday trend chart: today's price line, the average (VWAP) line, the
/// previous-close baseline, and a volume subchart — drawn directly in OnRender,
/// the same hand-drawn approach as <see cref="KlineChart"/>.
///
/// The y-axis is centred on the previous close so up and down read symmetrically,
/// which is the intraday convention. The x-axis is driven by the points the
/// endpoint returns, not a fixed session window, because each market's session
/// differs (and US has a night session). Red = up, green = down vs the previous
/// close, matching the K-line.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    private static readonly Brush UpBrush = Frozen(Tones.UpHex);
    private static readonly Brush DownBrush = Frozen(Tones.DownHex);
    private static readonly Brush MainTag = Frozen("#DCE4EE");
    private static readonly Brush CompareTag = Frozen("#4C8DFF");
    private static readonly Pen PriceLine = FrozenPen("#DCE4EE", 1.3);
    private static readonly Pen AvgLine = FrozenPen("#FFC107", 1.2);
    private static readonly Pen BaselinePen = FrozenPen("#5F6672", 1, dashed: true);
    private static readonly Pen GridPen = FrozenPen("#222A38", 1);
    private static readonly Pen CrosshairPen = FrozenPen("#8B93A3", 1, dashed: true);
    private static readonly Brush AxisText = Frozen("#8B93A3");
    private static readonly Brush ReadoutBg = Frozen("#111722");
    private static readonly Brush ReadoutBorder = Frozen("#33405C");
    private static readonly Pen ReadoutBorderPen = FrozenPen(ReadoutBorder, 1);

    private const double PadLeft = 54;    // left axis: price
    private const double PadRight = 54;   // right axis: 涨跌幅 %
    private const double PadTop = 22;      // top: hover readout row
    private const double PadBottom = 22;
    private const double VolumeFraction = 0.26;
    private const double GapFraction = 0.04;

    private static readonly Typeface Mono =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private TrendSeries? _series;
    private TrendSeries? _compare;
    private int _hoverIndex = -1;

    // Refreshed once per repaint instead of once per label; re-read every frame so
    // dragging the window to a monitor with a different scale still lays out text
    // correctly.
    private double _pixelsPerDip = 1;

    // Everything except the crosshair — grid, volume, both price lines, axis,
    // legend — baked into one frozen drawing. Moving the crosshair one slot used to
    // rebuild both PathGeometries (a LineSegment per minute, ~241–391 of them) on
    // every mouse move. A TrendSeries is immutable and replaced wholesale by each
    // poll, so reference identity plus the plot size is a complete cache key.
    private DrawingGroup? _layer;
    private TrendSeries? _layerSeries;
    private TrendSeries? _layerCompare;
    private double _layerWidth;
    private double _layerHeight;
    private double _layerDip;

    private static readonly Pen ComparePen = FrozenPen("#4C8DFF", 1.4);
    private static readonly Brush CompareText = Frozen("#4C8DFF");
    private static readonly Brush MainText = Frozen("#DCE4EE");

    public TrendChart()
    {
        Background = Frozen("#0F1420");
        ClipToBounds = true;
    }

    public Brush Background { get; }

    public void SetSeries(TrendSeries series)
    {
        _series = series;
        // The intraday view re-feeds this every few seconds. Blanking the
        // crosshair each time yanked it out from under a stationary pointer, so
        // keep it while the mouse is still over the chart (a later point simply
        // extends the series, same index = same minute) and drop it only when
        // the pointer is elsewhere — e.g. a contract switch, where the mouse is
        // on the dropdown, not the plot.
        if (!IsMouseOver) _hoverIndex = -1;
        else if (_hoverIndex >= Points.Count) _hoverIndex = Points.Count - 1;
        InvalidateVisual();
    }

    /// <summary>
    /// Overlays a second day of the SAME contract for comparison, or null to
    /// clear. Drawn on a normalized scale: each day relative to its own previous
    /// close, so the two lines share the percent axis even though absolute price
    /// levels differ.
    /// </summary>
    public void SetCompare(TrendSeries? compare)
    {
        _compare = compare;
        InvalidateVisual();
    }

    private IReadOnlyList<TrendPoint> Points => _series?.Points ?? Array.Empty<TrendPoint>();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var index = IndexAt(e.GetPosition(this).X);
        if (index == _hoverIndex) return;

        _hoverIndex = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private int IndexAt(double x)
    {
        var count = Points.Count;
        if (count == 0) return -1;

        // Must match OnRender's step, which spans the full session (ExpectedSlots),
        // not the points collected so far. Using count here put the crosshair on a
        // different x than the mouse whenever the day was only partly filled.
        var step = PlotWidth / ExpectedSlots(Points);
        if (step <= 0) return -1;

        var i = (int)((x - PadLeft) / step);
        return Math.Clamp(i, 0, count - 1);
    }

    private double PlotWidth => Math.Max(0, ActualWidth - PadLeft - PadRight);

    protected override void OnRender(DrawingContext dc)
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var points = Points;
        if (_series is null || points.Count == 0
            || ActualWidth <= PadLeft + PadRight || ActualHeight <= PadTop + PadBottom)
            return;

        var plotTop = PadTop;
        var plotBottom = ActualHeight - PadBottom;
        var totalHeight = plotBottom - plotTop;

        var volumeHeight = totalHeight * VolumeFraction;
        var gap = totalHeight * GapFraction;
        var priceBottom = plotTop + totalHeight - volumeHeight - gap;
        var volumeTop = priceBottom + gap;

        var pre = _series.PreClose;
        var (priceMin, priceMax) = PriceRange(points, pre);

        // The compare day rides the same axis after normalization; widen the
        // symmetric range so its swings fit too.
        if (_compare is { Points.Count: > 0 } cmpForRange && cmpForRange.PreClose > 0 && pre > 0)
        {
            var dev = Math.Max(pre - priceMin, priceMax - pre);
            foreach (var p in cmpForRange.Points)
                dev = Math.Max(dev, Math.Abs(pre * (p.Price / cmpForRange.PreClose) - pre));
            priceMin = pre - dev;
            priceMax = pre + dev;
        }

        // One shared volume scale across BOTH days, so in compare mode the two
        // lanes' bar heights are directly comparable.
        var cmpPoints = _compare is { Points.Count: > 0 } cmpForVolume ? cmpForVolume.Points : null;
        var volumeMax = points.Max(p => p.Volume);
        if (cmpPoints is not null) volumeMax = Math.Max(volumeMax, cmpPoints.Max(p => p.Volume));

        double PriceToY(double p) =>
            priceMax <= priceMin
                ? (plotTop + priceBottom) / 2
                : priceBottom - (p - priceMin) / (priceMax - priceMin) * (priceBottom - plotTop);

        // Full-session width even before the day fills: A-share 241, HK/US/KR more.
        var slots = ExpectedSlots(points);
        var step = PlotWidth / slots;

        var layer = _layer;
        if (layer is null
            || !ReferenceEquals(_layerSeries, _series) || !ReferenceEquals(_layerCompare, _compare)
            || _layerWidth != ActualWidth || _layerHeight != ActualHeight
            || _layerDip != _pixelsPerDip)
        {
            layer = new DrawingGroup();
            using (var lc = layer.Open())
            {
                DrawGrid(lc, priceMin, priceMax, pre, plotTop, priceBottom, PriceToY);

                if (cmpPoints is null)
                {
                    double VolumeToY(double v) =>
                        volumeMax <= 0 ? plotBottom : plotBottom - v / volumeMax * (plotBottom - volumeTop);
                    DrawVolume(lc, step, VolumeToY, volumeTop, plotBottom, volumeMax, pre, points, null);
                }
                else
                {
                    // Compare mode: the volume band splits into two lanes — the picked
                    // day on top, the compare day beneath — each tagged at its left
                    // edge with the day's legend colour.
                    const double laneGap = 3;
                    var laneHeight = Math.Max(1, (plotBottom - volumeTop - laneGap) / 2);
                    var laneOneBottom = volumeTop + laneHeight;
                    var laneTwoTop = laneOneBottom + laneGap;

                    double LaneOneY(double v) =>
                        volumeMax <= 0 ? laneOneBottom : laneOneBottom - v / volumeMax * laneHeight;
                    double LaneTwoY(double v) =>
                        volumeMax <= 0 ? plotBottom : plotBottom - v / volumeMax * laneHeight;

                    DrawVolume(lc, step, LaneOneY, volumeTop, laneOneBottom, volumeMax, pre, points, MainTag);
                    DrawVolume(lc, step, LaneTwoY, laneTwoTop, plotBottom, volumeMax,
                        _compare!.PreClose, cmpPoints, CompareTag);
                }
                DrawCompare(lc, step, PriceToY, pre);
                DrawLines(lc, step, PriceToY, points);
                DrawTimeAxis(lc, step, plotBottom, points);
                DrawLegend(lc);
            }

            if (layer.CanFreeze) layer.Freeze();
            _layer = layer;
            _layerSeries = _series;
            _layerCompare = _compare;
            _layerWidth = ActualWidth;
            _layerHeight = ActualHeight;
            _layerDip = _pixelsPerDip;
        }

        dc.DrawDrawing(layer);
        DrawCrosshair(dc, step, plotTop, plotBottom, PriceToY, points);
    }

    /// <summary>
    /// Symmetric around the previous close: the larger of the up and down swings
    /// sets both arms, so the baseline sits in the middle and gains/losses read at
    /// the same scale. The average line is included so it can't fall off.
    /// </summary>
    private static (double Min, double Max) PriceRange(IReadOnlyList<TrendPoint> points, double pre)
    {
        var dev = 0.0;
        foreach (var p in points)
        {
            dev = Math.Max(dev, Math.Abs(p.Price - pre));

            // Only when there IS an average. The Tencent fallback reports none for
            // BJ/US/KR (its rows have no amount column), and treating a 0 as a real
            // value stretches the axis from 0 to 2×pre and flattens the line into
            // a hairline across the middle.
            if (p.AvgPrice > 0) dev = Math.Max(dev, Math.Abs(p.AvgPrice - pre));
        }

        if (dev <= 0) dev = pre * 0.01 + 1;
        dev *= 1.05;
        return (pre - dev, pre + dev);
    }

    /// <summary>
    /// Total minute slots in the session, so a half-finished day still spans the
    /// full width. Rounds the current count up to the known session lengths.
    /// </summary>
    private static int ExpectedSlots(IReadOnlyList<TrendPoint> points)
    {
        var n = points.Count;
        foreach (var full in new[] { 241, 331, 391 })
            if (n <= full) return full;
        return n;
    }

    private void DrawGrid(
        DrawingContext dc, double min, double max, double pre, double top, double bottom,
        Func<double, double> priceToY)
    {
        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var price = min + (max - min) * i / lines;
            var y = priceToY(price);
            dc.DrawLine(GridPen, new Point(PadLeft, y), new Point(ActualWidth - PadRight, y));

            // Both axes coloured by their side of the previous close: LEFT = price,
            // RIGHT = 涨跌幅 % vs 昨收 (东财-style twin axes).
            var brush = price >= pre ? UpBrush : DownBrush;
            var priceText = Label(FormatPrice(price), brush);
            dc.DrawText(priceText, new Point(PadLeft - 5 - priceText.Width, y - priceText.Height / 2));

            var pct = pre > 0 ? (price / pre - 1) * 100 : 0;
            var pctText = Label(
                pct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%", brush);
            dc.DrawText(pctText, new Point(ActualWidth - PadRight + 5, y - pctText.Height / 2));
        }

        // Emphasised previous-close baseline.
        var yPre = priceToY(pre);
        dc.DrawLine(BaselinePen, new Point(PadLeft, yPre), new Point(ActualWidth - PadRight, yPre));
    }

    private void DrawLines(
        DrawingContext dc, double step, Func<double, double> priceToY, IReadOnlyList<TrendPoint> points)
    {
        var price = new PathFigure { StartPoint = new Point(X(0, step), priceToY(points[0].Price)) };
        for (var i = 1; i < points.Count; i++)
            price.Segments.Add(new LineSegment(new Point(X(i, step), priceToY(points[i].Price)), true));

        dc.DrawGeometry(null, PriceLine, new PathGeometry { Figures = { price } });

        // No average lines in compare mode: four lines tangled in one plot read
        // as clutter, and the crosshair readout still shows both days' 均价.
        if (_compare is { Points.Count: > 0 }) return;

        // The average line is skipped entirely when the source doesn't report one
        // (Tencent fallback on BJ/US/KR) — drawing it from zeros would put a flat
        // line along the bottom that reads as a real average of 0.
        if (!points.Any(p => p.AvgPrice > 0)) return;

        var avg = new PathFigure { StartPoint = new Point(X(0, step), priceToY(points[0].AvgPrice)) };
        for (var i = 1; i < points.Count; i++)
            avg.Segments.Add(new LineSegment(new Point(X(i, step), priceToY(points[i].AvgPrice)), true));

        dc.DrawGeometry(null, AvgLine, new PathGeometry { Figures = { avg } });
    }

    /// <summary>
    /// Minute-volume bars, coloured by TICK DIRECTION — up vs the previous
    /// minute red, down green, unchanged carries the previous colour (the
    /// mainstream 分时图 convention; the first bar compares against 昨收).
    /// Colouring vs 昨收, as before, painted a day that traded entirely above
    /// yesterday's close solid red. A non-null laneTag marks the lane's left
    /// edge with that day's legend colour (compare mode stacks two lanes).
    /// </summary>
    private void DrawVolume(
        DrawingContext dc, double step, Func<double, double> volumeToY, double laneTop,
        double laneBottom, double volumeMax, double pre, IReadOnlyList<TrendPoint> points,
        Brush? laneTag)
    {
        if (points.Count == 0) return;

        if (laneTag is not null)
            dc.DrawRectangle(laneTag, null,
                new Rect(PadLeft - 6, laneTop, 3, Math.Max(0, laneBottom - laneTop)));

        var width = Math.Max(1, step * 0.7);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var prev = i > 0 ? points[i - 1].Price : pre;
            // 涨或平 → 红,跌 → 绿(东财分时量柱惯例:平盘分钟归红,而不是沿用上一根的颜色
            // ——尾盘低波动有大量平盘分钟,沿用旧色会整片偏绿,与东财对不上)。
            var brush = p.Price >= prev ? UpBrush : DownBrush;

            var x = X(i, step);
            var y = volumeToY(p.Volume);
            dc.DrawRectangle(brush, null, new Rect(x - width / 2, y, width, Math.Max(0, laneBottom - y)));
        }
    }

    private void DrawTimeAxis(
        DrawingContext dc, double step, double bottom, IReadOnlyList<TrendPoint> points)
    {
        // Labels sit on the FIXED session grid (spanning ExpectedSlots), not on the
        // fraction of points collected so far. Using points.Count put the ticks at
        // arbitrary clocks (9:52, 10:37, …) bunched in the filled part whenever the
        // day was only partly done; projecting from the open keeps them regular
        // (9:30 / 10:30 / 11:30 / 14:00 / 15:00) across the whole width all day.
        var slots = ExpectedSlots(points);
        var open = ParseClock(points[0].Clock);
        const int ticks = 4;
        for (var t = 0; t <= ticks; t++)
        {
            var slot = (int)Math.Round((double)(slots - 1) * t / ticks);
            var text = Label(SlotToClock(slot, slots, open), AxisText);
            var tx = Math.Clamp(X(slot, step) - text.Width / 2, 0, ActualWidth - text.Width);
            dc.DrawText(text, new Point(tx, bottom + 4));
        }
    }

    /// <summary>Minutes since midnight for an "HH:mm" clock, or -1 if unparseable.</summary>
    private static int ParseClock(string clock) =>
        clock.Length >= 5
        && int.TryParse(clock.AsSpan(0, 2), out var h)
        && int.TryParse(clock.AsSpan(3, 2), out var m)
            ? h * 60 + m : -1;

    /// <summary>Wall-clock label for a fixed-grid slot, projected from the open so
    /// ticks stay regular before the day has filled. A-share (241 slots) and HK
    /// (331) resume at 13:00 after lunch; other sessions (US/KR, 391) run straight
    /// through. US night sessions wrap past midnight, hence the mod.</summary>
    private static string SlotToClock(int slot, int slots, int open)
    {
        if (open < 0) return "";
        const int afternoon = 13 * 60;   // 13:00 lunch resume (A-share & HK)
        var min = slots switch
        {
            241 => slot <= 120 ? open + slot : afternoon + (slot - 120),   // 9:30-11:30 / 13:00-15:00
            331 => slot <= 150 ? open + slot : afternoon + (slot - 150),   // 9:30-12:00 / 13:00-16:00
            _ => open + slot,                                              // continuous
        };
        min = ((min % 1440) + 1440) % 1440;
        return $"{min / 60:D2}:{min % 60:D2}";
    }

    private void DrawCrosshair(
        DrawingContext dc, double step, double plotTop, double plotBottom,
        Func<double, double> priceToY, IReadOnlyList<TrendPoint> points)
    {
        if (_hoverIndex < 0 || _hoverIndex >= points.Count) return;

        var p = points[_hoverIndex];
        var cx = X(_hoverIndex, step);

        dc.DrawLine(CrosshairPen, new Point(cx, plotTop), new Point(cx, plotBottom));
        var y = priceToY(p.Price);
        dc.DrawLine(CrosshairPen, new Point(PadLeft, y), new Point(ActualWidth - PadRight, y));

        // Compare mode keeps the twin per-day boxes; the single day uses 东财-style
        // axis tags (price left, 涨跌幅 right) plus a one-line readout across the top.
        if (_compare is { Points.Count: > 0 })
        {
            DrawReadout(dc, p, cx);
            return;
        }

        var pre = _series!.PreClose;
        var brush = p.Price >= pre ? UpBrush : DownBrush;
        DrawAxisTag(dc, y, FormatPrice(p.Price), brush, left: true);
        var pct = pre > 0 ? (p.Price / pre - 1) * 100 : 0;
        DrawAxisTag(dc, y,
            pct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%", brush, left: false);
        DrawTopRow(dc, p, pre);
    }

    /// <summary>A filled value chip on an axis at the crosshair's y — price on the
    /// left, 涨跌幅 on the right.</summary>
    private void DrawAxisTag(DrawingContext dc, double y, string text, Brush brush, bool left)
    {
        var t = Label(text, brush);
        var w = t.Width + 8;
        var h = t.Height + 4;
        var cy = Math.Clamp(y, h / 2, ActualHeight - h / 2);
        var boxLeft = left ? PadLeft - w : ActualWidth - PadRight;
        var box = new Rect(boxLeft, cy - h / 2, w, h);
        dc.DrawRectangle(ReadoutBg, new Pen(brush, 1), box);
        dc.DrawText(t, new Point(box.Left + 4, box.Top + 2));
    }

    /// <summary>The hover data as one horizontal line across the top (时间 价 均价
    /// 涨跌幅 量) — replaces the old floating box for the single-day view.</summary>
    private void DrawTopRow(DrawingContext dc, TrendPoint p, double pre)
    {
        var limit = ActualWidth - PadRight;
        var x = (double)PadLeft;
        const double y = 3;
        foreach (var (key, val) in ReadoutLines(p, pre))
        {
            if (x + key.Width + 4 + val.Width > limit) break;
            dc.DrawText(key, new Point(x, y));
            x += key.Width + 4;
            dc.DrawText(val, new Point(x, y));
            x += val.Width + 14;
        }
    }

    // Keep the readout on the side AWAY from the cursor, so it never sits over the
    // point being read. It used to be pinned top-left and covered a top-left hover.
    private double ReadoutLeft(double cx, double totalWidth) =>
        cx <= ActualWidth / 2
            ? Math.Max(PadLeft + 6, ActualWidth - PadRight - totalWidth - 6)
            : PadLeft + 6;

    private void DrawReadout(DrawingContext dc, TrendPoint p, double cx)
    {
        var main = ReadoutLines(p, _series!.PreClose);

        // Compare mode: one box PER day, side by side, identical rows — a
        // straight horizontal glance instead of the two days stacked into one
        // frame. Each box is headed by its date in the day's legend colour.
        if (_compare is { PreClose: > 0 } cmp
            && _hoverIndex >= 0 && _hoverIndex < cmp.Points.Count)
        {
            var other = ReadoutLines(cmp.Points[_hoverIndex], cmp.PreClose);

            // Shared column widths so the twin boxes line up as one grid.
            var all = main.Concat(other).ToArray();
            var keyWidth = all.Max(t => t.Key.Width);
            var valWidth = all.Max(t => t.Val.Width);

            var headMain = Label(Day(_series), MainText);
            var headCmp = Label(Day(cmp), CompareText);
            var width = Math.Max(keyWidth + valWidth + 22,
                Math.Max(headMain.Width, headCmp.Width) + 16);

            var x = ReadoutLeft(cx, width + 8 + width);
            DrawReadoutBox(dc, x, headMain, main, width, keyWidth);
            DrawReadoutBox(dc, x + width + 8, headCmp, other, width, keyWidth);
        }
    }

    private (FormattedText Key, FormattedText Val)[] ReadoutLines(TrendPoint p, double pre)
    {
        var pct = pre > 0 ? (p.Price / pre - 1) * 100 : 0;
        var brush = p.Price >= pre ? UpBrush : DownBrush;

        var lines = new (string, string, Brush)[]
        {
            ("时间", p.Clock, AxisText),
            ("价", FormatPrice(p.Price), brush),
            // "-" rather than 0.00 when the source has no average (Tencent fallback).
            ("均价", p.AvgPrice > 0 ? FormatPrice(p.AvgPrice) : "-", AvgLine.Brush),
            ("涨跌幅", pct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%", brush),
            ("量", FormatVolume(p.Volume), AxisText),
        };
        return lines.Select(l => (Label(l.Item1, AxisText), Label(l.Item2, l.Item3))).ToArray();
    }

    private void DrawReadoutBox(DrawingContext dc, double x, FormattedText header,
        (FormattedText Key, FormattedText Val)[] rows, double width, double keyWidth)
    {
        var rowHeight = rows[0].Key.Height + 3;
        var box = new Rect(x, PadTop + 6, width, rowHeight * (rows.Length + 1) + 12);
        dc.DrawRectangle(ReadoutBg, ReadoutBorderPen, box);

        dc.DrawText(header, new Point(box.Left + 8, box.Top + 5));
        var yy = box.Top + 7 + rowHeight;
        foreach (var (key, val) in rows)
        {
            dc.DrawText(key, new Point(box.Left + 8, yy));
            dc.DrawText(val, new Point(box.Right - 8 - val.Width, yy));
            yy += rowHeight;
        }
    }

    private static double X(int i, double step) => PadLeft + step * i + step / 2;

    private static string FormatPrice(double v) =>
        v >= 10000 ? v.ToString("N0", CultureInfo.InvariantCulture)
        : v.ToString(v < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    private static string FormatVolume(double v) =>
        v >= 1e8 ? (v / 1e8).ToString("0.##", CultureInfo.InvariantCulture) + "亿"
        : v >= 1e4 ? (v / 1e4).ToString("0.##", CultureInfo.InvariantCulture) + "万"
        : v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The compare day's price line, normalized onto the primary axis.</summary>
    private void DrawCompare(DrawingContext dc, double step, Func<double, double> priceToY, double pre)
    {
        if (_compare is not { Points.Count: > 1 } cmp || cmp.PreClose <= 0 || pre <= 0) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var started = false;
            for (var i = 0; i < cmp.Points.Count; i++)
            {
                var mapped = pre * (cmp.Points[i].Price / cmp.PreClose);
                var pt = new Point(X(i, step), priceToY(mapped));
                if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                else ctx.LineTo(pt, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, ComparePen, geometry);
    }

    private static string Day(TrendSeries s) =>
        s.Points.Count > 0 && s.Points[0].Time.Length >= 10 ? s.Points[0].Time[..10] : "?";

    /// <summary>Top-left legend, shown only while comparing: which date is which colour.</summary>
    private void DrawLegend(DrawingContext dc)
    {
        if (_compare is not { Points.Count: > 0 } cmp || _series is null) return;

        var main = Label(Day(_series), MainText);
        var other = Label(Day(cmp) + "（对比）", CompareText);
        dc.DrawText(main, new Point(PadLeft + 4, 4));
        dc.DrawText(other, new Point(PadLeft + 12 + main.Width, 4));
    }

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
