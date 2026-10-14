using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

/// <summary>
/// The stopped ending's folder tree (SL-110), from inside the app's process: stops the scan at a folder the case names,
/// chooses the tree beside the stopped list as a click would, and reads what the stopped store and the window drew.
/// Tree mode and reveal mode use it; its lines are JSON objects with a "kind", as tree mode's are.
/// </summary>
/// <remarks>
/// <code>
///   FSA_HOOK_TREE_STOP=&lt;folder under the root&gt;[\]   (tree and reveal only)
/// </code>
/// A hook thread spins on the scan's progress and presses Cancel when the folder most recently committed is that folder
/// (without a trailing backslash) or that folder or any folder under it (with one); <see cref="StopTarget"/> has the
/// words of the match and tests/FsAnalyser.Core.Tests/StopTargetCorpus.json the shapes it was built for. It logs one
/// "tree-stop" line: the target, the folder the match saw, "pressedAt" (the folder most recently committed when the
/// click ran on the UI thread: the last COMMITTED folder, so the folder whose listing was in flight is the NEXT one),
/// the time from the match to the callback, the measured poll interval and its worst gap. It never sleeps in the poll:
/// Thread.Sleep(1) waits about 15.6 ms unless the process raised its timer resolution (review m1), which is most of the
/// 20 to 30 ms a cut of m-big's listing has.
///
/// Every read of the stopped store is made by one of the NoInlining methods at the foot, each of which resolves the
/// store from the window's LastResult by reflection on each call and returns values only: no field, cached delegate or
/// closure of the hook may hold the store, or the case that waits for it to be collected after the next scan starts
/// would fail for the hook's sake (review m2).
/// </remarks>
internal static class StoppedTree
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>One armed stop: the spin thread and what it ended with.</summary>
    internal sealed class Run
    {
        internal Thread? Thread;
        internal bool Pressed;
        internal string? Reason;

        /// <summary>Waits for the spin to end, which it does once the scan has.</summary>
        internal void Join() => Thread?.Join(TimeSpan.FromSeconds(30));
    }

    // ---- Stopping at a folder ----

    /// <summary>
    /// Starts the spin for FSA_HOOK_TREE_STOP, or null when it is not set. Called before the window is waited for: the
    /// scan starts in the window's Loaded handler, and the spin must be reading by the time the folders before the target
    /// are being committed.
    /// </summary>
    internal static Run? Arm(Application app)
    {
        if (Environment.GetEnvironmentVariable("FSA_HOOK_TREE_STOP") is not { Length: > 0 } target)
            return null;
        if (!StopTarget.IsUsable(target))
            throw new InvalidOperationException("FSA_HOOK_TREE_STOP names a folder under the scan root");
        var run = new Run();
        run.Thread = new Thread(() =>
        {
            try
            {
                Spin(app, target, run);
            }
            catch (Exception e)
            {
                run.Reason ??= $"{e.GetType().Name}: {e.Message}";
                Emit("tree-stop", new() { ["target"] = target, ["pressed"] = false, ["refused"] = run.Reason });
            }
        }) { IsBackground = true, Name = "FsaWindowHook-tree-stop" };
        run.Thread.Start();
        return run;
    }

    private static void Spin(Application app, string target, Run run)
    {
        var since = Stopwatch.StartNew();
        Window? window = null;
        object? progress = null;
        FieldInfo? progressField = null;
        string root = "";
        while (progress is null)
        {
            if (since.Elapsed > Deadline || app.Dispatcher.HasShutdownStarted)
            {
                Refuse(run, target, "no-scan");
                return;
            }
            (window, progress, progressField, root) = Ui<(Window?, object?, FieldInfo?, string)>(app, () =>
            {
                if (app.MainWindow is not { } w)
                    return (null, null, null, "");
                FieldInfo? field = w.GetType().GetField("_progress", BindingFlags.Instance | BindingFlags.NonPublic);
                string? launch = w.GetType().GetField("_launchPath", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(w) as string;
                // The root as the scan names it: LongPath.NormalizeRoot's display form, the full path without a trailing backslash.
                return (w, field?.GetValue(w), field, launch is null ? "" : Path.GetFullPath(launch).TrimEnd('\\'));
            });
            if (progress is null)
                Thread.Sleep(1);
        }
        if (root.Length == 0)
        {
            Refuse(run, target, "no-launch-root");
            return;
        }

        Type progressType = progress.GetType();
        FieldInfo rawField = progressType.GetField("_currentFolder", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("ScanProgress has no _currentFolder");
        Func<string?> rawRead = VolatileRead<string?>(rawField, progress);
        Func<string?> display = (Func<string?>)(progressType.GetProperty("CurrentFolder")!.GetGetMethod()!.CreateDelegate(typeof(Func<string?>), progress));
        Func<object?> scanProgress = VolatileRead<object?>(progressField!, window!);

        // The spin: a volatile read of the raw field, so that CurrentFolder (which converts the path) is called only when
        // the reference changed. A match is posted at once, at Send priority, ahead of the 250 ms tick and everything else.
        long frequency = Stopwatch.Frequency;
        long startTicks = Stopwatch.GetTimestamp();
        long lastTicks = startTicks;
        long worstGap = 0;
        long reads = 0;
        string? lastRaw = null;
        string? seen = null;
        while (true)
        {
            string? raw = rawRead();
            long now = Stopwatch.GetTimestamp();
            long gap = now - lastTicks;
            if (gap > worstGap)
                worstGap = gap;
            lastTicks = now;
            reads++;
            if (!ReferenceEquals(raw, lastRaw))
            {
                lastRaw = raw;
                string? current = display();
                if (current is not null && StopTarget.Matches(root, target, current))
                {
                    seen = current;
                    break;
                }
            }
            if ((reads & 0x3FF) == 0 && (scanProgress() is null || since.Elapsed > Deadline))
            {
                Refuse(run, target, "ended-first", reads, startTicks, worstGap, root);
                return;
            }
        }

        long detected = Stopwatch.GetTimestamp();
        double pollMeanNs = (detected - startTicks) * 1e9 / frequency / Math.Max(reads, 1);
        double worstGapMs = worstGap * 1000.0 / frequency;
        long calledAt = 0;
        string? pressedAt = null;
        string? why = null;
        bool pressed = false;
        using var done = new ManualResetEventSlim();
        app.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            try
            {
                calledAt = Stopwatch.GetTimestamp();
                pressedAt = display();
                if (window!.FindName("CancelButton") is FrameworkElement button && StartupHook.Pressable(button))
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // what a click raises: OnCancel
                    pressed = true;
                }
                else
                {
                    why = "cancel-not-pressable";
                }
            }
            catch (Exception e)
            {
                why = $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                done.Set();
            }
        }));
        long posted = Stopwatch.GetTimestamp();
        if (!done.Wait(TimeSpan.FromSeconds(30)))
            why = "the click never ran";
        run.Pressed = pressed;
        run.Reason = why;
        Emit("tree-stop", new()
        {
            ["target"] = target,
            ["exact"] = !StopTarget.IsSubtree(target),
            ["root"] = root,
            ["seen"] = seen,
            ["pressedAt"] = pressedAt,
            ["pressed"] = pressed,
            ["refused"] = why,
            ["detectToPostUs"] = (posted - detected) * 1e6 / frequency,
            ["detectToCallbackUs"] = calledAt == 0 ? null : (calledAt - detected) * 1e6 / frequency,
            ["reads"] = reads,
            ["pollMeanNs"] = pollMeanNs,
            ["worstGapMs"] = worstGapMs,
            ["ms"] = StartupHook.Clock.ElapsedMilliseconds,
        });
    }

    private static void Refuse(Run run, string target, string reason, long reads = 0, long startTicks = 0, long worstGap = 0, string? root = null)
    {
        run.Reason = reason;
        Emit("tree-stop", new()
        {
            ["target"] = target,
            ["root"] = root,
            ["pressed"] = false,
            ["refused"] = reason,
            ["reads"] = reads,
            ["worstGapMs"] = worstGap * 1000.0 / Stopwatch.Frequency,
            ["ms"] = StartupHook.Clock.ElapsedMilliseconds,
        });
    }

    // A volatile read of an instance field, for a spin that must not see a stale value: the IL's volatile prefix on the
    // load, in a delegate bound to the instance, so the JIT cannot hoist the read out of the loop.
    private static Func<T> VolatileRead<T>(FieldInfo field, object target)
    {
        var method = new DynamicMethod("volatile_" + field.Name, typeof(T), [typeof(object)], typeof(StoppedTree).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, field.DeclaringType!);
        il.Emit(OpCodes.Volatile);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (Func<T>)method.CreateDelegate(typeof(Func<T>), target);
    }

    // ---- Choosing the tree, and the views ----

    /// <summary>
    /// Chooses the folder tree beside the stopped list as a click does (ShowTreeButton's IsChecked) and logs
    /// "tree-chosen": the tree, its items, the list, the header and how much of it shows. Refuses when the ending does not offer it.
    /// </summary>
    internal static void Choose(Application app, Window window)
    {
        if (!Ui(app, () => window.FindName("ShowTreeButton") is UIElement { Visibility: Visibility.Visible }))
            throw new InvalidOperationException("the ending offers no folder tree choice (it was not stopped, or the root's listing never committed)");
        Ui(app, () =>
        {
            ((ToggleButton)window.FindName("ShowTreeButton")).IsChecked = true;
            return true;
        });
        Settle(app);
        Emit("tree-chosen", Ui(app, () => ViewFields(window, "tree")));
    }

    /// <summary>
    /// FSA_HOOK_VIEWS=folders,tree,kinds: each choice set in turn as a click sets it, the layout settled, and a "view" line
    /// after each with the view it left on screen.
    /// </summary>
    internal static void Views(Application app, Window window, string spec)
    {
        string? last = null;
        foreach (string choice in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string button = choice switch
            {
                "folders" => "ShowFoldersButton",
                "tree" => "ShowTreeButton",
                "kinds" => "ShowKindsButton",
                _ => throw new InvalidOperationException($"FSA_HOOK_VIEWS names folders, tree or kinds, not '{choice}'"),
            };
            Ui(app, () =>
            {
                if (window.FindName(button) is not ToggleButton toggle || !StartupHook.Pressable(toggle))
                    throw new InvalidOperationException($"the {choice} choice is not on screen to press");
                toggle.IsChecked = true;
                return true;
            });
            Settle(app);
            Emit("view", Ui(app, () => ViewFields(window, choice)));
            last = choice;
        }
        if (last is null)
            return;

        // The header again at the narrowest the window goes (review m2): the line is cut at its end, so what must show is
        // its lead. The width is put back, so what follows reads the window at the size the run started at.
        double width = Ui(app, () => window.Width);
        Ui(app, () =>
        {
            window.Width = window.MinWidth;
            return true;
        });
        Settle(app);
        Emit("view-min", Ui(app, () => ViewFields(window, last)));
        Ui(app, () =>
        {
            window.Width = width;
            return true;
        });
        Settle(app);
    }

    private static Dictionary<string, object?> ViewFields(Window window, string choice)
    {
        var tree = (TreeView)window.FindName("FolderTree");
        var list = (ListBox)window.FindName("TopLevelList");
        var header = (TextBlock)window.FindName("TopLevelHeader");
        header.UpdateLayout();
        (double textWidth, int chars) = StartupHook.StatusFit(header);
        return new Dictionary<string, object?>
        {
            ["choice"] = choice,
            ["treeChoice"] = window.FindName("ShowTreeButton") is UIElement { Visibility: Visibility.Visible },
            ["foldersChecked"] = (window.FindName("ShowFoldersButton") as ToggleButton)?.IsChecked == true,
            ["treeChecked"] = (window.FindName("ShowTreeButton") as ToggleButton)?.IsChecked == true,
            ["kindsChecked"] = (window.FindName("ShowKindsButton") as ToggleButton)?.IsChecked == true,
            ["tree"] = tree.Visibility == Visibility.Visible,
            ["treeItems"] = tree.Items.Count,
            ["root"] = tree.Items.Count > 0 ? Prop(tree.Items[0], "Name") : null,
            ["headings"] = window.FindName("FolderTreeHeadings") is UIElement { Visibility: Visibility.Visible },
            ["list"] = list.Visibility == Visibility.Visible,
            ["listItems"] = list.Items.Count,
            ["listTitles"] = Titles(window), // each row's title as the list draws it, so going back to the list can be compared byte for byte
            ["kinds"] = window.FindName("KindsView") is UIElement { Visibility: Visibility.Visible },
            ["header"] = header.Text,
            ["headerTextWidth"] = textWidth, // untrimmed, as drawn
            ["headerWidth"] = header.ActualWidth, // what the line was given
            ["headerCharsShown"] = chars, // the characters that come before the ellipsis
            ["headerChars"] = header.Text.Length,
            ["windowWidth"] = window.ActualWidth,
        };
    }

    // ---- What the stopped ending drew ----

    /// <summary>
    /// The stopped ending as it opens, before any choice: one "top-row" line per row of the stopped list (name, state and the
    /// title the list draws, from TopLevelRowText.TitleOf), one "panel" line with the unreadable folders' paths, and one
    /// "store-marks" line counting the entries the stopped store marks not listed, independently of any row.
    /// </summary>
    internal static void LogEnding(Application app, Window window)
    {
        foreach (Dictionary<string, object?> row in Ui(app, () => TopRows(window)))
            Emit("top-row", row);
        Emit("panel", Ui(app, () => Panel(window)));
        Emit("store-marks", StoreMarks(window));
    }

    // TopLevelRow is Core's; the words that title it are the App's, in the window's own assembly.
    private static MethodInfo? TitleMethod(Window window) => window.GetType().Assembly.GetType("FsAnalyser.App.TopLevelRowText")
        ?.GetMethod("TitleOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    private static List<string?> Titles(Window window)
    {
        MethodInfo? title = TitleMethod(window);
        var titles = new List<string?>();
        foreach (object row in ((ListBox)window.FindName("TopLevelList")).Items)
            titles.Add(title?.Invoke(null, [row]) as string);
        return titles;
    }

    private static List<Dictionary<string, object?>> TopRows(Window window)
    {
        var rows = new List<Dictionary<string, object?>>();
        MethodInfo? title = TitleMethod(window);
        MethodInfo? figures = window.GetType().Assembly.GetType("FsAnalyser.App.TopLevelRowText")
            ?.GetMethod("FiguresOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        foreach (object row in ((ListBox)window.FindName("TopLevelList")).Items)
        {
            string? path = Prop(row, "Path") as string;
            Dictionary<string, object?>? store = RootChildOf(window, path);
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = Prop(row, "Name"),
                ["path"] = path,
                ["state"] = Prop(row, "State")?.ToString(),
                ["title"] = title?.Invoke(null, [row]),
                // SL-113: the second line the list draws (the row's figures), and what the store says of the same child of the
                // root, found by its path: the row carries no entry, and the list is sorted.
                ["figures"] = figures?.Invoke(null, [row]),
                ["isFolder"] = Prop(row, "IsFolder"),
                ["bytes"] = Prop(row, "Bytes"),
                ["onDisk"] = Prop(row, "OnDisk"),
                ["folders"] = Prop(row, "Folders"),
                ["unreadableBelow"] = Prop(row, "UnreadableBelow"),
                ["revised"] = Prop(row, "Revised"),
                ["rowAtLeast"] = Prop(row, "AtLeast") is { } bound
                    ? new Dictionary<string, object?> { ["size"] = Prop(bound, "Size"), ["onDisk"] = Prop(bound, "OnDisk"), ["files"] = Prop(bound, "Files") }
                    : null,
                ["entry"] = store?["entry"],
                ["storeUnfinished"] = store?["storeUnfinished"],
                ["storeNotListed"] = store?["storeNotListed"],
                ["storeAtLeast"] = store?["storeAtLeast"],
                ["storeRolled"] = store?["storeRolled"],
            });
        }
        return rows;
    }

    private static Dictionary<string, object?> Panel(Window window)
    {
        var list = (ListBox)window.FindName("UnreadableList");
        var paths = new List<string?>();
        var texts = new List<string?>();
        foreach (object item in list.Items)
        {
            paths.Add(Prop(item, "Path") as string);
            texts.Add(Prop(item, "Text") as string);
        }
        return new Dictionary<string, object?>
        {
            ["visible"] = window.FindName("UnreadableTab") is UIElement { Visibility: Visibility.Visible },
            ["header"] = (window.FindName("UnreadableTab") as TabItem)?.Header?.ToString(),
            ["rows"] = paths.Count,
            ["paths"] = paths,
            ["texts"] = texts,
        };
    }

    // ---- The stopped store, read by values only ----

    // Each of these resolves LastResult.Store on each call and lets go of it before it returns. They are not inlined, so no
    // local of a caller keeps the store alive however the JIT tiers it (review m2; TreeMode.WeakStore is the precedent).

    /// <summary>How many entries the store holds and how many of them it marks not listed.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<string, object?> StoreMarks(Window window)
    {
        object? store = StoreOf(window);
        if (store is null)
            return new() { ["entries"] = null, ["notListed"] = null };
        int count = (int)store.GetType().GetProperty("Count")!.GetValue(store)!;
        var notListed = (Func<int, bool>)store.GetType().GetMethod("NotListed", [typeof(int)])!.CreateDelegate(typeof(Func<int, bool>), store);
        int marked = 0;
        for (int entry = 0; entry < count; entry++)
        {
            if (notListed(entry))
                marked++;
        }
        return new() { ["entries"] = count, ["notListed"] = marked };
    }

    /// <summary>Whether the store marks the row's entry not listed; null for a row with no entry or a window with no store.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool? NotListedOf(Window? window, object? node)
    {
        if (window is null || EntryOf(node) is not int entry || StoreOf(window) is not { } store)
            return null;
        return (bool)store.GetType().GetMethod("NotListed", [typeof(int)])!.Invoke(store, [entry])!;
    }

    /// <summary>Whether the store reports the row's entry unfinished (SL-113); null for a row with no entry or a window with no store.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool? UnfinishedOf(Window? window, object? node)
    {
        if (window is null || EntryOf(node) is not int entry || StoreOf(window) is not { } store)
            return null;
        return (bool)store.GetType().GetMethod("Unfinished", [typeof(int)])!.Invoke(store, [entry])!;
    }

    /// <summary>
    /// What the store says the row's entry holds for certain (SL-113): its AtLeast read by property name, with the plain
    /// size texts the window's own formatter writes for those numbers. Null for a row with no entry or a window with no store.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<string, object?>? AtLeastOf(Window? window, object? node)
    {
        if (window is null || EntryOf(node) is not int entry || StoreOf(window) is not { } store)
            return null;
        return AtLeastValues(window, store, entry);
    }

    /// <summary>What the store says the row's entry rolls up to: RolledSize and RolledOnDisk, the figures a final row draws (SL-113).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<string, object?>? RolledOf(Window? window, object? node)
    {
        if (window is null || EntryOf(node) is not int entry || StoreOf(window) is not { } store)
            return null;
        return RolledValues(store, entry);
    }

    /// <summary>
    /// The store's answers for one child of the scan root, found by its path and never by its place in the list, since the
    /// list is sorted and a row carries no entry (SL-113): unfinished, not listed, AtLeast and the rolled figures. Null when
    /// the window has no store or no child of the root has that path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<string, object?>? RootChildOf(Window window, string? path)
    {
        if (path is null || StoreOf(window) is not { } store)
            return null;
        Type type = store.GetType();
        int count = (int)type.GetMethod("ChildCount", [typeof(int)])!.Invoke(store, [0])!;
        int first = (int)type.GetMethod("FirstChild", [typeof(int)])!.Invoke(store, [0])!;
        MethodInfo getPath = type.GetMethod("GetPath", [typeof(int)])!;
        for (int i = 0; i < count; i++)
        {
            int entry = first + i;
            if (!string.Equals((string)getPath.Invoke(store, [entry])!, path, StringComparison.Ordinal))
                continue;
            return new()
            {
                ["entry"] = entry,
                ["storeUnfinished"] = (bool)type.GetMethod("Unfinished", [typeof(int)])!.Invoke(store, [entry])!,
                ["storeNotListed"] = (bool)type.GetMethod("NotListed", [typeof(int)])!.Invoke(store, [entry])!,
                ["storeAtLeast"] = AtLeastValues(window, store, entry),
                ["storeRolled"] = RolledValues(store, entry),
            };
        }
        return null;
    }

    private static Dictionary<string, object?> AtLeastValues(Window window, object store, int entry)
    {
        object bound = store.GetType().GetMethod("AtLeast", [typeof(int)])!.Invoke(store, [entry])!;
        long size = (long)Prop(bound, "Size")!, onDisk = (long)Prop(bound, "OnDisk")!;
        int files = (int)Prop(bound, "Files")!;
        MethodInfo? format = window.GetType().Assembly.GetType("FsAnalyser.App.FigureText")
            ?.GetMethod("FormatSize", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        return new()
        {
            ["size"] = size,
            ["onDisk"] = onDisk,
            ["files"] = files,
            ["sizeText"] = format?.Invoke(null, [size]),
            ["onDiskText"] = format?.Invoke(null, [onDisk]),
        };
    }

    private static Dictionary<string, object?> RolledValues(object store, int entry)
    {
        Type type = store.GetType();
        return new()
        {
            ["size"] = (long)type.GetMethod("RolledSize", [typeof(int)])!.Invoke(store, [entry])!,
            ["onDisk"] = (long)type.GetMethod("RolledOnDisk", [typeof(int)])!.Invoke(store, [entry])!,
        };
    }

    /// <summary>How many of the node's children (once built) the store marks not listed; null before they are built.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int? NotListedChildren(Window? window, object? node)
    {
        if (window is null || StoreOf(window) is not { } store || Prop(node, "Children") is not IList children)
            return null;
        var notListed = (Func<int, bool>)store.GetType().GetMethod("NotListed", [typeof(int)])!.CreateDelegate(typeof(Func<int, bool>), store);
        FieldInfo? entryField = node!.GetType().GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic);
        int marked = 0;
        foreach (object? child in children)
        {
            if (child is not null && entryField?.GetValue(child) is int entry && notListed(entry))
                marked++;
        }
        return marked;
    }

    /// <summary>The path the store gives for the row's entry, which Open in Explorer must name; null without a store.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? StorePathOf(Window? window, object? node)
    {
        if (window is null || EntryOf(node) is not int entry || StoreOf(window) is not { } store)
            return null;
        return (string?)store.GetType().GetMethod("GetPath", [typeof(int)])!.Invoke(store, [entry]);
    }

    private static object? StoreOf(Window window)
    {
        object? result = window.GetType().GetProperty("LastResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(window);
        return result?.GetType().GetProperty("Store")?.GetValue(result);
    }

    private static int? EntryOf(object? node) =>
        node?.GetType().GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(node) as int?;

    // ---- Plumbing ----

    private static object? Prop(object? target, string name) =>
        target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);

    private static T Ui<T>(Application app, Func<T> work)
    {
        Exception? failed = null;
        T result = app.Dispatcher.Invoke(() =>
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
        }, DispatcherPriority.Send);
        if (failed is not null)
            throw new InvalidOperationException($"{failed.GetType().Name} on the UI thread: {failed.Message}");
        return result;
    }

    // Everything queued above ContextIdle has run: layout and render.
    private static void Settle(Application app) => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    internal static void Emit(string kind, Dictionary<string, object?> fields)
    {
        var line = new Dictionary<string, object?> { ["kind"] = kind };
        foreach ((string key, object? value) in fields)
            line[key] = value;
        StartupHook.Log(JsonSerializer.Serialize(line, Json));
    }
}
