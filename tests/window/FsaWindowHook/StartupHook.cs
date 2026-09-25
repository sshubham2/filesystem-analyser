using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

/// <summary>
/// The window cases' driver (SL-033): presses what a person would press in the published app, from inside
/// its process, with no UI Automation.
/// </summary>
/// <remarks>
/// <code>
///   DOTNET_STARTUP_HOOKS=&lt;absolute path to FsaWindowHook.dll&gt;  FSA_HOOK_MODE=stop|throw|close|rescan|expand  FSA_HOOK_LOG=&lt;file&gt;
///   [FSA_HOOK_ARMED=&lt;marker file&gt;  FSA_HOOK_CANCEL_DELAY_MS=&lt;n&gt;  FSA_HOOK_CLOSE_DELAY_MS=&lt;n&gt;]
///   [FSA_HOOK_MISSING=&lt;the root folder, under %TEMP%&gt;]  (rescan only)
/// </code>
/// The runtime calls <see cref="Initialize"/> before the app's Main, in every .NET process that inherits the
/// variable: pwsh 7, dotnet.exe and MSBuild nodes too. So it does nothing unless FSA_HOOK_MODE is set and the
/// process is FsAnalyser.App.exe, and nothing it does can end the host.
///
/// Without FSA_HOOK_ARMED every mode waits for the same thing: the window's CancelButton pressable
/// (<see cref="Pressable"/>), which is only while a scan is running. It reads that and acts in one dispatcher
/// callback, so the state it read is the state it acted on. stop raises the button's Click; close closes the
/// main window; throw posts an exception to the UI thread. If nothing is pressable within 20 s, or the mode is
/// unknown, it logs "refused" and does nothing. The hook never ends the process or picks its exit code: every
/// exit code a case reads is the app's own.
///
/// With FSA_HOOK_ARMED (SL-038, the stalled-share cases) it acts on the stall rig's clock instead: it waits
/// up to 60 s for the marker file the rig writes when its server stops answering, then presses Cancel
/// FSA_HOOK_CANCEL_DELAY_MS after it and closes the window FSA_HOOK_CLOSE_DELAY_MS after it. An unset delay
/// is an action not taken, so close mode without a cancel delay closes with no prior Cancel. stop needs a
/// cancel delay, close a close delay, and throw is not armed. Cancel is pressed only if pressable; the window
/// is closed only while its scan is still running (ScanningPanel on screen).
///
/// rescan (SL-040) starts a second scan once the first has shown its list of largest files, and makes the
/// report read the window at that second point instead of at the first ending. It watches LargestFilesPanel
/// from the window's first Loaded; when the first ending shows it, it waits for the list's rows to be laid
/// out and then, with no scan running and Choose folder pressable, raises the window's Loaded event again.
/// The window's own handler (OnLoaded) starts a scan of the root the app was started on: StartScan, the
/// entry a drive button or Choose folder reaches too. It is the one way in that names a root without the
/// folder dialog, which only UI Automation could fill in. All of this runs on the UI thread ahead of the
/// report, which reads the window at Loaded priority after the first ending, so the report reads the second
/// scan as started. With FSA_HOOK_MISSING the root is first moved aside (to &lt;root&gt;.moved, and only if it
/// is under %TEMP%), so the second scan fails before any entry, and the hook keeps one Render-priority item
/// queued until that scan's ending is on screen: the report, at Loaded, cannot run meanwhile, so it reads
/// the failure's ending. It changes nothing the app shows, and rescan is not armed.
///
/// expand (SL-040, O-024) watches the same way and, once the first ending shows the list of largest files
/// beside a closed, pressable list of unreadable folders, opens that list as a click on its header would
/// (IsExpanded, which the header's toggle sets). The layout that follows runs ahead of the report, so the
/// report reads the window with the list open. It is not armed and takes no FSA_HOOK_MISSING.
///
/// FSA_HOOK_LOG gets one ASCII line per event: "loaded mode=m pid=n", "waiting found=b enabled=b visible=b"
/// when that changes, "armed ms=n" when the marker appeared, "acted mode=m enabled=True visible=True ms=n"
/// (ms since load; armed runs add "sincearmed=n", and an armed close logs "scanning=True detail=text" and
/// "utc=time" instead of the button state), "closed ms=n utc=time" when an armed close has closed the window,
/// "rethrown mode=m reason=text" when the app's own handler threw during the press (the exception is raised
/// again on the dispatcher for the app to handle, as a real press's would be), and "refused mode=m
/// reason=text" when the hook itself did not or could not act.
///
/// After any Cancel it presses, it logs what the Stopping state shows (<see cref="StartStoppingLog"/>):
/// "stopping ms=n title=text detail=text" every 250 ms from the UI thread (ms since the press; detail is "-"
/// while StoppingDetail is collapsed or empty), "returned ms=n title=text detail=text" at the moment the scan
/// returns, and one last "stopping ... final=True" line as the app shuts down.
///
/// rescan logs "watching ms=n" when it attached to the window, "listed rows=n built=n STATE ms=n" when the
/// first list was on screen (built: rows laid out), "moved from=path to=path" for FSA_HOOK_MISSING, "acted
/// mode=rescan rows=n ms=n" as it raises Loaded, "started STATE" straight after it, and "ended STATE ms=n"
/// once a missing root's scan has ended. STATE is "scanning=b outcome=b largest=b rows=n": whether
/// ScanningPanel, OutcomePanel and LargestFilesPanel are set Visible, and the rows LargestFilesList holds.
/// expand logs "watching" and "listed" the same way, then "acted mode=expand rows=n unreadable=n ms=n" and
/// "expanded=b STATE" straight after it opened the list.
/// </remarks>
internal class StartupHook
{
    private const string TargetExe = "FsAnalyser.App.exe";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MarkerDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan StoppingEvery = TimeSpan.FromMilliseconds(250);

