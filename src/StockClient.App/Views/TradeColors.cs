using System.Windows.Media;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// The one 成交明细 colour scheme, shared by the live tape and the detail window:
/// 主动买 red (大单 → violet), 主动卖 green (大单 → cyan), 中性 gray. 大单 = the
/// print's 成交额 crossed the 万元 threshold.
/// </summary>
internal static class TradeColors
{
    private static readonly Brush Buy = Frozen(Tones.UpHex);
    private static readonly Brush Sell = Frozen(Tones.DownHex);
    private static readonly Brush Flat = Frozen(Tones.FlatHex);
    private static readonly Brush BigBuy = Frozen("#C77DFF");
    private static readonly Brush BigSell = Frozen("#2EE6D6");

    public static Brush For(TradeSide side, bool big) => side switch
    {
        TradeSide.Buy => big ? BigBuy : Buy,
        TradeSide.Sell => big ? BigSell : Sell,
        _ => Flat,
    };

    /// <summary>Whether a print counts as 大单: its 成交额 ≥ <paramref name="wan"/> 万元 (0 disables).</summary>
    public static bool IsBig(TradeTick tick, int wan) => wan > 0 && tick.Amount >= wan * 10_000.0;

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
