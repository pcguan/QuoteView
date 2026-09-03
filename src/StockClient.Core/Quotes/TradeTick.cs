namespace StockClient.Core.Quotes;

/// <summary>
/// One row of EastMoney 逐笔成交 (details/get). NOT a raw single trade: the feed
/// folds same-second same-price trades into one row, and <see cref="Trades"/> is
/// how many were folded. <see cref="Side"/> is the active side the feed tags the
/// row with.
/// </summary>
public sealed record TradeTick
{
    /// <summary>HH:MM:SS in the exchange's own clock.</summary>
    public required string Time { get; init; }

    public required double Price { get; init; }

    /// <summary>Volume in 手 (1 手 = 100 股 for A-shares).</summary>
    public long Volume { get; init; }

    /// <summary>How many raw trades this row folds together (details 笔数).</summary>
    public int Trades { get; init; }

    public TradeSide Side { get; init; }

    /// <summary>成交额 in 元, at the A-share 100-shares-per-手 convention.</summary>
    public double Amount => Price * Volume * 100;
}

/// <summary>
/// Active side EastMoney tags each 逐笔 row with (details 方向 column): 2 主动买,
/// 1 主动卖, 4 中性. Anything unrecognised reads as neutral.
/// </summary>
public enum TradeSide
{
    Neutral = 0,
    Buy = 1,
    Sell = 2,
}

/// <summary>A contract's 逐笔成交 for one session, newest last, as returned.</summary>
public sealed record TradeTickSnapshot
{
    public required string Code { get; init; }

    /// <summary>Previous close, the baseline the tape's colour-vs-flat could use.</summary>
    public double PrePrice { get; init; }

    /// <summary>Price decimal places the feed reports for this contract.</summary>
    public int Decimals { get; init; } = 2;

    public required IReadOnlyList<TradeTick> Ticks { get; init; }
}
