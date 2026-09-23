using System.Windows.Media;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// The one 成交明细 colour scheme, shared by the live tape, the historical replay
/// and the detail window (风格统一). Independent axes, matching the reference:
///   · 成交价 COLOUR vs 昨收: 红 above / 绿 below / 灰 equal — the whole column is
///     measured against the day's baseline, not the previous print.
///   · 成交价 ARROW vs the previous print: ↑ uptick / ↓ downtick / none if unchanged.
///   · 手数 by side: 外盘(主动买) 红, 内盘(主动卖) 绿, 中性 灰 — and when the print
///     is 大单 (成交额 ≥ 万元 threshold) the side goes vivid: 外盘 紫, 内盘 青.
/// </summary>
internal static class TradeColors
{
    public static readonly Brush Up = Frozen(Tones.UpHex);      // 红 涨 / 外盘
    public static readonly Brush Down = Frozen(Tones.DownHex);  // 绿 跌 / 内盘
    public static readonly Brush Flat = Frozen("#9AA4B2");      // 灰 首笔 / 中性

    private static readonly Brush VolBuy = Frozen("#EF5350");      // 红 外盘小单
    // A CLEAN green, deliberately NOT the teal 跌/DownHex (#26A69A) — that reads
    // too close to the 青 big-sell below, so 内盘 小 vs 大 wasn't distinguishable.
    private static readonly Brush VolSell = Frozen("#2ECC71");     // 绿 内盘小单
    private static readonly Brush VolBigBuy = Frozen("#C77DFF");   // 紫 外盘大单
    private static readonly Brush VolBigSell = Frozen("#22D3E8");  // 青 内盘大单

    /// <summary>手数 colour by side (外盘红/内盘绿), going vivid for 大单 (外盘紫/内盘青).</summary>
    public static Brush Volume(TradeSide side, bool big) => side switch
    {
        TradeSide.Buy => big ? VolBigBuy : VolBuy,
        TradeSide.Sell => big ? VolBigSell : VolSell,
        _ => Flat,
    };

    /// <summary>Whether a print counts as 大单: 成交额 ≥ <paramref name="wan"/> 万元 (0 disables).</summary>
    public static bool IsBig(TradeTick tick, int wan) => wan > 0 && tick.Amount >= wan * 10_000.0;

    /// <summary>
    /// 成交价 look for one print. Two independent axes:
    ///   · Colour vs the day's 昨收 (<paramref name="preClose"/>): 红 above / 绿 below / 灰 equal.
    ///   · Arrow vs the PREVIOUS print (<paramref name="prevPrice"/>): ↑ uptick / ↓ downtick /
    ///     none if unchanged (or no prior print).
    /// </summary>
    public static (Brush Brush, string Arrow) PriceLook(double price, double preClose, double prevPrice)
    {
        var brush = preClose <= 0 || price == preClose ? Flat : price > preClose ? Up : Down;
        var arrow = prevPrice <= 0 || price == prevPrice ? "" : price > prevPrice ? "↑" : "↓";
        return (brush, arrow);
    }

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
