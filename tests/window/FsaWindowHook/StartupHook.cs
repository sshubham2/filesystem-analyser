using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// The window cases' driver (SL-033): presses what a person would press in the published app, from inside
/// its process, with no UI Automation.
/// </summary>
/// <remarks>
/// <code>
///   DOTNET_STARTUP_HOOKS=&lt;absolute path to FsaWindowHook.dll&gt;  FSA_HOOK_MODE=stop|throw|close|watch|measure
///   FSA_HOOK_LOG=&lt;file&gt;  [FSA_HOOK_ARMED=&lt;marker file&gt;  FSA_HOOK_CANCEL_DELAY_MS=&lt;n&gt;  FSA_HOOK_CLOSE_DELAY_MS=&lt;n&gt;]
///   [FSA_HOOK_SLOW_REFRESH_MS=&lt;n&gt;]
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
/// Two modes read the results list (SL-039), TopLevelList, whose items are the app's own TopLevelRow objects,
/// read by reflection because the hook shares no code with the app. Neither is armed.
/// <list type="bullet">
/// <item>watch waits up to 30 s for the scan to be running (CancelButton pressable) while the list already
/// holds a Folder row with Bytes over 0 and another still NotRead. It logs "watch rows=n folderBytes=n
/// notRead=n containers=n skipped=n armed=b running=True" (containers: the most items the list had realized
/// once any refresh so far was laid out; skipped and armed: the window's refresh-skip state), then
/// "acted mode=watch ..." and presses Cancel as stop does. With
/// FSA_HOOK_SLOW_REFRESH_MS it also waits until a tick has skipped its refresh and the next skip is armed
/// again, so the scan's final snapshot arrives right after a skipped tick, with the skip still armed. On
/// timeout it logs "watch timeout ..." and presses Cancel, so the case fails on the missing watch line.</item>
/// <item>measure presses nothing. While the scan runs it posts an Input-priority no-op every 50 ms and
/// records how long each waited, and records the window's own time for each refresh of the list (handler
/// and layout). When the scan returns it logs "measured refreshes=n refreshP95=ms refreshMax=ms probes=n
/// inputP95=ms inputMax=ms skipped=n rows=n containers=n".</item>
/// </list>
/// FSA_HOOK_SLOW_REFRESH_MS=n (watch and measure only) sleeps n ms on the UI thread inside every refresh of
/// the list, as a slow machine would, so the window's rule that a refresh over 100 ms skips the next tick is
/// exercised.
/// </remarks>
internal class StartupHook
{
    private const string TargetExe = "FsAnalyser.App.exe";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MarkerDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan StoppingEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WatchDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(50);

    private static readonly Stopwatch Clock = new();
    private static readonly object LogGate = new();
    private static string? _logPath;

