using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// FSA_HOOK_MODE=tree (SL-044): reads the folder tree a completed scan shows, and opens, scrolls, selects and
/// starts scans in it as a person would, from inside the app's process. Its evidence is what the tree's rows
/// draw, not what the rows hold: a wrong binding in the template draws a blank or wrong text that the row's own
/// properties would not show.
/// </summary>
/// <remarks>
/// <code>
///   FSA_HOOK_MODE=tree  FSA_HOOK_LOG=&lt;file&gt;  [FSA_HOOK_HEIGHT=&lt;px&gt;]
///   [FSA_HOOK_WALK=&lt;depth, default 4; 0 reads nothing&gt;]  [FSA_HOOK_RECYCLE=&lt;folder&gt;]  [FSA_HOOK_EXPAND=&lt;folder&gt;]
///   [FSA_HOOK_MULTIPLY=1]  [FSA_HOOK_SELECT=&lt;row&gt; FSA_HOOK_RELEASE=&lt;second root&gt;]  [FSA_HOOK_GAP=&lt;folder&gt;]
/// </code>
/// Paths are relative to the scan root, with backslashes, as the rows' names join; the root row's path is "".
/// For a launch without --report (the app then stays open), because the report ends the app at its ending. It
/// waits up to 120 s for the ending, then runs the steps below in this order, and closes the window: the app
/// exits as a closed window does. Every line it logs is one JSON object with a "kind"; a step that cannot go
/// on logs {"kind":"error","reason":...} and the window is closed. FSA_HOOK_HEIGHT makes the window that tall (at
/// most the work area) as the window is made, before it has a window handle, and so before its scan: a "sized" line,
/// with the height the window was laid out at ("actual"). A height within the work area that the window does not take (under its MinHeight) is refused,
/// "height not applied (asked/actual)", so a run meant for one height never runs at another (SL-080).
/// <list type="bullet">
/// <item>ending: how the scan ended and whether the tree is what is shown, the tree's viewport, and where the
/// window is against the work area.</item>
/// <item>WALK: every row breadth-first down to that depth, opening each folder with 1 to 1,000 children on the
/// way. For each row, the row is brought into view through its parent's items panel
/// (VirtualizingPanel.BringIndexIntoViewPublic), a Loaded-priority pass lets layout run, and then its container
/// is read before the next row is brought in, because recycling hands containers on (review M3). A
/// "tree-row" line has the row's own values and "rendered": what its container draws, or null when no
/// container could be had.</item>
/// <item>RECYCLE: opens that folder, opens its third child from the end, scrolls the tree to its end and back
/// twice, closes and reopens the folder, then reads every child as drawn: "recycle-row" lines and one
/// "recycle" line (review M4).</item>
/// <item>EXPAND: opens that folder, a child of the root at any depth but never the root, five times, closing it
/// between: an "expand" line each, timed from setting IsExpanded on its container to the first ContextIdle
/// callback at which its first child's container exists. Open 1 is cold (its rows are built then) and logs how
/// long Core's sort and the row build took (the window's LastFolderSortMs and LastFolderBuildMs); "layoutMs" is
/// what the rest took. It is left open (review M5, m7).</item>
/// <item>MULTIPLY: closes every folder but the root, selects and focuses the root row, and sends it the numpad
/// * key as the keyboard does: PreviewKeyDown, then KeyDown if nothing handled it. A "multiply" line says which
/// folders are open afterwards (review M1).</item>
/// <item>SELECT and RELEASE: selects and focuses that row, takes a weak reference to the ending's store, starts
/// a scan of the second root through the window's own StartScan (what a drive button and Choose folder call),
/// and after a ContextIdle pass collects garbage and logs whether the first store is gone ("store-released"),
/// and again after the second ending, with the second tree's root ("second-ending") (review M2).</item>
/// <item>GAP: an Input-priority probe every 10 ms from the hook's start, the window moved and resized every
/// 50 ms while a scan runs; after the ending, 1 s of hand-off, that folder opened once, then a second scan of
/// the same root over the open tree, to its ending and 1 s beyond. One "gap" line a phase (scan, handoff, open,
/// rescan, rescan-handoff) with how many probes it had, their p95 and max wait and when the longest was posted
/// (review m7) and got through ("maxEndAt"); a "rescan-started" line with how long the window's StartScan held the
/// UI thread; and on each ending line, when the first move came and the longest a move took. Every 10 ms, so that
/// a phase of a second has enough probes for its p95 to be something other than its longest wait (the code
/// review's M1). First, a "facts" line (SL-080): the UI thread's OS id, the process's priority class, the runtime,
/// and when the hook's clock started, in UTC and in ms since the process started, so that a trace of the run can
/// be read on the hook's clock.</item>
/// </list>
/// </remarks>
internal static class TreeMode
{
    private static readonly TimeSpan EndingDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    private const int WalkLimit = 1000;
    private const int Opens = 5;

    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static Application _app = null!;

    // Whether this run stopped the scan and chose the stopped tree (FSA_HOOK_TREE_STOP, SL-110): Release then reads the
    // radio buttons and the second tree after the second scan, which a completed run has no reason to.
    private static bool _stoppedRun;

    /// <summary>A step that cannot go on, in words for the log.</summary>
    private sealed class Refusal(string reason) : Exception(reason);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Drive()
    {
        if (StartupHook.WaitForApplication("tree") is not { } app)
            return;
        _app = app;
        Window? window = null;
        Probe? probe = null;
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--report"))
                throw new Refusal("tree mode reads a window that stays open: launch the app without --report");
            int walk = Number("FSA_HOOK_WALK") ?? 4;
            string? recycle = Text("FSA_HOOK_RECYCLE");
            string? expand = Text("FSA_HOOK_EXPAND");
            bool multiply = Text("FSA_HOOK_MULTIPLY") switch
            {
                null => false,
                "1" => true,
                _ => throw new Refusal("FSA_HOOK_MULTIPLY is 1 or unset"),
            };
            string? select = Text("FSA_HOOK_SELECT");
            string? release = Text("FSA_HOOK_RELEASE");
            string? gap = Text("FSA_HOOK_GAP");
            string? canReveal = Text("FSA_HOOK_CAN_REVEAL");
            string? views = Text("FSA_HOOK_VIEWS");
            if (release is not null && select is null)
                throw new Refusal("FSA_HOOK_RELEASE needs FSA_HOOK_SELECT: the row a person had selected before picking a new folder");

