using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// A 成交明细 (逐笔) tape: rows of 时间 · 价 · 量(手), coloured by active side
/// (红买 / 绿卖 / 灰中), with 大单 (成交额 ≥ the 万元 threshold) given an amber
/// wash and bold. Shared by the live K-line window and the historical replay in
/// 历史分时对比, which differ only in newest-first vs chronological ordering.
/// Rows are rebuilt whole on each <see cref="SetTicks"/> — cheap at tape sizes.
/// </summary>
public partial class TradeTapeView : UserControl
{
    private static readonly Brush BigRowBrush = Frozen("#3B2B10");
    private static readonly Brush TapeTimeBrush = Frozen("#6E7686");

    public TradeTapeView() => InitializeComponent();

    /// <param name="bigTradeWan">成交额 万元 threshold for the 大单 highlight; 0 disables.</param>
    /// <param name="newestFirst">Draw the newest row at the top (live tape) vs in
    /// chronological order (historical replay).</param>
    public void SetTicks(IReadOnlyList<TradeTick> ticks, int decimals, int bigTradeWan,
        bool newestFirst = true)
    {
        var bigYuan = bigTradeWan * 10_000.0;
        var buy = Frozen(Tones.UpHex);
        var sell = Frozen(Tones.DownHex);
        var flat = Frozen(Tones.FlatHex);

        Host.Children.Clear();

        void Add(TradeTick t)
        {
            var fg = t.Side switch { TradeSide.Buy => buy, TradeSide.Sell => sell, _ => flat };
            Host.Children.Add(Row(t, decimals, fg, bigYuan > 0 && t.Amount >= bigYuan));
        }

        if (newestFirst)
            for (var i = ticks.Count - 1; i >= 0; i--) Add(ticks[i]);
        else
            foreach (var t in ticks) Add(t);

        // Historical replay reads top-to-bottom, so start it at the first row.
        if (!newestFirst) Scroll.ScrollToTop();
    }

    public void Clear() => Host.Children.Clear();

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static UIElement Row(TradeTick tick, int decimals, Brush sideBrush, bool big)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        if (big) grid.Background = BigRowBrush;

        var weight = big ? FontWeights.SemiBold : FontWeights.Normal;
        grid.Children.Add(Cell(tick.Time, TapeTimeBrush, TextAlignment.Left, FontWeights.Normal, 0));
        grid.Children.Add(Cell(tick.Price.ToString("F" + decimals), sideBrush, TextAlignment.Right, weight, 1));
        grid.Children.Add(Cell(tick.Volume.ToString(), sideBrush, TextAlignment.Right, weight, 2));
        return grid;
    }

    private static TextBlock Cell(string text, Brush fg, TextAlignment align, FontWeight weight, int col)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = 11,
            FontWeight = weight,
            TextAlignment = align,
            Margin = new Thickness(col == 0 ? 0 : 6, 0, 0, 0),
        };
        Grid.SetColumn(tb, col);
        return tb;
    }
}
