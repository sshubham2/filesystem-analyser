using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// most the work area) as it loads, before the scan: a "sized" line.
/// <list type="bullet">
/// <item>ending: how the scan ended and whether the tree is what is shown.</item>
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
/// <item>GAP: an Input-priority probe every 50 ms from the hook's start, the window moved and resized every
/// 50 ms while a scan runs; after the ending, 1 s of hand-off, that folder opened once, then a second scan of
/// the same root over the open tree, to its ending and 1 s beyond. One "gap" line a phase (scan, handoff, open,
/// rescan) with the probes' p95 and max wait (review m7).</item>
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
            if (release is not null && select is null)
                throw new Refusal("FSA_HOOK_RELEASE needs FSA_HOOK_SELECT: the row a person had selected before picking a new folder");

            window = WaitForWindow();
            if (Number("FSA_HOOK_HEIGHT") is int height)
                Size(window, height);
            if (gap is not null)
                probe = Probe.Start(app);
            WaitForEnding(window, "ending", probe, first: true);
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
    // the tree has the room a person who enlarges the window gives it.
    private static void Size(Window window, int height) => Emit("sized", Ui(() =>
    {
        window.Top = SystemParameters.WorkArea.Top;
        window.Height = Math.Min(height, SystemParameters.WorkArea.Height);
        return new Dictionary<string, object?> { ["asked"] = height, ["height"] = window.Height, ["workArea"] = SystemParameters.WorkArea.Height };
    }));

    // Until the scan has returned and its ending is on screen. With a probe, the window is moved and resized every
    // 50 ms while the scan runs, as a person dragging it would (row 6).
    private static void WaitForEnding(Window window, string kind, Probe? probe, bool first)
    {
        var since = Stopwatch.StartNew();
        var moved = Stopwatch.StartNew();
        int moves = 0;
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
                Ui(() =>
                {
                    window.Left += 12 * step;
                    window.Top += 8 * step;
                    window.Width += 24 * step;
                    window.Height += 16 * step;
                    return true;
                });
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
                ["moves"] = moves,
                ["ms"] = StartupHook.Clock.ElapsedMilliseconds,
            };
        }));
        if (!Ui(() => Tree(window).Visibility == Visibility.Visible && Tree(window).Items.Count == 1))
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
        Ui(() =>
        {
            if (window.FindName("ChooseFolderButton") is not FrameworkElement chooser || !StartupHook.Pressable(chooser))
                throw new Refusal("no new scan could be started: Choose folder is not pressable");
            StartScan(window, root);
            return true;
        });
        WaitForEnding(window, "second-ending", probe, first: false);
        probe.Phase("rescan-handoff", TimeSpan.FromSeconds(1));
        foreach (Dictionary<string, object?> phase in probe.Report())
            Emit("gap", phase);
    }

    /// <summary>
    /// An Input-priority no-op posted to the UI thread every 50 ms from a thread of its own, and how long each
    /// waited: the UI thread's gaps. Each probe belongs to the phase open when it was posted.
    /// </summary>
    private sealed class Probe
    {
        private readonly List<(string Phase, double Waited)> _waits = [];
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
                        probe._waits.Add((phase, waited.Elapsed.TotalMilliseconds));
                    probe._stop.Wait(50);
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
            List<(string Phase, double Waited)> waits;
            lock (_waits)
                waits = [.. _waits];
            foreach (string phase in new[] { "scan", "handoff", "open", "rescan", "rescan-handoff" })
            {
                double[] ms = [.. waits.Where(w => w.Phase == phase).Select(w => w.Waited).Order()];
                yield return new Dictionary<string, object?>
                {
                    ["phase"] = phase,
                    ["probes"] = ms.Length,
                    ["p95"] = ms.Length == 0 ? null : ms[(int)Math.Ceiling(0.95 * ms.Length) - 1],
                    ["max"] = ms.Length == 0 ? null : ms[^1],
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
    };

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
