using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using StockClient.App.ViewModels;
using StockClient.Core.Groups;
using StockClient.Core.Quotes;

namespace StockClient.App.Views;

/// <summary>
/// The stealth ticker: one transparent, always-on-top line showing the active
/// group's first contract while the main window sits in the background.
/// </summary>
public partial class StealthWindow : Window
{
    private const int HotkeyBrighter = 0xB001;
    private const int HotkeyDarker = 0xB002;
    private const int HotkeyNext = 0xB003;
    private const int HotkeyPrev = 0xB004;
    private const int HotkeyNextGroup = 0xB005;
    private const int HotkeyPrevGroup = 0xB006;
    private const int HotkeyCycleChart = 0xB007;

    private const uint ModAlt = 0x0001;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// How often a held-down hotkey is allowed to fire. Auto-repeat is left ON
    /// (no MOD_NOREPEAT) so holding a key keeps acting, but WM_HOTKEY then streams
    /// at the OS keyboard repeat rate — fast enough to run the shade 10→0 in a
    /// blink. This throttles it to a controllable cadence: ~1.3s to darken fully,
    /// ~7 contracts/sec when scrolling through a group. A single tap still fires
    /// once immediately.
    /// </summary>
    private const long RepeatThrottleMs = 130;

    /// <summary>
    /// Win+Alt — not plain Ctrl, not Ctrl+Alt.
    ///
    /// RegisterHotKey is system-wide and takes the key away from every other app,
    /// so the combination has to be one nothing else wants. A ticker's hotkeys
    /// have no business outranking the shortcuts people use all day.
    ///
    /// Ruled out, each for its own reason:
    ///   plain Ctrl+arrows — word-jumping in an editor and Ctrl+Down in Excel are
    ///     constant, ordinary navigation. They were silently swallowed and dimmed
    ///     this panel instead: ten presses and it was invisible, looking for all
    ///     the world like the app had died.
    ///   Ctrl+Alt+arrows — Intel/AMD display drivers ship screen rotation on this
    ///     combination, and a driver-level hook outranks RegisterHotKey.
    ///   anything +Shift — Alt+Shift is the input-language switcher, so on any
    ///     machine with two IMEs it registers fine and then never fires.
    ///
    /// Win+Alt is what's left. Windows itself claims Win+arrows (snap),
    /// Win+Ctrl+arrows (virtual desktops) and Win+Shift+arrows (move to next
    /// monitor) — but not Win+Alt. Verified on corp-win by pressing each one and
    /// checking WM_HOTKEY actually arrives, because registering successfully and
    /// never firing is exactly the failure this comment is here to prevent.
    /// </summary>
    // Auto-repeat left ON (no MOD_NOREPEAT) so holding a key keeps firing; the
    // cadence is tamed in WndProc by RepeatThrottleMs.
    private const uint ModPanel = ModWin | ModAlt;

    /// <summary>Win+Alt with MOD_NOREPEAT (0x4000): for a toggle, holding must fire once.</summary>
    private const uint ModPanelNoRepeat = ModWin | ModAlt | 0x4000;

    private const uint VkUp = 0x26;
    private const uint VkDown = 0x28;
    private const uint VkLeft = 0x25;
    private const uint VkRight = 0x27;

    /// <summary>
    /// PageUp/PageDown for groups, rather than adding Shift to the arrows.
    ///
    /// Alt+Shift is Windows' switch-input-language shortcut. On a machine with
    /// more than one input method installed — any Chinese desktop — the language
    /// switcher eats any Alt+Shift+arrow before RegisterHotKey ever sees it, so
    /// the group hotkeys registered successfully and then silently never fired.
    /// Verified: they do fire on a single-layout machine and don't on corp-win.
    /// </summary>
    private const uint VkPageUp = 0x21;
    private const uint VkPageDown = 0x22;
    private const uint VkDelete = 0x2E;

    private const int WmHotkey = 0x0312;

    private const int MaxShade = 10;

