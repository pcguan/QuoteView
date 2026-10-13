using System.Windows;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;

namespace StockClient.App.Views;

/// <summary>Ownerless so opening it never restores the minimized main window.</summary>
public partial class MarketWatchWindow : Window
{
    public MarketWatchWindow(MarketWatchViewModel vm, Action<Contract> openContract)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);
        WindowPlacement.Attach(this, "marketwatch");
        WindowMinimizeGesture.Attach(this);
        MarketView.Initialize(vm);
        MarketView.OpenContract += openContract;
        Closed += (_, _) => MarketView.Dispose();
    }
}
