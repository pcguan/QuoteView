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

/// <summary>Disables input while the lists are still loading.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