    private static readonly Stopwatch Clock = new();
    private static readonly object LogGate = new();
    private static string? _logPath;

    // The stopping log's state: written and read on the UI thread only.
    private static Stopwatch? _sinceCancel;
    private static DispatcherTimer? _stoppingTimer;
    private static bool _returned;

    // rescan's and expand's state: written and read on the UI thread only.
    private static bool _watching;
    private static bool _listed;

    public static void Initialize()
    {
        try
        {
            string? mode = Environment.GetEnvironmentVariable("FSA_HOOK_MODE");
            if (string.IsNullOrEmpty(mode) ||
                !string.Equals(Path.GetFileName(Environment.ProcessPath), TargetExe, StringComparison.OrdinalIgnoreCase))
                return;
            _logPath = Environment.GetEnvironmentVariable("FSA_HOOK_LOG");
            Clock.Start();
            new Thread(() => Run(mode)) { IsBackground = true, Name = "FsaWindowHook" }.Start();
        }
        catch (Exception)
        {
            // An exception out of Initialize would end the host before its Main.
        }
    }

    /// <summary>
    /// Whether a person could press <paramref name="element"/> now: enabled, and on screen with every
    /// ancestor visible. A raw RaiseEvent or handler call goes around both (spike SL-033
    /// in-process-click-respects-disabled-and-hidden), so every action checks this first.
    /// </summary>
    internal static bool Pressable(FrameworkElement element) => element.IsEnabled && element.IsVisible;

