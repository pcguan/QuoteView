using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// A tiny intraday sparkline for the stealth panel: today's price line for one
/// contract, coloured red/green against the previous close, on a transparent
/// background so it fades with the panel's shade like the text. The live price is
/// appended as the trailing point, so the tip tracks the 1s quote between fetches.
/// </summary>
public sealed class PanelSparkline : FrameworkElement
{
    private static readonly Brush Up = Frozen("#EF5350");
    private static readonly Brush Down = Frozen("#26A69A");
    private static readonly Brush UpFill = Frozen("#59EF5350");
    private static readonly Brush DownFill = Frozen("#5926A69A");
    private static readonly Pen BaselinePen = FrozenPen("#5F6672", 0.6, dashed: true);

    private TrendSeries? _series;
    private double _live;

    public void Set(TrendSeries? series, double live)
    {
        _series = series;
        _live = live;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        // Say so instead of drawing nothing. An empty strip is indistinguishable
        // from a broken chart — which is exactly how a throttled trend source got
        // reported as "画不出来".
        if (_series is null || _series.Points.Count == 0)
        {
            var text = new FormattedText(
                "分时暂无数据", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"), 10, Frozen("#8B93A3"),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(1, (h - text.Height) / 2));
            return;
        }

        // x is the fraction of the full session, NOT of the points gathered so far,
        // so equal elapsed time draws equal length across contracts and the line
        // grows left-to-right through the day. slots-1 is the last drawable slot.
        var count = _series.Points.Count;
        var slots = Math.Max(2, ExpectedSlots(count));

        var pts = new List<(double Frac, double Price)>(count + 1);
        for (var i = 0; i < count; i++)
        {
            var price = _series.Points[i].Price;
            if (price > 0) pts.Add(((double)i / (slots - 1), price));
        }

        // Live price sits in the next slot after the last minute point.
        if (_live > 0) pts.Add(((double)count / (slots - 1), _live));
        if (pts.Count < 2) return;

        var pre = _series.PreClose;
        var min = pts.Min(p => p.Price);
        var max = pts.Max(p => p.Price);
        if (pre > 0) { min = Math.Min(min, pre); max = Math.Max(max, pre); }
        if (max <= min) max = min + 1;

        const double pad = 2;
        double Y(double v) => h - pad - (v - min) / (max - min) * (h - 2 * pad);
        double X(double frac) => Math.Clamp(frac, 0, 1) * w;

        // Previous-close baseline, so a flat day still reads against yesterday.
        if (pre > 0)
        {
            var yb = Y(pre);
            dc.DrawLine(BaselinePen, new Point(0, yb), new Point(w, yb));
        }

        var rising = pts[^1].Price >= pre;
        var pen = new Pen(rising ? Up : Down, 1.6);
        pen.Freeze();

        var figure = new PathFigure { StartPoint = new Point(X(pts[0].Frac), Y(pts[0].Price)) };
        for (var i = 1; i < pts.Count; i++)
            figure.Segments.Add(new LineSegment(new Point(X(pts[i].Frac), Y(pts[i].Price)), true));

        // Filled down to the baseline, and a thicker line than a normal chart would
        // use. The panel runs at whatever opacity the shade dial is set to — at 20%
        // a 1px hairline is effectively invisible, and early in the session the line
        // covers only the fraction of the width the day has elapsed (x spans the
        // WHOLE session so contracts stay comparable), so at 09:40 there is very
        // little of it to see. An area has bulk; a hairline doesn't.
        var area = figure.Clone();
        area.Segments.Add(new LineSegment(new Point(X(pts[^1].Frac), h), false));
        area.Segments.Add(new LineSegment(new Point(X(pts[0].Frac), h), false));
        area.IsClosed = true;

        dc.DrawGeometry(rising ? UpFill : DownFill, null, new PathGeometry { Figures = { area } });
        dc.DrawGeometry(null, pen, new PathGeometry { Figures = { figure } });
    }

    /// <summary>
    /// Full session length in minute slots, so a half-finished day spans only part
    /// of the width. Rounds the current count up to the known session lengths
    /// (A-share 241, HK 331, US 391); same rule as the full-size trend chart.
    /// </summary>
    private static int ExpectedSlots(int count)
    {
        foreach (var full in new[] { 241, 331, 391 })
            if (count <= full) return full;
        return count;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string hex, double thickness, bool dashed = false)
    {
        var pen = new Pen(Frozen(hex), thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 2, 2 }, 0);
        pen.Freeze();
        return pen;
    }
}
