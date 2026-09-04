using System.Windows;
using System.Windows.Threading;

namespace StockClient.App.Views;

/// <summary>
/// Bottom-right desktop toast for a freshly found release — visible even while
/// the main window is minimized or in stealth mode. 「后台升级」 downloads with
/// inline progress and the app restarts itself; 「关闭」 (or 20s of silence)
/// dismisses it. The owner pops it at most once per version, and the whole
/// notification can be turned off in 系统设置.
/// </summary>
public partial class UpdateToastWindow : Window
{
    private readonly DispatcherTimer _autoClose;
    private bool _busy;

    /// <summary>Raised once when 后台升级 is clicked; the owner runs the update
    /// and reports failure back via <see cref="ShowError"/>.</summary>
    public event Action? UpgradeRequested;

    public UpdateToastWindow(string displayName, string notes)
    {
        InitializeComponent();
        WindowDimmer.Attach(this);

        TitleText.Text = $"发现新版本 {displayName}";
        NotesText.Text = string.IsNullOrWhiteSpace(notes) ? "已准备好更新。" : notes.Trim();
        NotesText.ToolTip = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 12;
            Top = area.Bottom - ActualHeight - 12;
        };

        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); if (!_busy) Close(); };
        _autoClose.Start();
    }

    /// <summary>Download progress rendered into the button itself.</summary>
    public IProgress<double> Progress =>
        new Progress<double>(p => UpgradeButton.Content = $"下载中 {p * 100:0}%");

    public void ShowError(string message)
    {
        _busy = false;
        NotesText.Text = message;
        UpgradeButton.Content = "重试";
        UpgradeButton.IsEnabled = true;
        _autoClose.Start();
    }

    private void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _autoClose.Stop();
        UpgradeButton.IsEnabled = false;
        UpgradeButton.Content = "准备中…";
        UpgradeRequested?.Invoke();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
