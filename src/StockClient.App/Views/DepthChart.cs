using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// The order book drawn as a ladder: asks on top counting down to 卖一, bids
/// below starting at 买一, each row a horizontal bar whose length is that level's
/// size against the largest size on screen.
///
/// Self-drawn rather than a grid of controls because it redraws every second with
/// the quote poll — a dozen TextBlocks re-measuring at 1Hz behind a transparent
/// always-on-top window is exactly the kind of thing that makes a ticker stutter.
///
/// One control serves both the main window and the stealth panel; only
/// <see cref="RowHeight"/>/<see cref="FontSize"/> differ, and
/// <see cref="Dim"/> lets the panel fade it with the rest of its text. The
/// background is transparent for the same reason.
/// </summary>
public sealed class DepthChart : FrameworkElement
{
    private static readonly Brush AskBar = Frozen("#3326A69A");
    private static readonly Brush BidBar = Frozen("#33EF5350");
    private static readonly Brush AskText = Frozen("#26A69A");
    private static readonly Brush BidText = Frozen("#EF5350");
    private static readonly Brush Label = Frozen("#8B93A3");
    private static readonly Brush Size = Frozen("#C7CEDB");
    private static readonly Pen Split = FrozenPen("#3F4756", 0.6);

    private static readonly Typeface Face =
        new(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Digits =
        new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private QuoteDepth _depth = new();
    private double _reference;
    private int _decimals = 2;

    /// <summary>Height of one level row. Five per side, so the control wants 10× this.</summary>
    public double RowHeight { get; init; } = 16;

    public double FontSize { get; init; } = 11;

    /// <summary>0-1 fade applied to everything, matching the panel's shade.</summary>
    public double Dim { get; set; } = 1;

    /// <summary>How many levels a side to draw; the panel shows fewer than the main window.</summary>
    public int Levels { get; init; } = 5;

    /// <summary>
    /// Feeds a new book. <paramref name="reference"/> is the previous close, used
    /// only to tint prices red/green the same way the rest of the app does.
    /// </summary>
    public void Set(QuoteDepth depth, double reference, int decimals)
    {
        _depth = depth;
        _reference = reference;
        _decimals = Math.Clamp(decimals, 0, 4);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        // The scale comes from the largest size on screen, so the bars compare
        // levels against each other.
        var max = _depth.MaxVolume;

        // No sizes anywhere means the feed isn't really giving a book. Measured on
        // HK: it returns ONE level a side, size hard-zero, and the price is just
        // the last trade echoed back (买一 = 卖一 = 现价). Drawing that is worse
        // than drawing nothing — two identical rows read as a real spread.
        if (_depth.IsEmpty || max <= 0)
        {
            Draw(dc, "该市场不提供盘口", Label, FontSize, 2, (h - FontSize) / 2);
            return;
        }

        var asks = _depth.Asks.Take(Levels).Reverse().ToArray();   // 卖五 … 卖一
        var bids = _depth.Bids.Take(Levels).ToArray();             // 买一 … 买五

        var y = 0.0;
        for (var i = 0; i < asks.Length; i++)
        {
            Row(dc, asks[i], $"卖{Cn(asks.Length - i)}", AskBar, AskText, max, w, y);
            y += RowHeight;
        }

        if (asks.Length > 0 && bids.Length > 0)
        {
            dc.DrawLine(Pen(Split), new Point(0, y), new Point(w, y));
            y += 1;
        }

        for (var i = 0; i < bids.Length; i++)
        {
            Row(dc, bids[i], $"买{Cn(i + 1)}", BidBar, BidText, max, w, y);
            y += RowHeight;
        }
    }

    private void Row(
        DrawingContext dc, DepthLevel level, string label, Brush bar, Brush price,
        double max, double w, double top)
    {
        // Bar first, underneath: it is a background, and drawing it after the text
        // would wash the digits out at these sizes.
        if (max > 0 && level.Volume > 0)
        {
            var width = Math.Max(1, level.Volume / max * w);
            dc.DrawRectangle(Fade(bar), null, new Rect(0, top, width, RowHeight - 1));
        }

        var mid = top + (RowHeight - FontSize * 1.25) / 2;

        Draw(dc, label, Label, FontSize * 0.92, 2, mid + FontSize * 0.1);

        var priceText = level.Price.ToString("F" + _decimals, CultureInfo.InvariantCulture);
        var tint = _reference > 0 ? (level.Price >= _reference ? BidText : AskText) : price;
        Draw(dc, priceText, tint, FontSize, FontSize * 1.9, mid);

        // Size right-aligned: the eye compares a column of numbers by its right
        // edge, and these span 1 to 6 digits.
        var sizeText = Compact(level.Volume);
        if (sizeText.Length > 0)
        {
            var text = Text(sizeText, Size, FontSize);
            Draw(dc, text, w - text.Width - 2, mid);
        }
    }

    /// <summary>手/股 counts get long; 12345 reads better as 1.2万 in a 170px panel.</summary>
    private static string Compact(double volume)
    {
        if (volume <= 0) return "";
        if (volume >= 1e8) return (volume / 1e8).ToString("0.##") + "亿";
        if (volume >= 1e4) return (volume / 1e4).ToString("0.##") + "万";
        return volume.ToString("0");
    }

    private static string Cn(int n) => n switch
    {
        1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", _ => n.ToString(),
    };

    private void Draw(DrawingContext dc, string s, Brush brush, double size, double x, double y) =>
        Draw(dc, Text(s, brush, size), x, y);

    private static void Draw(DrawingContext dc, FormattedText text, double x, double y) =>
        dc.DrawText(text, new Point(x, y));

    private FormattedText Text(string s, Brush brush, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            s.Any(char.IsDigit) ? Digits : Face, size, Fade(brush),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Applies <see cref="Dim"/> without allocating a brush per frame at full opacity.</summary>
    private Brush Fade(Brush brush)
    {
        if (Dim >= 0.999) return brush;

        var faded = brush.Clone();
        faded.Opacity = Math.Clamp(Dim, 0, 1);
        faded.Freeze();
        return faded;
    }

    private Pen Pen(Pen pen)
    {
        if (Dim >= 0.999) return pen;

        var faded = new Pen(Fade(pen.Brush), pen.Thickness);
        faded.Freeze();
        return faded;
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string hex, double thickness)
    {
        var pen = new Pen(Frozen(hex), thickness);
        pen.Freeze();
        return pen;
    }
}