            int? height = Number("FSA_HOOK_HEIGHT"); // applied by BeforeMain's watch, before the window had a handle
            // SL-110: the stop is armed before the window is waited for, because the window's Loaded handler starts the scan.
            StoppedTree.Run? stop = StoppedTree.Arm(app);
            _stoppedRun = stop is not null;
            window = WaitForWindow();
            if (height is int wanted)
                Sized(window, wanted);
            if (gap is not null)
            {
                Facts();
                probe = Probe.Start(app);
            }
            WaitForEnding(window, "ending", probe, first: true, stopped: stop is not null);
            if (stop is not null)
            {
                // The stop must have landed, and the ending be the stopped one; then what the stopped list and panel and store
                // hold, before the tree is chosen (which swaps the list out).
                stop.Join();
                if (!stop.Pressed)
                    throw new Refusal($"the stop did not land ({stop.Reason}): this run is void");
                StoppedTree.LogEnding(app, window);
            }
            if (views is not null)
                StoppedTree.Views(app, window, views);
            else if (stop is not null)
                StoppedTree.Choose(app, window);
            if ((stop is not null || views is not null) && !Ui(() => Tree(window).Visibility == Visibility.Visible && Tree(window).Items.Count == 1))
                throw new Refusal("the ending shows no folder tree: end FSA_HOOK_VIEWS on tree for a stopped ending, or on folders for a completed one");
            if (probe is not null)
                probe.Phase("handoff", TimeSpan.FromSeconds(1));
            if (walk > 0)
                Walk(window, walk);
            if (recycle is not null)
                Recycle(window, recycle);
            if (expand is not null)
                ExpandTimed(window, expand);
            if (multiply)
                Multiply(window);
            if (canReveal is not null)
                CanReveal(window, canReveal);
            if (select is not null)
                Select(window, select);
            if (release is not null)
                Release(window, release);
            if (gap is not null && probe is not null)
                Gap(window, gap, probe);
        }
        catch (Refusal refusal)
        {
            Emit("error", new() { ["reason"] = refusal.Message });
        }
        catch (Exception e)
        {
            Emit("error", new() { ["reason"] = $"{e.GetType().Name}: {e.Message}" });
        }
        finally
        {
            probe?.Stop();
            Close(window);
        }
    }

    // ---- The ending ----

    private static Window WaitForWindow()
    {
        while (StartupHook.Clock.Elapsed < EndingDeadline)
        {
            if (Ui(() => _app.MainWindow is { IsLoaded: true } ? _app.MainWindow : null) is { } window)
                return window;
            Thread.Sleep(Poll);
        }
        throw new Refusal("the main window did not load");
    }

    // FSA_HOOK_HEIGHT: the window made that tall, or as tall as the work area allows, at the top of the screen, so
    // the tree has the room a person who enlarges the window gives it. It is set as the window is made, once its XAML
    // has set its own Height and before it has a window handle: the handle is then created at that height, as it is at
    // the XAML default, and neither arm of a Height batch is ever resized before the hook's first move. Both ways of
    // setting it later were measured to make the arms differ in more than their height (SL-080 code review M3): in a
    // Loaded class handler the window was already on screen, and the set did nothing at the 860 px default while it
    // resized the window at any other height; in SourceInitialized, before the window is shown, a window resized from
    // 860 to 850 px still took about 140 ms less at the scan's first move than one left at 860, because its first
    // resize had already been paid. The set is made in the window's Initialized, raised at the end of its XAML, or at
    // once when that has passed and the window has no handle yet; it is reached from the first dispatcher operation the
    // UI thread posts once the window exists, which needs a watch on the main thread's dispatcher from before the app's
    // Main (BeforeMain, called by StartupHook.Initialize). Written on the UI thread, read by the hook's thread after a
    // dispatcher call.
    private static Dictionary<string, object?>? _sized;
    private static string? _unsized; // why the window could not be sized in time, if so

    internal static void BeforeMain()
    {
        if (Text("FSA_HOOK_HEIGHT") is not { } text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int height))
            return; // Drive refuses a height that is not a whole number
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher; // the main thread's, which the app's Application takes as its own
        DispatcherHookEventHandler? watch = null;
        watch = (_, _) =>
        {
            if (!dispatcher.CheckAccess() || Application.Current?.MainWindow is not { } window)
                return;
            dispatcher.Hooks.OperationPosted -= watch;
            if (!window.IsInitialized)
                window.Initialized += (_, _) => Size(window, height, "Initialized");
            else if (PresentationSource.FromVisual(window) is null)
                Size(window, height, "constructed");
            else
                _unsized = "the window had its handle before the hook could size it";
        };
        dispatcher.Hooks.OperationPosted += watch;
    }

    private static void Size(Window window, int height, string when)
    {
        window.Top = SystemParameters.WorkArea.Top;
        window.Height = Math.Min(height, SystemParameters.WorkArea.Height);
        _sized = new Dictionary<string, object?>
        {
            ["asked"] = height,
            ["height"] = window.Height,
            ["workArea"] = SystemParameters.WorkArea.Height,
            ["when"] = when, // Initialized or constructed: before the window had a handle, either way
            ["ms"] = StartupHook.Clock.ElapsedMilliseconds,
        };
    }

    // The height the window was laid out at. One the window did not take within the work area (under its MinHeight) is
    // refused (SL-080); asked past the work area, it is T-003's "as tall as it goes", and the work area's is what was
    // meant. So is a window that had its handle before the hook could size it: its handle was made at another height.
    private static void Sized(Window window, int height)
    {
        Settle(DispatcherPriority.Loaded);
        (Dictionary<string, object?>? sized, double actual) = Ui(() => (_sized, window.ActualHeight));
        if (sized is null)
            throw new Refusal(_unsized ?? "the window had its handle before the hook could size it");
        sized["actual"] = actual;
        Emit("sized", sized);
        if (height <= (double)sized["workArea"]! && Math.Abs(actual - height) > 1)
            throw new Refusal(string.Create(CultureInfo.InvariantCulture, $"height not applied ({height}/{actual})"));
    }

    // SL-080: what a trace of this run needs to be read on the hook's clock and against the right thread.
    private static void Facts() => Emit("facts", Ui(() =>
    {
        using Process self = Process.GetCurrentProcess();
        return new Dictionary<string, object?>
        {
            // The OS thread id, which a trace names threads by. Neither ManagedThreadId nor AppDomain.GetCurrentThreadId
            // (the managed id on .NET: it gave 2 at SL-080's build) is it; Thread.CurrentOSThreadId is, and is internal.
            ["uiThread"] = typeof(Thread).GetProperty("CurrentOSThreadId", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null),
            ["priorityClass"] = self.PriorityClass.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["renderMode"] = RenderOptions.ProcessRenderMode.ToString(), // SoftwareOnly under FSA_HOOK_RENDER=software
            ["renderTier"] = RenderCapability.Tier >> 16,
            ["pid"] = Environment.ProcessId,
            ["clockOriginUtc"] = StartupHook.ClockOriginUtc.ToString("o", CultureInfo.InvariantCulture),
            ["clockOriginSinceProcessStartMs"] = (StartupHook.ClockOriginUtc - self.StartTime.ToUniversalTime()).TotalMilliseconds,
        };
    }));

    // Until the scan has returned and its ending is on screen. With a probe, the window is moved and resized every
    // 50 ms while the scan runs, as a person dragging it would (row 6).
    private static void WaitForEnding(Window window, string kind, Probe? probe, bool first, bool stopped = false)
    {
        var since = Stopwatch.StartNew();
        var moved = Stopwatch.StartNew();
        int moves = 0;
        long? firstMoveAt = null;
        double longestMoveMs = 0;
        while (true)
        {
            (bool scanning, bool outcome) = Ui(() => (Shown(window, "ScanningPanel"), Shown(window, "OutcomePanel")));
            if (!scanning && outcome)
                break;
            if (since.Elapsed > EndingDeadline)
                throw new Refusal($"no ending within {EndingDeadline.TotalSeconds:N0} s");
            if (probe is not null && scanning && moved.ElapsedMilliseconds >= 50)
            {
                int step = moves++ % 2 == 0 ? 1 : -1;
                firstMoveAt ??= StartupHook.Clock.ElapsedMilliseconds;
                var moving = Stopwatch.StartNew();
                Ui(() =>
                {
                    window.Left += 12 * step;
                    window.Top += 8 * step;
                    window.Width += 24 * step;
                    window.Height += 16 * step;
                    return true;
                });
                longestMoveMs = Math.Max(longestMoveMs, moving.Elapsed.TotalMilliseconds);
                moved.Restart();
            }
            Thread.Sleep(probe is null ? Poll : TimeSpan.FromMilliseconds(10));
        }
        probe?.Mark(first ? "scan" : "rescan");
        Settle(DispatcherPriority.ContextIdle);
        Emit(kind, Ui(() =>
        {
            TreeView tree = Tree(window);
            return new Dictionary<string, object?>
            {
                ["title"] = (window.FindName("OutcomeTitle") as TextBlock)?.Text,
                ["header"] = (window.FindName("TopLevelHeader") as TextBlock)?.Text,
                ["tree"] = tree.Visibility == Visibility.Visible,
                ["treeItems"] = tree.Items.Count,
                ["root"] = tree.Items.Count > 0 ? Prop(tree.Items[0], "Name") : null,
                ["headings"] = Shown(window, "FolderTreeHeadings"),
                ["list"] = Shown(window, "TopLevelList"),
                ["listItems"] = (window.FindName("TopLevelList") as ItemsControl)?.Items.Count,
                ["treeChoice"] = Shown(window, "ShowTreeButton"), // SL-110: a stopped ending that offers the tree beside its list
                ["stopRun"] = stopped,
                ["moves"] = moves,
                ["firstMoveAt"] = firstMoveAt,
                ["longestMoveMs"] = longestMoveMs, // the longest a move and resize held the hook, on the UI thread
                ["viewportHeight"] = Find<ScrollViewer>(tree)?.ViewportHeight,
                ["windowTop"] = window.Top,
                ["windowHeight"] = window.ActualHeight,
                ["workAreaTop"] = SystemParameters.WorkArea.Top,
                ["workAreaHeight"] = SystemParameters.WorkArea.Height,
                ["ms"] = StartupHook.Clock.ElapsedMilliseconds,
            };
        }));
        // A stopped ending shows its list: the tree is the person's choice, made by the caller (StoppedTree.Choose).
        if (!stopped && !Ui(() => Tree(window).Visibility == Visibility.Visible && Tree(window).Items.Count == 1))
            throw new Refusal("the ending shows no folder tree");
    }

    // ---- WALK ----

    private static void Walk(Window window, int depthLimit)
    {
        var queue = new Queue<(int[] Indexes, string Path, int Depth, int Order)>();
        queue.Enqueue(([0], "", 0, 0));
        int rows = 0, unrendered = 0;
        while (queue.Count > 0)
        {
            (int[] indexes, string path, int depth, int order) = queue.Dequeue();
            // Bring the row in and open it when the walk goes into it; then let layout run before it is read.
            Ui(() =>
            {
                if (Resolve(window, indexes) is { } item && depth < depthLimit && !item.IsExpanded
                    && (int)(Prop(item.DataContext, "ChildCount") ?? 0) is > 0 and <= WalkLimit)
                    item.SetCurrentValue(TreeViewItem.IsExpandedProperty, true);
                return true;
            });
            Settle(DispatcherPriority.Loaded);
            (Dictionary<string, object?> line, List<string> children) = Ui(() =>
            {
                TreeViewItem? item = Resolve(window, indexes);
                object? node = item?.DataContext ?? NodeAt(window, indexes);
                Dictionary<string, object?> fields = NodeFields(node);
                fields["depth"] = depth;
                fields["path"] = path;
                fields["order"] = order;
                fields["rendered"] = item is null ? null : Rendered(item);
                var names = new List<string>();
                if (depth < depthLimit && node is not null && Prop(node, "IsExpanded") is true && Children(node) is { Count: <= WalkLimit } list)
                {
                    foreach (object child in list)
                        names.Add((string?)Prop(child, "Name") ?? "");
                }
                return (fields, names);
            });
            Emit("tree-row", line);
            rows++;
            if (line["rendered"] is null)
                unrendered++;
            for (int i = 0; i < children.Count; i++)
                queue.Enqueue(([.. indexes, i], path.Length == 0 ? children[i] : $"{path}\\{children[i]}", depth + 1, i));
        }
        Emit("walked", new() { ["rows"] = rows, ["unrendered"] = unrendered, ["depth"] = depthLimit });
        CellFit(window);
    }

    // ---- RECYCLE ----

    private static void Recycle(Window window, string path)
    {
        int[] parent = FindPath(window, path);
        SetExpanded(window, parent, true);
        List<(string Name, long OnDisk, int ChildCount)> before = Ui(() => ChildList(NodeAt(window, parent)));
        int low = before.Count - 3;
        if (low < 0 || before[low].ChildCount == 0)
            throw new Refusal($"{path} needs at least 3 children, the third from the end a folder with something in it");
        int[] child = [.. parent, low];
        SetExpanded(window, child, true);
        List<string> childRowsBefore = Ui(() => ChildList(NodeAt(window, child)).Select(c => c.Name).ToList());
        (double extent, double viewport) = Ui(() => Find<ScrollViewer>(Tree(window)) is { } sv ? (sv.ExtentHeight, sv.ViewportHeight) : (0.0, 0.0));

        // Away and back, twice, so the opened child's container is handed to other rows and it gets another.
        for (int pass = 0; pass < 2; pass++)
        {
            Ui(() => Find<ScrollViewer>(Tree(window))!.ScrollToEnd());
            Settle(DispatcherPriority.Loaded);
            Ui(() => Find<ScrollViewer>(Tree(window))!.ScrollToHome());
            Settle(DispatcherPriority.Loaded);
        }
        SetExpanded(window, parent, false);
        SetExpanded(window, parent, true);

        var after = new List<string>();
        var expandedNodes = new List<string>();
        var expandedContainers = new List<string>();
        int unrendered = 0;
        for (int i = 0; i < before.Count; i++)
        {
            int[] row = [.. parent, i];
            Ui(() => Resolve(window, row) is not null);
            Settle(DispatcherPriority.Loaded);
            Dictionary<string, object?> fields = Ui(() =>
            {
                TreeViewItem? item = Resolve(window, row);
                object? node = item?.DataContext ?? NodeAt(window, row);
                Dictionary<string, object?> f = NodeFields(node);
                f["order"] = i;
                f["containerExpanded"] = item?.IsExpanded;
                f["rendered"] = item is null ? null : Rendered(item);
                return f;
            });
            Emit("recycle-row", fields);
            string name = (string?)fields["name"] ?? "";
            after.Add(name);
            if (fields["nodeExpanded"] is true)
                expandedNodes.Add(name);
            if (fields["containerExpanded"] is true)
                expandedContainers.Add(name);
            if (fields["rendered"] is null)
                unrendered++;
        }
        var childRowsAfter = new List<string?>();
        for (int j = 0; j < childRowsBefore.Count; j++)
        {
            int[] row = [.. child, j];
            Ui(() => Resolve(window, row) is not null);
            Settle(DispatcherPriority.Loaded);
            childRowsAfter.Add(Ui(() => Resolve(window, row) is { } item ? Part<TextBlock>(item, "NameText")?.Text : null));
        }
        Emit("recycle", new()
        {
            ["parent"] = path,
            ["child"] = before[low].Name,
            ["childIndex"] = low,
            ["rows"] = before.Count,
            ["extentHeight"] = extent,
            ["viewportHeight"] = viewport,
            ["before"] = before.Select(c => c.Name).ToList(),
            ["beforeOnDisk"] = before.Select(c => c.OnDisk).ToList(),
            ["after"] = after,
            ["expandedNodes"] = expandedNodes,
            ["expandedContainers"] = expandedContainers,
            ["childRowsBefore"] = childRowsBefore,
            ["childRowsAfter"] = childRowsAfter,
            ["unrendered"] = unrendered,
        });
    }

    // ---- EXPAND ----

    private static void ExpandTimed(Window window, string path)
    {
        int[] indexes = FindPath(window, path);
        if (indexes.Length < 2)
            throw new Refusal("FSA_HOOK_EXPAND is the scan root, which the ending has already opened untimed; name a folder under it");
        for (int open = 1; open <= Opens; open++)
        {
            if (open > 1)
                SetExpanded(window, indexes, false);
            Settle(DispatcherPriority.ContextIdle);
            Stopwatch? clock = null;
            (bool loadedBefore, double handlerMs, double? sortMs, double? buildMs) = Ui(() =>
            {
                TreeViewItem item = Resolve(window, indexes) ?? throw new Refusal($"{path} has no row to open");
                bool loaded = Loaded(item.DataContext);
                clock = Stopwatch.StartNew();
                item.SetCurrentValue(TreeViewItem.IsExpandedProperty, true); // its Expanded builds the rows, if not yet
                double handler = clock.Elapsed.TotalMilliseconds;
                return (loaded, handler, WindowValue<double?>(window, "LastFolderSortMs"), WindowValue<double?>(window, "LastFolderBuildMs"));
            });
            bool first = false;
            double ms = 0;
            int realized = 0, passes = 0;
            string? firstName = null;
            long? firstOnDisk = null;
            while (!first && passes++ < 400)
            {
                (first, ms, realized, firstName, firstOnDisk) = Ui(() =>
                {
                    TreeViewItem? item = Resolve(window, indexes);
                    var firstRow = item?.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                    return (firstRow is not null, clock!.Elapsed.TotalMilliseconds, TreeRows(Tree(window)).Count,
                        firstRow is null ? null : Part<TextBlock>(firstRow, "NameText")?.Text,
                        firstRow is null ? null : (long?)Prop(firstRow.DataContext, "OnDisk"));
                }, DispatcherPriority.ContextIdle);
            }
            Emit("expand", new()
            {
                ["path"] = path,
                ["open"] = open,
                ["cold"] = !loadedBefore,
                ["ms"] = ms,
                ["handlerMs"] = handlerMs,
                ["layoutMs"] = ms - handlerMs,
                ["sortMs"] = loadedBefore ? null : sortMs,
                ["buildMs"] = loadedBefore ? null : buildMs,
                ["firstChild"] = first,
                ["firstName"] = firstName,
                ["firstOnDisk"] = firstOnDisk,
                ["children"] = Ui(() => Children(NodeAt(window, indexes))?.Count),
                // SL-110: how many of the folder's rows the store marks not listed (first open only: a count over 100,000 rows).
                ["notListedChildren"] = open == 1 ? Ui(() => StoppedTree.NotListedChildren(_app.MainWindow, NodeAt(window, indexes))) : null,
                // SL-113: how many of the opened folder's own rows on screen draw their Size as 'at least ...' (the realized rows only: a
                // row is read when drawn). The folder's row and its ancestors are not counted, so a folder that is itself marked cannot
                // stand in for the rows under it.
                ["atLeastRealized"] = Ui(() => Resolve(window, indexes) is { } opened
                    ? TreeRows(opened).Count(r => Part<TextBlock>(r, "SizeText")?.Text?.StartsWith("at least ", StringComparison.Ordinal) == true)
                    : 0),
                ["realized"] = realized,
                ["viewportHeight"] = Ui(() => Find<ScrollViewer>(Tree(window))?.ViewportHeight),
            });
        }
    }

    // ---- MULTIPLY ----

    private static void Multiply(Window window)
    {
        // Every folder but the root closed, as a person would close them.
        foreach (int[] open in Ui(() => OpenRows(window)).Where(p => p.Length > 1).OrderByDescending(p => p.Length))
            SetExpanded(window, open, false);
        Dictionary<string, object?> pressed = Ui(() =>
        {
            TreeViewItem root = Resolve(window, [0]) ?? throw new Refusal("no root row");
            root.SetCurrentValue(TreeViewItem.IsSelectedProperty, true);
            root.Focus();
            PresentationSource source = PresentationSource.FromVisual(root) ?? throw new Refusal("the root row is not on screen");
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Multiply) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            root.RaiseEvent(preview);
            bool? keyDownHandled = null;
            if (!preview.Handled)
            {
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Multiply) { RoutedEvent = Keyboard.KeyDownEvent };
                root.RaiseEvent(down);
                keyDownHandled = down.Handled;
            }
            return new Dictionary<string, object?>
            {
                ["selected"] = root.IsSelected,
                ["focused"] = root.IsKeyboardFocusWithin,
                ["previewHandled"] = preview.Handled,
                ["keyDownHandled"] = keyDownHandled,
            };
        });
        Settle(DispatcherPriority.ContextIdle);
        List<string> expanded = Ui(() => OpenRows(window).Where(p => p.Length > 1).Select(p => PathOf(window, p)).ToList());
        pressed["expandedBeyondRoot"] = expanded.Count;
        pressed["expanded"] = expanded;
        pressed["realized"] = Ui(() => TreeRows(Tree(window)).Count);
        Emit("multiply", pressed);
    }

    // ---- SELECT and RELEASE ----

    private static void Select(Window window, string path)
    {
        int[] indexes = FindPath(window, path);
        Ui(() => Resolve(window, indexes) is not null);
        Settle(DispatcherPriority.Loaded);
        Emit("selected", Ui(() =>
        {
            TreeViewItem item = Resolve(window, indexes) ?? throw new Refusal($"{path} has no row to select");
            item.SetCurrentValue(TreeViewItem.IsSelectedProperty, true); // as a click selects it
            item.Focus();
            return new Dictionary<string, object?>
            {
                ["path"] = path,
                ["depth"] = indexes.Length - 1,
                ["selected"] = item.IsSelected,
                ["focused"] = item.IsKeyboardFocusWithin,
                ["treeSelected"] = Tree(window).SelectedItem is not null,
            };
        }));
    }

    private static void Release(Window window, string secondRoot)
    {
        WeakReference store = Ui(() => WeakStore(window)) ?? throw new Refusal("the ending holds no result with a store");
        Ui(() =>
        {
            if (window.FindName("ChooseFolderButton") is not FrameworkElement chooser || !StartupHook.Pressable(chooser) || Shown(window, "ScanningPanel"))
                throw new Refusal("no new scan could be started: Choose folder is not pressable, or a scan is running");
            StartScan(window, secondRoot);
            return true;
        });
        Settle(DispatcherPriority.ContextIdle);
        (bool scanning, bool tree, int items) = Ui(() => (Shown(window, "ScanningPanel"), Tree(window).Visibility == Visibility.Visible, Tree(window).Items.Count));
        Emit("store-released", new()
        {
            ["released"] = Collected(store),
            ["when"] = "the second scan started",
            ["scanning"] = scanning,
            ["tree"] = tree,
            ["treeItems"] = items,
        });
        WaitForEnding(window, "second-ending", null, first: false);
        Emit("store-released", new() { ["released"] = Collected(store), ["when"] = "the second scan ended" });
        if (_stoppedRun)
            AfterRelease(window);
    }

    // SL-110: after a stopped tree was chosen and a second scan completed: the window must have put the choice away (hidden,
    // Folders checked) and shown the second scan's own tree, whose rows read the second store, none of them unread. The
    // first level of that tree is read as WALK reads any (its tree-row lines follow the second-ending line).
    private static void AfterRelease(Window window)
    {
        Emit("after-release", Ui(() => new Dictionary<string, object?>
        {
            ["treeChoice"] = Shown(window, "ShowTreeButton"),
            ["foldersChecked"] = (window.FindName("ShowFoldersButton") as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true,
            ["treeChecked"] = (window.FindName("ShowTreeButton") as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true,
            ["tree"] = Tree(window).Visibility == Visibility.Visible,
            ["treeItems"] = Tree(window).Items.Count,
            ["list"] = Shown(window, "TopLevelList"),
            ["marks"] = StoppedTree.StoreMarks(window),
        }));
        Walk(window, 1);
    }

    // ---- CAN_REVEAL ----

    // SL-110, criterion 6: Open in Explorer on a row, without executing it. The row is selected and focused as a click leaves
    // it, and the window's own command is asked whether it can run for it, and which path it would open, beside the path the
    // store gives for the row's entry and the store's mark for it.
    private static void CanReveal(Window window, string path)
    {
        int[] indexes = FindPath(window, path);
        Ui(() => Resolve(window, indexes) is not null);
        Settle(DispatcherPriority.Loaded);
        Emit("can-reveal", Ui(() =>
        {
            TreeViewItem item = Resolve(window, indexes) ?? throw new Refusal($"{path} has no row to ask Open in Explorer about");
            item.SetCurrentValue(TreeViewItem.IsSelectedProperty, true);
            item.Focus();
            object? node = item.DataContext;
            var command = window.GetType().GetField("OpenInExplorer", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as RoutedUICommand
                ?? throw new Refusal("the window has no OpenInExplorer command");
            MethodInfo pathOf = window.GetType().GetMethod("PathOf", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Refusal("the window has no PathOf");
            return new Dictionary<string, object?>
            {
                ["path"] = path,
                ["name"] = Prop(node, "Name"),
                ["note"] = Prop(node, "Note"),
                ["selected"] = item.IsSelected,
                ["focused"] = item.IsKeyboardFocusWithin,
                ["canExecute"] = command.CanExecute(node, window), // as the row's menu sends it: the row is the parameter
                ["canExecuteFocused"] = command.CanExecute(null, window), // as the keyboard's Ctrl+Enter reaches it
                ["pathOf"] = pathOf.Invoke(null, [node]) as string,
                ["storePath"] = StoppedTree.StorePathOf(_app.MainWindow, node),
                ["storeNotListed"] = StoppedTree.NotListedOf(_app.MainWindow, node),
            };
        }));
    }

    // Only a weak reference leaves this method, and it is not inlined, so no local of the caller keeps the store
    // alive however the JIT tiers it (review M2).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference? WeakStore(Window window)
    {
        object? result = window.GetType().GetProperty("LastResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(window);
        object? store = result?.GetType().GetProperty("Store")?.GetValue(result);
        return store is null ? null : new WeakReference(store);
    }

    // On the hook's thread, while the UI thread waits in its message loop with nothing of the old scan on its stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collected(WeakReference store)
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
        return !store.IsAlive;
    }

    // The window's own start, which a drive button and Choose folder call; not public, so reached by name.
    private static void StartScan(Window window, string root) =>
        (window.GetType().GetMethod("StartScan", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Refusal("the window has no StartScan")).Invoke(window, [root]);

    // ---- GAP ----

    private static void Gap(Window window, string path, Probe probe)
    {
        int[] indexes = FindPath(window, path);
        if (indexes.Length < 2)
            throw new Refusal("FSA_HOOK_GAP names the folder to open before the second scan, never the root");
        probe.Mark("handoff");
        SetExpanded(window, indexes, true);
        probe.Phase("open", TimeSpan.FromMilliseconds(500));
        string root = Ui(() => (string?)Prop(Tree(window).Items[0], "Name")) ?? throw new Refusal("no root row");
        double startMs = Ui(() =>
        {
            if (window.FindName("ChooseFolderButton") is not FrameworkElement chooser || !StartupHook.Pressable(chooser))
                throw new Refusal("no new scan could be started: Choose folder is not pressable");
            var clock = Stopwatch.StartNew();
            StartScan(window, root);
            return clock.Elapsed.TotalMilliseconds;
        });
        // How long the window's own start held the UI thread, the tree's release included.
        Emit("rescan-started", new() { ["startScanMs"] = startMs, ["ms"] = StartupHook.Clock.ElapsedMilliseconds });
        WaitForEnding(window, "second-ending", probe, first: false);
        probe.Phase("rescan-handoff", TimeSpan.FromSeconds(1));
        foreach (Dictionary<string, object?> phase in probe.Report())
            Emit("gap", phase);
    }

    /// <summary>
    /// An Input-priority no-op posted to the UI thread every 10 ms from a thread of its own, and how long each
    /// waited: the UI thread's gaps. Each probe belongs to the phase open when it was posted.
    /// </summary>
    private sealed class Probe
    {
        private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(10);

        private readonly List<(string Phase, double Waited, long At)> _waits = [];
        private readonly ManualResetEventSlim _stop = new();
        private string _phase = "scan";
        private Thread? _thread;

        public static Probe Start(Application app)
        {
            var probe = new Probe();
            probe._thread = new Thread(() =>
            {
                while (!probe._stop.IsSet)
                {
                    string phase;
                    lock (probe._waits)
                        phase = probe._phase;
                    long at = StartupHook.Clock.ElapsedMilliseconds;
                    var waited = Stopwatch.StartNew();
                    try
                    {
                        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    lock (probe._waits)
                        probe._waits.Add((phase, waited.Elapsed.TotalMilliseconds, at));
                    probe._stop.Wait(Every);
                }
            }) { IsBackground = true, Name = "FsaWindowHook-gap" };
            probe._thread.Start();
            return probe;
        }

        /// <summary>The phase that ended now; later probes belong to the next one named.</summary>
        public void Mark(string ended)
        {
            lock (_waits)
                _phase = ended switch { "scan" => "handoff", "handoff" => "open", "open" => "rescan", "rescan" => "rescan-handoff", _ => "after" };
        }

        /// <summary>Stays in the phase <paramref name="phase"/> for <paramref name="length"/>, then leaves it.</summary>
        public void Phase(string phase, TimeSpan length)
        {
            lock (_waits)
                _phase = phase;
            Thread.Sleep(length);
            Mark(phase);
        }

        public void Stop() => _stop.Set();

        public IEnumerable<Dictionary<string, object?>> Report()
        {
            List<(string Phase, double Waited, long At)> waits;
            lock (_waits)
                waits = [.. _waits];
            foreach (string phase in new[] { "scan", "handoff", "open", "rescan", "rescan-handoff" })
            {
                (string Phase, double Waited, long At)[] mine = [.. waits.Where(w => w.Phase == phase).OrderBy(w => w.Waited)];
                yield return new Dictionary<string, object?>
                {
                    ["phase"] = phase,
                    ["probes"] = mine.Length,
                    ["p95"] = mine.Length == 0 ? null : mine[(int)Math.Ceiling(0.95 * mine.Length) - 1].Waited,
                    ["max"] = mine.Length == 0 ? null : mine[^1].Waited,
                    ["maxAt"] = mine.Length == 0 ? null : mine[^1].At, // the hook's clock when the longest was posted
                    ["maxEndAt"] = mine.Length == 0 ? null : mine[^1].At + mine[^1].Waited, // and when it got through
                };
            }
        }
    }

    // ---- Rows ----

    private static TreeView Tree(Window window) =>
        window.FindName("FolderTree") as TreeView ?? throw new Refusal("the window has no FolderTree");

    // The container of the row at these child indexes, from the tree down, each brought into view through its
    // parent's items panel when it has none (Microsoft's way into a virtualized TreeView).
    private static TreeViewItem? Resolve(Window window, IReadOnlyList<int> indexes)
    {
        ItemsControl owner = Tree(window);
        TreeViewItem? item = null;
        foreach (int index in indexes)
        {
            item = Realize(owner, index);
            if (item is null)
                return null;
            owner = item;
        }
        return item;
    }

    private static TreeViewItem? Realize(ItemsControl owner, int index)
    {
        if (index >= owner.Items.Count)
            return null;
        if (owner.ItemContainerGenerator.ContainerFromIndex(index) is TreeViewItem found)
            return found;
        if (ItemsHost(owner) is not { } panel)
            return null;
        panel.BringIndexIntoViewPublic(index);
        return owner.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    }

    private static VirtualizingPanel? ItemsHost(ItemsControl owner)
    {
        owner.ApplyTemplate();
        ItemsPresenter? presenter = Presenter(owner);
        if (presenter is null)
        {
            owner.UpdateLayout();
            presenter = Presenter(owner);
        }
        if (presenter is null)
            return null;
        presenter.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0)
            owner.UpdateLayout();
        return VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as VirtualizingPanel : null;
    }

    private static ItemsPresenter? Presenter(ItemsControl owner) =>
        owner is TreeViewItem item ? item.Template?.FindName("ItemsHost", item) as ItemsPresenter : Find<ItemsPresenter>(owner);

    // The row's node, read from the rows alone, for a row that has no container.
    private static object? NodeAt(Window window, IReadOnlyList<int> indexes)
    {
        IList? list = Tree(window).Items;
        object? node = null;
        foreach (int index in indexes)
        {
            if (list is null || index >= list.Count)
                return null;
            node = list[index];
            list = Children(node);
        }
        return node;
    }

    // The child indexes of the row at a path, opening each folder on the way as a person would.
    private static int[] FindPath(Window window, string path)
    {
        var indexes = new List<int> { 0 };
        if (path.Length == 0)
            return [.. indexes];
        foreach (string name in path.Split('\\'))
        {
            int[] parent = [.. indexes];
            SetExpanded(window, parent, true);
            int index = Ui(() => ChildList(NodeAt(window, parent)).FindIndex(c => c.Name == name));
            if (index < 0)
                throw new Refusal($"no row {path} (no {name} under {PathOfNames(indexes, window)})");
            indexes.Add(index);
        }
        return [.. indexes];
    }

    private static string PathOfNames(List<int> indexes, Window window) => Ui(() => PathOf(window, [.. indexes]));

    private static string PathOf(Window window, int[] indexes)
    {
        var names = new List<string>();
        IList? list = Tree(window).Items;
        for (int k = 0; k < indexes.Length && list is not null; k++)
        {
            object node = list[indexes[k]]!;
            if (k > 0)
                names.Add((string?)Prop(node, "Name") ?? "");
            list = Children(node);
        }
        return string.Join('\\', names);
    }

    // Opens or closes a row through its container, as its expander or the keyboard does, and lets layout run.
    private static void SetExpanded(Window window, int[] indexes, bool expanded)
    {
        Ui(() =>
        {
            TreeViewItem item = Resolve(window, indexes) ?? throw new Refusal($"no row at {string.Join('/', indexes)} to {(expanded ? "open" : "close")}");
            if (item.IsExpanded != expanded)
                item.SetCurrentValue(TreeViewItem.IsExpandedProperty, expanded);
            return true;
        });
        Settle(DispatcherPriority.Loaded);
    }

    // The index paths of every open row, from the rows' own state.
    private static List<int[]> OpenRows(Window window)
    {
        var open = new List<int[]>();
        void Visit(object node, int[] indexes)
        {
            if (Prop(node, "IsExpanded") is not true)
                return;
            open.Add(indexes);
            if (Children(node) is { } list && Loaded(node))
            {
                for (int i = 0; i < list.Count; i++)
                    Visit(list[i]!, [.. indexes, i]);
            }
        }
        IList items = Tree(window).Items;
        if (items.Count > 0)
            Visit(items[0]!, [0]);
        return open;
    }

    private static List<(string Name, long OnDisk, int ChildCount)> ChildList(object? node) =>
        Children(node) is { } list && Loaded(node!)
            ? list.Cast<object>().Select(c => ((string?)Prop(c, "Name") ?? "", (long)(Prop(c, "OnDisk") ?? 0L), (int)(Prop(c, "ChildCount") ?? 0))).ToList()
            : [];

    // Whether a row's children have been built: not the one row that stands in for them until then, whose kind
    // is null because it reads no store.
    private static bool Loaded(object? node) =>
        Children(node) is not { Count: 1 } list || Prop(list[0], "Kind") is not null;

    private static IList? Children(object? node) => Prop(node, "Children") as IList;

    private static Dictionary<string, object?> NodeFields(object? node) => new()
    {
        ["name"] = Prop(node, "Name"),
        ["entryKind"] = Prop(node, "Kind")?.ToString(),
        ["isRoot"] = Prop(node, "IsRoot"),
        ["childCount"] = Prop(node, "ChildCount"),
        ["nodeExpanded"] = Prop(node, "IsExpanded"),
        ["onDisk"] = Prop(node, "OnDisk"),
        ["size"] = Prop(node, "Size"),
        ["share"] = Prop(node, "Share"),
        ["onDiskText"] = Prop(node, "OnDiskText"),
        ["sizeText"] = Prop(node, "SizeText"),
        ["shareText"] = Prop(node, "ShareText"),
        ["barWidth"] = Prop(node, "BarWidth"),
        ["note"] = Prop(node, "Note"),
        // SL-110: the store's own mark for the row's entry, read through the window's LastResult and the row's entry number,
        // never through a property of the row, so a row that mis-reads its mark cannot vouch for itself.
        ["storeNotListed"] = StoppedTree.NotListedOf(_app.MainWindow, node),
        // SL-113: what the store says the row's entry holds, read the same way: whether it is unfinished, its AtLeast, and its
        // rolled figures (the ones a final row draws and an unfinished row must not draw as its own).
        ["storeUnfinished"] = StoppedTree.UnfinishedOf(_app.MainWindow, node),
        ["storeAtLeast"] = StoppedTree.AtLeastOf(_app.MainWindow, node),
        ["storeRolled"] = StoppedTree.RolledOf(_app.MainWindow, node),
    };

    // What the row's container draws: the text of each named part of the row template, the bar's laid-out width,
    // and whether the expander shows.
    private static Dictionary<string, object?> Rendered(TreeViewItem item) => new()
    {
        ["name"] = Part<TextBlock>(item, "NameText")?.Text,
        ["note"] = Part<TextBlock>(item, "NoteText")?.Text,
        ["onDisk"] = Part<TextBlock>(item, "OnDiskText")?.Text,
        ["size"] = Part<TextBlock>(item, "SizeText")?.Text,
        ["share"] = Part<TextBlock>(item, "ShareText")?.Text,
        ["barActualWidth"] = Part<FrameworkElement>(item, "ShareBar")?.ActualWidth,
        ["barTrackWidth"] = Part<FrameworkElement>(item, "ShareTrack")?.ActualWidth,
        ["barShown"] = Part<FrameworkElement>(item, "ShareTrack")?.IsVisible,
        ["expander"] = item.Template?.FindName("Expander", item) is UIElement { Visibility: Visibility.Visible },
        ["expanded"] = item.IsExpanded,
        ["rowHeight"] = (item.Template?.FindName("PART_Header", item) as FrameworkElement)?.ActualHeight,
        // SL-113: whether each figure cell cut its text (the layout clip WPF puts on an element given less than it asked for),
        // and the text's one-line width at the cell's own typeface and DPI against the width the cell was given, so a figure that
        // reads in full as a property but is drawn short is seen (ActualWidth alone hides it: spike 125, 101.3 against 80).
        ["sizeClipped"] = Part<TextBlock>(item, "SizeText") is { } sizeCell ? System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(sizeCell) is not null : null,
        ["onDiskClipped"] = Part<TextBlock>(item, "OnDiskText") is { } onDiskCell ? System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(onDiskCell) is not null : null,
        ["sizeTextWidth"] = Part<TextBlock>(item, "SizeText") is { Text.Length: > 0 } sizeDrawn ? TextWidth(sizeDrawn, sizeDrawn.Text) : null,
        ["onDiskTextWidth"] = Part<TextBlock>(item, "OnDiskText") is { Text.Length: > 0 } onDiskDrawn ? TextWidth(onDiskDrawn, onDiskDrawn.Text) : null,
        ["sizeCellWidth"] = Part<TextBlock>(item, "SizeText")?.ActualWidth,
        ["onDiskCellWidth"] = Part<TextBlock>(item, "OnDiskText")?.ActualWidth,
    };

    // The one-line width of a text in a cell's own typeface, size and DPI.
    private static double TextWidth(TextBlock cell, string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight, cell.FontStretch), cell.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(cell).PixelsPerDip).WidthIncludingTrailingWhitespace;

    // SL-113: the widest figure a stopped scan's row can draw, 'at least ' in front of the widest text FormatSize writes, measured
    // in the typeface, size and DPI of a realized Size cell against the width that cell was given. One line: the root row is
    // always realized. Spike 125 measured 101.26 DIP for 'at least 1,000 bytes' on this PC at 125% DPI.
    private static void CellFit(Window window)
    {
        Dictionary<string, object?>? fit = Ui(() =>
        {
            TreeViewItem? row = TreeRows(Tree(window)).FirstOrDefault(r => Part<TextBlock>(r, "SizeText") is not null);
            if (row is null || Part<TextBlock>(row, "SizeText") is not { } size || Part<TextBlock>(row, "OnDiskText") is not { } onDisk)
                return null;
            MethodInfo? format = window.GetType().Assembly.GetType("FsAnalyser.App.FigureText")
                ?.GetMethod("FormatSize", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (format is null)
                return null;
            string widest = "";
            double widestWidth = 0;
            for (int unit = 0; unit <= 5; unit++)
            {
                long bytes = unit == 0 ? 1023 : (long)(1023.9 * Math.Pow(1024, unit));
                string text = "at least " + (string)format.Invoke(null, [bytes])!;
                double width = TextWidth(size, text);
                if (width > widestWidth)
                    (widest, widestWidth) = (text, width);
            }
            return new Dictionary<string, object?>
            {
                ["widestText"] = widest,
                ["widestWidth"] = widestWidth,
                ["widestOnDiskWidth"] = TextWidth(onDisk, widest),
                ["cellWidth"] = size.Width,
                ["onDiskCellWidth"] = onDisk.Width,
                ["pixelsPerDip"] = VisualTreeHelper.GetDpi(size).PixelsPerDip,
                ["fontSize"] = size.FontSize,
                ["fontFamily"] = size.FontFamily.Source,
            };
        });
        if (fit is not null)
            Emit("cell-fit", fit);
    }

    private static T? Part<T>(TreeViewItem item, string name) where T : class =>
        item.Template?.FindName("PART_Header", item) is ContentPresenter header
            ? header.ContentTemplate?.FindName(name, header) as T
            : null;

    private static List<TreeViewItem> TreeRows(DependencyObject node, List<TreeViewItem>? rows = null)
    {
        rows ??= [];
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is TreeViewItem row)
                rows.Add(row);
            TreeRows(child, rows);
        }
        return rows;
    }

    private static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if ((child as T ?? Find<T>(child)) is { } found)
                return found;
        }
        return null;
    }

    // ---- Plumbing ----

    private static object? Prop(object? target, string name) =>
        target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    private static T WindowValue<T>(Window window, string name) =>
        (T)window.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(window)!;

    // The element's own Visibility, what the app set.
    private static bool Shown(Window window, string name) =>
        window.FindName(name) is UIElement { Visibility: Visibility.Visible };

    // Runs on the UI thread. A fault there is caught there and raised here, so the app never sees it as its own.
    private static T Ui<T>(Func<T> work, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        Exception? failed = null;
        T result = _app.Dispatcher.Invoke(() =>
        {
            try
            {
                return work();
            }
            catch (Exception e)
            {
                failed = e;
                return default!;
            }
        }, priority);
        if (failed is Refusal refusal)
            throw new Refusal(refusal.Message);
        if (failed is not null)
            throw new Refusal($"{failed.GetType().Name} on the UI thread: {failed.Message}");
        return result;
    }

    private static void Ui(Action work) => Ui(() =>
    {
        work();
        return true;
    });

    // Returns once everything the dispatcher had queued above this priority has run: at Loaded, layout; at
    // ContextIdle, layout and render.
    private static void Settle(DispatcherPriority priority) => _app.Dispatcher.Invoke(() => { }, priority);

    private static void Close(Window? window)
    {
        try
        {
            _app.Dispatcher.Invoke(() => (window ?? _app.MainWindow)?.Close());
            Emit("closed", new() { ["ms"] = StartupHook.Clock.ElapsedMilliseconds });
        }
        catch (Exception e)
        {
            StartupHook.Log($"{{\"kind\":\"error\",\"reason\":\"closing: {e.GetType().Name}\"}}");
        }
    }

    private static void Emit(string kind, Dictionary<string, object?> fields)
    {
        var line = new Dictionary<string, object?> { ["kind"] = kind };
        foreach ((string key, object? value) in fields)
            line[key] = value;
        StartupHook.Log(JsonSerializer.Serialize(line, Json));
    }

    private static string? Text(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;

    private static int? Number(string variable) => Text(variable) is not { } text ? null
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value
        : throw new Refusal($"{variable} is not a whole number");
}
