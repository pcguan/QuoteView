using System.Windows;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;

namespace StockClient.App.Views;

/// <summary>Ownerless so opening it never restores the minimized main window.</summary>
public partial class BoardWatchWindow : Window
{
    public BoardWatchWindow(BoardWatchViewModel vm, Action<Contract> openContract)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);
        WindowPlacement.Attach(this, "boardwatch");
        WindowMinimizeGesture.Attach(this);
        BoardView.Initialize(vm);
        BoardView.OpenContract += openContract;
        Closed += (_, _) => BoardView.Dispose();
    }
}
