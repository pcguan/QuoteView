using System.Windows;
using StockClient.App.Services;
using StockClient.Core.Groups;

namespace StockClient.App.Views;

/// <summary>
/// The one settings window: 「系统」 (app-level switches — auto-update etc.,
/// stored machine-locally in <see cref="AppPrefs"/>) plus 「简洁面板」 (the
/// stealth-panel page, hosted as a tab). Openers pick the initial tab: the
/// main toolbar lands on 系统, the panel's right-click lands on 简洁面板.
/// </summary>
public partial class SystemSettingsWindow : Window
{
    public const int TabSystem = 0;
    public const int TabPanel = 1;

    public SystemSettingsWindow(GroupConfig root, Action save, Action onChanged, int tab)
    {
        InitializeComponent();

        PanelHost.Content = new StealthSettingsView(root, save, onChanged);
        (AppPrefs.AutoUpdateMode switch
        {
            AppPrefs.AutoInstant => AutoInstantRadio,
            AppPrefs.AutoOff => AutoOffRadio,
            _ => AutoSilentRadio,
        }).IsChecked = true;
        UpdateToastBox.IsChecked = AppPrefs.UpdateToast;
        VersionText.Text = $"当前版本 v{UpdateService.Current}";
        Tabs.SelectedIndex = tab;
    }

    public void SelectTab(int tab) => Tabs.SelectedIndex = tab;

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        // The ctor's seeding assignment fires this before the window is loaded.
        if (!IsLoaded) return;
        AppPrefs.AutoUpdateMode =
            AutoInstantRadio.IsChecked == true ? AppPrefs.AutoInstant
            : AutoOffRadio.IsChecked == true ? AppPrefs.AutoOff
            : AppPrefs.AutoSilent;
    }

    private void UpdateToast_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) AppPrefs.UpdateToast = UpdateToastBox.IsChecked == true;
    }
}