    private static void Run(string mode)
    {
        try
        {
            Log($"loaded mode={mode} pid={Environment.ProcessId}");
            if (mode is not ("stop" or "throw" or "close" or "rescan" or "expand"))
            {
                Log($"refused mode={mode} reason=unknown mode");
                return;
            }
            string? marker = Environment.GetEnvironmentVariable("FSA_HOOK_ARMED");
            if (mode is "rescan" or "expand")
            {
                string? missing = Environment.GetEnvironmentVariable("FSA_HOOK_MISSING") is { Length: > 0 } folder ? folder : null;
                if (!string.IsNullOrEmpty(marker))
                    Log($"refused mode={mode} reason={mode} mode is not armed");
                else if (mode == "expand" && missing is not null)
                    Log("refused mode=expand reason=FSA_HOOK_MISSING is for rescan only");
                else
                    DriveAfterList(mode, missing);
                return;
            }
            if (string.IsNullOrEmpty(marker))
            {
                Drive(mode);
                return;
            }
            if (!Delay("FSA_HOOK_CANCEL_DELAY_MS", out int? cancelDelay) || !Delay("FSA_HOOK_CLOSE_DELAY_MS", out int? closeDelay))
            {
                Log($"refused mode={mode} reason=a delay is not a whole number of milliseconds");
                return;
            }
            string? wrong = mode switch
            {
                "throw" => "throw mode is not armed",
                "stop" when cancelDelay is null => "armed stop needs FSA_HOOK_CANCEL_DELAY_MS",
                "stop" when closeDelay is not null => "armed stop does not close; use close mode",
                "close" when closeDelay is null => "armed close needs FSA_HOOK_CLOSE_DELAY_MS",
                "close" when cancelDelay >= closeDelay => "the cancel delay must come before the close delay",
                _ => null,
            };
            if (wrong is not null)
            {
                Log($"refused mode={mode} reason={wrong}");
                return;
            }
            DriveArmed(mode, marker, cancelDelay, closeDelay);
        }
        catch (Exception e)
        {
            Log($"refused mode={mode} reason={e.GetType().Name}: {e.Message}");
        }
    }

