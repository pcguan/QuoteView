using StockClient.Core.Boards;
using StockClient.Core.Contracts;

namespace StockClient.Core.MarketWatch;

public sealed record MarketQuote
{
    public required Contract Contract { get; init; }
    public string Code => Contract.Code;
    public string Name => Contract.Name;
    public string MarketName => Contract.Market == Market.SH ? "沪市" : "深市";
    public string BoardName => Contract.TypeDisplay;
    public string Industry => Contract.Industry ?? "";
    public string Concepts => Contract.Concepts ?? "";
    public string Region => Contract.Region ?? "";
    public string ListedOn => Contract.ListedOn ?? "";
    public double? Price { get; init; }
    public double? Percent { get; init; }
    public double? Change { get; init; }
    public double? Open { get; init; }
    public double? Previous { get; init; }
    public double? High { get; init; }
    public double? Low { get; init; }
    public double? Volume { get; init; }
    public double? Amount { get; init; }
    public double? Turnover { get; init; }
    public double? VolumeRatio { get; init; }
    public double? Amplitude { get; init; }
    public double? Speed { get; init; }
    public double? TotalCap { get; init; }
    public double? FloatCap { get; init; }
    public double? Pe { get; init; }
    public double? Pb { get; init; }
    public double? MainInflow { get; init; }
    public double? MainInflowPercent { get; init; }
    public double? SuperInflow { get; init; }
    public double? BigInflow { get; init; }
    public double? MidInflow { get; init; }
    public double? SmallInflow { get; init; }
    public DateTimeOffset? QuoteTime { get; init; }
    public string TimeDisplay => QuoteTime?.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss") ?? "—";
}

public sealed record MarketSnapshot(IReadOnlyList<MarketQuote> Rows, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, string Host, int Pages)
{
    public bool Delayed => Host.Contains("delay", StringComparison.Ordinal);
}

public sealed record MarketField(string Key, string Name, Func<MarketQuote, double?> Read, string Format = "N2")
{
    public override string ToString() => Name;
}

public static class MarketFields
{
    // Numeric properties are normalized to the units printed in the column/filter label.
    public static IReadOnlyList<MarketField> All { get; } = new MarketField[]
    {
        new(nameof(MarketQuote.Price), "最新价", r => r.Price),
        new(nameof(MarketQuote.Percent), "涨跌幅(%)", r => r.Percent),
        new(nameof(MarketQuote.Change), "涨跌额", r => r.Change),
        new(nameof(MarketQuote.Open), "今开", r => r.Open),
        new(nameof(MarketQuote.Previous), "昨收", r => r.Previous),
        new(nameof(MarketQuote.High), "最高", r => r.High),
        new(nameof(MarketQuote.Low), "最低", r => r.Low),
        new(nameof(MarketQuote.Volume), "成交量(万手)", r => r.Volume),
        new(nameof(MarketQuote.Amount), "成交额(亿)", r => r.Amount),
        new(nameof(MarketQuote.Turnover), "换手率(%)", r => r.Turnover),
        new(nameof(MarketQuote.VolumeRatio), "量比", r => r.VolumeRatio),
        new(nameof(MarketQuote.Amplitude), "振幅(%)", r => r.Amplitude),
        new(nameof(MarketQuote.Speed), "涨速(%)", r => r.Speed),
        new(nameof(MarketQuote.TotalCap), "总市值(亿)", r => r.TotalCap),
        new(nameof(MarketQuote.FloatCap), "流通市值(亿)", r => r.FloatCap),
        new(nameof(MarketQuote.Pe), "市盈率(动)", r => r.Pe),
        new(nameof(MarketQuote.Pb), "市净率", r => r.Pb),
        new(nameof(MarketQuote.MainInflow), "主力净流入(万)", r => r.MainInflow),
        new(nameof(MarketQuote.MainInflowPercent), "主力净占比(%)", r => r.MainInflowPercent),
        new(nameof(MarketQuote.SuperInflow), "超大单净额(万)", r => r.SuperInflow),
        new(nameof(MarketQuote.BigInflow), "大单净额(万)", r => r.BigInflow),
        new(nameof(MarketQuote.MidInflow), "中单净额(万)", r => r.MidInflow),
        new(nameof(MarketQuote.SmallInflow), "小单净额(万)", r => r.SmallInflow),
    };
}

