namespace StockClient.Core;

/// <summary>
/// Normalized codes are market prefix + native code:
/// SH600519 / SZ000651 / BJ430418 / HK02020 / USAAPL / KR005930.
/// </summary>
public static class CodeMapper
{
    /// <summary>
    /// BJ and KR are included deliberately. Tencent's quote endpoint serves both
    /// (bj430418 -> 苏轴股份, kr005930 -> Samsung), and using the wrong prefix
    /// makes the row vanish from the response without any error.
    /// </summary>
    private static readonly string[] Markets = { "SH", "SZ", "BJ", "HK", "US", "KR" };

    public static bool TryParse(string code, out string market, out string number)
    {
        market = "";
        number = "";

        if (string.IsNullOrWhiteSpace(code)) return false;

        var upper = code.Trim().ToUpperInvariant();
        foreach (var candidate in Markets)
        {
            if (!upper.StartsWith(candidate, StringComparison.Ordinal)) continue;

            var rest = upper[candidate.Length..];
            if (rest.Length == 0) return false;

            market = candidate;
            number = rest;
            return true;
        }

        return false;
    }

    public static bool IsValid(string code) => TryParse(code, out _, out _);
}
