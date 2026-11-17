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
///   DOTNET_STARTUP_HOOKS=&lt;absolute path to FsaWindowHook.dll&gt;  FSA_HOOK_MODE=stop|throw|close|watch|measure|rescan|expand|hide|kinds|size|tree|reveal|settings|rows
///   [FSA_HOOK_ANSWER=yes|no]  (settings only, SL-095: SettingsMode.cs turns the first precaution off and answers its question)
///   FSA_HOOK_LOG=&lt;file&gt;  [FSA_HOOK_ARMED=&lt;marker file&gt;  FSA_HOOK_CANCEL_DELAY_MS=&lt;n&gt;  FSA_HOOK_CLOSE_DELAY_MS=&lt;n&gt;]
///   [FSA_HOOK_SLOW_REFRESH_MS=&lt;n&gt;]  [FSA_HOOK_RENAMED=LastRefresh|SkippedRefreshes|SkipsNextRefresh]  (watch and measure only)
///   [FSA_HOOK_MISSING=&lt;the root folder, under %TEMP%&gt;]  (rescan only)
///   [FSA_HOOK_READABLE=&lt;a folder under %TEMP%&gt;  FSA_HOOK_BEFORE=&lt;a tab's x:Name&gt;  FSA_HOOK_AFTER=&lt;a tab's x:Name&gt;]  (rescan only)
///   [FSA_HOOK_EXPAND=1]  (watch only; a folder's path for tree)  [FSA_HOOK_MINSIZE=1]  (watch, expand and kinds only)
///   [FSA_HOOK_MAXIMIZE=1]  (watch, expand and size only)  [FSA_HOOK_BACK=1]  (expand, and watch with FSA_HOOK_EXPAND)
///   [FSA_HOOK_DRIVES=&lt;n&gt;]  (watch, expand, kinds and size only)
///   [FSA_HOOK_WALK, FSA_HOOK_RECYCLE, FSA_HOOK_MULTIPLY, FSA_HOOK_SELECT, FSA_HOOK_RELEASE, FSA_HOOK_GAP, FSA_HOOK_HEIGHT]  (tree only)
///   [FSA_HOOK_TREE_STOP=&lt;folder under the root&gt;[\]]  (SL-110, tree and reveal only: StoppedTree.cs stops the scan at that folder, or
///   with the trailing backslash anywhere under it, and chooses the folder tree beside the stopped list)
///   [FSA_HOOK_CAN_REVEAL=&lt;row&gt;  FSA_HOOK_VIEWS=&lt;folders|tree|kinds,...&gt;]  (SL-110, tree only)
///   [FSA_HOOK_RENDER=software]  (SL-080: WPF draws the process in software, set before Main; tree mode's facts line says so)
///   [FSA_HOOK_FAIL_LISTING=&lt;folder name&gt;|&lt;page&gt;|&lt;Win32 error&gt;]  (SL-082, any mode: Core's listing failpoint, armed
///   before Main; see ArmFailListing)
///   [FSA_HOOK_EXPLORER_VIEW=&lt;hidden&gt;|&lt;protected&gt;]  (SL-091, any mode: the Explorer view reveals are decided under,
///   ANDed with the real one, armed before Main; see ArmExplorerView)
///   [FSA_HOOK_ROWS=1]  (SL-089, rows and stop only: the unreadable-folders panel's rows, written at shutdown; RowsMode.cs)
/// </code>
/// tree (SL-044) reads and drives the folder tree a completed scan shows, and logs JSON lines; TreeMode.cs has
/// all of it. It is not armed and takes no FSA_HOOK_SLOW_REFRESH_MS. rows (SL-089) presses nothing and has no deadline:
/// the scan runs to its own ending, a whole drive's included, with the listing failpoint armed as under any mode, and
/// with FSA_HOOK_ROWS=1 the panel's rows are written as the app shuts down (RowsMode.cs). It is not armed.
///
/// The runtime calls <see cref="Initialize"/> before the app's Main, in every .NET process that inherits the
/// variable: pwsh 7, dotnet.exe and MSBuild nodes too. So it does nothing unless FSA_HOOK_MODE is set and the
/// process is FsAnalyser.App.exe, and nothing it does can end the host but one thing it does on purpose: an
/// FSA_HOOK_FAIL_LISTING (SL-082) or FSA_HOOK_EXPLORER_VIEW (SL-091) it cannot arm is thrown out of Initialize, and the
/// app ends before its Main.
///
/// Without FSA_HOOK_ARMED every mode waits for the same thing: the window's CancelButton pressable
/// (<see cref="Pressable"/>), which is only while a scan is running. It reads that and acts in one dispatcher
/// callback, so the state it read is the state it acted on. stop raises the button's Click; close closes the
/// main window; throw posts an exception to the UI thread, and logs "shutdown tree=b treeItems=n ms=n" as the app
/// shuts down on it (whether the folder tree was shown, SL-044). If nothing is pressable within 20 s, or the mode
/// is unknown, it logs "refused" and does nothing. The hook never ends the process or picks its exit code: every
/// exit code a case reads is the app's own.
///
/// With FSA_HOOK_ARMED (SL-038, the stalled-share cases) it acts on the stall rig's clock instead: it waits
/// up to 60 s for the marker file the rig writes when its server stops answering, then presses Cancel
/// FSA_HOOK_CANCEL_DELAY_MS after it and closes the window FSA_HOOK_CLOSE_DELAY_MS after it. An unset delay
/// is an action not taken, so close mode without a cancel delay closes with no prior Cancel. stop needs a
/// cancel delay, close a close delay, and throw is not armed. Cancel is pressed only if pressable; the window
/// is closed only while its scan is still running (ScanningPanel on screen).
///
/// With a progress trigger (SL-095, the pool protocol's cancelled runs) stop and close act at a point of the scan's own
/// progress, read off the window, instead of the first moment Cancel is pressable: FSA_HOOK_AT_FOLDERS=n once the
/// counters show n folders; FSA_HOOK_PAST=&lt;path&gt; with FSA_HOOK_PAST_FOLDERS=n once the folder being listed has
/// gone past that path in the walk's order (logged "passed folders=n") and n more folders were listed; FSA_HOOK_AT_SIZED=n
/// once n files have been sized. One of the three, never with FSA_HOOK_ARMED; FSA_HOOK_TRIGGER_DEADLINE_S bounds the
/// wait (3600 s). The action logs "acted mode=... folders=n sized=n current=...", the count the run's mark records.
/// A scan that ends first logs "refused ... ended before the trigger": that run is a full one. Closing the window
/// mid-scan writes no --report (exit 70, as WindowReport documents), so for a closed run this log is the record.
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
/// With FSA_HOOK_READABLE (SL-065) it deletes that folder, which must be under %TEMP% and empty, before the second
/// scan, as a person who made an unreadable folder go would: a folder the first scan could not open is not there for
/// the second. With FSA_HOOK_BEFORE it selects that tab of the outcome's sections as a click on its header would
/// (IsSelected), lays the window out, and only then starts the second scan, so the scan starts with that section on
/// screen; with FSA_HOOK_AFTER it selects that tab once the second scan's ending is on screen, ahead of the report.
/// FSA_HOOK_READABLE holds the report until the second scan's ending, as FSA_HOOK_MISSING does, and FSA_HOOK_AFTER
/// needs one of the two. They log "readable deleted=path", "before selected=name unreadableHeight=px" and "after
/// selected=name unreadableRows=n STATE".
///
/// expand (SL-040, O-024; SL-065) watches the same way and, once the first ending shows the list of largest files
/// beside a pressable, unselected tab of unreadable folders, selects that tab as a click on its header would
/// (IsSelected, which the click sets): before SL-065 the folders were an expander, opened the same way. The layout
/// that follows runs ahead of the report, so the report reads the window with their list on screen. Its "acted" line
/// carries "tabsHeight=px", the sections' height before the click. With FSA_HOOK_BACK=1 it then lays the window out,
/// logs "shown unreadableHeight=px unreadableRows=n" and selects the first tab again, as a second click would, and
/// logs "back selected=name STATE": the report then reads the unreadable folders' list after it was on screen and
/// left. It is not armed and takes no FSA_HOOK_MISSING.
///
/// hide (SL-065) watches the same way and, once the first ending shows the largest files selected beside the
/// unreadable folders, collapses the largest files' tab as a section whose own condition turned false would, and
/// calls the window's EnsureSelectedTabIsVisible, as the window does after every such change. No ending of the app
/// shows the unreadable folders without the largest files, so this is the one way to its branch that selects a
/// section other than the first. It logs "acted mode=hide rows=n ms=n" and "hidden selected=name tabs=Visibility".
///
/// FSA_HOOK_MAXIMIZE=1 (SL-065) maximizes the window in the same Loaded class handler as FSA_HOOK_MINSIZE, and
/// holds the report the same way; in size mode it takes the place of the sizing to the minimum. It logs
/// "maximized ms=n", and size mode "acted mode=size maximized=True ms=n".
///
/// kinds (SL-046) watches the same way and, once the first ending shows the list of largest files, chooses the files
/// by kind on the results cell's header line as a click would (ShowKindsButton's IsChecked, which a click sets), if a
/// person could. The layout that follows runs ahead of the report, so the report reads the window with the kinds shown.
/// It logs "watching" and "listed" as expand does, then "acted mode=kinds rows=n ms=n" and "kinds=b STATE" straight
/// after it chose them. It is not armed and takes no FSA_HOOK_MISSING; FSA_HOOK_MINSIZE and FSA_HOOK_DRIVES size the
/// window and add drive buttons ahead of it, as they do for expand.
///
/// size (SL-040, O-027 and the window-size rework) sets the window's Width and Height to its own MinWidth and
/// MinHeight in a Loaded class handler, which runs before the window's own Loaded handler starts the scan, and
/// does nothing else: every ending is then laid out at the smallest size a person can drag the window to. It
/// logs "sized width=n minWidth=n height=n minHeight=n ms=n" and "acted mode=size width=n ... ms=n", and is not
/// armed. FSA_HOOK_MINSIZE=1 does the same sizing, logging only the "sized" line, ahead of watch or expand.
/// Both then hold the report, once the ending is on screen, until the drive buttons are in. FSA_HOOK_DRIVES=n
/// (SL-040, O-028) does the same hold, at any size, and then adds n drive buttons shaped like the app's own
/// beside the real ones, so a case can show the pickers take the same room however many drives are ready.
/// The hold logs "drives in ms=n held=n added=n buttons=n labels=a/b ink=c/d windowWidth=px windowHeight=px state=WindowState"
/// (the size the ending was laid out at, SL-065). labels and ink (SL-002) count the app's own b drive buttons, before the extra
/// ones are added: a have a TextBlock with text as their Content, and c draw it in the disabled colour of the button's template when the button is disabled (the hook disables each for the reading). FSA_HOOK_STATUS=text (SL-047) also puts that text in the
/// window's reveal status line as the drives come in, ahead of the report, and adds "status=b statusHeight=px" to
/// that line: whether the line is visible and how tall it was laid out; and "statusTextWidth=px statusWidth=px
/// statusFits=n statusChars=n" (SL-091): the text's untrimmed width against the line's, and how many of its characters
/// show before the ellipsis.
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
///
/// Two modes read the results list (SL-039), TopLevelList, whose items are the app's own TopLevelRow objects,
/// read by reflection because the hook shares no code with the app. Neither is armed.
/// <list type="bullet">
/// <item>watch waits up to 30 s for the scan to be running (CancelButton pressable) while the list already
/// holds a Folder row with Bytes over 0 and another still NotRead. It logs "watch rows=n folderBytes=n
/// notRead=n containers=n skipped=n armed=b running=True listHeight=px" (containers: the most items the list
/// had realized once any refresh so far was laid out; skipped and armed: the window's refresh-skip state;
/// listHeight: the list's ActualHeight as it is pressed, SL-040), then "acted mode=watch ..." and presses
/// Cancel as stop does. With FSA_HOOK_EXPAND=1 it also opens the unreadable-folders list once the Cancel's
/// ending shows the largest files, exactly as expand mode does after its ending, with expand's lines. With
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
/// exercised. FSA_HOOK_RENAMED=name (watch and measure only, SL-073) reads that one of the window's refresh state
/// as if the app had renamed it, so a case can show the hook's own callbacks on the UI thread refuse ("refused
/// mode=m reason=InvalidOperationException: the window has no name") and never hand the app an exception.
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

    internal static readonly Stopwatch Clock = new();
    // When Clock started, so that its times can be put on another clock of this process: a trace's (SL-080).
    internal static DateTime ClockOriginUtc;
    private static readonly object LogGate = new();
    private static string? _logPath;

    // FSA_HOOK_RENAMED (watch and measure only, SL-073): one of the window's refresh state properties, read as missing.
    private static readonly string? Renamed = Environment.GetEnvironmentVariable("FSA_HOOK_RENAMED") is { Length: > 0 } renamed ? renamed : null;

    // The stopping log's state: written and read on the UI thread only.
    private static Stopwatch? _sinceCancel;
    private static DispatcherTimer? _stoppingTimer;
    private static bool _returned;

    // rescan's and expand's state, and size's: written and read on the UI thread only.
    private static bool _watching;
    private static bool _listed;
    private static bool _sized;

    public static void Initialize()
    {
        Exception? unarmed = null;
        try
        {
            string? mode = Environment.GetEnvironmentVariable("FSA_HOOK_MODE");
            if (string.IsNullOrEmpty(mode) ||
                !string.Equals(Path.GetFileName(Environment.ProcessPath), TargetExe, StringComparison.OrdinalIgnoreCase))
                return;
            _logPath = Environment.GetEnvironmentVariable("FSA_HOOK_LOG");
            ClockOriginUtc = DateTime.UtcNow;
            Clock.Start();
            // FSA_HOOK_RENDER=software (SL-080): WPF draws this process in software, set here, before the app's Main, as
            // RenderOptions needs, so a stall waiting on the GPU can be told from one waiting on the render thread. Tree
            // mode's facts line says which mode the process ran in.
            if (Environment.GetEnvironmentVariable("FSA_HOOK_RENDER") == "software")
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            // Tree mode's FSA_HOOK_HEIGHT sizes the window before it has a window handle, from a watch on this thread's
            // dispatcher that has to be in place before the app's Main makes the window (SL-080 code review M3).
            if (mode == "tree")
                TreeMode.BeforeMain();
            unarmed = ArmFailListing() ?? ArmExplorerView();
            if (unarmed is null)
                new Thread(() => Run(mode)) { IsBackground = true, Name = "FsaWindowHook" }.Start();
        }
        catch (Exception)
        {
            // An exception out of Initialize would end the host before its Main.
        }
        // The one exception let out, on purpose (SL-082): the app ends before its Main, with no window and no scan.
        if (unarmed is not null)
            throw unarmed;
    }

    private const string FailListingType = "FsAnalyser.Core.Scanning.SynchronousIoCancel, FsAnalyser.Core";
    private const string FailListingMethod = "FailListingOf";

    // FSA_HOOK_FAIL_LISTING=<folder name>|<page>|<Win32 error> (SL-082), with any mode: arms Core's listing failpoint
    // (SynchronousIoCancel.FailListingOf, internal, reached by name because the hook shares no code with the app) here,
    // before the app's Main, and so before its window or any scan exists. Every listing of a folder of that name then
    // fails with that error at that page, keeping the pages before it. It logs "fail-listing armed <name> page <n> error
    // <e>", and "fail-listing fired <name> page <n>" from the scan thread each time a listing is failed. A value that is
    // not three parts, a page or error that is not a whole number, a member it cannot find, or Core refusing the value
    // (a page under 2, an error of 0 or 18) logs "fail-listing refused reason=..." and returns what Initialize throws:
    // the app then ends before its Main, so a case never reads an unfaulted scan as a faulted one.
    private static Exception? ArmFailListing()
    {
        if (Environment.GetEnvironmentVariable("FSA_HOOK_FAIL_LISTING") is not { Length: > 0 } value)
            return null;
        try
        {
            string[] parts = value.Split('|');
            if (parts.Length != 3)
                throw new FormatException($"'{value}' is not <folder name>|<page>|<Win32 error>");
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int page))
                throw new FormatException($"the page '{parts[1]}' is not a whole number");
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int error))
                throw new FormatException($"the error '{parts[2]}' is not a whole number");
            MethodInfo arm = Type.GetType(FailListingType)?.GetMethod(FailListingMethod, BindingFlags.Static | BindingFlags.NonPublic,
                    [typeof(string), typeof(int), typeof(int), typeof(Action<string, int>)])
                ?? throw new MissingMethodException($"the app has no {FailListingMethod}(string, int, int, Action<string, int>) on {FailListingType}");
            Action<string, int> fired = (name, n) => Log($"fail-listing fired {name} page {n}");
            arm.Invoke(null, [parts[0], page, error, fired]);
            Log($"fail-listing armed {parts[0]} page {page} error {error}");
            return null;
        }
        catch (Exception e)
        {
            Exception cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            Log($"fail-listing refused reason={cause.GetType().Name}: {cause.Message}");
            return new InvalidOperationException($"FsaWindowHook: FSA_HOOK_FAIL_LISTING was not armed: {cause.Message}", cause);
        }
    }

    private const string ExplorerViewType = "FsAnalyser.Core.Native.ExplorerView, FsAnalyser.Core";
    private const string RevealType = "FsAnalyser.Core.Native.ShellReveal, FsAnalyser.Core";
    private const string OverrideMethod = "OverrideExplorerView";

    // FSA_HOOK_EXPLORER_VIEW=<hidden>|<protected> (SL-091), each 0 or 1, with any mode: the Explorer view the app's
    // reveals are decided under, set through Core's ShellReveal.OverrideExplorerView (internal, reached by name) here,
    // before the app's Main. Core ANDs it with the real view, so it can only hide: 0|0 is Windows' defaults on any PC,
    // and no value makes a reveal land that the real settings refuse. A case can then assert the refusals of the
    // defaults with no Explorer window opened and without touching the person's settings. It logs
    // "explorer-view-injected injected=<hidden>|<protected>". A value that is not two parts of 0 or 1 (reason=format), the
    // view's type missing or not built (reason=type), the member missing (reason=member) or the call failing
    // (reason=invoke) logs "explorer-view refused reason=<that> <message>" and returns what Initialize throws: the app
    // ends before its Main, so no reveal is ever clicked under the real view in place of the injected one.
    private static Exception? ArmExplorerView()
    {
        if (Environment.GetEnvironmentVariable("FSA_HOOK_EXPLORER_VIEW") is not { Length: > 0 } value)
            return null;
        string reason = "format";
        try
        {
            string[] parts = value.Split('|');
            if (parts.Length != 2 || parts.Any(p => p is not ("0" or "1")))
                throw new FormatException($"'{value}' is not <hidden>|<protected>, each 0 or 1");
            reason = "type";
            Type view = Type.GetType(ExplorerViewType) ?? throw new TypeLoadException($"the app has no {ExplorerViewType}");
            object mask = Activator.CreateInstance(view, parts[0] == "1", parts[1] == "1")
                ?? throw new TypeLoadException($"{ExplorerViewType} was not built");
            reason = "member";
            MethodInfo set = Type.GetType(RevealType)?.GetMethod(OverrideMethod, BindingFlags.Static | BindingFlags.NonPublic, [view])
                ?? throw new MissingMethodException($"the app has no {OverrideMethod}({view.Name}) on {RevealType}");
            reason = "invoke";
            set.Invoke(null, [mask]);
            Log($"explorer-view-injected injected={parts[0]}|{parts[1]}");
            return null;
        }
        catch (Exception e)
        {
            Exception cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            Log($"explorer-view refused reason={reason} {cause.GetType().Name}: {cause.Message}");
            return new InvalidOperationException($"FsaWindowHook: FSA_HOOK_EXPLORER_VIEW was not armed: {cause.Message}", cause);
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
            if (mode is not ("stop" or "throw" or "close" or "watch" or "measure" or "rescan" or "expand" or "hide" or "kinds" or "size" or "tree" or "reveal" or "settings" or "rows"))
            {
                Log($"refused mode={mode} reason=unknown mode");
                return;
            }
            // FSA_HOOK_TREE_STOP (SL-110) stops the scan at a folder and chooses the stopped tree: tree and reveal modes only.
            // FSA_HOOK_CAN_REVEAL and FSA_HOOK_VIEWS are tree mode's alone.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSA_HOOK_TREE_STOP")) && mode is not ("tree" or "reveal"))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_TREE_STOP is for tree and reveal only");
                return;
            }
            if ((!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSA_HOOK_CAN_REVEAL"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSA_HOOK_VIEWS"))) && mode != "tree")
            {
                Log($"refused mode={mode} reason=FSA_HOOK_CAN_REVEAL and FSA_HOOK_VIEWS are for tree only");
                return;
            }
            // FSA_HOOK_ROWS (SL-089): the panel's rows, beside stop's press or under rows mode's none.
            string? dumpRows = Environment.GetEnvironmentVariable("FSA_HOOK_ROWS");
            if (!string.IsNullOrEmpty(dumpRows) && (dumpRows != "1" || mode is not ("stop" or "rows")))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_ROWS is 1, for rows and stop only");
                return;
            }
            if (mode == "rows")
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSA_HOOK_ARMED")))
                    Log("refused mode=rows reason=rows mode is not armed");
                else if (!string.IsNullOrEmpty(dumpRows))
                    RowsMode.Arm(mode);
                return;
            }
            if (!string.IsNullOrEmpty(dumpRows))
                RowsMode.Arm(mode);
            string? minSize = Environment.GetEnvironmentVariable("FSA_HOOK_MINSIZE");
            bool sizeFirst = !string.IsNullOrEmpty(minSize);
            if (sizeFirst && (minSize != "1" || mode is not ("watch" or "expand" or "kinds")))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_MINSIZE is 1, and for watch, expand and kinds only (size mode sizes by itself)");
                return;
            }
            string? maximize = Environment.GetEnvironmentVariable("FSA_HOOK_MAXIMIZE");
            bool maximized = !string.IsNullOrEmpty(maximize);
            if (maximized && (maximize != "1" || sizeFirst || mode is not ("watch" or "expand" or "size")))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_MAXIMIZE is 1, for watch, expand and size only, and never with FSA_HOOK_MINSIZE");
                return;
            }
            if (!Delay("FSA_HOOK_DRIVES", out int? extraDrives) || (extraDrives is not null && mode is not ("watch" or "expand" or "kinds" or "size")))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_DRIVES is a whole number, and for watch, expand, kinds and size only");
                return;
            }
            // Size to the minimum, maximize, add drive buttons, or both, before the window loads.
            bool prepare = sizeFirst || maximized || extraDrives is not null;
            if (!Delay("FSA_HOOK_SLOW_REFRESH_MS", out int? pad))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_SLOW_REFRESH_MS is not a whole number of milliseconds");
                return;
            }
            string? marker = Environment.GetEnvironmentVariable("FSA_HOOK_ARMED");
            string? expandAfter = Environment.GetEnvironmentVariable("FSA_HOOK_EXPAND");
            if (!string.IsNullOrEmpty(expandAfter) && mode != "tree" && (mode != "watch" || expandAfter != "1"))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_EXPAND is 1 for watch, or a folder's path for tree");
                return;
            }
            string? back = Environment.GetEnvironmentVariable("FSA_HOOK_BACK");
            if (!string.IsNullOrEmpty(back) && (back != "1" || !(mode == "expand" || (mode == "watch" && expandAfter == "1"))))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_BACK is 1, for expand, and watch with FSA_HOOK_EXPAND");
                return;
            }
            string? readable = Environment.GetEnvironmentVariable("FSA_HOOK_READABLE") is { Length: > 0 } gone ? gone : null;
            string? before = Environment.GetEnvironmentVariable("FSA_HOOK_BEFORE") is { Length: > 0 } first ? first : null;
            string? after = Environment.GetEnvironmentVariable("FSA_HOOK_AFTER") is { Length: > 0 } last ? last : null;
            if ((readable ?? before ?? after) is not null && mode != "rescan")
            {
                Log($"refused mode={mode} reason=FSA_HOOK_READABLE, FSA_HOOK_BEFORE and FSA_HOOK_AFTER are for rescan only");
                return;
            }
            if (after is not null && readable is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FSA_HOOK_MISSING")))
            {
                Log($"refused mode={mode} reason=FSA_HOOK_AFTER needs the report held until the second ending (FSA_HOOK_READABLE or FSA_HOOK_MISSING)");
                return;
            }
            if (mode == "tree")
            {
                if (!string.IsNullOrEmpty(marker) || pad is not null)
                    Log("refused mode=tree reason=tree mode is not armed and takes no FSA_HOOK_SLOW_REFRESH_MS");
                else
                    TreeMode.Drive();
                return;
            }
            if (mode == "reveal")
            {
                RevealMode.Drive();
                return;
            }
            if (mode is "watch" or "measure")
            {
                if (!string.IsNullOrEmpty(marker))
                {
                    Log($"refused mode={mode} reason={mode} mode is not armed");
                    return;
                }
                if (prepare && !PrepareWindow(mode, sizeFirst, maximized, extraDrives ?? 0))
                    return;
                // Before the window loads, as expand mode does: the Cancel's ending then opens the list.
                if (!string.IsNullOrEmpty(expandAfter))
                    DriveAfterList("expand", null);
                WatchList(mode, pad ?? 0);
                return;
            }
            if (pad is not null)
            {
                Log($"refused mode={mode} reason=FSA_HOOK_SLOW_REFRESH_MS is for watch and measure only");
                return;
            }
            if (Renamed is not null)
            {
                Log($"refused mode={mode} reason=FSA_HOOK_RENAMED is for watch and measure only");
                return;
            }
            if (mode == "size")
            {
                if (!string.IsNullOrEmpty(marker))
                    Log("refused mode=size reason=size mode is not armed");
                else
                    PrepareWindow(mode, !maximized, maximized, extraDrives ?? 0);
                return;
            }
            if (mode == "settings")
            {
                string? answer = Environment.GetEnvironmentVariable("FSA_HOOK_ANSWER");
                if (answer is not ("yes" or "no") || !string.IsNullOrEmpty(marker) || prepare)
                    Log("refused mode=settings reason=FSA_HOOK_ANSWER is yes or no, and settings mode is not armed and does not size the window");
                else
                    SettingsMode.Drive(answer);
                return;
            }
            if (mode is "rescan" or "expand" or "hide" or "kinds")
            {
                string? missing = Environment.GetEnvironmentVariable("FSA_HOOK_MISSING") is { Length: > 0 } folder ? folder : null;
                if (!string.IsNullOrEmpty(marker))
                    Log($"refused mode={mode} reason={mode} mode is not armed");
                else if (mode != "rescan" && missing is not null)
                    Log($"refused mode={mode} reason=FSA_HOOK_MISSING is for rescan only");
                else if (!prepare || PrepareWindow(mode, sizeFirst, maximized, extraDrives ?? 0))
                    DriveAfterList(mode, missing);
                return;
            }
            if (mode is "stop" or "close" && ProgressTrigger.FromEnvironment(out string? triggerError) is var trigger
                && (trigger is not null || triggerError is not null))
            {
                if (triggerError is not null || !string.IsNullOrEmpty(marker))
                    Log($"refused mode={mode} reason={triggerError ?? "a progress trigger is not armed by a marker"}");
                else
                    DriveAtProgress(mode, trigger!);
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

    // SL-095's cancelled runs, at a point of the scan's own progress rather than a clock: stop presses Cancel, close
    // closes the window, once the trigger is met and while the scan still runs. Read off the window as a person sees it:
    // CountersText's folder count, and CurrentFolderText, the folder being listed or, once every folder is, the files
    // sized so far. The hook holds no path of its own: FSA_HOOK_PAST names it. A scan that ends first is logged as
    // "refused ... ended before the trigger", and the run counts as a full one.
    private sealed record ProgressTrigger(int? AtFolders, string? Past, int PastFolders, int? AtSized, TimeSpan Deadline)
    {
        public static ProgressTrigger? FromEnvironment(out string? error)
        {
            error = null;
            int? atFolders = Number("FSA_HOOK_AT_FOLDERS", ref error);
            int? pastFolders = Number("FSA_HOOK_PAST_FOLDERS", ref error);
            int? atSized = Number("FSA_HOOK_AT_SIZED", ref error);
            int? deadline = Number("FSA_HOOK_TRIGGER_DEADLINE_S", ref error);
            string? past = Environment.GetEnvironmentVariable("FSA_HOOK_PAST") is { Length: > 0 } p ? p.TrimEnd('\\') : null;
            if (error is not null)
                return null;
            int set = (atFolders is null ? 0 : 1) + (past is null ? 0 : 1) + (atSized is null ? 0 : 1);
            if (set == 0)
            {
                if (pastFolders is not null)
                    error = "FSA_HOOK_PAST_FOLDERS needs FSA_HOOK_PAST";
                return null;
            }
            if (set > 1)
            {
                error = "set one of FSA_HOOK_AT_FOLDERS, FSA_HOOK_PAST and FSA_HOOK_AT_SIZED";
                return null;
            }
            return new ProgressTrigger(atFolders, past, pastFolders ?? 0, atSized, TimeSpan.FromSeconds(deadline ?? 3600));
        }

        private static int? Number(string name, ref string? error)
        {
            string? text = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(text))
                return null;
            if (int.TryParse(text, out int n) && n >= 0)
                return n;
            error ??= $"{name} is not a whole number";
            return null;
        }

        public string Describe() =>
            AtFolders is int f ? $"atfolders={f}" : Past is not null ? $"past={Past} pastfolders={PastFolders}" : $"atsized={AtSized}";
    }

    // Whether the walk, depth-first in listing order, has gone past <past> without being in it: at the first path
    // component where they differ, the folder's sorts after the store's. Never true for a folder at or under it.
    private static bool HasPassed(string folder, string past)
    {
        string[] a = folder.TrimEnd('\\').Split('\\'), b = past.Split('\\');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int order = string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
            if (order != 0)
                return order > 0;
        }
        return false; // one begins the other: an ancestor of the store, or the store or below it
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DriveAtProgress(string mode, ProgressTrigger trigger)
    {
        if (WaitForApplication(mode) is not { } app)
            return;
        Log($"trigger mode={mode} {trigger.Describe()}");
        bool scanSeen = false;
        int? passedAt = null;
        while (Clock.Elapsed < trigger.Deadline)
        {
            string? outcome = null;
            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    Window? window = app.MainWindow;
                    if (window?.FindName("ScanningPanel") is not UIElement panel)
                        return;
                    if (!panel.IsVisible)
                    {
                        if (scanSeen)
                            outcome = "ended";
                        return;
                    }
                    scanSeen = true;
                    string counters = (window.FindName("CountersText") as TextBlock)?.Text ?? "";
                    string current = (window.FindName("CurrentFolderText") as TextBlock)?.Text ?? "";
                    int folders = CountersReading.FoldersIn(counters);
                    const string Sizing = "Reading each file's own size: ";
                    int? sized = current.StartsWith(Sizing, StringComparison.Ordinal) ? LeadingNumber(current[Sizing.Length..]) : null;
                    if (trigger.Past is { } past && passedAt is null && sized is null && HasPassed(current, past))
                    {
                        passedAt = folders;
                        Log($"passed folders={folders} current={current} ms={Clock.ElapsedMilliseconds}");
                    }
                    // A folder trigger the listing outran still acts, in the sizing pass: the log's sized= says which.
                    bool due = trigger.AtFolders is int at ? folders >= at
                        : trigger.AtSized is int atSized ? sized >= atSized
                        : passedAt is int from && folders >= from + trigger.PastFolders;
                    if (!due)
                        return;
                    var button = window.FindName("CancelButton") as FrameworkElement;
                    if (mode == "stop" && (button is null || !Pressable(button)))
                        return;
                    Log($"acted mode={mode} {trigger.Describe()} folders={folders} sized={sized?.ToString() ?? "-"} current={current} ms={Clock.ElapsedMilliseconds} utc={Utc()}");
                    if (mode == "close")
                        window.Closed += (_, _) => Log($"closed ms={Clock.ElapsedMilliseconds} utc={Utc()}");
                    Pressing(app, mode, () => Act(mode, app, mode == "stop" ? button : null));
                    outcome = "acted";
                }, DispatcherPriority.Normal);
            }
            catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted)
            {
                Log($"refused mode={mode} reason=the app shut down before the trigger");
                return;
            }
            if (outcome == "acted")
                return;
            if (outcome == "ended")
            {
                Log($"refused mode={mode} reason=the scan ended before the trigger {trigger.Describe()}; a full run");
                return;
            }
            Thread.Sleep(ProbeEvery);
        }
        Log($"refused mode={mode} reason=the trigger {trigger.Describe()} was not met within {trigger.Deadline.TotalSeconds:N0} s");
    }

    private static int? LeadingNumber(string text)
    {
        string digits = new(text.TakeWhile(c => char.IsAsciiDigit(c) || c is ',' or '.' or ' ' or ' ').Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : null;
    }

    // size, FSA_HOOK_MINSIZE and FSA_HOOK_DRIVES (SL-040, O-027, the window-size rework and O-028): the window at
    // its own MinWidth by MinHeight, the smallest a person can make it, and extra drive buttons. A class handler,
    // registered before the window loads, runs ahead of the window's own Loaded handler, which starts the scan,
    // so every ending is laid out at that size. Only size mode waits on the UI thread to see whether it
    // registered in time: that wait lasts until the window has loaded, so ahead of watch or expand it would make
    // their own handlers late. There a missing "sized" or "drives in" line is the sign.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PrepareWindow(string mode, bool minimum, bool maximize, int extraDrives)
    {
        if (WaitForApplication(mode) is not { } app)
            return false;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var window = (Window)sender;
            if (_sized || !ReferenceEquals(window, app.MainWindow))
                return;
            _sized = true;
            if (minimum)
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                string size = string.Create(CultureInfo.InvariantCulture,
                    $"width={window.Width:F0} minWidth={window.MinWidth:F0} height={window.Height:F0} minHeight={window.MinHeight:F0}");
                Log($"sized {size} ms={Clock.ElapsedMilliseconds}");
                if (mode == "size")
                    Log($"acted mode=size {size} ms={Clock.ElapsedMilliseconds}");
            }
            if (maximize)
            {
                window.WindowState = System.Windows.WindowState.Maximized;
                Log($"maximized ms={Clock.ElapsedMilliseconds}");
                if (mode == "size")
                    Log($"acted mode=size maximized=True ms={Clock.ElapsedMilliseconds}");
            }
            // The drive buttons come in after the window loads. So once the ending is on screen the report is held
            // until they are in (DriveStatus has changed from what it shows at load), and the extra ones are added
            // beside them then: the report reads the pickers with every drive in.
            if (window.FindName("OutcomePanel") is not UIElement outcome || window.FindName("DriveStatus") is not TextBlock status
                || window.FindName("DrivePanel") is not Panel drives)
            {
                Log($"refused mode={mode} reason=the window has no OutcomePanel, DriveStatus or DrivePanel");
                return;
            }
            string loading = status.Text;
            bool held = false;
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, outcome.GetType()).AddValueChanged(outcome, (_, _) =>
            {
                if (held || outcome.Visibility != Visibility.Visible)
                    return;
                held = true;
                app.Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    new Action(() => HoldForDrives(app, mode, status, loading, drives, extraDrives, Stopwatch.StartNew())));
            });
        }));
        if (mode == "size" && app.Dispatcher.Invoke(() => !_sized && app.MainWindow is { IsLoaded: true }))
        {
            Log($"refused mode={mode} reason=the window loaded before the hook could size it");
            return false;
        }
        return true;
    }

    // How much of the status line's text shows (SL-091, review n4): the text's own width, untrimmed, measured as the line
    // draws it, and how many of its characters come before the ellipsis in the width the line was given. Its DesiredSize
    // says neither: WPF clips it to the room given, and a trimmed line measures its trimmed text.
    internal static (double TextWidth, int Fits) StatusFit(TextBlock line)
    {
        var typeface = new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(line).PixelsPerDip;
        double Width(string text) => new FormattedText(text, CultureInfo.CurrentUICulture, line.FlowDirection, typeface, line.FontSize,
            Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;
        string all = line.Text;
        double full = Width(all);
        if (full <= line.ActualWidth)
            return (full, all.Length);
        int low = 0, high = all.Length;
        while (low < high)
        {
            int n = (low + high + 1) / 2;
            if (Width(all[..n] + "…") <= line.ActualWidth)
                low = n;
            else
                high = n - 1;
        }
        return (full, low);
    }

    // As HoldUntilEnded: an item of Render priority kept queued holds the report, at Loaded, while the drives'
    // continuation, at Normal, and the layout, at Render, still run.
    private static void HoldForDrives(Application app, string mode, TextBlock status, string loading, Panel drives, int extra, Stopwatch since)
    {
        if (status.Text != loading)
        {
            string census = LabelCensus(drives);
            AddDrives(drives, extra);
            string? hookStatus = Environment.GetEnvironmentVariable("FSA_HOOK_STATUS");
            string statusInfo = "";
            if (!string.IsNullOrEmpty(hookStatus) && app.MainWindow?.FindName("RevealStatus") is TextBlock revealTb)
            {
                revealTb.Text = hookStatus;
                revealTb.Visibility = Visibility.Visible;
                revealTb.UpdateLayout();
                (double textWidth, int fits) = StatusFit(revealTb);
                statusInfo = string.Create(CultureInfo.InvariantCulture,
                    $" status={revealTb.Visibility == Visibility.Visible} statusHeight={revealTb.ActualHeight:F2} statusTextWidth={textWidth:F2} statusWidth={revealTb.ActualWidth:F2} statusFits={fits} statusChars={hookStatus.Length}");
            }
            Window window = app.MainWindow!;
            string laidOut = string.Create(CultureInfo.InvariantCulture,
                $"windowWidth={window.ActualWidth:F0} windowHeight={window.ActualHeight:F0} state={window.WindowState}");
            Log($"drives in ms={Clock.ElapsedMilliseconds} held={since.ElapsedMilliseconds} added={extra} buttons={drives.Children.Count}{census} {laidOut}{statusInfo}");
            return;
        }
        if (since.Elapsed > Deadline)
        {
            Log($"refused mode={mode} reason=the drive buttons did not come in within {Deadline.TotalSeconds:N0} s");
            return;
        }
        Thread.Sleep(1);
        app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => HoldForDrives(app, mode, status, loading, drives, extra, since)));
    }

    // What the app's own drive buttons hold, counted before any extra is added (SC-050, review M1): " labels=a/b ink=c/d", over b
    // real buttons, a of which have a TextBlock with text as their Content (a string would be read for access keys by the
    // button's presenter: '_' in a volume label drew as nothing and made a key), and c of which draw that label in the colour the
    // template gives a disabled button, as they are for the whole of a scan. For that each button is disabled for the reading
    // and put back: the label's Foreground must then be its ContentPresenter's TextElement.Foreground, and not what it was while
    // enabled. A label with a Foreground of its own, or one bound to an ancestor that is not its presenter, stays black on a greyed
    // button and reads as not c. The one layout pass puts each label in its presenter, as the report's own would a moment later.
    private static string LabelCensus(Panel drives)
    {
        drives.UpdateLayout();
        int real = 0, labels = 0, ink = 0;
        foreach (Button button in drives.Children.OfType<Button>())
        {
            real++;
            if (button.Content is not TextBlock { Text.Length: > 0 } label)
                continue;
            labels++;
            if (GreysWithItsButton(button, label))
                ink++;
        }
        return $" labels={labels}/{real} ink={ink}/{real}";
    }

    private static bool GreysWithItsButton(Button button, TextBlock label)
    {
        bool wasEnabled = button.IsEnabled;
        Brush? enabledInk = label.Foreground;
        try
        {
            button.IsEnabled = false;
            button.UpdateLayout();
            return VisualTreeHelper.GetParent(label) is ContentPresenter presenter
                && label.Foreground is SolidColorBrush disabledInk
                && System.Windows.Documents.TextElement.GetForeground(presenter) is SolidColorBrush templateInk
                && disabledInk.Color == templateInk.Color
                && !(wasEnabled && enabledInk is SolidColorBrush before && before.Color == disabledInk.Color);
        }
        finally
        {
            button.IsEnabled = wasEnabled;
        }
    }
    // Drive buttons shaped like the app's own (LoadDrives): the first real one's padding, margin and alignment,
    // and the same two lines of text. They are never clicked; they only take the room a ready drive would.
    private static void AddDrives(Panel drives, int extra)
    {
        Button? model = drives.Children.OfType<Button>().FirstOrDefault();
        for (int i = 0; i < extra; i++)
        {
            drives.Children.Add(new Button
            {
                Content = new TextBlock { Text = $"X{i}:  Extra drive (NTFS)\n1.0 GB free of 2.0 GB" },
                Padding = model?.Padding ?? new Thickness(12, 6, 12, 6),
                Margin = model?.Margin ?? new Thickness(0, 0, 8, 0),
                HorizontalContentAlignment = model?.HorizontalContentAlignment ?? HorizontalAlignment.Left,
                IsEnabled = model?.IsEnabled ?? true,
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
            if (mode == "hide")
            {
                Hide(app, window, rows);
                return;
            }
            if (mode == "kinds")
            {
                ChooseKinds(app, window, rows);
                return;
            }
            var chooser = window.FindName("ChooseFolderButton") as FrameworkElement;
            if (rows == 0 || Shown(window, "ScanningPanel") || chooser is null || !Pressable(chooser))
            {
                Log($"refused mode=rescan reason=no list on screen with a new scan startable (rows={rows} chooser={chooser is not null && Pressable(chooser)})");
                return;
            }
            if (Environment.GetEnvironmentVariable("FSA_HOOK_BEFORE") is { Length: > 0 } before && !SelectTab(app, window, "before", before))
                return;
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

    // expand (SL-040, O-024; SL-065): selects the unreadable folders' tab as a click on its header would, once the
    // ending shows it beside the largest files. Its layout runs at Render, still ahead of the report, so the report
    // reads their list on screen. With FSA_HOOK_BACK=1 the list is laid out, and then the first tab selected again.
    private static void Expand(Application app, Window window, int rows)
    {
        var tab = window.FindName("UnreadableTab") as TabItem;
        int unreadable = window.FindName("UnreadableList") is ItemsControl folders ? folders.Items.Count : 0;
        if (rows == 0 || unreadable == 0 || tab is null || !Pressable(tab) || tab.IsSelected)
        {
            Log($"refused mode=expand reason=no list of largest files beside a pressable, unselected tab of unreadable folders (rows={rows} unreadable={unreadable} pressable={tab is not null && Pressable(tab)} selected={tab?.IsSelected})");
            return;
        }
        Log($"acted mode=expand rows={rows} unreadable={unreadable} tabsHeight={Px(window, "OutcomeTabs")} ms={Clock.ElapsedMilliseconds}");
        Pressing(app, "expand", () => tab.IsSelected = true); // what a click on its header sets
        Log($"expanded={tab.IsSelected} {State(window)}");
        if (Environment.GetEnvironmentVariable("FSA_HOOK_BACK") != "1")
            return;
        window.UpdateLayout(); // the list on screen, as a person would see it before clicking back
        Log($"shown unreadableHeight={Px(window, "UnreadableList")} unreadableRows={Realized(window, "UnreadableList")}");
        if (window.FindName("OutcomeTabs") is not TabControl tabs
            || tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible) is not { } first || ReferenceEquals(first, tab))
        {
            Log("refused mode=expand reason=no other tab to go back to");
            return;
        }
        Pressing(app, "back", () => first.IsSelected = true);
        Log($"back selected={first.Name} {State(window)}");
    }

    // hide (SL-065): the largest files' tab collapsed as a section whose own condition turned false would, and then
    // the window's own guard, as the window calls it after every such change. It must select the unreadable folders,
    // the first section still shown, and not the collapsed tab a guard naming the largest files would.
    private static void Hide(Application app, Window window, int rows)
    {
        var tabs = window.FindName("OutcomeTabs") as TabControl;
        var largest = window.FindName("LargestFilesTab") as TabItem;
        var unreadable = window.FindName("UnreadableTab") as TabItem;
        MethodInfo? guard = window.GetType().GetMethod("EnsureSelectedTabIsVisible", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (tabs is null || largest is null || unreadable is null || guard is null || !largest.IsSelected || !Pressable(unreadable))
        {
            Log($"refused mode=hide reason=no selected tab of largest files beside a shown one of unreadable folders, or no guard (largest={largest?.IsSelected} unreadable={unreadable is not null && Pressable(unreadable)} guard={guard is not null})");
            return;
        }
        Log($"acted mode=hide rows={rows} ms={Clock.ElapsedMilliseconds}");
        Pressing(app, "hide", () =>
        {
            largest.Visibility = Visibility.Collapsed;
            guard.Invoke(window, null);
        });
        Log($"hidden selected={(tabs.SelectedItem as TabItem)?.Name} tabs={tabs.Visibility} {State(window)}");
    }

    // rescan's FSA_HOOK_BEFORE and FSA_HOOK_AFTER: the named tab selected as a click on its header would, if a person
    // could click it, and the window laid out, so its section is on screen.
    private static bool SelectTab(Application app, Window window, string when, string name)
    {
        if (window.FindName(name) is not TabItem tab || !Pressable(tab))
        {
            Log($"refused mode=rescan reason=no pressable tab {name} to select {when} the second scan");
            return false;
        }
        Pressing(app, when, () => tab.IsSelected = true);
        window.UpdateLayout();
        Log($"{when} selected={((window.FindName("OutcomeTabs") as TabControl)?.SelectedItem as TabItem)?.Name} unreadableHeight={Px(window, "UnreadableList")} unreadableRows={Realized(window, "UnreadableList")} {State(window)}");
        return true;
    }

    // rescan's FSA_HOOK_READABLE: an unreadable folder of the first scan deleted, so the second scan does not meet it.
    // Only under %TEMP%, so a mistyped variable cannot delete anything of the person's, and only if empty.
    private static bool DeleteReadable(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\');
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (!full.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
        {
            Log($"refused mode=rescan reason=FSA_HOOK_READABLE is not a folder under {temp}: {full}");
            return false;
        }
        Directory.Delete(full, recursive: false);
        Log($"readable deleted={full}");
        return true;
    }

    private static string Px(Window window, string name) =>
        window.FindName(name) is FrameworkElement element ? element.ActualHeight.ToString("F2", CultureInfo.InvariantCulture) : "-";

    // The rows the list has built: containers, with virtualization about a screenful.
    private static int Realized(Window window, string name) =>
        window.FindName(name) is ItemsControl list
            ? Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null)
            : 0;

    // kinds (SL-046): chooses the files by kind on the results cell's header line as a click would, once the ending is
    // on screen. Its layout runs at Render, still ahead of the report, so the report reads the kinds shown.
    private static void ChooseKinds(Application app, Window window, int rows)
    {
        var choice = window.FindName("ShowKindsButton") as ToggleButton;
        if (choice is null || !Pressable(choice) || choice.IsChecked == true)
        {
            Log($"refused mode=kinds reason=no pressable, unchosen choice of the files by kind (pressable={choice is not null && Pressable(choice)} checked={choice?.IsChecked})");
            return;
        }
        Log($"acted mode=kinds rows={rows} ms={Clock.ElapsedMilliseconds}");
        Pressing(app, "kinds", () => choice.IsChecked = true); // what a click on it sets
        Log($"kinds={choice.IsChecked} {State(window)}");
    }

    private static void StartSecondScan(Application app, Window window, string? missing, int rows)
    {
        try
        {
            if (missing is not null && !MoveAside(missing))
                return;
            string? readable = Environment.GetEnvironmentVariable("FSA_HOOK_READABLE") is { Length: > 0 } folder ? folder : null;
            if (readable is not null && !DeleteReadable(readable))
                return;
            Log($"acted mode=rescan rows={rows} ms={Clock.ElapsedMilliseconds}");
            Pressing(app, "rescan", () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window)));
            Log($"started {State(window)}");
            if (missing is not null || readable is not null)
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
            if (Environment.GetEnvironmentVariable("FSA_HOOK_AFTER") is { Length: > 0 } after)
                SelectTab(app, window, "after", after);
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
                                try
                                {
                                    seen.RefreshMs.Add(WindowState<TimeSpan>(window, "LastRefresh").TotalMilliseconds);
                                    seen.Containers = Math.Max(seen.Containers, Containers(found));
                                }
                                catch (Exception e)
                                {
                                    // Never onto the dispatcher: the app would take it for its own fault and exit 70.
                                    Log($"refused mode={mode} reason={e.GetType().Name}: {e.Message}");
                                }
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
                    string height = list.ActualHeight.ToString("F1", CultureInfo.InvariantCulture);
                    last = $"rows={rows} folderBytes={folderBytes} notRead={notRead} containers={seen.Containers} skipped={skipped} armed={armed} running={running} listHeight={height}";
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
                try
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
                }
                catch (Exception e)
                {
                    // Never into the app's StartScan, whose finally collapses ScanningPanel: it would take it for its own
                    // fault and exit 70.
                    Log($"refused mode=measure reason={e.GetType().Name}: {e.Message}");
                }
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

    // The window's refresh state (MainWindow's internal LastRefresh, SkippedRefreshes, SkipsNextRefresh). The one
    // FSA_HOOK_RENAMED names is read as if the app had renamed it.
    private static T WindowState<T>(Window window, string property) =>
        (T)((property == Renamed ? null : window.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))?.GetValue(window)
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

    internal static Application? WaitForApplication(string mode)
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
                RowsMode.StopUtc = Utc();
                StartStoppingLog(app);
                // What a click raises: reaches OnCancel through the Click wiring in MainWindow.xaml.
                cancelButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "close":
                app.MainWindow!.Close();
                break;
            case "throw":
                // What the window shows as the app shuts down on the fault: a scan that failed shows no folder
                // tree (SL-044). Found now: by then the window may be gone from the app.
                var tree = app.MainWindow?.FindName("FolderTree") as ItemsControl;
                app.Dispatcher.ShutdownStarted += (_, _) =>
                    Log($"shutdown tree={tree is { Visibility: Visibility.Visible }} treeItems={tree?.Items.Count ?? -1} ms={Clock.ElapsedMilliseconds}");
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

    internal static void Log(string line)
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