public enum MarketComparison { GreaterOrEqual, LessOrEqual, Greater, Less, Equal }

public sealed record MarketCondition(MarketField Field, MarketComparison Comparison, double Value)
{
    public string Display => $"{Field.Name} {Symbol(Comparison)} {Value:G}";
    public static string Symbol(MarketComparison comparison) => comparison switch
    { MarketComparison.GreaterOrEqual => "≥", MarketComparison.LessOrEqual => "≤",
      MarketComparison.Greater => ">", MarketComparison.Less => "<", _ => "=" };
    public bool Matches(MarketQuote row)
    {
        if (Field.Read(row) is not { } value) return false;
        return Comparison switch
        {
            MarketComparison.GreaterOrEqual => value >= Value,
            MarketComparison.LessOrEqual => value <= Value,
            MarketComparison.Greater => value > Value,
            MarketComparison.Less => value < Value,
            _ => value == Value,
        };
    }
}

public sealed record MarketQuery
{
    public Market? Market { get; init; }
    public ListingBoards Boards { get; init; } = ListingBoards.All;
    public string Keyword { get; init; } = "";
    public bool ExcludeSt { get; init; }
    public bool QuotedOnly { get; init; }
    public IReadOnlyList<MarketCondition> Conditions { get; init; } = Array.Empty<MarketCondition>();

    public IReadOnlyList<MarketQuote> Apply(IEnumerable<MarketQuote> rows, string sortKey, bool descending)
    {
        var query = Keyword.Trim();
        var filtered = rows.Where(r => (Market is null || r.Contract.Market == Market)
            && BoardSearch.Includes(r.Contract, Boards)
            && (!ExcludeSt || !r.Name.Contains("ST", StringComparison.OrdinalIgnoreCase))
            && (!QuotedOnly || r.Price is > 0)
            && (query.Length == 0 || new[] { r.Code, r.Name, r.Industry, r.Concepts, r.Region,
                r.Contract.Pinyin, r.Contract.PinyinInitials }.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)))
            && Conditions.All(c => c.Matches(r)));
        var field = MarketFields.All.FirstOrDefault(f => f.Key == sortKey);
        IOrderedEnumerable<MarketQuote> ordered;
        if (field is not null)
        {
            // Missing values stay last in both directions; "-" is never zero.
            var present = filtered.OrderBy(r => field.Read(r) is null);
            ordered = descending ? present.ThenByDescending(field.Read) : present.ThenBy(field.Read);
        }
        else if (sortKey == nameof(MarketQuote.QuoteTime))
        {
            var present = filtered.OrderBy(r => r.QuoteTime is null);
            ordered = descending ? present.ThenByDescending(r => r.QuoteTime) : present.ThenBy(r => r.QuoteTime);
        }
        else
        {
            string Read(MarketQuote r) => sortKey switch
            {
                nameof(MarketQuote.Name) => r.Name, nameof(MarketQuote.MarketName) => r.MarketName,
                nameof(MarketQuote.BoardName) => r.BoardName, nameof(MarketQuote.Industry) => r.Industry,
                nameof(MarketQuote.Concepts) => r.Concepts, nameof(MarketQuote.Region) => r.Region,
                nameof(MarketQuote.ListedOn) => r.ListedOn, _ => r.Code,
            };
            var present = filtered.OrderBy(r => Read(r).Length == 0);
            ordered = descending ? present.ThenByDescending(Read, StringComparer.CurrentCulture)
                : present.ThenBy(Read, StringComparer.CurrentCulture);
        }
        return ordered.ThenBy(r => r.Code, StringComparer.Ordinal).ToArray();
    }
}

public static class MarketRefreshPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ClosedInterval = TimeSpan.FromMinutes(15);
    public static bool IsTradingSession(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8));
        var time = TimeOnly.FromDateTime(local.DateTime);
        return TradingCalendar.IsTradingDay(Market.SH, DateOnly.FromDateTime(local.DateTime))
            && ((time >= new TimeOnly(9, 15) && time <= new TimeOnly(11, 31))
                || (time >= new TimeOnly(12, 59) && time <= new TimeOnly(15, 5)));
    }
    public static TimeSpan FailureDelay(int failures) => TimeSpan.FromSeconds(
        Math.Min(600, 60 * Math.Pow(2, Math.Clamp(failures, 1, 4))));
}