    private static readonly int[] AllHotkeys =
    {
        HotkeyBrighter, HotkeyDarker, HotkeyNext, HotkeyPrev, HotkeyNextGroup, HotkeyPrevGroup,
        HotkeyCycleChart,
    };

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("白", "#FFFFFF"), ("红", "#EF5350"), ("绿", "#26A69A"), ("黄", "#FFC107"),
        ("蓝", "#4C8DFF"), ("灰", "#8B93A3"), ("黑", "#000000"),
    };

    private readonly QuotesViewModel _vm;
    private readonly StealthConfig _config;
    private readonly Action _save;
    private readonly TrendRepository _trends;
    private HwndSource? _source;
    private IReadOnlyList<QuoteRow> _rows = Array.Empty<QuoteRow>();

    // Charts of the anchor (current) contract only — one contract at a time, so
    // the request load stays minimal. The order book needs no request at all: it
    // rides along in the 1s quote.
    private PanelSparkline? _sparkline;
    private DepthChart? _depthChart;
    private TrendSeries? _trendSeries;
    private string _trendCode = "";
    private bool _trendBusy;
    private DateTime _lastTrendAttempt = DateTime.MinValue;

    /// <summary>Sparkline (44) + its host margins (1+3): the height the 分时 chart adds.</summary>
    private const double SparkBlockHeight = 48;

    /// <summary>Depth rows (5+5 at 13px, +1 split) + host margins: the height the 五档 chart adds.</summary>
    private const double DepthBlockHeight = 135;

    private const double DepthRowHeight = 13;

    /// <summary>The chart already reflected in the window's Top, to detect a change.</summary>
    private PanelChart _appliedChart;

    // Last hotkey fired and when, so a held key's repeat stream can be throttled.
    private int _lastHotkey;
    private long _lastHotkeyAt;


    /// <summary>
    /// Heartbeat state. The panel is reported as vanishing "after a few minutes"
    /// with nobody touching it, and the four ways that can happen — Close(),
    /// Visibility, Opacity=0, and losing topmost — are indistinguishable from
    /// outside the process. This samples all four.
    ///
    /// Logged only when the snapshot changes, plus once a minute regardless so a
    /// silent log means "stopped running" rather than "nothing to say". Rendering
    /// ticks once a second; writing that every time buried the signal in 3,600
    /// identical lines an hour and still never recorded the opacity.
    /// </summary>
    private DispatcherTimer? _heartbeat;

    private string _lastSnapshot = "";
    private DateTime _lastSnapshotAt = DateTime.MinValue;

    /// <summary>
    /// Watches for the panel being buried.
    ///
    /// Polled, and not for lack of trying: Windows has no notification for "another
    /// window was inserted above you". Measured on corp-win, all three candidates
    /// stay silent while the taskbar takes the front — EVENT_OBJECT_REORDER (it
    /// reports child reorders inside a container, not top-level Z-order),
    /// EVENT_SYSTEM_FOREGROUND (the foreground window doesn't change), and
    /// WM_WINDOWPOSCHANGED (only the window being explicitly repositioned is told;
    /// the one that just lost its place is not).
    ///
    /// So the trigger has to be a poll, but the *decision* is not: EnsureOnTop
    /// looks first and acts only when actually covered, instead of re-asserting
    /// topmost on a schedule and permanently wrestling the shell. 500ms is free —
    /// IsCovered walks only the windows in front of us, which is none at all in
    /// the normal case.
    /// </summary>
    private DispatcherTimer? _topmostWatch;

    private DateTime _lastRaise = DateTime.MinValue;
    private int _raises;

    /// <summary>True when the combinations are already taken by another process.</summary>
    public bool HotkeysFailed { get; private set; }

    public event Action? RestoreRequested;

    /// <summary>Raised when the panel's "设置…" is chosen; the host opens the (single) settings window.</summary>
    public event Action? SettingsRequested;

    public StealthWindow(QuotesViewModel vm, StealthConfig config, Action save, TrendRepository trends)
    {
        InitializeComponent();

        _vm = vm;
        _config = config;
        _save = save;
        _trends = trends;
        _appliedChart = config.Chart; // initial state is placed as-is; only changes shift Top
        _vm.StealthTick += OnTick;

        Root.ContextMenu = BuildMenu();

        LocationChanged += (_, _) =>
        {
            if (!IsLoaded) return;

            _config.Left = Left;
            _config.Top = Top;
            _save();
        };

        // Every state change that can hide the panel, recorded at the moment it
        // happens rather than inferred afterwards from a corpse.
        IsVisibleChanged += (_, e) => Snapshot($"IsVisibleChanged -> {e.NewValue}");
        StateChanged += (_, _) => Snapshot($"StateChanged -> {WindowState}");

        Loaded += (_, _) =>
        {
            HookHotkeys();
            _rows = _vm.StealthRows();
            Render();

            _heartbeat = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _heartbeat.Tick += (_, _) => Snapshot("heartbeat");
            _heartbeat.Start();

            _topmostWatch = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _topmostWatch.Tick += (_, _) => EnsureOnTop("watch");
            _topmostWatch.Start();

            Snapshot("panel loaded");

            // Reported in the menu, not a tooltip: a tooltip over this panel
            // covers the quotes it is meant to annotate.
            if (HotkeysFailed && Root.ContextMenu is { } menu)
            {
                menu.Items.Insert(0, new MenuItem
                {
                    Header = "⚠ 快捷键被其它程序占用，已停用",
                    IsEnabled = false,
                });
                menu.Items.Insert(1, new Separator());
            }
        };
    }

    private void OnTick(IReadOnlyList<QuoteRow> rows) => Dispatcher.InvokeAsync(() =>
    {
        _rows = rows;
        Render();
    });

    /// <summary>
    /// Puts the panel back in front the moment anything gets over it.
    ///
    /// WS_EX_TOPMOST only orders you above *non*-topmost windows. Inside the
    /// topmost band it is ordinary Z-order — and the taskbar is in that band too,
    /// re-asserting itself whenever the shell feels like it. Measured on corp-win:
    /// the panel is z#1 when shown and found at z#2, behind Shell_SecondaryTrayWnd,
    /// minutes later. Every property still reads healthy — IsVisible, the rect,
    /// WS_EX_TOPMOST, the opacity — because none of them describe the band's
    /// internal order. It is simply painted over. That is the whole bug.
    ///
    /// Event-driven rather than polled, deliberately: a timer leaves the ticker
    /// invisible for up to its own interval, which is the symptom, not the fix.
    /// The hooks are the shell's own reorder/foreground events, so the correction
    /// lands in the same beat as whatever pushed us down.
    ///
    /// SetWindowPos(HWND_TOPMOST) alone is not enough: on a window that is already
    /// topmost it is a no-op for Z-order. Dropping to NOTOPMOST first and going
    /// back up is what actually re-inserts us at the front — verified by pixel,
    /// not by WindowFromPoint, which lies here because the panel's near-zero alpha
    /// makes it hit-test transparent.
    /// </summary>
    private void EnsureOnTop(string why)
    {
        var me = _source?.Handle ?? IntPtr.Zero;
        if (me == IntPtr.Zero || !IsLoaded) return;
        if (!IsCovered(me, out var by)) return;

        // Throttle: the shell can emit reorder events in bursts, and each of our
        // own SetWindowPos calls provokes more of them.
        var now = DateTime.Now;
        if ((now - _lastRaise) < TimeSpan.FromMilliseconds(200)) return;
        _lastRaise = now;

        var flags = Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate;
        Native.SetWindowPos(me, Native.HwndNoTopmost, 0, 0, 0, 0, flags);
        Native.SetWindowPos(me, Native.HwndTopmost, 0, 0, 0, 0, flags);

        _raises++;
        Probe.Log($"[RAISE ] {why,-28} was covered by {by} -> re-asserted topmost (raise #{_raises})");
    }

    /// <summary>
    /// True when a visible window from another process sits above us and overlaps
    /// our rect. Walks only the windows in front of us, so it costs nothing while
    /// we are already on top — which is the normal case.
    ///
    /// Our own windows are skipped on purpose: the context menu is supposed to be
    /// over the panel, and treating it as an intruder would make right-clicking
    /// fight itself.
    /// </summary>
    private bool IsCovered(IntPtr me, out string by)
    {
        by = "";
        if (!Native.GetWindowRect(me, out var mine)) return false;

        Native.GetWindowThreadProcessId(me, out var self);

        for (var w = Native.GetWindow(me, Native.GwHwndPrev);
             w != IntPtr.Zero;
             w = Native.GetWindow(w, Native.GwHwndPrev))
        {
            if (!Native.IsWindowVisible(w)) continue;

            Native.GetWindowThreadProcessId(w, out var pid);
            if (pid == self) continue;

            if (!Native.GetWindowRect(w, out var other)) continue;
            if (other.Right <= other.Left || other.Bottom <= other.Top) continue;

            var overlaps = other.Left < mine.Right && other.Right > mine.Left &&
                           other.Top < mine.Bottom && other.Bottom > mine.Top;
            if (!overlaps) continue;

            by = $"0x{w:X}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// One line of the log per meaningful change, tagged with what provoked it.
    ///
    /// Opacity is the field this whole exercise turned on: a panel at Opacity=0
    /// is completely invisible, yet IsWindowVisible still reports true, the rect
    /// is still normal, and the process is still healthy — so an outside observer
    /// watching window state sees nothing wrong at all.
    /// </summary>
    private void Snapshot(string why)
    {
        var hwnd = _source?.Handle ?? IntPtr.Zero;

        var snapshot =
            $"shade={_config.Shade} opacity={Opacity:F2} visible={IsVisible} state={WindowState} " +
            $"size={ActualWidth:F0}x{ActualHeight:F0} pos={Left:F0},{Top:F0} " +
            $"children={Rows.Children.Count} rows={_rows.Count} " +
            $"first={(_rows.Count > 0 ? _rows[0].Code : "NULL")} {Native.Describe(hwnd)}";

        var changed = snapshot != _lastSnapshot;
        var stale = (DateTime.Now - _lastSnapshotAt) > TimeSpan.FromSeconds(60);
        if (!changed && !stale) return;

        Probe.Log($"[{(changed ? "CHANGE" : "alive ")}] {why,-28} {snapshot}");
        _lastSnapshot = snapshot;
        _lastSnapshotAt = DateTime.Now;
    }

    /// <summary>Rebuilds the panel: one row per contract, each row the chosen fields in colour.</summary>
    private void Render()
    {
        Rows.Children.Clear();
        ApplyShade();

        if (_rows.Count == 0)
        {
            Rows.Children.Add(RowFor(null));
            return;
        }

        // Row gap: a top margin on every row but the first, so N rows get N-1 gaps.
        var gap = Math.Clamp(_config.RowGap, 0, StealthConfig.MaxRowGap);
        var first = true;
        foreach (var row in _rows)
        {
            var line = RowFor(row);
            if (line is FrameworkElement fe) fe.Margin = new Thickness(0, first ? 0 : gap, 0, 0);
            Rows.Children.Add(line);
            first = false;
        }

        UpdateTrend();
    }

    /// <summary>
    /// Drives whichever chart is switched on above the rows: shows/hides it per
    /// the setting, tracks the anchor (first) contract, and redraws it from the
    /// live 1s quote every tick.
    ///
    /// 分时 additionally kicks a cached (per-day, stale-refetched) trend fetch;
    /// 五档 needs nothing fetched — the book arrives inside the quote already.
    /// </summary>
    private void UpdateTrend()
    {
        // A change must not shove the quote rows down: absorb the block's height
        // into Top so the chart grows UPWARD, above the rows, leaving them where
        // they were. Only fires on an actual change, never per tick or on the
        // initial placement (which is positioned as-is).
        if (_config.Chart != _appliedChart)
        {
            if (IsLoaded) Top += BlockHeight(_appliedChart) - BlockHeight(_config.Chart);
            _appliedChart = _config.Chart;
        }

        if (_config.Chart == PanelChart.None)
        {
            if (SparkHost.Visibility != Visibility.Collapsed) SparkHost.Visibility = Visibility.Collapsed;
            _trendCode = "";
            return;
        }

        // Visibility follows the setting only (not whether a contract is ready), so
        // it stays in lockstep with the Top adjustment above.
        var chart = _config.Chart == PanelChart.Trend ? (UIElement)Sparkline() : Book();
        if (!ReferenceEquals(SparkHost.Child, chart)) SparkHost.Child = chart;
        SparkHost.Visibility = Visibility.Visible;

        var anchor = _rows.FirstOrDefault(r => r is { IsMissing: false });

        if (_config.Chart == PanelChart.Depth)
        {
            Book().Set(
                anchor?.Depth ?? new QuoteDepth(),
                anchor?.Yesterday ?? 0,
                anchor?.PriceDecimals ?? 2);
            return;
        }

        if (anchor is null)
        {
            Sparkline().Set(null, 0); // no contract yet: blank chart, but the space stays
            _trendCode = "";
            return;
        }

        if (anchor.Code != _trendCode)
        {
            _trendCode = anchor.Code;
            _trendSeries = null;
            _lastTrendAttempt = DateTime.MinValue; // fetch the new contract at once
        }

        // Redraw every tick so the tip tracks the live price without a request.
        Sparkline().Set(_trendSeries, anchor.Now);

        // Fetch on a contract change (attempt reset above) or periodically; the repo
        // serves a fresh cache without a network call, so this stays cheap.
        if (!_trendBusy && (DateTime.UtcNow - _lastTrendAttempt).TotalSeconds >= 20)
            _ = FetchTrend(anchor.Code);
    }

    private static double BlockHeight(PanelChart chart) => chart switch
    {
        PanelChart.Trend => SparkBlockHeight,
        PanelChart.Depth => DepthBlockHeight,
        _ => 0,
    };

    private PanelSparkline Sparkline()
    {
        // Fixed width, NOT stretched to the panel: the panel auto-sizes to each
        // contract's text, so a stretched chart came out a different length per
        // contract. A constant width keeps every thumbnail the same span. Taller
        // than the text rows so the day's swing is easy to read.
        return _sparkline ??= new PanelSparkline
        {
            Height = 44,
            Width = 168,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private DepthChart Book()
    {
        // Same fixed width as the sparkline so switching charts doesn't resize the
        // panel. Tighter rows and smaller type than the main window's ladder —
        // ten levels have to fit in a strip that stays unobtrusive.
        return _depthChart ??= new DepthChart
        {
            RowHeight = DepthRowHeight,
            FontSize = 9.5,
            Height = DepthRowHeight * 10 + 1,
            Width = 168,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    /// <summary>
    /// Cycles the chart above the rows: 关 → 分时 → 五档 → 关 (Win+Alt+Delete / menu).
    /// One key for both charts, since they occupy the same strip and a user
    /// wanting one rarely wants the other at the same time.
    /// </summary>
    private void CycleChart()
    {
        _config.Chart = _config.Chart switch
        {
            PanelChart.None => PanelChart.Trend,
            PanelChart.Trend => PanelChart.Depth,
            _ => PanelChart.None,
        };

        _config.ShowTrend = _config.Chart == PanelChart.Trend; // keep the legacy key in step
        _save();
        _rows = _vm.StealthRows();
        Render();
    }

    /// <summary>Jumps straight to one chart (settings window / menu), no cycling.</summary>
    public void SetChart(PanelChart chart)
    {
        if (_config.Chart == chart) return;

        _config.Chart = chart;
        _config.ShowTrend = chart == PanelChart.Trend;
        _save();
        _rows = _vm.StealthRows();
        Render();
    }

    private async Task FetchTrend(string code)
    {
        _trendBusy = true;
        _lastTrendAttempt = DateTime.UtcNow;
        try
        {
            var contract = _vm.FindContract(code);
            if (contract is null) return;

            var series = await _trends.GetAsync(contract, CancellationToken.None);
            if (series is null || code != _trendCode) return; // switched away meanwhile

            _trendSeries = series;
            var live = _rows.FirstOrDefault(r => r.Code == code)?.Now ?? 0;
            _sparkline?.Set(series, live);
        }
        catch
        {
            // Best-effort thumbnail; keep whatever was last drawn.
        }
        finally
        {
            _trendBusy = false;
        }
    }

    /// <summary>One horizontal line of field TextBlocks for a contract (or "无行情").</summary>
    private UIElement RowFor(QuoteRow? row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (row is null || row.IsMissing)
        {
            line.Children.Add(new TextBlock { Text = "无行情", FontSize = 12, Foreground = Brushes.Gray });
            return line;
        }

        var first = true;
        foreach (var f in _config.Fields.Where(f => f.Visible))
        {
            var text = Value(f.Field, row);
            if (text.Length == 0) continue;

            line.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = f.Field == StealthField.Name ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = f.Field is StealthField.Code or StealthField.Name
                    ? new FontFamily("Microsoft YaHei")
                    : new FontFamily("Consolas"),
                Foreground = Brush(ColorFor(f, row)),
                Margin = new Thickness(first ? 0 : 7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            first = false;
        }

        return line;
    }

    /// <summary>
    /// A signed field takes its up/down colour from its own move; everything else
    /// takes its single colour.
    /// </summary>
    private static string ColorFor(StealthFieldConfig f, QuoteRow row)
    {
        if (!StealthFields.IsSigned(f.Field)) return f.Color;
        return Sign(f.Field, row) >= 0 ? f.PositiveColor : f.NegativeColor;
    }

    /// <summary>The value whose sign decides a field's rise/fall colour.</summary>
    private static double Sign(StealthField field, QuoteRow r) => field switch
    {
        StealthField.Percent => r.Percent,
        StealthField.Change => r.Change,
        // Prices colour by their distance from yesterday's close (a green open on a
        // red day, etc.); fall back to the day's move when there is no baseline.
        StealthField.Price => r.Yesterday > 0 ? r.Now - r.Yesterday : r.Change,
        StealthField.Open => r.Yesterday > 0 ? r.Open - r.Yesterday : r.Percent,
        StealthField.High => r.Yesterday > 0 ? r.High - r.Yesterday : r.Percent,
        StealthField.Low => r.Yesterday > 0 ? r.Low - r.Yesterday : r.Percent,
        _ => 0,
    };

    private static string Value(StealthField field, QuoteRow r) => field switch
    {
        StealthField.Code => r.Code,
        StealthField.Name => r.Name,
        StealthField.Price => Price(r.Now),
        StealthField.Change => r.Change.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture),
        StealthField.Percent => r.Percent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%",
        StealthField.Open => Price(r.Open),
        StealthField.High => Price(r.High),
        StealthField.Low => Price(r.Low),
        StealthField.Yesterday => Price(r.Yesterday),
        StealthField.Time => r.Time,
        StealthField.Volume => Scale(r.Volume),
        StealthField.Amount => Scale(r.Amount),
        StealthField.TotalCap => Scale(r.TotalCap),
        StealthField.FloatCap => Scale(r.FloatCap),
        StealthField.TurnoverRate => Pct(r.TurnoverRate),
        StealthField.VolumeRatio => Plain(r.VolumeRatio),
        StealthField.Amplitude => Pct(r.Amplitude),
        StealthField.AvgPrice => r.AvgPrice is { } a ? Price(a) : "",
        StealthField.PeTtm => Plain(r.PeTtm),
        StealthField.Pb => Plain(r.Pb),
        _ => "",
    };

    /// <summary>进位显示 (311.01万 / 1.66万亿); empty when the market didn't report it.</summary>
    private static string Scale(double? value)
    {
        if (value is not { } v || v == 0) return "";
        var sign = v < 0 ? "-" : "";
        var a = Math.Abs(v);
        return a >= 1e12 ? sign + (a / 1e12).ToString("0.00", CultureInfo.InvariantCulture) + "万亿"
            : a >= 1e8 ? sign + (a / 1e8).ToString("0.00", CultureInfo.InvariantCulture) + "亿"
            : a >= 1e4 ? sign + (a / 1e4).ToString("0.00", CultureInfo.InvariantCulture) + "万"
            : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Pct(double? v) =>
        v is { } x && x != 0 ? x.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "";

    private static string Plain(double? v) =>
        v is { } x && x != 0 ? x.ToString("0.00", CultureInfo.InvariantCulture) : "";

    /// <summary>Korean prices run to six figures; cheap ETFs need three decimals.</summary>
    private static string Price(double v) =>
        v == 0 ? "--"
        : v >= 10000 ? v.ToString("N0", CultureInfo.InvariantCulture)
        : v.ToString(v < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    private static Brush Brush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return Brushes.White;
        }
    }

    /// <summary>
    /// Shade drives window opacity, 0 meaning fully invisible.
    ///
    /// It has to reach a true 0: colours don't fade at the same rate. Relative
    /// luminance of white is 255, but of the default red (#EF5350) only ~116, so
    /// against a dark desktop the red text sinks out of sight while white is
    /// still legible. Any floor above zero would leave the brighter fields
    /// showing. The tray icon is what makes a fully invisible panel safe.
    /// </summary>
    private void ApplyShade()
    {
        var t = Math.Clamp(_config.Shade, 0, MaxShade) / (double)MaxShade;
        Opacity = t;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // Fields, colours, row count and shade now live in a dedicated settings
        // window; the menu just opens it and keeps the quick navigation actions.
        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settings);

        // One item per chart rather than the old on/off, so the state is visible
        // (the hotkey cycles blind) and either chart is one click away.
        var chart = new MenuItem { Header = "面板图表", InputGestureText = "Win+Alt+Delete" };
        foreach (var (kind, label) in new[]
                 {
                     (PanelChart.None, "关闭"),
                     (PanelChart.Trend, "分时缩略图"),
                     (PanelChart.Depth, "五档盘口"),
                 })
        {
            var captured = kind;
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = _config.Chart == kind,
            };
            item.Click += (_, _) => SetChart(captured);
            chart.Items.Add(item);
        }
        menu.Items.Add(chart);

        menu.Items.Add(new Separator());

        var next = new MenuItem { Header = "下一个合约", InputGestureText = "Win+Alt+→" };
        next.Click += (_, _) => _vm.StealthStep(1);
        menu.Items.Add(next);

        var prev = new MenuItem { Header = "上一个合约", InputGestureText = "Win+Alt+←" };
        prev.Click += (_, _) => _vm.StealthStep(-1);
        menu.Items.Add(prev);

        var nextGroup = new MenuItem { Header = "下一个分组", InputGestureText = "Win+Alt+PageDown" };
        nextGroup.Click += (_, _) => _vm.StealthStepGroup(1);
        menu.Items.Add(nextGroup);

        var prevGroup = new MenuItem { Header = "上一个分组", InputGestureText = "Win+Alt+PageUp" };
        prevGroup.Click += (_, _) => _vm.StealthStepGroup(-1);
        menu.Items.Add(prevGroup);

        menu.Items.Add(new Separator());

        var restore = new MenuItem { Header = "还原主窗口" };
        restore.Click += (_, _) => RestoreRequested?.Invoke();
        menu.Items.Add(restore);

        return menu;
    }

    /// <summary>
    /// Re-reads the config after a settings change: row count, fields, colour,
    /// shade. Called by the host when the (shared) settings window applies a change.
    /// </summary>
    public void ApplySettings()
    {
        _rows = _vm.StealthRows();   // row count may have changed
        Render();                    // fields/colour + ApplyShade
    }

    private static string Label(StealthField f) => f switch
    {
        StealthField.Code => "合约编码",
        StealthField.Name => "合约名称",
        StealthField.Price => "最新价",
        StealthField.Change => "涨跌额",
        StealthField.Percent => "涨跌幅",
        StealthField.Open => "今开",
        StealthField.High => "最高",
        StealthField.Low => "最低",
        StealthField.Yesterday => "昨收",
        StealthField.Time => "时间",
        StealthField.Volume => "成交量",
        StealthField.Amount => "成交额",
        StealthField.TotalCap => "总市值",
        StealthField.FloatCap => "流通市值",
        StealthField.TurnoverRate => "换手率",
        StealthField.VolumeRatio => "量比",
        StealthField.Amplitude => "振幅",
        StealthField.AvgPrice => "均价",
        StealthField.PeTtm => "市盈TTM",
        StealthField.Pb => "市净率",
        _ => f.ToString(),
    };

    private void HookHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        // Global, not window-level: the point is not having to touch the panel —
        // clicking it to give it focus would defeat the hiding.
        //
        // The result is checked: RegisterHotKey fails when another process
        // already owns the combination, and swallowing that leaves the arrows
        // silently dead with nothing on screen to explain why.
        Probe.Log($"HookHotkeys hwnd=0x{handle:X} source={(_source is null ? "NULL" : "ok")}");

        var ok = Register(handle, HotkeyBrighter, ModPanel, VkUp, "Win+Alt+Up")
                 & Register(handle, HotkeyDarker, ModPanel, VkDown, "Win+Alt+Down")
                 & Register(handle, HotkeyNext, ModPanel, VkRight, "Win+Alt+Right")
                 & Register(handle, HotkeyPrev, ModPanel, VkLeft, "Win+Alt+Left")
                 & Register(handle, HotkeyNextGroup, ModPanel, VkPageDown, "Win+Alt+PageDown")
                 & Register(handle, HotkeyPrevGroup, ModPanel, VkPageUp, "Win+Alt+PageUp")
                 & Register(handle, HotkeyCycleChart, ModPanelNoRepeat, VkDelete, "Win+Alt+Delete");

        if (ok) return;

        Probe.Log("HookHotkeys FAILED -> rolling back all six");
        foreach (var id in AllHotkeys) Native.UnregisterHotKey(handle, id);
        HotkeysFailed = true;
    }

    private static bool Register(IntPtr handle, int id, uint mods, uint vk, string label)
    {
        var ok = Native.RegisterHotKey(handle, id, mods, vk);
        var err = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        Probe.Log($"  RegisterHotKey {label,-22} mods=0x{mods:X4} -> {(ok ? "ok" : $"FAIL err={err}")}");
        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        var id = wParam.ToInt32();

        // Throttle a held key's repeat stream to a controllable cadence, so
        // holding still fires but doesn't run the shade to zero in a blink. A tap,
        // or a switch to a different key, always fires immediately.
        var now = Environment.TickCount64;
        if (id == _lastHotkey && now - _lastHotkeyAt < RepeatThrottleMs)
        {
            handled = true;
            return IntPtr.Zero;
        }

        _lastHotkey = id;
        _lastHotkeyAt = now;

        Probe.Log($"WM_HOTKEY id=0x{id:X4}");

        switch (id)
        {
            // Up brightens, down darkens — the way the arrows read.
            case HotkeyBrighter:
                _config.Shade = Math.Min(MaxShade, _config.Shade + 1);
                ApplyShade();
                _save();
                Snapshot("hotkey brighter");
                break;

            case HotkeyDarker:
                _config.Shade = Math.Max(0, _config.Shade - 1);
                ApplyShade();
                _save();
                // Shade 0 is a fully invisible panel and a legal state. If the
                // ticker is "gone" and this line says shade=0, the answer is
                // Win+Alt+Up, not a bug hunt.
                Snapshot(_config.Shade == 0 ? "hotkey darker -> INVISIBLE" : "hotkey darker");
                break;

            case HotkeyNext:
                _vm.StealthStep(1);
                break;

            case HotkeyPrev:
                _vm.StealthStep(-1);
                break;

            case HotkeyNextGroup:
                _vm.StealthStepGroup(1);
                break;

            case HotkeyPrevGroup:
                _vm.StealthStepGroup(-1);
                break;

            case HotkeyCycleChart:
                CycleChart();
                Snapshot($"hotkey cycle chart -> {_config.Chart}");
                break;

            default:
                return IntPtr.Zero;
        }

        handled = true;
        return IntPtr.Zero;
    }

    private void Root_Drag(object sender, MouseButtonEventArgs e)
    {
        Probe.Log($"Root_Drag clicks={e.ClickCount} before: size={ActualWidth:F0}x{ActualHeight:F0} " +
                  $"pos={Left:F0},{Top:F0} opacity={Opacity:F2} children={Rows.Children.Count}");

        if (e.ClickCount == 1) DragMove();

        Probe.Log($"Root_Drag after:  size={ActualWidth:F0}x{ActualHeight:F0} " +
                  $"pos={Left:F0},{Top:F0} opacity={Opacity:F2} children={Rows.Children.Count}");
    }

    protected override void OnClosed(EventArgs e)
    {
        _heartbeat?.Stop();
        _topmostWatch?.Stop();

        var handle = new WindowInteropHelper(this).Handle;
        Probe.Log($"OnClosed hwnd=0x{handle:X} <<< PANEL CLOSED — the caller is in the RestoreFromStealth line above");
        foreach (var id in AllHotkeys)
        {
            var ok = Native.UnregisterHotKey(handle, id);
            Probe.Log($"  UnregisterHotKey 0x{id:X4} -> {(ok ? "ok" : $"FAIL err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}")}");
        }

        _source?.RemoveHook(WndProc);
        _vm.StealthTick -= OnTick;

        base.OnClosed(e);
    }
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    public const int GwlExStyle = -20;
    public const int WsExTopmost = 0x8;

    /// <summary>Previous in Z-order means nearer the front.</summary>
    public const uint GwHwndPrev = 3;

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndNoTopmost = new(-2);

    public const uint SwpNoSize = 0x1;
    public const uint SwpNoMove = 0x2;
    public const uint SwpNoActivate = 0x10;


    /// <summary>
    /// What Windows thinks, not what WPF thinks.
    ///
    /// A panel that is Opacity=0, or that quietly lost WS_EX_TOPMOST and slid
    /// behind Chrome, is invisible to the user while every WPF property still
    /// reads perfectly healthy. Both have to be sampled from the OS side.
    /// </summary>
    public static string Describe(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "hwnd=0";

        var ex = GetWindowLong(hWnd, GwlExStyle);
        return $"hwnd=0x{hWnd:X} win32visible={IsWindowVisible(hWnd)} " +
               $"topmost={((ex & WsExTopmost) != 0)}";
    }
}
