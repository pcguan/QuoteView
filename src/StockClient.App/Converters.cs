using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace StockClient.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Splits the comma-joined concept string into chips. Capped: some rows carry
/// 200+ characters of concepts, and rendering every one would blow out the row.
/// </summary>
public sealed class ConceptsToTagsConverter : IValueConverter
{
    private const int MaxTags = 4;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string raw || raw.Length == 0) return Array.Empty<string>();

        var all = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (all.Length <= MaxTags) return all;

        // The full list stays reachable through the cell's tooltip.
        return all.Take(MaxTags).Append($"+{all.Length - MaxTags}").ToArray();
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>A-share convention: red for up, green for down.</summary>
public static class Tones
{
    public static readonly Brush Up = Freeze(Color.FromRgb(0xEF, 0x53, 0x50));
    public static readonly Brush Down = Freeze(Color.FromRgb(0x26, 0xA6, 0x9A));
    public static readonly Brush Flat = Freeze(Color.FromRgb(0x8B, 0x93, 0xA3));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed class SignToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return v > 0 ? Tones.Up : v < 0 ? Tones.Down : Tones.Flat;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Cheap instruments quote to more decimals (ETFs to 0.001), and Korean prices
/// are whole won in the hundred-thousands. 0 means "no data" and must not render
/// as 0.00.
/// </summary>
public sealed class PriceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (v == 0) return "--";
        if (v >= 10000) return v.ToString("N0", CultureInfo.InvariantCulture);
        return v.ToString(v < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Tencent's [32] is already a percentage (2.98 = 2.98%).</summary>
public sealed class PctConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Convert.ToDouble(value, CultureInfo.InvariantCulture)
            .ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class SignedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (v == 0) return "--";
        return v.ToString(Math.Abs(v) >= 1000 ? "+#,##0;-#,##0" : "+0.00;-0.00", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Formats the structured quote numerics. Null (market doesn't report it) and 0
/// both render "--". ConverterParameter picks the shape:
///   scale — 进位显示: 3110123 → 311.01万, 1.66e12 → 1.66万亿 (grid columns;
///           the detail badges below the grid keep the raw values)
///   pct   — 0.85 → 0.85% (already a percentage, not a fraction)
///   num   — plain 0.00 (量比 / 市盈 / 市净)
///   raw   — thousands-grouped raw value, for cell tooltips
/// </summary>
public sealed class NumConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double v || v == 0) return "--";

        return parameter as string switch
        {
            "pct" => v.ToString("0.00", CultureInfo.InvariantCulture) + "%",
            "num" => v.ToString("0.00", CultureInfo.InvariantCulture),
            "raw" => v.ToString("#,##0.##", CultureInfo.InvariantCulture),
            _ => Scale(v),
        };
    }

    private static string Scale(double v)
    {
        var sign = v < 0 ? "-" : "";
        var a = Math.Abs(v);
        return a >= 1e12 ? sign + (a / 1e12).ToString("0.00", CultureInfo.InvariantCulture) + "万亿"
            : a >= 1e8 ? sign + (a / 1e8).ToString("0.00", CultureInfo.InvariantCulture) + "亿"
            : a >= 1e4 ? sign + (a / 1e4).ToString("0.00", CultureInfo.InvariantCulture) + "万"
            : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Disables input while the lists are still loading.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
