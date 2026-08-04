using System.Globalization;
using System.Text;
using StockClient.Core.Contracts;

namespace StockClient.Core.Quotes;

/// <summary>
/// Tencent batch quotes: every code in ONE request, which is what makes a 1s
/// refresh viable without getting rate-limited.
///
///   https://qt.gtimg.cn/q=sh600519,hk00700,usAAPL,kr005930
///
/// Response is GBK, one row per code, terminated by ";\n":
///   v_sh600519="1~贵州茅台~600519~1251.06~1214.88~...";
/// </summary>
public sealed class TencentQuoteClient
{
    private readonly HttpClient _http;

    public TencentQuoteClient(HttpClient http)
    {
        _http = http;
        GbkText.EnsureRegistered();
    }

    public async Task<IReadOnlyList<Quote>> GetQuotesAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0) return Array.Empty<Quote>();

        var apiCodes = codes.Select(ToApiCode).ToArray();
        var url = "https://qt.gtimg.cn/q=" + string.Join(",", apiCodes);

        var body = await GbkText.GetAsync(_http, url, cancellationToken);
        return Parse(codes, apiCodes, body);
    }

    /// <summary>SH600519 -> sh600519, USAAPL -> usAAPL, BJ920992 -> bj920992.</summary>
    public static string ToApiCode(string code)
    {
        if (!CodeMapper.TryParse(code, out var market, out var number))
            throw new ArgumentException($"无法识别的合约代码: {code}", nameof(code));

        return market.ToLowerInvariant() + number;
    }

    /// <summary>Split from the request so it can be unit tested offline.</summary>
    public static IReadOnlyList<Quote> Parse(
        IReadOnlyList<string> codes, IReadOnlyList<string> apiCodes, string body)
    {
        // Index rows by api code: a reordered or missing row must not shift every
        // later quote onto the wrong instrument.
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in body.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = row.IndexOf('=');
            if (eq < 0) continue;

            var key = row[..eq].Trim();
            if (key.StartsWith("v_", StringComparison.OrdinalIgnoreCase)) key = key[2..];
            rows[key] = row[(eq + 1)..];
        }

        var result = new List<Quote>(codes.Count);
        for (var i = 0; i < codes.Count; i++)
        {
            result.Add(rows.TryGetValue(apiCodes[i], out var raw)
                ? ParseRow(codes[i], raw) ?? Quote.Missing(codes[i])
                : Quote.Missing(codes[i]));
        }

        return result;
    }

    private static Quote? ParseRow(string code, string raw)
    {
        var p = raw.Replace("\"", "").Split('~');
        if (p.Length <= 34) return null;

        var name = At(p, 1);
        if (name.Length == 0) return null;

        CodeMapper.TryParse(code, out var market, out _);

        // Structured numerics with per-market unit normalization (all verified
        // against live data): A-share 成交额[37] is in 万, HK/US [37] is raw
        // currency; caps [44]/[45] are in 亿 everywhere (incl. KR's [45], 亿 KRW).
        // [46]-[49] diverge: PB/涨停/跌停/量比 for A-shares, but English name and
        // the 52-week range for HK/US/KR.
        var isA = market is "SH" or "SZ" or "BJ";
        var isHkUs = market is "HK" or "US";

        return new Quote
        {
            Code = code.ToUpperInvariant(),
            Name = name,
            Now = Num(p, 3),
            Yesterday = Num(p, 4),
            Open = Num(p, 5),
            Change = Num(p, 31),
            Percent = Num(p, 32),
            High = Num(p, 33),
            Low = Num(p, 34),
            Time = At(p, 30),

            Volume = Opt(p, 6),
            Amount = isA ? Opt(p, 37) * 1e4 : isHkUs ? Opt(p, 37) : null,
            TurnoverRate = isA || market == "US" ? Opt(p, 38) : null,
            VolumeRatio = isA ? Opt(p, 49) : null,
            Amplitude = isA || isHkUs ? Opt(p, 43) : null,
            AvgPrice = isA ? Opt(p, 51) : null,
            PeTtm = isA || isHkUs ? Opt(p, 39) : null,
            Pb = isA ? Opt(p, 46) : null,
            FloatCap = isA || isHkUs ? Opt(p, 44) * 1e8 : null,
            TotalCap = Opt(p, 45) * 1e8,
            LimitUp = isA ? Opt(p, 47) : null,
            LimitDown = isA ? Opt(p, 48) : null,
            Week52High = isA ? null : Opt(p, 48),
            Week52Low = isA ? null : Opt(p, 49),
            DividendYield = market == "HK" ? Opt(p, 59) : null,
            OuterVolume = isA ? Opt(p, 7) : null,
            InnerVolume = isA ? Opt(p, 8) : null,

            Depth = Depth(p),
            Extras = Extras(market, p),
        };
    }

    /// <summary>
    /// The order book, at [9]–[28]: five bid levels as price/size pairs from [9],
    /// then five ask levels from [19]. Same response as the rest of the quote, so
    /// it is free — no second endpoint, no extra request.
    ///
    /// What actually comes back differs by market (measured): SH/SZ/BJ give all
    /// five levels with sizes in 手; US gives level one only, size in 股; HK gives
    /// level-one PRICES with the sizes hard-zero; KR sends empty strings. A level
    /// with no price is dropped, so "not reported" reads as an empty list instead
    /// of a stack of zero rows.
    /// </summary>
    private static QuoteDepth Depth(string[] p)
    {
        return new QuoteDepth { Bids = Side(9), Asks = Side(19) };

        IReadOnlyList<DepthLevel> Side(int start)
        {
            var levels = new List<DepthLevel>(5);

            for (var i = 0; i < 5; i++)
            {
                var price = Num(p, start + i * 2);
                if (price <= 0) continue;

                levels.Add(new DepthLevel(price, Num(p, start + i * 2 + 1)));
            }

            return levels;
        }
    }

    /// <summary>
    /// Per-market extras. Deliberately NOT one shared table: indices past ~45
    /// mean different things per market. [46] is 市净率 for A-shares but the
    /// English name for HK/US; [48]/[49] are 涨跌停价/量比 for A-shares but
    /// 52-week high/low for HK/US/KR. A shared table renders plausible-looking
    /// nonsense without erroring.
    /// </summary>
    private static IReadOnlyList<QuoteField> Extras(string market, string[] p)
    {
        var fields = new List<QuoteField>();

        void Add(string label, int index, string suffix = "")
        {
            var v = At(p, index);
            if (v.Length == 0 || v == "0" || v == "0.00") return;
            fields.Add(new QuoteField(label, v + suffix));
        }

        switch (market)
        {
            case "SH" or "SZ" or "BJ":
                Add("成交量(手)", 6);
                Add("成交额(万)", 37);
                Add("换手率", 38, "%");
                Add("市盈率TTM", 39);
                Add("振幅", 43, "%");
                Add("流通市值(亿)", 44);
                Add("总市值(亿)", 45);
                Add("市净率", 46);
                Add("涨停价", 47);
                Add("跌停价", 48);
                Add("量比", 49);
                Add("均价", 51);
                Add("外盘", 7);
                Add("内盘", 8);
                break;

            case "HK":
                Add("成交量(股)", 6);
                Add("成交额", 37);
                Add("市盈率TTM", 39);
                Add("振幅", 43, "%");
                Add("流通市值(亿)", 44);
                Add("总市值(亿)", 45);
                // [48]/[49] are the 52-week range here, not 涨跌停/量比.
                Add("52周最高", 48);
                Add("52周最低", 49);
                Add("市盈率静", 57);
                Add("股息率", 59, "%");
                Add("每手股数", 60);
                Add("货币", 75);
                break;

            case "US":
                Add("成交量(股)", 6);
                Add("成交额", 37);
                Add("换手率", 38, "%");
                Add("市盈率TTM", 39);
                Add("振幅", 43, "%");
                Add("流通市值(亿)", 44);
                Add("总市值(亿)", 45);
                Add("52周最高", 48);
                Add("52周最低", 49);
                Add("货币", 35);
                break;

            case "KR":
                // Korea reports almost nothing: 16 of 72 fields carry a value.
                Add("成交量", 6);
                Add("总市值", 45);
                Add("52周最高", 48);
                Add("52周最低", 49);
                break;
        }

        return fields;
    }

    private static string At(string[] p, int i) => i < p.Length ? p[i].Trim() : "";

    private static double Num(string[] p, int i) =>
        double.TryParse(At(p, i), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;

    /// <summary>Like Num but null when absent/unparsable — "not reported" ≠ 0.</summary>
    private static double? Opt(string[] p, int i) =>
        double.TryParse(At(p, i), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}

/// <summary>
/// The quote endpoint answers in GBK. .NET Core ships only a few code pages, so
/// GBK must be registered explicitly or every Chinese name decodes to mojibake.
/// </summary>
public static class GbkText
{
    private static readonly Encoding Gbk;

    static GbkText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding("GBK");
    }

    public static void EnsureRegistered()
    {
        // Touching the class runs the static ctor.
    }

    public static async Task<string> GetAsync(
        HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Gbk.GetString(bytes);
    }
}
