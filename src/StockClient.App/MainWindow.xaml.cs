using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using StockClient.App.Services;
using StockClient.App.ViewModels;
using StockClient.App.Views;
using StockClient.Core.Contracts;
using StockClient.Core.Quotes;
using StockClient.Core.Updates;
using Wpf.Ui.Controls;

namespace StockClient.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private QuotesViewModel? _quotes;
    private Views.StealthWindow? _stealth;
    private Views.StealthSettingsWindow? _stealthSettings;
    private System.Windows.Forms.NotifyIcon? _tray;

    // Global Win+Alt+End toggles between the main window and the stealth panel.
    // Registered on the main window (which always exists — entering stealth only
    // minimises it), so the key works from either state. Left Win+Alt with a
    // right-hand navigation key (End) is comfortable two-handed and one of the
    // least-claimed combos. NoRepeat: a toggle must not flip back and forth while held.
    private const int HotkeyToggleStealth = 0xB010;
    private const uint ModWinAltNoRepeat = 0x0008 | 0x0001 | 0x4000;
    private const uint VkEnd = 0x23;
    private const int WmHotkey = 0x0312;
    private HwndSource? _hotkeySource;

    // K-line history is a slower call than the 1s quote poll — its own client
    // with a longer timeout, shared by every chart window.
    private readonly HttpClient _klineHttp;
    private readonly KlineRepository _klineRepo;
    private readonly EastMoneyTrendClient _trendClient;
    private readonly TrendRepository _trendRepo;
    private readonly UpdateService _updates = new();

    // Chart windows are ownerless (so activating one doesn't drag the main window
    // to the front), so they're tracked here to be closed when the app closes —
    // otherwise an ownerless window would keep the process alive.
    private readonly List<Views.KlineWindow> _klineWindows = new();

    public MainWindow()
    {
        InitializeComponent();

        // Sweep any leftover *.old from a previous self-update.
        UpdateService.CleanupOld();

        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;

        _klineHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _klineHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockClient/1.0)");
        _klineRepo = new KlineRepository(
            new EastMoneyKlineClient(_klineHttp),
            new TencentKlineClient(_klineHttp),
            new KlineCache(),
            new MarketClock());
        _trendClient = new EastMoneyTrendClient(_klineHttp);
        _trendRepo = new TrendRepository(_trendClient, new MarketClock());

        // Mica needs Windows 11 (build 22000+). Asking for it on Windows 10
        // yields a window with no backdrop at all — it renders invisible.
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
            Background = System.Windows.Media.Brushes.Transparent;
        }

        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync();

            // The quotes view reuses the loaded contract lists so "add contract"
            // can search by name without a second data source.
            _quotes = new QuotesViewModel(Dispatcher, _vm.Repository);
            Quotes.DataContext = _quotes;

            // Quiet background update check; only speaks up if there's a newer release.
            _ = CheckForUpdatesAsync(manual: false);
        };

        // Double-clicking a live-quote row opens its chart; the view forwards the
        // code because it has neither the repository nor the K-line client.
        Quotes.OpenKlineRequested += OpenKlineByCode;

        // hwnd exists by SourceInitialized, which is where a global hotkey hooks in.
        SourceInitialized += (_, _) => RegisterToggleHotkey();

        // Nobody reads the numbers while minimized — unless the stealth panel is
        // up, which is exactly the case where polling must continue.
        StateChanged += (_, _) =>
        {
            if (_stealth is not null) return;

            if (WindowState == WindowState.Minimized) _quotes?.Pause();
            else _quotes?.Resume();
        };

        Closed += async (_, _) =>
        {
            HideTrayIcon();

            if (_hotkeySource is { } src)
            {
                Native.UnregisterHotKey(src.Handle, HotkeyToggleStealth);
                src.RemoveHook(HotkeyProc);
            }

            _stealth?.Close();
            _stealthSettings?.Close();

            // ToArray: each Close removes itself from the list via its Closed handler.
            foreach (var window in _klineWindows.ToArray()) window.Close();

            if (_quotes is not null) await _quotes.DisposeAsync();
            _vm.Dispose();
            _klineHttp.Dispose();
        };
    }

    /// <summary>
    /// Opens (or re-focuses) the single stealth-settings window. Reachable from
    /// both the main window's button and the panel's right-click, so it's managed
    /// here rather than in the panel — one window, whichever entry point is used.
    /// Changes apply live to the panel if it's up, and persist regardless.
    /// </summary>
    private void OpenStealthSettings()
    {
        if (_quotes is null) return;

        if (_stealthSettings is { } open)
        {
            open.Activate();
            return;
        }

        _stealthSettings = new Views.StealthSettingsWindow(
            _quotes.Stealth, _quotes.SaveConfig, () => _stealth?.ApplySettings());
        _stealthSettings.Closed += (_, _) => _stealthSettings = null;
        _stealthSettings.Show();
    }

    private void StealthSettings_Click(object sender, RoutedEventArgs e) => OpenStealthSettings();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// Checks GitHub for a newer release. Silent unless there's an update (or the
    /// user asked manually), so the startup check never nags.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateCheck check;
        try
        {
            check = await _updates.CheckAsync();
        }
        catch (Exception ex)
        {
            if (manual) await InfoDialog("检查更新", "检查失败：" + ex.Message);
            return;
        }

        if (!check.HasUpdate)
        {
            if (manual)
                await InfoDialog("检查更新", check.Release is null
                    ? $"当前版本 v{check.Current}\n暂无发布版本。"
                    : $"已是最新版本 v{check.Current}。");
            return;
        }

        var release = check.Release!;
        var notes = string.IsNullOrWhiteSpace(release.Notes)
            ? ""
            : "\n\n" + (release.Notes.Length > 400 ? release.Notes[..400] + "…" : release.Notes);

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "发现新版本",
            Content = $"当前 v{check.Current}  →  最新 {release.DisplayName}（{release.Source}）{notes}\n\n是否现在更新？下载完成后会自动重启。",
            PrimaryButtonText = "立即更新",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "稍后",
        };

        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        await ApplyUpdateAsync(release);
    }

    private async Task ApplyUpdateAsync(ReleaseInfo release)
    {
        var original = UpdateButton.Content;
        UpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateButton.Content = $"下载中 {p * 100:0}%");

        try
        {
            // On success the app restarts and shuts down — this call won't return.
            await _updates.DownloadAndApplyAsync(release, progress);
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = original;
            await InfoDialog("更新失败", ex.Message);
        }
    }

    private static async Task InfoDialog(string title, string content) =>
        await new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            CloseButtonText = "知道了",
        }.ShowDialogAsync();

    /// <summary>Opens a K-line window for a contract row (contract-search grid).</summary>
    private void ResultGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ClickedItem(e.OriginalSource as DependencyObject);
        Probe.Log($"ResultGrid_DoubleClick item={(item as Contract)?.Code ?? "none"}");

        if (item is Contract contract)
            OpenKline(contract);
    }

    /// <summary>
    /// Opens a K-line window from a bare code (live-quote row). Resolves the full
    /// contract for its market number; if the code isn't in the loaded lists —
    /// delisted, or hand-typed — charts a bare contract, which still works for
    /// every market except an ambiguous US exchange.
    /// </summary>
    private void OpenKlineByCode(string code)
    {
        var contract = _vm.Repository.Find(code)
                       ?? new Contract { Code = code, Name = _quotes?.RowName(code) ?? code };

        OpenKline(contract);
    }

    private void OpenKline(Contract contract)
    {
        Probe.Log($"OpenKline {contract.Code} {contract.Name} secid={contract.EastMoneySecId}");

        var vm = new ViewModels.KlineViewModel(contract, _klineRepo, _trendClient, Dispatcher);

        // No Owner: an owned window drags its owner to the front when activated,
        // which surfaced the main window every time a chart was clicked. Tracked
        // instead, and closed when the main window closes.
        var window = new Views.KlineWindow(vm);
        _klineWindows.Add(window);
        window.Closed += (_, _) => _klineWindows.Remove(window);
        window.Show();
    }

    /// <summary>Walks up from the double-clicked element to its DataGridRow's item.</summary>
    private static object? ClickedItem(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        return (source as DataGridRow)?.Item;
    }

    private void Stealth_Click(object sender, RoutedEventArgs e) => EnterStealth();

    /// <summary>Drops to the stealth ticker: main window to the background,
    /// one transparent always-on-top line left on the desktop.</summary>
    private void EnterStealth()
    {
        if (_quotes is null || _stealth is not null) return;

        _stealth = new Views.StealthWindow(_quotes, _quotes.Stealth, _quotes.SaveConfig, _trendRepo);
        _stealth.RestoreRequested += () => RestoreFromStealth("panel context menu 还原主窗口");
        _stealth.SettingsRequested += OpenStealthSettings;
        _stealth.Closed += (_, _) => _stealth = null;

        PlacePanel(_stealth, _quotes.Stealth);
        _stealth.Show();

        ShowTrayIcon();

        WindowState = WindowState.Minimized;
        ShowInTaskbar = false;
    }

    /// <summary>Win+Alt+End flips between the main window and the stealth ticker.</summary>
    private void ToggleStealth()
    {
        if (_stealth is null) EnterStealth();
        else RestoreFromStealth("toggle hotkey");
    }

    private void RegisterToggleHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(HotkeyProc);

        var ok = Native.RegisterHotKey(handle, HotkeyToggleStealth, ModWinAltNoRepeat, VkEnd);
        Probe.Log($"RegisterHotKey Win+Alt+End (toggle stealth) -> {(ok ? "ok" : "FAIL, key taken")}");
    }

    private IntPtr HotkeyProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyToggleStealth)
        {
            ToggleStealth();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The tray icon is the way back.
    ///
    /// In stealth mode the main window is out of the taskbar and the panel can
    /// be dimmed to near-invisible, so without this there is no handle on the
    /// app at all — it just looks like it died.
    /// </summary>
    private void ShowTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("恢复本体", null, (_, _) => Dispatcher.Invoke(() => RestoreFromStealth("tray menu 恢复本体")));
        menu.Items.Add("显示/隐藏行情条", null, (_, _) => Dispatcher.Invoke(TogglePanel));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "行情终端 · 简洁面板",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => RestoreFromStealth("tray icon double-click"));
    }

    /// <summary>
    /// Restores the last position, falling back to the top-right corner.
    ///
    /// The saved point is checked against the current screens: monitors get
    /// unplugged and resolutions change, and a panel restored onto a screen that
    /// no longer exists would be invisible with no way to drag it back.
    /// </summary>
    private static void PlacePanel(Views.StealthWindow panel, StockClient.Core.Groups.StealthConfig config)
    {
        var area = SystemParameters.WorkArea;

        if (config.Left is { } left && config.Top is { } top && IsOnAScreen(left, top))
        {
            panel.Left = left;
            panel.Top = top;
            return;
        }

        panel.Left = area.Right - 260;
        panel.Top = area.Top + 8;
    }

    /// <summary>
    /// Bounds, not WorkingArea: the panel is topmost, so parking it over the
    /// taskbar is a normal thing to do — and a natural one for a ticker. Judging
    /// it against the work area declared any such position off-screen and
    /// bounced the panel back to the primary monitor's corner on every launch.
    ///
    /// The margins keep a grabbable sliver on screen: 40px across, 10px down.
    /// </summary>
    private static bool IsOnAScreen(double left, double top) =>
        System.Windows.Forms.Screen.AllScreens.Any(s =>
            left >= s.Bounds.Left - 40 && left <= s.Bounds.Right - 40 &&
            top >= s.Bounds.Top - 10 && top <= s.Bounds.Bottom - 10);

    private void TogglePanel()
    {
        if (_stealth is null) return;

        _stealth.Visibility = _stealth.Visibility == Visibility.Visible
            ? Visibility.Hidden
            : Visibility.Visible;

        Probe.Log($"TogglePanel -> {_stealth.Visibility} (tray menu 显示/隐藏行情条)");
    }

    private void HideTrayIcon()
    {
        if (_tray is null) return;

        // Must be disposed explicitly or the icon lingers in the tray until the
        // user hovers over it.
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    /// <summary>Called when a second launch asks the app to surface.</summary>
    public void LeaveStealth(string reason = "LeaveStealth") => RestoreFromStealth(reason);

    /// <summary>
    /// Every caller passes why. Restoring closes the panel, so when the ticker is
    /// reported "gone" this line is the difference between knowing which of the
    /// four entry points fired and guessing.
    /// </summary>
    private void RestoreFromStealth(string reason)
    {
        Probe.Log($"RestoreFromStealth: {reason} -> closing panel (panel={(_stealth is null ? "already null" : "open")})");

        HideTrayIcon();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();

        _stealth?.Close();
        _stealth = null;
    }

    private void ResultGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid) ColumnMenu.Attach(grid);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearAll();

        // The view model owns the sort state, so the header arrows have to be
        // cleared here or they'd still point at a column that is no longer sorted.
        foreach (var column in ResultGrid.Columns) column.SortDirection = null;

        SearchBox.Focus();
    }

    /// <summary>
    /// Sorting is taken over from the grid so it survives the next search: the
    /// view model replaces ItemsSource wholesale, which would otherwise discard
    /// the view's sort descriptions and silently drop back to relevance order.
    /// </summary>
    private void ResultGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        var field = e.Column.SortMemberPath switch
        {
            "Name" => SortField.Name,
            "Market" => SortField.Market,
            "Type" => SortField.Type,
            "Industry" => SortField.Industry,
            "ListedOn" => SortField.ListedOn,
            "Concepts" => SortField.Concepts,
            "Region" => SortField.Region,
            "TotalShares" => SortField.TotalShares,
            "FloatShares" => SortField.FloatShares,
            _ => SortField.Code,
        };

        // Toggle off the view model's state, not e.Column.SortDirection: the
        // grid nulls that out every time ItemsSource is replaced, which a sort
        // always does, so reading it back would report "unsorted" forever and
        // the direction would never flip.
        var ascending = _vm.SortField != field || !_vm.SortAscending;

        _vm.SetSort(field, ascending);

        // Set the arrow after the re-sort, otherwise the rebind clears it.
        foreach (var column in ResultGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
    }
}
