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
    private static readonly Brush UpBrush = Frozen("#EF5350");
    private static readonly Brush DownBrush = Frozen("#26A69A");
    private static readonly Pen PriceLine = FrozenPen("#DCE4EE", 1.3);
    private static readonly Pen AvgLine = FrozenPen("#FFC107", 1.2);
    private static readonly Pen BaselinePen = FrozenPen("#5F6672", 1, dashed: true);
    private static readonly Pen GridPen = FrozenPen("#222A38", 1);
    private static readonly Pen CrosshairPen = FrozenPen("#8B93A3", 1, dashed: true);
    private static readonly Brush AxisText = Frozen("#8B93A3");
    private static readonly Brush ReadoutBg = Frozen("#111722");
    private static readonly Brush ReadoutBorder = Frozen("#33405C");

    private const double PadLeft = 8;
    private const double PadRight = 62;
    private const double PadTop = 22;
    private const double PadBottom = 22;
    private const double VolumeFraction = 0.26;
    private const double GapFraction = 0.04;

    private static readonly Typeface Mono =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private TrendSeries? _series;
    private int _hoverIndex = -1;

    public TrendChart()
    {
        Background = Frozen("#0F1420");
        ClipToBounds = true;
    }

    public Brush Background { get; }

    public void SetSeries(TrendSeries series)
    {
        _series = series;
        _hoverIndex = -1;
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
        var volumeMax = points.Max(p => p.Volume);

        double PriceToY(double p) =>
            priceMax <= priceMin
                ? (plotTop + priceBottom) / 2
                : priceBottom - (p - priceMin) / (priceMax - priceMin) * (priceBottom - plotTop);

        double VolumeToY(double v) =>
            volumeMax <= 0 ? plotBottom : plotBottom - v / volumeMax * (plotBottom - volumeTop);

        // Full-session width even before the day fills: A-share 241, HK/US/KR more.
        var slots = ExpectedSlots(points);
        var step = PlotWidth / slots;

        DrawGrid(dc, priceMin, priceMax, pre, plotTop, priceBottom, PriceToY);
        DrawVolume(dc, step, VolumeToY, plotBottom, pre, points);
        DrawLines(dc, step, PriceToY, points);
        DrawTimeAxis(dc, step, plotBottom, points);
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

            // Right axis: price, coloured by its side of the previous close. The
            // exact % is in the crosshair readout.
            var brush = price >= pre ? UpBrush : DownBrush;
            var text = Label(FormatPrice(price), brush);
            dc.DrawText(text, new Point(ActualWidth - PadRight + 5, y - text.Height / 2));
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

        // The average line is skipped entirely when the source doesn't report one
        // (Tencent fallback on BJ/US/KR) — drawing it from zeros would put a flat
        // line along the bottom that reads as a real average of 0.
        if (!points.Any(p => p.AvgPrice > 0)) return;

        var avg = new PathFigure { StartPoint = new Point(X(0, step), priceToY(points[0].AvgPrice)) };
        for (var i = 1; i < points.Count; i++)
            avg.Segments.Add(new LineSegment(new Point(X(i, step), priceToY(points[i].AvgPrice)), true));

        dc.DrawGeometry(null, AvgLine, new PathGeometry { Figures = { avg } });
    }

    private void DrawVolume(
        DrawingContext dc, double step, Func<double, double> volumeToY, double volumeBottom,
        double pre, IReadOnlyList<TrendPoint> points)
    {
        var width = Math.Max(1, step * 0.7);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var brush = p.Price >= pre ? UpBrush : DownBrush;
            var x = X(i, step);
            var y = volumeToY(p.Volume);
            dc.DrawRectangle(brush, null, new Rect(x - width / 2, y, width, Math.Max(0, volumeBottom - y)));
        }
    }

    private void DrawTimeAxis(
        DrawingContext dc, double step, double bottom, IReadOnlyList<TrendPoint> points)
    {
        const int ticks = 4;
        for (var t = 0; t <= ticks; t++)
        {
            var i = (int)Math.Round((double)(points.Count - 1) * t / ticks);
            i = Math.Clamp(i, 0, points.Count - 1);

            var text = Label(points[i].Clock, AxisText);
            var tx = Math.Clamp(X(i, step) - text.Width / 2, 0, ActualWidth - text.Width);
            dc.DrawText(text, new Point(tx, bottom + 4));
        }
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

        DrawReadout(dc, p);
    }

    private void DrawReadout(DrawingContext dc, TrendPoint p)
    {
        var pre = _series!.PreClose;
        var pct = pre > 0 ? (p.Price / pre - 1) * 100 : 0;
        var brush = p.Price >= pre ? UpBrush : DownBrush;

        var lines = new[]
        {
            ("时间", p.Clock, AxisText),
            ("价", FormatPrice(p.Price), brush),
            // "-" rather than 0.00 when the source has no average (Tencent fallback).
            ("均价", p.AvgPrice > 0 ? FormatPrice(p.AvgPrice) : "-", AvgLine.Brush),
            ("涨跌幅", pct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%", brush),
            ("量", FormatVolume(p.Volume), AxisText),
        };

        var texts = lines
            .Select(l => (Key: Label(l.Item1, AxisText), Val: Label(l.Item2, l.Item3)))
            .ToArray();

        var rowHeight = texts[0].Key.Height + 3;
        var keyWidth = texts.Max(t => t.Key.Width);
        var valWidth = texts.Max(t => t.Val.Width);
        var box = new Rect(PadLeft + 6, PadTop + 6, keyWidth + valWidth + 22, rowHeight * texts.Length + 10);
        dc.DrawRectangle(ReadoutBg, new Pen(ReadoutBorder, 1), box);

        var yy = box.Top + 5;
        foreach (var (key, val) in texts)
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

    private FormattedText Label(string text, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

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
}
