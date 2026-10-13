using System.Globalization;

namespace StockClient.Core.MarketWatch;

/// <summary>Display-only scaling; query values retain the filter's documented units.</summary>
public static class MarketValueFormat
{
    public static double? Scale(string key) => key switch
    {
        nameof(MarketQuote.Volume) or nameof(MarketQuote.MainInflow) or nameof(MarketQuote.SuperInflow)
            or nameof(MarketQuote.BigInflow) or nameof(MarketQuote.MidInflow) or nameof(MarketQuote.SmallInflow) => 1e4,
        nameof(MarketQuote.Amount) or nameof(MarketQuote.TotalCap) or nameof(MarketQuote.FloatCap) => 1e8,
        _ => null,
    };

    public static string Format(double? value, string key, IFormatProvider? culture = null)
    {
        if (value is not { } number || !double.IsFinite(number)) return "—";
        culture ??= CultureInfo.CurrentCulture;
        if (Scale(key) is not { } scale) return number.ToString("N2", culture);
        number *= scale;
        if (!double.IsFinite(number)) return "—";
        var units = new[] { "", "万", "亿", "万亿" };
        var unit = 0;
        // Promote a value that would otherwise round to 10,000 of the smaller unit.
        while (unit < units.Length - 1 && Math.Round(Math.Abs(number), 2, MidpointRounding.AwayFromZero) >= 10000)
        { number /= 10000; unit++; }
        return number.ToString("N2", culture) + units[unit] + (key == nameof(MarketQuote.Volume) ? "手" : "元");
    }
}