    // The stopping log's state: written and read on the UI thread only.
    private static Stopwatch? _sinceCancel;
    private static DispatcherTimer? _stoppingTimer;
    private static bool _returned;

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
            if (mode is not ("stop" or "throw" or "close" or "watch" or "measure"))
            {
                Log($"refused mode={mode} reason=unknown mode");
                return;
            }
            if (!Delay("FSA_HOOK_SLOW_REFRESH_MS", out int? pad))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_SLOW_REFRESH_MS is not a whole number of milliseconds");
                return;
            }
            string? marker = Environment.GetEnvironmentVariable("FSA_HOOK_ARMED");
            if (mode is "watch" or "measure")
            {
                if (!string.IsNullOrEmpty(marker))
                    Log($"refused mode={mode} reason={mode} mode is not armed");
                else
                    WatchList(mode, pad ?? 0);
                return;
            }
            if (pad is not null)
            {
                Log($"refused mode={mode} reason=FSA_HOOK_SLOW_REFRESH_MS is for watch and measure only");
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

    // SL-039: the results list. Finds it, and hooks every refresh of it: the pad, and the window's own time
    // for the refresh, read once its Loaded-priority marker has run. Then watches or measures.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WatchList(string mode, int pad)
    {
        if (WaitForApplication(mode) is not { } app)
            return;
        var seen = new ListSeen(); // the UI thread's only
        ListBox? list = null;
        while (list is null)
        {
            if (Clock.Elapsed > Deadline)
            {
                Log($"refused mode={mode} reason=no TopLevelList within {Deadline.TotalSeconds:N0} s");
                return;
            }
            try
            {
                list = app.Dispatcher.Invoke(() =>
                {
                    if (app.MainWindow is not { } window || window.FindName("TopLevelList") is not ListBox found)
                        return null;
                    DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox))
                        .AddValueChanged(found, (_, _) =>
                        {
                            if (found.ItemsSource is null)
                                return;
                            if (pad > 0)
                                Thread.Sleep(pad);
                            // Below Loaded and Render: the window has timed this refresh and laid the list out.
                            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                            {
                                seen.RefreshMs.Add(WindowState<TimeSpan>(window, "LastRefresh").TotalMilliseconds);
                                seen.Containers = Math.Max(seen.Containers, Containers(found));
                            });
                        });
                    return found;
                }, DispatcherPriority.Normal);
            }
            catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted)
            {
                Log($"refused mode={mode} reason=the app shut down before TopLevelList was found");
                return;
            }
            if (list is null)
                Thread.Sleep(Poll);
        }
        if (mode == "measure")
            Measure(app, list, seen);
        else
            Watch(app, list, seen, padded: pad > 0);
    }

    /// <summary>
    /// What the hook saw of the list's refreshes, after each had been laid out: the window's own time for it,
    /// and the most items the list had realized. Read and written on the UI thread only.
    /// </summary>
    private sealed class ListSeen
    {
        public List<double> RefreshMs { get; } = [];

        public int Containers { get; set; }
    }

    // The watch: reads the window and presses Cancel in one callback, so the state it logs is the state it
    // pressed in.
    private static void Watch(Application app, ListBox list, ListSeen seen, bool padded)
    {
        string last = "nothing read";
        while (Clock.Elapsed < WatchDeadline)
        {
            bool pressed = false;
            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    Window window = app.MainWindow!;
                    var button = window.FindName("CancelButton") as FrameworkElement;
                    bool running = button is not null && Pressable(button);
                    (int rows, long folderBytes, int notRead) = ReadRows(list);
                    int skipped = WindowState<int>(window, "SkippedRefreshes");
                    bool armed = WindowState<bool>(window, "SkipsNextRefresh");
                    last = $"rows={rows} folderBytes={folderBytes} notRead={notRead} containers={seen.Containers} skipped={skipped} armed={armed} running={running}";
                    if (!running || folderBytes <= 0 || notRead == 0 || (padded && (skipped == 0 || !armed)))
                        return;
                    Log($"watch {last}");
                    Log($"acted mode=watch enabled={button!.IsEnabled} visible={button.IsVisible} ms={Clock.ElapsedMilliseconds}");
                    Pressing(app, "watch", () => Act("stop", app, button));
                    pressed = true;
                }, DispatcherPriority.Normal);
            }
            catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted)
            {
                Log($"refused mode=watch reason=the app shut down first; last seen {last}");
                return;
            }
            if (pressed)
                return;
            Thread.Sleep(Poll);
        }
        Log($"watch timeout {last}");
        // Stopped anyway, so the run ends; the case fails on the missing watch line.
        OnUiThread(app, "watch", () =>
        {
            if (app.MainWindow?.FindName("CancelButton") is not FrameworkElement button || !Pressable(button))
                return "CancelButton not pressable at the watch timeout";
            Pressing(app, "watch", () => Act("stop", app, button));
            return null;
        });
    }

    // The measure: an Input-priority probe every 50 ms from its own thread while the scan runs, and one line
    // when the scan returns (ScanningPanel collapses), before the ending's own refresh.
    private static void Measure(Application app, ListBox list, ListSeen seen)
    {
        var inputMs = new List<double>();
        var ended = new ManualResetEventSlim();
        var prober = new Thread(() =>
        {
            while (!ended.IsSet)
            {
                var waited = Stopwatch.StartNew();
                try
                {
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                lock (inputMs)
                    inputMs.Add(waited.Elapsed.TotalMilliseconds);
                ended.Wait(ProbeEvery);
            }
        }) { IsBackground = true, Name = "FsaWindowHook-probe" };
        prober.Start();

        OnUiThread(app, "measure", () =>
        {
            Window window = app.MainWindow!;
            if (window.FindName("ScanningPanel") is not UIElement panel)
                return "no ScanningPanel";
            bool logged = false;
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, panel.GetType()).AddValueChanged(panel, (_, _) =>
            {
                if (logged || panel.Visibility == Visibility.Visible)
                    return;
                logged = true;
                ended.Set();
                double[] input;
                lock (inputMs)
                    input = [.. inputMs];
                Log($"measured refreshes={seen.RefreshMs.Count} refreshP95={P95(seen.RefreshMs)} refreshMax={Max(seen.RefreshMs)} " +
                    $"probes={input.Length} inputP95={P95(input)} inputMax={Max(input)} " +
                    $"skipped={WindowState<int>(window, "SkippedRefreshes")} rows={list.Items.Count} containers={seen.Containers}");
            });
            return null;
        });
    }

    // How many rows the list holds, the largest Folder row's Bytes, and how many Folder rows are NotRead.
    private static (int Rows, long FolderBytes, int NotRead) ReadRows(ItemsControl list)
    {
        int rows = 0, notRead = 0;
        long folderBytes = 0;
        foreach (object item in list.Items)
        {
            rows++;
            Type type = item.GetType();
            if (type.GetProperty("Kind")?.GetValue(item)?.ToString() != "Folder")
                continue;
            folderBytes = Math.Max(folderBytes, (long)(type.GetProperty("Bytes")?.GetValue(item) ?? 0L));
            if (type.GetProperty("State")?.GetValue(item)?.ToString() == "NotRead")
                notRead++;
        }
        return (rows, folderBytes, notRead);
    }

    // The list's realized items: with virtualization on, about a screenful, however many rows it holds.
    private static int Containers(DependencyObject element)
    {
        int count = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            count += child is ListBoxItem ? 1 : Containers(child);
        }
        return count;
    }

    // The window's refresh state (MainWindow's internal LastRefresh, SkippedRefreshes, SkipsNextRefresh).
    private static T WindowState<T>(Window window, string property) =>
        (T)(window.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException($"the window has no {property}"));

    private static string P95(IReadOnlyCollection<double> ms)
    {
        if (ms.Count == 0)
            return "-";
        double[] sorted = [.. ms.Order()];
        return sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1].ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string Max(IReadOnlyCollection<double> ms) =>
        ms.Count == 0 ? "-" : ms.Max().ToString("F1", CultureInfo.InvariantCulture);

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
