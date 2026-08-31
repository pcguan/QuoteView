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

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private QuotesViewModel? _quotes;
    private Views.StealthWindow? _stealth;
    private Views.SystemSettingsWindow? _systemSettings;
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
    private readonly TencentTrendClient _trendFallback;
    private readonly TrendCache _trendCache;
    private readonly TrendRepository _trendRepo;
    private readonly AccountSession _session;
    private DispatcherTimer? _pingTimer;
    private readonly PresenceChannel _presence;
    private readonly UpdateService _updates = new();
    private DispatcherTimer? _updateTimer;

    // Chart windows are ownerless (so activating one doesn't drag the main window
    // to the front), so they're tracked here to be closed when the app closes —
    // otherwise an ownerless window would keep the process alive.
    private readonly List<Views.KlineWindow> _klineWindows = new();

    public MainWindow()
    {
        InitializeComponent();

        // A background-update relaunch must not surface: start minimized and
        // unactivated — the stealth panel (if it was up) re-opens from Loaded,
        // and a foreground relaunch stays exactly as it was.
        if (App.StartBackground)
        {
            WindowState = WindowState.Minimized;
            ShowActivated = false;
        }

        // Sweep any leftover *.old from a previous self-update. The newest one
        // is usually still LOCKED here — the process that renamed itself is
        // exiting but not gone — so a delayed retry sweeps what this pass
        // can't; otherwise the file sat on the desktop until the NEXT launch.
        UpdateService.CleanupOld();
        _ = Task.Delay(TimeSpan.FromSeconds(15))
            .ContinueWith(_ => UpdateService.CleanupOld());

        _vm = new MainViewModel(Dispatcher);
        DataContext = _vm;

        _klineHttp = Services.DirectHttp.Create(TimeSpan.FromSeconds(15));
        _klineHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockClient/1.0)");

        var appVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "";
        _session = new AccountSession(new AccountClient(_klineHttp, appVersion));
        _presence = new PresenceChannel(_session, appVersion);
        _presence.Start();
        _session.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            UpdateAccountButton();
            UpdateGates();
        });
        _session.Kicked += () => Dispatcher.InvokeAsync(async () =>
            await InfoDialog("已退出登录", "该账户已被管理员登出；如需继续使用请重新登录。"));

        _klineRepo = new KlineRepository(
            new EastMoneyKlineClient(_klineHttp),
            new TencentKlineClient(_klineHttp),
            new KlineCache(),
            new MarketClock(),
            // Server-first: one upstream hit serves every signed-in client; the
            // direct chain above remains the fallback when it is unreachable.
            server: (contract, period, adjust, count, _) =>
                _session.IsSignedIn
                    ? _session.KlineJsonAsync(
                        contract.EastMoneySecId,
                        EastMoneyKlineClient.PeriodCode(period),
                        EastMoneyKlineClient.AdjustCode(adjust),
                        Math.Max(count, 0))
                    : Task.FromResult<string?>(null));
        _trendClient = new EastMoneyTrendClient(_klineHttp);
        // Tencent as the backup source: EastMoney throttles trends2 with connection
        // resets, which used to leave the panel thumbnail simply blank.
        _trendFallback = new TencentTrendClient(_klineHttp);
        _trendCache = new TrendCache();
        _trendRepo = new TrendRepository(
            _trendClient, new MarketClock(), _trendFallback, _trendCache);


        // Mica needs Windows 11 (build 22000+). Asking for it on Windows 10
        // yields a window with no backdrop at all — it renders invisible.
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
            Background = System.Windows.Media.Brushes.Transparent;
        }

        Loaded += async (_, _) =>
        {
            // Update loop first — its 1.5s first check must not wait behind the
            // multi-second contract load.
            _ = RunUpdateLoopAsync();

            await _vm.LoadAsync();

            // Profile selection: 离线模式 gets its own file; a signed-in
            // account gets ITS own per-account file (first sign-in adopts the
            // legacy groups.json it owned). Set BEFORE anything reads a store.
            GroupStore.ActiveProfile =
                _session.OfflineMode && !_session.IsSignedIn ? "offline" : "";

            // Sign in and pull the account's data BEFORE the view model loads:
            // the merge lands in the profile file, and every view then simply
            // reads the merged result — no live re-apply plumbing.
            await _session.TryAutoLoginAsync();
            if (_session.IsSignedIn && _session.Username is { } startUser)
            {
                GroupStore.AdoptLegacyFor(startUser);
                GroupStore.ActiveProfile = startUser;
            }
            await PullAccountDataAsync();

            // The quotes view reuses the loaded contract lists so "add contract"
            // can search by name without a second data source.
            // The daily-kline fetch behind 昨日涨幅 mirrors _klineRepo's
            // server-first routing, but with a tiny lmt and NO shared-cache
            // write — storing a 12-candle series there would become what the
            // chart draws for the rest of the day.
            var dailyEast = new EastMoneyKlineClient(_klineHttp);
            var dailyTencent = new TencentKlineClient(_klineHttp);
            // Circuit breaker for the EastMoney kline host: it throttles with
            // connection resets, and each dead call burns its full timeout —
            // with a whole group crawled sequentially that added up to minutes
            // of blank 昨日涨幅. One failure skips the host for a while.
            var eastKlineDownUntil = DateTimeOffset.MinValue;
            _quotes = new QuotesViewModel(Dispatcher, _vm.Repository,
                fetchDaily: async (contract, ct) =>
                {
                    const int count = 12;
                    static KlineSeries Trim(KlineSeries s) =>
                        s.Candles.Count <= count ? s
                            : s with { Candles = s.Candles.TakeLast(count).ToArray() };

                    // Korea has no queryable daily history upstream (EastMoney's
                    // period fields are broken there, Tencent klines carry only
                    // the current day) — the SERVER archives each session's
                    // close itself and serves the pairs back.
                    if (contract.Market == Market.KR)
                    {
                        if (!_session.IsSignedIn) return null;
                        var body = await _session.KrDailyJsonAsync(contract.Code);
                        if (body is null) return null;

                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        if (!doc.RootElement.TryGetProperty("candles", out var arr)
                            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                            return null;

                        var candles = new List<Kline>();
                        foreach (var c in arr.EnumerateArray())
                        {
                            var close = c.TryGetProperty("close", out var cl) ? cl.GetDouble() : 0;
                            if (close <= 0) continue;
                            var date = c.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
                            candles.Add(new Kline
                            {
                                Date = date, Open = close, Close = close,
                                High = close, Low = close,
                            });
                        }
                        return candles.Count == 0 ? null : new KlineSeries
                        {
                            Code = contract.Code, Name = contract.Name,
                            Period = KlinePeriod.Day, Adjust = KlineAdjust.None,
                            Candles = candles,
                        };
                    }

                    async Task<KlineSeries?> East()
                    {
                        if (DateTimeOffset.Now < eastKlineDownUntil) return null;

                        if (_session.IsSignedIn)
                        {
                            try
                            {
                                var json = await _session.KlineJsonAsync(
                                    contract.EastMoneySecId,
                                    EastMoneyKlineClient.PeriodCode(KlinePeriod.Day),
                                    EastMoneyKlineClient.AdjustCode(KlineAdjust.Qfq),
                                    count);
                                if (json is not null)
                                {
                                    var fromServer = EastMoneyKlineClient.ParseSeries(
                                        json, contract, KlinePeriod.Day, KlineAdjust.Qfq);
                                    if (fromServer.Candles.Count > 0) return fromServer;
                                }
                            }
                            catch (Exception)
                            {
                                // Fall through to the direct fetch.
                            }
                        }

                        try
                        {
                            var east = await dailyEast.FetchAsync(
                                contract, KlinePeriod.Day, KlineAdjust.Qfq, count, ct);
                            if (east.Candles.Count > 0) return east;
                        }
                        catch (Exception)
                        {
                            // Routine throttling (connection resets); trip below.
                        }

                        eastKlineDownUntil = DateTimeOffset.Now + TimeSpan.FromMinutes(5);
                        return null;
                    }

                    // SH/SZ/HK: Tencent FIRST — full history, same vendor as the
                    // quote poll (so 昨收 matches to the tick), and immune to the
                    // EastMoney kline host's routine outages; EastMoney only as
                    // its backup.
                    if (contract.Market is Market.SH or Market.SZ or Market.HK)
                    {
                        try
                        {
                            var t = await dailyTencent.FetchAsync(
                                contract, KlinePeriod.Day, KlineAdjust.Qfq, count, ct);
                            if (t.Candles.Count > 0) return Trim(t);
                        }
                        catch (Exception)
                        {
                            // Fall through to EastMoney.
                        }
                        return await East();
                    }

                    // BJ/US: EastMoney only — Tencent serves a single same-day
                    // candle there, and caching that as "fresh" would stop the
                    // retries; null keeps the 10-minute sweep trying instead.
                    return await East();
                });
            Quotes.DataContext = _quotes;

            // Every later config save pushes the preference slice back up,
            // debounced — colour-picker drags save on every tick of the drag.
            _lastPushedSettings = _pullSettingsFailed ? "" : _quotes.ExportSettingsJson();
            _lastPushedGroups = _quotes.ExportGroupsJson();
            _quotes.ConfigSaved += ScheduleSettingsPush;

            // Pull failed → the local copy is authoritative-but-unpushed: send
            // it up right away so the divergence lands in the audit log instead
            // of lurking until a random touch pushes a misleading full diff.
            if (_pullSettingsFailed) ScheduleSettingsPush();

            History.Init(_quotes, _trendCache, _vm.Repository, _session);
            Brief.InitNews(_session);
            _presence.NewsPushed += () => Dispatcher.InvokeAsync(Brief.NudgeNews);
            UpdateAccountButton();
            UpdateGates();

            // One seed push right after the login pull (local == server here, or
            // local IS the seed for a brand-new account). After this, groups go
            // up only when they change, through the debounced ConfigSaved path —
            // a periodic re-push would let an idle machine keep overwriting
            // another machine's newer state (last push wins, by design).
            _ = PushGroupsAsync();

            // 60s heartbeat: keeps the console's 活跃 view near-real-time, and
            // heals a server-side token loss within one beat (401 -> re-login).
            _pingTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(60),
            };
            _pingTimer.Tick += async (_, _) =>
            {
                if (_session.IsSignedIn) await _session.PingAsync();
            };
            _pingTimer.Start();

            // Restore the stealth panel if it was up when the app last ran —
            // without this an idle-time auto-update restart would swallow the
            // ticker someone left on the desk.
            if (AppPrefs.PanelOpen && Operational) EnterStealth();
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

            if (WindowState == WindowState.Minimized || !Operational) _quotes?.Pause();
            else _quotes?.Resume();
        };

        // Cancel-close → flush → re-close. WPF tears the dispatcher down before
        // an async Closed continuation gets to run, so a change made seconds
        // before quitting silently missed the wire (one lost brightness tweak is
        // how corp-win spent a night out of sync). Cancellation is ignored
        // during Application.Shutdown — the update path flushes on its own
        // before restarting for exactly that reason.
        Closing += async (_, e) =>
        {
            if (_closeFlushed || _quotes is null || !_session.IsSignedIn) return;
            if (_quotes.ExportSettingsJson() == _lastPushedSettings
                && _quotes.ExportGroupsJson() == _lastPushedGroups) return;

            e.Cancel = true;
            _settingsPushTimer?.Stop();
            await FlushSettingsPushAsync();
            _closeFlushed = true;
            Close();
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
            _systemSettings?.Close();
            _updateToast?.Close();

            // ToArray: each Close removes itself from the list via its Closed handler.
            foreach (var window in _klineWindows.ToArray()) window.Close();

            _pingTimer?.Stop();
            await _presence.DisposeAsync();
            _settingsPushTimer?.Stop();
            await FlushSettingsPushAsync();
            if (_quotes is not null) await _quotes.DisposeAsync();
            _vm.Dispose();
            _klineHttp.Dispose();
        };
    }

    /// <summary>
    /// Opens (or re-focuses) the single settings window, landing on the given
    /// tab. Reachable from the toolbar's 「设置」 (系统 tab) and the panel's
    /// right-click (简洁面板 tab) — one window, whichever entry point is used.
    /// Panel changes apply live if the panel is up, and persist regardless.
    /// </summary>
    private void OpenSettings(int tab)
    {
        if (_quotes is null) return;

        if (_systemSettings is { } open)
        {
            open.SelectTab(tab);
            open.Activate();
            return;
        }

        _systemSettings = new Views.SystemSettingsWindow(
            _quotes.Config, _quotes.SaveConfig, () =>
            {
                _stealth?.ApplySettings();
                _quotes?.StealthSettingsChanged();   // fund-flow fields drive the secondary poll
            }, tab);
        _systemSettings.Closed += (_, _) => _systemSettings = null;
        _systemSettings.Show();
    }

    /// <summary>
    /// One-shot groups upload, used right after a login pull — seeds the
    /// server's per-account copy (and with it the sweep union). Upload only:
    /// the server NEVER pushes back mid-session; settings/groups come down
    /// exactly once, at login. Later changes ride the debounced ConfigSaved
    /// push — there is deliberately no periodic re-push.
    /// </summary>
    private async Task PushGroupsAsync()
    {
        if (_quotes is null || !_session.IsSignedIn) return;
        if (!string.Equals(_quotes.ConfigOwner, _session.Username, StringComparison.OrdinalIgnoreCase))
            return;

        if (await _session.SyncGroupsAsync(_quotes.ExportGroups(), _quotes.GroupsUpdatedAt))
            _lastPushedGroups = _quotes.ExportGroupsJson();
    }

    private void GroupColWidthFromConfig()
    {
        if (_quotes is not null) Quotes.SetGroupPaneWidth(_quotes.GroupPaneWidth);
    }

    private string _lastPushedSettings = "";
    private string _lastPushedGroups = "";
    private DispatcherTimer? _settingsPushTimer;

    /// <summary>Last sign-in pull couldn't fetch the settings slice (network
    /// blip / fresh account) — local is then treated as pending-push.</summary>
    private bool _pullSettingsFailed;
    private bool _closeFlushed;

    /// <summary>
    /// Debounced settings push: many UI paths save the config several times a
    /// second (slider drags, colour picking), so the upload waits for 2s of
    /// quiet and skips entirely when nothing in the synced slice changed
    /// (group switching saves the config too, but touches no preference).
    /// </summary>
    private void ScheduleSettingsPush()
    {
        if (_settingsPushTimer is null)
        {
            _settingsPushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _settingsPushTimer.Tick += async (_, _) =>
            {
                _settingsPushTimer!.Stop();
                if (_quotes is null) return;

                var ok = true;

                var json = _quotes.ExportSettingsJson();
                if (json != _lastPushedSettings)
                {
                    // Stamp FIRST, signed in or not: the stamp is what protects
                    // an offline change from being rolled back by a later pull.
                    _quotes.StampPrefsChanged();
                    json = _quotes.ExportSettingsJson();

                    if (!_session.IsSignedIn)
                    {
                        _lastPushedSettings = json;   // baseline; stamp already persisted
                    }
                    else if (await _session.PutSettingsAsync(json))
                    {
                        _lastPushedSettings = json;
                        Probe.Log($"settings push: {json.Length}B ok");
                    }
                    else
                    {
                        ok = false;
                    }
                }

                var groupsJson = _quotes.ExportGroupsJson();
                if (groupsJson != _lastPushedGroups)
                {
                    _quotes.StampGroupsChanged();

                    if (!_session.IsSignedIn
                        || !string.Equals(_quotes.ConfigOwner, _session.Username,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _lastPushedGroups = groupsJson;
                    }
                    else if (await _session.SyncGroupsAsync(
                        _quotes.ExportGroups(), _quotes.GroupsUpdatedAt))
                    {
                        _lastPushedGroups = groupsJson;
                        Probe.Log("groups push ok");
                    }
                    else
                    {
                        // 409 stale included: the reconcile pass pulls the newer copy.
                        ok = false;
                    }
                }

                // 实时同步 means keep trying, not try once: failures re-arm at a
                // gentler cadence until they land.
                _settingsPushTimer.Interval = ok ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30);
                if (!ok) _settingsPushTimer.Start();
            };
        }

        _settingsPushTimer.Interval = TimeSpan.FromSeconds(2);
        _settingsPushTimer.Stop();
        _settingsPushTimer.Start();
    }

    /// <summary>
    /// Last-chance flush: a change made moments before closing hasn't cleared
    /// the debounce yet. Pushes both slices sequentially, updates the baselines
    /// on success (so a second call is a no-op), capped at 3s overall so a dead
    /// network can't hold the window open.
    /// </summary>
    private async Task FlushSettingsPushAsync()
    {
        if (_quotes is null || !_session.IsSignedIn) return;

        var quotes = _quotes;
        var json = quotes.ExportSettingsJson();
        var settingsDirty = json != _lastPushedSettings;
        var groupsJson = quotes.ExportGroupsJson();
        var groupsDirty = groupsJson != _lastPushedGroups
            && string.Equals(quotes.ConfigOwner, _session.Username,
                StringComparison.OrdinalIgnoreCase);
        if (!settingsDirty && !groupsDirty) return;

        if (settingsDirty)
        {
            quotes.StampPrefsChanged();
            json = quotes.ExportSettingsJson();
        }
        if (groupsDirty) quotes.StampGroupsChanged();

        async Task DoFlush()
        {
            if (settingsDirty && await _session.PutSettingsAsync(json))
                _lastPushedSettings = json;
            if (groupsDirty && await _session.SyncGroupsAsync(
                    quotes.ExportGroups(), quotes.GroupsUpdatedAt))
                _lastPushedGroups = groupsJson;
        }

        await Task.WhenAny(DoFlush(), Task.Delay(3000));
    }

    private void UpdateAccountButton() =>
        AccountButton.Content = _session.IsSignedIn ? _session.Username
            : _session.OfflineMode ? "离线模式" : "登录";

    /// <summary>Signed in OR the local offline profile — either unlocks the app.</summary>
    private bool Operational => _session.IsSignedIn || _session.OfflineMode;

    /// <summary>
    /// Sign-in gate: signed out (and not offline), only 合约查询 works — the
    /// other tabs show a login prompt and quote polling stops. 离线模式 opens
    /// quotes and 资讯 (both need no account); 历史分时 stays server-side data,
    /// so its gate remains until a real sign-in.
    /// </summary>
    private void UpdateGates()
    {
        var open = Operational;
        var gate = open ? Visibility.Collapsed : Visibility.Visible;

        QuotesGate.Visibility = gate;
        BriefGate.Visibility = gate;
        HistoryGate.Visibility = _session.IsSignedIn ? Visibility.Collapsed : Visibility.Visible;
        HistoryGateText.Text = _session.OfflineMode
            ? "历史分时数据存于服务端，离线模式下不可用"
            : "登录后查询历史分时";

        if (!open) _quotes?.Pause();
        else if (WindowState != WindowState.Minimized || _stealth is not null) _quotes?.Resume();
    }

    /// <summary>
    /// Post-sign-in pull: the account's settings always merge in; groups are
    /// restored from the server only when this machine's file belongs to a
    /// DIFFERENT account (ownerless pre-account data is adopted by the first
    /// sign-in instead). Returns true when the store file changed.
    /// </summary>
    private async Task<bool> PullAccountDataAsync()
    {
        if (!_session.IsSignedIn || _session.Username is not { } username) return false;

        var store = new GroupStore();
        var changed = false;

        // The login pull is the ONE moment the server copy comes down, so a
        // transient fetch failure must not be silently accepted: the machine
        // then runs on a local state the sync layer believes is already pushed,
        // and the divergence surfaces days later as a full-blob overwrite
        // nobody remembers making. Retry, then flag (see Loaded).
        string? settingsJson = null;
        for (var attempt = 0; attempt < 3 && settingsJson is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500);
            settingsJson = await _session.GetSettingsAsync();
        }
        _pullSettingsFailed = settingsJson is null;
        if (settingsJson is not null)
        {
            store.MergeSettings(settingsJson);
            changed = true;
        }

        // Groups: the login-time pull. Server has a copy -> adopt it (that is
        // "whatever any client pushed last"). Server empty + this machine
        // belonged to ANOTHER account -> start from defaults, never inherit the
        // previous user's groups. Server empty + same owner -> keep local (the
        // periodic upload republishes it).
        var config = store.Load();
        var otherOwner = config.Owner is not null
                         && !string.Equals(config.Owner, username, StringComparison.OrdinalIgnoreCase);

        var remoteGroups = await _session.GroupsWithAtAsync();
        for (var attempt = 0; attempt < 2 && remoteGroups is null; attempt++)
        {
            await Task.Delay(1500);
            remoteGroups = await _session.GroupsWithAtAsync();
        }

        if (remoteGroups is { } remote)
        {
            if (remote.Groups.Count > 0)
            {
                config = store.Load();

                // 轮换 (panel) is client-local: adopting the account's groups
                // must not import another machine's rotation choices — the local
                // flag carries over by name; brand-new groups default to on.
                var localPanel = new Dictionary<string, bool>();
                foreach (var g in config.Groups) localPanel[g.Name] = g.InPanel;

                config.Groups = remote.Groups
                    .Select(g => new Group
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = g.Name.Length > 0 ? g.Name : "分组",
                        Codes = g.Codes.ToList(),
                        InPanel = !localPanel.TryGetValue(g.Name, out var p) || p,
                    })
                    .ToList();
                config.ActiveGroupId = config.Groups.FirstOrDefault()?.Id;
                config.Owner = username;
                store.Save(config);
                changed = true;
            }
            else if (otherOwner)
            {
                config = store.Load();
                config.Groups = new List<Group>();
                config.ActiveGroupId = null;
                config.Owner = username;
                store.Save(config);
                changed = true;
            }
            else if (config.Owner is null)
            {
                config.Owner = username;
                store.Save(config);
                changed = true;
            }
        }

        return changed;
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        var wasUser = _session.Username;
        var dialog = new Views.LoginWindow(_session) { Owner = this };
        dialog.ShowDialog();
        UpdateAccountButton();
        UpdateGates();

        // The identity decides the profile file: offline / per-account /
        // legacy. A change swaps the store and reloads everything — BEFORE any
        // pull, so server data merges into the right account's file and never
        // into offline.json or another user's profile.
        var profile = _session.OfflineMode && !_session.IsSignedIn ? "offline"
            : _session.IsSignedIn && _session.Username is { } user ? user : "";
        if (_session.IsSignedIn && _session.Username is { } u) GroupStore.AdoptLegacyFor(u);
        if (!string.Equals(profile, GroupStore.ActiveProfile, StringComparison.OrdinalIgnoreCase)
            && _quotes is not null)
        {
            GroupStore.ActiveProfile = profile;
            _quotes.ReloadFromStore();
            _lastPushedSettings = _quotes.ExportSettingsJson();
            _lastPushedGroups = _quotes.ExportGroupsJson();
            _stealth?.ApplySettings();
            Quotes.ReapplyColumnLayout();
            GroupColWidthFromConfig();
        }

        if (!_session.IsSignedIn) return;

        // Fresh sign-in: pull the account's data and reload the views if the
        // store changed (always true on a user switch, usually false otherwise).
        var changed = await PullAccountDataAsync();
        if (changed && _quotes is not null)
        {
            _quotes.ReloadFromStore();
            _lastPushedSettings = _quotes.ExportSettingsJson();
            _lastPushedGroups = _quotes.ExportGroupsJson();
            _stealth?.ApplySettings();
            Quotes.ReapplyColumnLayout();
            GroupColWidthFromConfig();
        }

        // Same rule as startup: a failed pull means local is pending-push.
        if (_pullSettingsFailed && _quotes is not null)
        {
            _lastPushedSettings = "";
            ScheduleSettingsPush();
        }

        _ = PushGroupsAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(Views.SystemSettingsWindow.TabSystem);

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
            var mode = AppPrefs.AutoUpdateMode;
            if (!manual && mode != AppPrefs.AutoOff && !_updateApplying
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

        var vm = new ViewModels.KlineViewModel(
            contract, _klineRepo, _trendRepo, Dispatcher, new TencentQuoteClient(_klineHttp));

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
        _stealth.SettingsRequested += () => OpenSettings(Views.SystemSettingsWindow.TabPanel);
        _stealth.Closed += (_, _) => _stealth = null;

        PlacePanel(_stealth, _quotes.Stealth);
        _stealth.Show();
        AppPrefs.PanelOpen = true;

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
        var auto = new System.Windows.Forms.ToolStripMenuItem("自动更新")
        {
            Checked = AppPrefs.AutoUpdateMode != AppPrefs.AutoOff,
            CheckOnClick = true,
        };
        // Three modes collapse to on/off out here: unchecking turns auto-update
        // off, checking restores 静默; the finer choice lives in 系统设置.
        auto.CheckedChanged += (_, _) =>
            AppPrefs.AutoUpdateMode = auto.Checked ? AppPrefs.AutoSilent : AppPrefs.AutoOff;
        // The settings window can flip the mode while the tray sits open-less;
        // re-read on every open so the check mark never shows a stale state.
        menu.Opening += (_, _) => auto.Checked = AppPrefs.AutoUpdateMode != AppPrefs.AutoOff;
        menu.Items.Add(auto);
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

        AppPrefs.PanelOpen = false;
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
