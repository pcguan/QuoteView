using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace StockClient.App;

public partial class App : Application
{
    private const string ShowSignalName = @"Local\StockClient.Show";

    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A background poll failure should surface in the UI, not kill the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Single instance. Two copies meant two stealth panels stacked on the
        // desktop (reading as one doubled line), and the second copy's
        // RegisterHotKey silently lost to the first — so Ctrl+arrows appeared
        // dead on whichever panel was on top.
        //
        // An update relaunch (--updated) is the one case that must WAIT for the
        // mutex instead of yielding: the updater starts the new copy and only
        // then shuts the old one down, so for a moment both are alive — and the
        // freshly installed version used to judge itself "a second instance" and
        // exit, which read as "update never restarted".
        _single = new Mutex(false, @"Local\StockClient.SingleInstance");
        var wait = e.Args.Contains("--updated") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
        bool isFirst;
        try
        {
            isFirst = _single.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing (normal for a process
            // that just shut down); ownership has passed to us.
            isFirst = true;
        }

        if (!isFirst)
        {
            // Don't just vanish. Silently exiting meant clicking the icon looked
            // like nothing happened while a copy sat in the background — and if
            // the stealth panel was dimmed right down, there was no way back to
            // it at all. Tell the running copy to surface instead.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other copy is mid-shutdown; nothing to surface.
            }

            Shutdown();
            return;
        }

        // Listen for later launches asking us to come back to the front.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => Dispatcher.Invoke(SurfaceMainWindow),
            null,
            Timeout.Infinite,
            false);

        var probe = Array.IndexOf(e.Args, "--uiprobe");
        Probe.Enable(probe >= 0 && probe + 1 < e.Args.Length ? e.Args[probe + 1] : Probe.DefaultPath);
        HookDiagnostics();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Records the things that can make the panel vanish without anyone touching it.
    ///
    /// Each of these is a way the ticker disappears while looking, from the
    /// outside, exactly like the app died:
    ///   - a background-thread crash kills the process, panel and all;
    ///   - an unobserved Task exception does the same on some configurations;
    ///   - the corporate lock screen and a resolution/monitor change both tear
    ///     down and rebuild a topmost layered window's rendering;
    ///   - resume-from-sleep does the same.
    /// None of them left a trace before, so "it was gone when I came back" was
    /// unfalsifiable.
    /// </summary>
    private void HookDiagnostics()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Probe.Log($"!!! UNHANDLED {(args.IsTerminating ? "TERMINATING" : "non-fatal")}: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Probe.Log($"!!! UNOBSERVED TASK: {args.Exception}");
            args.SetObserved();
        };

        Microsoft.Win32.SystemEvents.SessionSwitch += (_, args) =>
            Probe.Log($"SystemEvents.SessionSwitch {args.Reason}");

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Probe.Log($"SystemEvents.DisplaySettingsChanged  virtual={SystemParameters.VirtualScreenWidth:F0}x" +
                      $"{SystemParameters.VirtualScreenHeight:F0} work={SystemParameters.WorkArea}");

        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, args) =>
            Probe.Log($"SystemEvents.PowerModeChanged {args.Mode}");

        Exit += (_, _) => Probe.Log("=== Application.Exit ===");
    }

    /// <summary>Brings the app back when someone launches it a second time.</summary>
    private void SurfaceMainWindow()
    {
        if (MainWindow is not MainWindow main) return;

        // A second launch closes the stealth panel. If someone double-clicks the
        // desktop icon while the panel is up — easy to do when it's dimmed and
        // they think the app isn't running — that reads as the panel vanishing.
        Probe.Log("SurfaceMainWindow: second instance signalled us to come back");
        main.LeaveStealth("second instance launched");
        main.ShowInTaskbar = true;
        main.WindowState = WindowState.Normal;
        main.Show();
        main.Activate();
        main.Topmost = true;
        main.Topmost = false;
    }

    private void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Probe.Log($"!!! DISPATCHER UNHANDLED: {e.Exception}");

        // Wpf.Ui's dialog, not the Win32 MessageBox — same reason as everywhere else.
        _ = new Wpf.Ui.Controls.MessageBox
        {
            Title = "发生错误",
            Content = e.Exception.ToString(),
            CloseButtonText = "关闭",
        }.ShowDialogAsync();

        e.Handled = true;
    }
}
