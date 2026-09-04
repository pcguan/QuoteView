using System.Windows;
using System.Windows.Input;
using StockClient.App.Services;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// Standalone 历史分时对比 window, opened from the stealth panel's right-click so
/// the compare view is reachable WITHOUT dropping back to the main window — the
/// panel stays exactly as it is. Hosts its own <see cref="TrendHistoryView"/>,
/// which is a pure disk reader (never makes a request, keeps no shared mutable
/// state), so any number can be open alongside the panel at once.
///
/// Ownerless, like <see cref="KlineWindow"/>: an owned window would drag the
/// minimized main window to the front on activation. MainWindow tracks these and
/// closes them when it closes.
/// </summary>
public partial class TrendHistoryWindow : Window
{
    public TrendHistoryWindow(QuotesViewModel vm, TrendCache cache,
        ContractRepository contracts, AccountSession session, string code)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);

        History.Init(vm, cache, contracts, session);
        // Selected once the view is loaded: SelectContract kicks off the async
        // date-list read, which is steadier when the tree is fully realized than
        // from inside the constructor (before the window is shown).
        Loaded += (_, _) => History.SelectContract(code);

        var name = contracts.Find(code)?.Name;
        Title = string.IsNullOrEmpty(name) ? $"历史分时对比 · {code}" : $"历史分时对比 · {name}";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
