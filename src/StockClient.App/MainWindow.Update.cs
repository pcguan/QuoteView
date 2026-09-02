using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using StockClient.App.Services;
using StockClient.App.ViewModels;
using StockClient.App.Views;
using StockClient.Core.Contracts;
using StockClient.Core.Groups;
using StockClient.Core.Quotes;
using StockClient.Core.Updates;
using Wpf.Ui.Controls;

namespace StockClient.App;

// MainWindow 的更新链部分：检查/自动/灰度延迟/更新条/桌面弹窗/换血应用。
// 与 MainWindow.xaml.cs 同一个 partial class，仅按职责分文件，行为不变。
public partial class MainWindow
{
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    private bool _updateCheckBusy;

    /// <summary>
    /// The background cadence: a quiet first check 1.5s after startup, then a
    /// 30-second poll. Each check reads NAS first, GitHub as fallback (10s per
    /// source, throttled inside UpdateService).
    /// </summary>
    private async Task RunUpdateLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await AutoCheckAsync();

        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _updateTimer.Tick += (_, _) => _ = AutoCheckAsync();
        _updateTimer.Start();
    }

    /// <summary>Reentrancy guard: a slow check (both sources timing out) outlives the 30s tick.</summary>
    private async Task AutoCheckAsync()
    {
        if (_updateCheckBusy || _updateApplying) return;
        _updateCheckBusy = true;
        try
        {
            await CheckForUpdatesAsync(manual: false);
        }
        finally
        {
            _updateCheckBusy = false;
        }
    }

    private ReleaseInfo? _pendingRelease;

    /// <summary>Version the user dismissed from the bar; auto-checks won't re-nag for it.</summary>
    private Version? _dismissedVersion;

    /// <summary>
    /// Checks for a newer release (domestic first, GitHub fallback). Found → shows
    /// the bottom update bar. Silent otherwise, unless the user asked manually.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        // 检查更新 clicked while a download is already running used to do
        // NOTHING (every path was guarded) — answer instead of ignoring.
        if (manual && _updateApplying)
        {
            await InfoDialog("正在更新",
                "新版本正在下载安装中（进度见底部提示条），完成后将按当前状态自动重启。");
            return;
        }

        UpdateCheck check;
        try
        {
            check = await _updates.CheckAsync(force: manual);
        }
        catch (Exception ex)
        {
            if (manual) await InfoDialog("检查更新", "检查失败：" + ex.Message);
            return;
        }

        if (check.HasUpdate)
        {
            // 自动更新, three modes: silent waits for 10 idle minutes (an
            // active user is never interrupted), instant applies on detection,
            // off never applies (bar/toast still prompt). The relaunch keeps
            // the current state either way. A failed attempt backs off so a
            // broken download isn't retried every 30 seconds.
            // Staged rollout: a canary machine runs delay=0 and installs first;
            // others defer N hours from when THIS version was first seen, so the
            // canary has a window to catch a bad release before the fleet takes
            // it. Manual 检查更新 ignores the delay (you asked for it now). The
            // clock is in-memory: a restart re-arms it, fine for a few-hour delay.
            if (check.Release!.Version != _delayedVersion)
            {
                _delayedVersion = check.Release!.Version;
                _delayedSince = DateTime.Now;
            }
            var delayPassed = AppPrefs.UpdateDelayHours == 0
                || DateTime.Now - _delayedSince >= TimeSpan.FromHours(AppPrefs.UpdateDelayHours);

            var mode = AppPrefs.AutoUpdateMode;
            if (!manual && delayPassed && mode != AppPrefs.AutoOff && !_updateApplying
                && (mode == AppPrefs.AutoInstant
                    || Views.Native.IdleTime() >= TimeSpan.FromMinutes(10))
                && DateTime.Now - _autoUpdateFailedAt >= TimeSpan.FromMinutes(30))
            {
                var error = await ApplyUpdateAsync(check.Release!, AutoUpdateProgress(check.Release!));
                if (error is null) return;   // unreachable on success (app restarts)
                _autoUpdateFailedAt = DateTime.Now;
                Probe.Log($"auto-update failed: {error.Message}");
                RestoreUpdateBarPrompt(check);
            }

            // An auto-check doesn't re-pop a version the user already closed; a
            // manual check always shows it.
            if (!manual && _dismissedVersion == check.Release!.Version) return;
            ShowUpdateBar(check);

            // Desktop toast, once per version — the visible-from-anywhere twin
            // of the in-app bar (minimized window, stealth mode).
            if (!manual && AppPrefs.UpdateToast && !_updateApplying
                && _toastVersion != check.Release!.Version)
            {
                _toastVersion = check.Release!.Version;
                ShowUpdateToast(check.Release!);
            }
            return;
        }

        if (manual)
            await InfoDialog("检查更新", check.Release is null
                ? $"当前版本 v{check.Current}\n暂无发布版本。"
                : $"已是最新版本 v{check.Current}。");
    }

    /// <summary>True from the moment 更新 is clicked until the app restarts (or
    /// the download fails). Blocks every path that could redraw the bar.</summary>
    private bool _updateApplying;
    private Version? _delayedVersion;
    private DateTime _delayedSince;

    private void ShowUpdateBar(UpdateCheck check)
    {
        // Never clobber an in-flight download: the 30s auto-check used to land
        // here mid-download and reset the button to an enabled 「更新」— the
        // progress "vanished", and a second click collided with the running
        // download (file-in-use error).
        if (_updateApplying) return;

        var release = check.Release!;
        _pendingRelease = release;

        // No source label here (was "国内(NAS)"/"GitHub"): the bar is visible to
        // anyone glancing at the screen, and the mirror's identity is private infra.
        UpdateBarText.Text = $"发现新版本 {release.DisplayName}　当前 v{check.Current}";
        UpdateBarText.ToolTip = string.IsNullOrWhiteSpace(release.Notes) ? null : release.Notes;
        UpdateBarUpdate.Content = "更新";
        UpdateBarUpdate.IsEnabled = true;
        UpdateBar.Visibility = Visibility.Visible;
    }

    private void UpdateBar_Dismiss(object sender, RoutedEventArgs e)
    {
        _dismissedVersion = _pendingRelease?.Version;
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    private DateTime _autoUpdateFailedAt = DateTime.MinValue;

    /// <summary>
    /// An automatic update is silent by nature — surface it in the update bar
    /// so a foreground user SEES the download happening instead of wondering
    /// why the app "does nothing" (立即自动更新 especially). The button shows
    /// live progress, disabled.
    /// </summary>
    private IProgress<double> AutoUpdateProgress(ReleaseInfo release)
    {
        _pendingRelease = release;
        UpdateBarText.Text = $"正在自动更新到 {release.DisplayName}，完成后按当前状态自动重启";
        UpdateBarText.ToolTip = string.IsNullOrWhiteSpace(release.Notes) ? null : release.Notes;
        UpdateBarUpdate.IsEnabled = false;
        UpdateBarUpdate.Content = "准备中…";
        UpdateBar.Visibility = Visibility.Visible;
        return new Progress<double>(p => UpdateBarUpdate.Content = $"下载中 {p * 100:0}%");
    }

    /// <summary>A failed auto attempt hands the bar back to the manual prompt.</summary>
    private void RestoreUpdateBarPrompt(UpdateCheck check)
    {
        UpdateBarText.Text = $"发现新版本 {check.Release!.DisplayName}　当前 v{check.Current}";
        UpdateBarUpdate.Content = "更新";
        UpdateBarUpdate.IsEnabled = true;
    }
    private Version? _toastVersion;
    private Views.UpdateToastWindow? _updateToast;

    private void ShowUpdateToast(ReleaseInfo release)
    {
        _updateToast?.Close();
        var toast = new Views.UpdateToastWindow(release.DisplayName, release.Notes);
        toast.UpgradeRequested += async () =>
        {
            if (_updateApplying) return;
            var error = await ApplyUpdateAsync(release, toast.Progress);
            if (error is not null) toast.ShowError(error.Message);
        };
        toast.Closed += (_, _) => { if (ReferenceEquals(_updateToast, toast)) _updateToast = null; };
        _updateToast = toast;
        toast.Show();
    }

    /// <summary>
    /// The one download-and-restart path, shared by the bar's 更新 button and
    /// the idle-time silent auto-update. Flushes pending config pushes first
    /// (the restart can't rely on Closing — cancellation is ignored during
    /// Shutdown). Returns the failure, or never returns on success.
    /// </summary>
    private async Task<Exception?> ApplyUpdateAsync(ReleaseInfo release, IProgress<double> progress)
    {
        _updateApplying = true;
        _settingsPushTimer?.Stop();
        await FlushSettingsPushAsync();

        // State-preserving restart: foreground stays foreground; a minimized
        // window or an open stealth panel means the whole update runs — and
        // finishes — in the background (the panel itself re-opens via
        // AppPrefs.PanelOpen).
        var background = WindowState == WindowState.Minimized || _stealth is not null;

        try
        {
            // On success the app restarts and shuts down — this call won't return.
            await _updates.DownloadAndApplyAsync(release, progress, background);
            return null;
        }
        catch (Exception ex)
        {
            _updateApplying = false;
            return ex;
        }
    }

    private async void UpdateBar_Update(object sender, RoutedEventArgs e)
    {
        if (_pendingRelease is null || _updateApplying) return;

        UpdateBarUpdate.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateBarUpdate.Content = $"下载中 {p * 100:0}%");

        var error = await ApplyUpdateAsync(_pendingRelease, progress);
        if (error is not null)
        {
            UpdateBarUpdate.IsEnabled = true;
            UpdateBarUpdate.Content = "更新";
            await InfoDialog("更新失败", error.Message);
        }
    }
}