    // Every WPF type is behind this call, so a host without WPF fails here, inside Run's catch, and not
    // while the runtime compiles Initialize.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drive(string mode)
    {
        if (WaitForApplication(mode) is not { } app)
            return;

        string seen = "found=False enabled=False visible=False";
        bool logged = false;
        while (Clock.Elapsed < Deadline)
        {
            bool acted = false;
            string state = seen;
            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    var button = app.MainWindow?.FindName("CancelButton") as FrameworkElement;
                    state = button is null
                        ? "found=False enabled=False visible=False"
                        : $"found=True enabled={button.IsEnabled} visible={button.IsVisible}";
                    if (button is null || !Pressable(button))
                        return;
                    Log($"acted mode={mode} enabled={button.IsEnabled} visible={button.IsVisible} ms={Clock.ElapsedMilliseconds}");
                    Pressing(app, mode, () => Act(mode, app, button));
                    acted = true;
                }, DispatcherPriority.Normal);
            }
            catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted)
            {
                Log($"refused mode={mode} reason=the app shut down first; last seen {state}");
                return;
            }
            if (acted)
                return;
            if (!logged || state != seen)
            {
                Log($"waiting {state}");
                seen = state;
                logged = true;
            }
            Thread.Sleep(Poll);
        }
        Log($"refused mode={mode} reason=CancelButton not pressable within {Deadline.TotalSeconds:N0} s; last seen {seen}");
    }

    // The stall rig's clock: nothing happens until its server has stopped answering, and then each action
    // at its own delay after that. Every check is made in the same dispatcher callback as its action.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveArmed(string mode, string marker, int? cancelDelay, int? closeDelay)
    {
        if (WaitForApplication(mode) is not { } app)
            return;
        while (!File.Exists(marker))
        {
            if (Clock.Elapsed > MarkerDeadline)
            {
                Log($"refused mode={mode} reason=the rig's marker {marker} did not appear within {MarkerDeadline.TotalSeconds:N0} s, so the stall never armed");
                return;
            }
            Thread.Sleep(Poll);
        }
        var sinceArmed = Stopwatch.StartNew();
        Log($"armed ms={Clock.ElapsedMilliseconds}");

        if (cancelDelay is int cancelAt)
        {
            SleepUntil(sinceArmed, cancelAt);
            if (!OnUiThread(app, "stop", () =>
                {
                    var button = app.MainWindow?.FindName("CancelButton") as FrameworkElement;
                    if (button is null || !Pressable(button))
                        return $"CancelButton not pressable {sinceArmed.ElapsedMilliseconds} ms after the marker (found={button is not null} enabled={button?.IsEnabled} visible={button?.IsVisible})";
                    Log($"acted mode=stop enabled={button.IsEnabled} visible={button.IsVisible} ms={Clock.ElapsedMilliseconds} sincearmed={sinceArmed.ElapsedMilliseconds}");
                    Pressing(app, "stop", () => Act("stop", app, button));
                    return null;
                }))
                return;
        }

        if (closeDelay is int closeAt)
        {
            SleepUntil(sinceArmed, closeAt);
            OnUiThread(app, "close", () =>
            {
                Window? window = app.MainWindow;
                if (window?.FindName("ScanningPanel") is not UIElement panel || !panel.IsVisible)
                    return $"the scan was no longer running {sinceArmed.ElapsedMilliseconds} ms after the marker";
                window.Closed += (_, _) => Log($"closed ms={Clock.ElapsedMilliseconds} utc={Utc()}");
                Log($"acted mode=close scanning=True detail={Detail(window)} ms={Clock.ElapsedMilliseconds} sincearmed={sinceArmed.ElapsedMilliseconds} utc={Utc()}");
                Pressing(app, "close", () => Act("close", app, null));
                return null;
            });
        }
    }

    // rescan: a class handler, registered before the window loads, because the window's Loaded handler starts
    // the first scan and a small tree's list is on screen a few milliseconds later.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveAfterList(string mode, string? missing)
    {
        if (WaitForApplication(mode) is not { } app)
            return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Watch(app, (Window)sender, mode, missing)));
        if (app.Dispatcher.Invoke(() => !_watching && app.MainWindow is { IsLoaded: true }))
            Log($"refused mode={mode} reason=the window loaded before the hook could watch its first scan");
    }

    // On the window's first Loaded. The Loaded that rescan raises itself lands here again and is ignored.
    private static void Watch(Application app, Window window, string mode, string? missing)
    {
        if (_watching || !ReferenceEquals(window, app.MainWindow))
            return;
        _watching = true;
        if (window.FindName("LargestFilesPanel") is not UIElement panel)
        {
            Log($"refused mode={mode} reason=the window has no LargestFilesPanel");
            return;
        }
        Log($"watching ms={Clock.ElapsedMilliseconds}");
        DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, panel.GetType())
            .AddValueChanged(panel, (_, _) =>
            {
                if (_listed || panel.Visibility != Visibility.Visible)
                    return;
                _listed = true;
                // Render: after the layout that builds the list's rows, and still ahead of the report, which
                // reads the window at Loaded priority once the ending is on screen.
                app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => AfterList(app, window, mode, missing, 0)));
            });
    }

    private static void AfterList(Application app, Window window, string mode, string? missing, int waited)
    {
        try
        {
            int rows = Rows(window);
            int built = window.FindName("LargestFilesList") is ItemsControl list
                ? Enumerable.Range(0, rows).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                : 0;
            if (rows > 0 && built == 0 && waited < 20)
            {
                app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => AfterList(app, window, mode, missing, waited + 1)));
                return;
            }
            Log($"listed rows={rows} built={built} {State(window)} ms={Clock.ElapsedMilliseconds}");
            if (mode == "expand")
            {
                Expand(app, window, rows);
                return;
            }
            var chooser = window.FindName("ChooseFolderButton") as FrameworkElement;
            if (rows == 0 || Shown(window, "ScanningPanel") || chooser is null || !Pressable(chooser))
            {
                Log($"refused mode=rescan reason=no list on screen with a new scan startable (rows={rows} chooser={chooser is not null && Pressable(chooser)})");
                return;
            }
            // At Normal, as a click's handler runs: WPF hands the running operation's priority to what the
            // handler awaits, so the second scan's continuations come back at Normal, as a person's would.
            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => StartSecondScan(app, window, missing, rows)));
        }
        catch (Exception e)
        {
            // Never onto the dispatcher: the app would take it for its own fault and exit 70.
            Log($"refused mode=rescan reason={e.GetType().Name}: {e.Message}");
        }
    }

    // expand (SL-040, O-024): opens the unreadable-folders list as a click on its header would, once the ending
    // shows both lists. Its layout runs at Render, still ahead of the report, so the report reads it opened.
    private static void Expand(Application app, Window window, int rows)
    {
        var expander = window.FindName("UnreadableExpander") as Expander;
        int unreadable = window.FindName("UnreadableList") is ItemsControl folders ? folders.Items.Count : 0;
        if (rows == 0 || unreadable == 0 || expander is null || !Pressable(expander) || expander.IsExpanded)
        {
            Log($"refused mode=expand reason=no list of largest files beside a closed, pressable list of unreadable folders (rows={rows} unreadable={unreadable} pressable={expander is not null && Pressable(expander)} expanded={expander?.IsExpanded})");
            return;
        }
        Log($"acted mode=expand rows={rows} unreadable={unreadable} ms={Clock.ElapsedMilliseconds}");
        Pressing(app, "expand", () => expander.IsExpanded = true); // what its header's toggle sets when clicked
        Log($"expanded={expander.IsExpanded} {State(window)}");
    }

    private static void StartSecondScan(Application app, Window window, string? missing, int rows)
    {
        try
        {
            if (missing is not null && !MoveAside(missing))
                return;
            Log($"acted mode=rescan rows={rows} ms={Clock.ElapsedMilliseconds}");
            Pressing(app, "rescan", () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window)));
            Log($"started {State(window)}");
            if (missing is not null)
                HoldUntilEnded(app, window, Stopwatch.StartNew());
        }
        catch (Exception e)
        {
            Log($"refused mode=rescan reason={e.GetType().Name}: {e.Message}");
        }
    }

    // The missing-root shape: the root is moved aside, as if deleted after its first scan. Only a folder under
    // %TEMP%, so a mistyped variable cannot move anything of the person's.
    private static bool MoveAside(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\');
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (!full.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
        {
            Log($"refused mode=rescan reason=FSA_HOOK_MISSING is not a folder under {temp}: {full}");
            return false;
        }
        string aside = full + ".moved";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(full, aside);
                break;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50); // a scanner's handle closing late, or an antivirus read
            }
        }
        Log($"moved from={full} to={aside}");
        return true;
    }

    // The report waits at Loaded priority. While an item of Render priority is queued nothing below it runs, so
    // this re-queues itself at Render until the second scan's ending is on screen. The scan's continuations
    // (Normal) and layout (Render) run ahead of it or in turn with it; only the report and lower wait. Holding
    // at Normal would starve a continuation that came back at Render and never end (measured on the first try).
    private static void HoldUntilEnded(Application app, Window window, Stopwatch since)
    {
        if (Shown(window, "OutcomePanel") && !Shown(window, "ScanningPanel"))
        {
            Log($"ended {State(window)} ms={Clock.ElapsedMilliseconds}");
            return;
        }
        if (since.Elapsed > Deadline)
        {
            Log($"refused mode=rescan reason=the second scan did not end within {Deadline.TotalSeconds:N0} s; {State(window)}");
            return;
        }
        Thread.Sleep(1);
        app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => HoldUntilEnded(app, window, since)));
    }

    // The element's own Visibility, what the app set, as for Detail.
    private static bool Shown(Window window, string name) =>
        window.FindName(name) is UIElement { Visibility: Visibility.Visible };

    private static int Rows(Window window) => window.FindName("LargestFilesList") is ItemsControl list ? list.Items.Count : 0;

    private static string State(Window window) =>
        $"scanning={Shown(window, "ScanningPanel")} outcome={Shown(window, "OutcomePanel")} largest={Shown(window, "LargestFilesPanel")} rows={Rows(window)}";

    private static Application? WaitForApplication(string mode)
    {
        Application? app;
        while ((app = Application.Current) is null)
        {
            if (Clock.Elapsed > Deadline)
            {
                Log($"refused mode={mode} reason=no Application within {Deadline.TotalSeconds:N0} s");
                return null;
            }
            Thread.Sleep(Poll);
        }
        return app;
    }

    // Runs a check-and-act on the UI thread; it returns why it did not act, or null when it did.
    private static bool OnUiThread(Application app, string mode, Func<string?> checkAndAct)
    {
        string? refusal;
        try
        {
            refusal = app.Dispatcher.Invoke(checkAndAct, DispatcherPriority.Normal);
        }
        catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted)
        {
            refusal = "the app shut down first";
        }
        if (refusal is not null)
            Log($"refused mode={mode} reason={refusal}");
        return refusal is null;
    }

    private static void SleepUntil(Stopwatch since, int milliseconds)
    {
        long left = milliseconds - since.ElapsedMilliseconds;
        if (left > 0)
            Thread.Sleep(TimeSpan.FromMilliseconds(left));
    }

    // The app's own handler threw (OnCancel for stop, OnClosing for close). A real press raises that on the
    // dispatcher, where App.DispatcherUnhandledException sees it; so it goes there, and not back to the
    // hook's thread to be logged as the hook's.
    private static void Pressing(Application app, string mode, Action press)
    {
        try
        {
            press();
        }
        catch (Exception e)
        {
            Log($"rethrown mode={mode} reason={e.GetType().Name}: {e.Message}");
            var fault = ExceptionDispatchInfo.Capture(e);
            app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(fault.Throw));
        }
    }

    // On the UI thread, straight after the check.
    private static void Act(string mode, Application app, FrameworkElement? cancelButton)
    {
        switch (mode)
        {
            case "stop":
                StartStoppingLog(app);
                // What a click raises: reaches OnCancel through the Click wiring in MainWindow.xaml.
                cancelButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "close":
                app.MainWindow!.Close();
                break;
            case "throw":
                // BeginInvoke, not InvokeAsync: an exception in an InvokeAsync callback goes to its Task. No
                // SetErrorMode: interop lives only in Native (ArchitectureRulesTests), and with the app's
                // handler removed the process still ended at once, 0xE0434352 and no crash dialog.
                app.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => throw new InvalidOperationException("FsaWindowHook throw mode: an unhandled exception on the UI thread.")));
                break;
        }
    }

    /// <summary>
    /// From the press of Cancel until the scan returns, what the Stopping state shows, every 250 ms. The timer
    /// runs on the UI thread at the priority of the app's own progress tick, so a gap between two lines is a
    /// UI thread that did not get to it. The return is caught as it happens, when ScanningPanel collapses in
    /// the scan's finally, and shutdown writes one last line: report mode writes its report and exits in one
    /// continuation, so no tick need fall between the return and the exit.
    /// </summary>
    private static void StartStoppingLog(Application app)
    {
        Window window = app.MainWindow!;
        _sinceCancel = Stopwatch.StartNew();
        if (window.FindName("ScanningPanel") is UIElement panel)
        {
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, panel.GetType())
                .AddValueChanged(panel, (_, _) =>
                {
                    if (_returned || panel.Visibility == Visibility.Visible)
                        return;
                    _returned = true;
                    _stoppingTimer?.Stop();
                    Log($"returned {Stopping(window)}");
                });
        }
        _stoppingTimer = new DispatcherTimer(StoppingEvery, DispatcherPriority.Background,
            (_, _) => Log($"stopping {Stopping(window)}"), app.Dispatcher);
        app.Dispatcher.ShutdownStarted += (_, _) =>
        {
            _stoppingTimer?.Stop();
            Log($"stopping {Stopping(window)} final=True");
        };
    }

    private static string Stopping(Window window)
    {
        string title = (window.FindName("ScanningTitle") as TextBlock)?.Text ?? "<none>";
        return $"ms={_sinceCancel!.ElapsedMilliseconds} title={title} detail={Detail(window)}";
    }

    // The element's own Visibility, not IsVisible: once ScanningPanel collapses every child reads invisible,
    // and a line the app forgot to clear would read "-" by accident.
    private static string Detail(Window window) =>
        window.FindName("StoppingDetail") is TextBlock { Visibility: Visibility.Visible } detail && detail.Text.Length > 0
            ? detail.Text
            : "-";

    private static string Utc() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static bool Delay(string variable, out int? milliseconds)
    {
        milliseconds = null;
        string? text = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(text))
            return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            return false;
        milliseconds = value;
        return true;
    }

    private static void Log(string line)
    {
        if (string.IsNullOrEmpty(_logPath))
            return;
        try
        {
            lock (LogGate)
                File.AppendAllText(_logPath, line.ReplaceLineEndings(" ") + "\r\n", Encoding.ASCII);
        }
        catch (Exception)
        {
            // A lost line shows up as a case failure ("never acted"), never as a crash of the app.
        }
    }
}
