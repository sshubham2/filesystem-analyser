using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// Each target's line also carries revealStartQpc (SL-088): Stopwatch.GetTimestamp() as the Open in Explorer command
// starts to execute, taken in a class handler for the command's PreviewExecuted, which only reads, so a window case can
// time the landing it sees in Explorer from the click with the same system-wide clock (qpcFrequency ticks a second).
// With FSA_HOOK_REVEAL_GATE=<file>, once the scan has ended the hook writes <file>.ready and waits up to 60 s for <file>
// before the first target, so a case can change the tree the scan found, as a person's delete would, before the click.
internal static class RevealMode
{
    private static Application _app = null!;
    private static readonly TimeSpan EndingDeadline = TimeSpan.FromSeconds(30);
    private const int Poll = 50;
    private static long _revealStartQpc;

    internal static void Drive()
    {
        if (StartupHook.WaitForApplication("reveal") is not { } app)
            return;
        _app = app;

        Window? window = null;
        try
        {
            // SL-110: FSA_HOOK_TREE_STOP stops the scan at a folder the case names and chooses the tree beside the stopped list,
            // before the targets run; armed here because the window's Loaded handler starts the scan.
            StoppedTree.Run? stop = StoppedTree.Arm(app);
            Ui(() => EventManager.RegisterClassHandler(typeof(Window), CommandManager.PreviewExecutedEvent,
                new ExecutedRoutedEventHandler(OnPreviewExecuted), handledEventsToo: true));

            string? revealEnv = Environment.GetEnvironmentVariable("FSA_HOOK_REVEAL");
            if (string.IsNullOrWhiteSpace(revealEnv))
            {
                StartupHook.Log("{\"error\":\"FSA_HOOK_REVEAL is empty\"}");
                return;
            }

            string[] targets = System.Text.RegularExpressions.Regex.Split(revealEnv, @",(?=(?:largest|tree|top|top-live|top-key|unreadable|gesture|tree-competing|placeholder):)");
            window = WaitForWindow();

            bool hasTopTarget = targets.Any(t => t.Trim().StartsWith("top:", StringComparison.OrdinalIgnoreCase));
            bool hasTopLive = targets.Any(t => t.Trim().StartsWith("top-live:", StringComparison.OrdinalIgnoreCase)
                || t.Trim().StartsWith("top-key:", StringComparison.OrdinalIgnoreCase));

            if (hasTopTarget)
            {
                string? topTarget = targets.FirstOrDefault(t => t.Trim().StartsWith("top:", StringComparison.OrdinalIgnoreCase));
                int c = topTarget!.IndexOf(':');
                string targetName = topTarget[(c + 1)..].Trim();

                // Stop the scan mid-scan after the target row has appeared in TopLevelList
                WaitForScanToStart(window);
                WaitForTopRow(window, targetName);
                StopScan(window);
                WaitForScanToStop(window);
            }
            else if (!hasTopLive)
            {
                // Wait for scan to complete normally
                WaitForScanToEnd(window);
            }

            if (stop is not null)
            {
                if (hasTopTarget || hasTopLive)
                    throw new InvalidOperationException("FSA_HOOK_TREE_STOP stops the scan itself: it takes tree: targets, not top: or top-live: ones");
                stop.Join();
                if (!stop.Pressed)
                    throw new InvalidOperationException($"tree-stop: the stop did not land ({stop.Reason}): this run is void");
                StoppedTree.Choose(app, window);
            }

            if (Environment.GetEnvironmentVariable("FSA_HOOK_REVEAL_GATE") is { Length: > 0 } gate)
                WaitAtGate(gate);

            foreach (string target in targets)
            {
                int colon = target.IndexOf(':');
                if (colon < 0)
                    continue;

                string surface = target[..colon].Trim();
                string name = target[(colon + 1)..];

                ProcessTarget(window, surface, name);
            }
        }
        catch (Exception e)
        {
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["error"] = $"{e.GetType().Name}: {e.Message}"
            }));
        }
        finally
        {
            Ui(() =>
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                }
            });
        }
    }

    private static void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command is RoutedUICommand { Name: "OpenInExplorer" })
            Interlocked.Exchange(ref _revealStartQpc, Stopwatch.GetTimestamp());
    }

    private static void WaitAtGate(string gate)
    {
        File.WriteAllText(gate + ".ready", "");
        var since = Stopwatch.StartNew();
        while (!File.Exists(gate))
        {
            if (since.Elapsed > TimeSpan.FromSeconds(60))
                throw new InvalidOperationException($"FSA_HOOK_REVEAL_GATE: {gate} did not appear within 60 s.");
            Thread.Sleep(10);
        }
    }

    private static void ProcessTarget(Window window, string surface, string name)
    {
        if (surface.Equals("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            ProcessPlaceholder(window, name);
            return;
        }

        if (surface.Equals("top-live", StringComparison.OrdinalIgnoreCase))
        {
            ProcessTopLive(window, name);
            return;
        }

        if (surface.Equals("top-key", StringComparison.OrdinalIgnoreCase))
        {
            ProcessTopKey(window, name);
            return;
        }

        string via = "menu";
        FrameworkElement? container = null;

        if (surface.Equals("gesture", StringComparison.OrdinalIgnoreCase))
        {
            via = "gesture";
            container = Ui(() => FindContainer(window, "tree", name) ?? FindContainer(window, "largest", name) ?? FindContainer(window, "top", name));
        }
        else if (surface.Equals("tree-competing", StringComparison.OrdinalIgnoreCase))
        {
            container = Ui(() =>
            {
                if (window.FindName("FolderTree") is TreeView tree && tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem rootItem)
                {
                    rootItem.IsSelected = true;
                    rootItem.UpdateLayout();
                }
                return FindTreeContainer(window, name);
            });
        }
        else
        {
            container = Ui(() => FindContainer(window, surface, name));
        }

        if (container == null)
        {
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["name"] = name,
                ["error"] = "row-not-found"
            }));
            return;
        }

        object? initialOutcome = Ui(() => GetLastOutcome(window));
        Interlocked.Exchange(ref _revealStartQpc, 0);

        // The menu is opened as ContextMenuService opens it on a right-click (its PlacementTarget, then IsOpen) and is
        // left to the app: the row it acts on is whatever the app's own Opened handler put in its Tag, which is read and
        // logged as "captured", never written, here (B3-3). The click comes only once the dispatcher has run everything
        // the opening queued, as it has by the time a person's click lands.
        bool? captured = null;
        bool? enabled = null;
        bool executed;
        if (via == "gesture")
        {
            executed = Ui(() =>
            {
                container.Focus();
                Keyboard.Focus(container);

                var cmd = window.GetType().GetField("OpenInExplorer", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as RoutedUICommand;
                if (cmd == null)
                    return false;

                bool hasGesture = cmd.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.Enter && g.Modifiers == ModifierKeys.Control);
                if (!hasGesture)
                    return false;

                if (!cmd.CanExecute(null, window))
                    return false;

                cmd.Execute(null, window);
                return true;
            });
        }
        else
        {
            ContextMenu? menu = Ui(() => OpenMenu(container));
            MenuItem? item = null;
            if (menu != null)
            {
                Flush();
                (captured, enabled, item) = Ui(() =>
                {
                    MenuItem? found = ItemOf(menu);
                    return ((bool?)(container.DataContext != null && ReferenceEquals(menu.Tag, container.DataContext)), (bool?)found?.IsEnabled, found);
                });
            }
            executed = item != null && Ui(() => Click(item));
        }

        if (!executed)
        {
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["name"] = name,
                ["via"] = via,
                ["captured"] = captured,
                ["enabled"] = enabled,
                ["error"] = "click-failed"
            }));
            return;
        }

        (object? finalOutcome, string statusText) = WaitForOutcome(window, initialOutcome);

        Ui(() =>
        {
            if (MenuOf(container) is { } menu)
                menu.IsOpen = false;
        });

        var entry = OutcomeEntry(surface, name, finalOutcome, statusText, container);
        entry["via"] = via;
        entry["captured"] = captured;
        entry["enabled"] = enabled;
        // SL-110: what a tree row says about itself and what the store marks for it, so a case can void a run whose target
        // was listed after all (a row that is not 'not read yet' proves nothing about a reveal of one).
        object? data = Ui(() => container.DataContext);
        entry["note"] = Ui(() => data?.GetType().GetProperty("Note")?.GetValue(data) as string);
        entry["storeNotListed"] = Ui(() => StoppedTree.NotListedOf(window, data));
        StartupHook.Log(JsonSerializer.Serialize(entry));
    }

    // top-live (B3-1): 'Open in Explorer' from a results-list row while the scan is still running. The menu is opened on
    // the row, then left open until a live refresh has replaced the list's items and so detached the row's container,
    // as it does for a person who right-clicks mid-scan and reads the menu. Then the entry is re-queried, as the
    // CommandManager does after input, and clicked. The line logs what the premise needs (the container no longer holds
    // the row, "detached", while the scan still runs, "scanning") beside what the fix must give: the entry still enabled
    // and a reveal of the row the menu was opened on.
    private static void ProcessTopLive(Window window, string name)
    {
        WaitForScanToStart(window);
        WaitForTopRow(window, name);

        FrameworkElement? container = Ui(() => FindTopContainer(window, name));
        object? row = container == null ? null : Ui(() => container.DataContext);
        ContextMenu? menu = container == null ? null : Ui(() => OpenMenu(container));
        if (container == null || row == null || menu == null)
        {
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["surface"] = "top-live",
                ["name"] = name,
                ["error"] = container == null ? "row-not-found" : "open-failed"
            }));
            StopScan(window);
            WaitForScanToStop(window);
            return;
        }

        object? initialOutcome = Ui(() => GetLastOutcome(window));
        Interlocked.Exchange(ref _revealStartQpc, 0);
        Flush();
        bool captured = Ui(() => ReferenceEquals(menu.Tag, row));

        bool detached = false;
        bool sentinel = false;
        bool scanning = false;
        var since = Stopwatch.StartNew();
        while (since.Elapsed < TimeSpan.FromSeconds(10))
        {
            (detached, sentinel, scanning) = Ui(() => (
                !ReferenceEquals(container.DataContext, row),
                ReferenceEquals(container.DataContext, BindingOperations.DisconnectedSource),
                IsScanning(window)));
            if (detached)
                break;
            Thread.Sleep(10);
        }

        Ui(() => CommandManager.InvalidateRequerySuggested());
        Flush();
        (bool enabled, bool open, MenuItem? item) = Ui(() =>
        {
            MenuItem? found = ItemOf(menu);
            return (found?.IsEnabled ?? false, menu.IsOpen, found);
        });
        bool clicked = item != null && Ui(() => Click(item));
        (object? finalOutcome, string statusText) = clicked ? WaitForOutcome(window, initialOutcome) : (null, "");

        Ui(() => menu.IsOpen = false);
        StopScan(window);
        WaitForScanToStop(window);

        var entry = OutcomeEntry("top-live", name, finalOutcome, statusText, null);
        entry["rowPath"] = row.GetType().GetProperty("Path")?.GetValue(row) as string;
        entry["via"] = "menu";
        entry["captured"] = captured;
        entry["detached"] = detached;
        entry["sentinel"] = sentinel;
        entry["scanning"] = scanning;
        entry["enabled"] = enabled;
        entry["open"] = open;
        StartupHook.Log(JsonSerializer.Serialize(entry));
    }

    // top-key (SL-100, Q-078): Ctrl+Enter on a results-list row after live refreshes. A row that is still being read is
    // selected and given the keyboard focus, as a click on it leaves it; the scan's own ticks then replace the list's
    // items three times; once the dispatcher has run what the third swap queued (Flush: Ui() marshals at Normal priority,
    // above the Loaded priority the restore runs at, so a read without it can fall between the swap and the restore),
    // the command's own KeyGesture is raised on the focused element and the reveal is waited for. The line logs what the
    // fix must give: the focus on the same row's container ("focusPath"), the row still selected ("selectedPath"), the
    // command enabled from the focus ("canExecute"), and a reveal of the row's path beside "rowPath". name is a row's
    // name, or * for the first row that is still being read and has a container at the current offset. A run that
    // cannot make its premise logs a line of its own: scan-ended (the list was replaced before three refreshes, with
    // "refreshes" counted), no-reading-row, row-not-found, row-not-realised, focus-failed (with "active"), no-gesture.
    private static void ProcessTopKey(Window window, string name)
    {
        WaitForScanToStart(window);

        FrameworkElement? container = null;
        string? error = null;
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < EndingDeadline)
        {
            (container, error) = Ui(() => FindKeyContainer(window, name));
            if (container != null || error != "no-reading-row")
                break;
            if (!Ui(() => IsScanning(window)))
                break;
            Thread.Sleep(10);
        }
        if (container == null)
        {
            LogTopKeyError(name, error ?? "no-reading-row", null);
            StopScan(window);
            WaitForScanToStop(window);
            return;
        }

        (string? rowPath, bool active, bool focused) = Ui(() =>
        {
            var list = (ListBox)window.FindName("TopLevelList");
            list.SelectedItem = container.DataContext;
            container.Focus();
            Keyboard.Focus(container);
            return (PathOfRow(container.DataContext), window.IsActive, container.IsKeyboardFocused);
        });
        if (!focused)
        {
            LogTopKeyError(name, "focus-failed", new Dictionary<string, object?> { ["rowPath"] = rowPath, ["active"] = active });
            StopScan(window);
            WaitForScanToStop(window);
            return;
        }

        object? seen = Ui(() => ((ListBox)window.FindName("TopLevelList")).ItemsSource);
        int refreshes = 0;
        var since = Stopwatch.StartNew();
        while (refreshes < 3 && since.Elapsed < TimeSpan.FromSeconds(10))
        {
            (object? now, bool scanning) = Ui(() => (((ListBox)window.FindName("TopLevelList")).ItemsSource, IsScanning(window)));
            if (now == null || !scanning)
                break;
            if (!ReferenceEquals(now, seen))
            {
                refreshes++;
                seen = now;
            }
            Thread.Sleep(10);
        }
        if (refreshes < 3)
        {
            LogTopKeyError(name, "scan-ended", new Dictionary<string, object?> { ["rowPath"] = rowPath, ["refreshes"] = refreshes });
            StopScan(window);
            WaitForScanToStop(window);
            return;
        }

        Flush();
        object? initialOutcome = Ui(() => GetLastOutcome(window));
        Interlocked.Exchange(ref _revealStartQpc, 0);
        (string focusType, string? focusPath, string? selectedPath, bool canExecute, bool pressed) = Ui(() =>
        {
            IInputElement? focus = Keyboard.FocusedElement;
            var cmd = window.GetType().GetField("OpenInExplorer", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as RoutedUICommand;
            bool can = cmd != null && focus != null && cmd.CanExecute(null, focus);
            bool sent = cmd != null && cmd.InputGestures.OfType<KeyGesture>().Any(g => g.Key == Key.Enter && g.Modifiers == ModifierKeys.Control)
                && PressCtrlEnter(window);
            return (focus?.GetType().Name ?? "nothing",
                PathOfRow((focus as FrameworkElement)?.DataContext),
                PathOfRow(((ListBox)window.FindName("TopLevelList")).SelectedItem),
                can, sent);
        });

        (object? finalOutcome, string statusText) = pressed ? WaitForOutcome(window, initialOutcome) : (null, "");
        StopScan(window);
        WaitForScanToStop(window);

        var entry = OutcomeEntry("top-key", name, finalOutcome, statusText, null);
        entry["via"] = "gesture";
        entry["rowPath"] = rowPath;
        entry["active"] = active;
        entry["refreshes"] = refreshes;
        entry["focusType"] = focusType;
        entry["focusPath"] = focusPath;
        entry["selectedPath"] = selectedPath;
        entry["canExecute"] = canExecute;
        entry["pressed"] = pressed;
        if (!pressed)
            entry["error"] = "no-gesture";
        StartupHook.Log(JsonSerializer.Serialize(entry));
    }

    private static void LogTopKeyError(string name, string error, Dictionary<string, object?>? more)
    {
        var entry = new Dictionary<string, object?> { ["surface"] = "top-key", ["name"] = name, ["error"] = error };
        if (more != null)
            foreach (KeyValuePair<string, object?> pair in more)
                entry[pair.Key] = pair.Value;
        StartupHook.Log(JsonSerializer.Serialize(entry));
    }

    // The row to press on, without scrolling: a named row, or the first one still being read, that has a container at
    // the list's current offset. The error says why none was found.
    private static (FrameworkElement? Container, string? Error) FindKeyContainer(Window window, string name)
    {
        if (window.FindName("TopLevelList") is not ListBox list)
            return (null, "row-not-found");
        bool named = name != "*";
        bool anyRow = false;
        foreach (object item in list.Items)
        {
            anyRow = true;
            string? rowName = item.GetType().GetProperty("Name")?.GetValue(item) as string;
            bool match = named
                ? rowName == name || PathOfRow(item) == name || (PathOfRow(item) is { } p && p.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                : item.GetType().GetProperty("State")?.GetValue(item)?.ToString() == "Reading";
            if (!match)
                continue;
            return list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement found
                ? (found, null)
                : (null, "row-not-realised");
        }
        return (null, named ? (anyRow ? "row-not-found" : "no-reading-row") : "no-reading-row");
    }

    private static string? PathOfRow(object? row) => row?.GetType().GetProperty("Path")?.GetValue(row) as string;

    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    // Ctrl held in this thread's key state, then a KeyDown(Enter) raised on the focused element: CommandManager matches
    // the command's own KeyGesture as it does for a real press. The key state is put back after.
    private static bool PressCtrlEnter(Window window)
    {
        if (Keyboard.FocusedElement is not UIElement target)
            return false;
        var saved = new byte[256];
        GetKeyboardState(saved);
        var held = (byte[])saved.Clone();
        held[0x11] = 0x80; // VK_CONTROL
        held[0xA2] = 0x80; // VK_LCONTROL
        SetKeyboardState(held);
        try
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            return true;
        }
        finally
        {
            SetKeyboardState(saved);
        }
    }

    // placeholder (m3, B3-3): the row that stands in for a folder's children until it is opened, with the app's own
    // RowContextMenu on its own container. The window replaces that row inside its Expanded handler, before layout, so
    // no container of it is ever drawn; to draw one, the hook opens a folder of the root with that handler held back
    // (a handler on the folder's own container marks Expanded handled, and is removed straight after). The folder's
    // children are left unread. The line logs whether a real placeholder container was found and its menu opened
    // ("found"), whether the app's Opened handler captured the placeholder ("captured"), and whether the entry is
    // disabled after a re-query ("disabled").
    private static void ProcessPlaceholder(Window window, string name)
    {
        (TreeViewItem? folder, TreeViewItem? placeholder) = Ui(() => RealizePlaceholder(window));
        object? node = placeholder == null ? null : Ui(() => placeholder.DataContext);
        bool isPlaceholder = node != null && node.GetType().GetProperty("FullPath")?.GetValue(node) as string == "";
        ContextMenu? menu = placeholder == null || !isPlaceholder ? null : Ui(() => OpenMenu(placeholder));

        bool captured = false;
        bool disabled = false;
        if (menu != null)
        {
            Flush();
            Ui(() => CommandManager.InvalidateRequerySuggested());
            Flush();
            (captured, disabled) = Ui(() => (ReferenceEquals(menu.Tag, node), ItemOf(menu) is { IsEnabled: false }));
            Ui(() => menu.IsOpen = false);
        }
        if (folder != null)
            Ui(() => folder.IsExpanded = false);

        StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["surface"] = "placeholder",
            ["name"] = name,
            ["found"] = menu != null,
            ["captured"] = captured,
            ["disabled"] = disabled,
            ["enabled"] = menu != null && !disabled
        }));
    }

    private static (TreeViewItem? Folder, TreeViewItem? Placeholder) RealizePlaceholder(Window window)
    {
        if (window.FindName("FolderTree") is not TreeView tree || tree.Items.Count == 0)
            return (null, null);
        tree.UpdateLayout();
        if (tree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem rootItem)
            return (null, null);

        object rootNode = tree.Items[0];
        if (rootNode.GetType().GetProperty("Children")?.GetValue(rootNode) is not IEnumerable<object> children)
            return (null, null);
        int index = 0;
        foreach (object child in children)
        {
            object? first = (child.GetType().GetProperty("Children")?.GetValue(child) as IEnumerable<object>)?.FirstOrDefault();
            if (first != null && first.GetType().GetProperty("FullPath")?.GetValue(first) as string == "")
            {
                TreeViewItem? folder = Realize(rootItem, index);
                if (folder == null)
                    return (null, null);
                RoutedEventHandler holdBack = (_, e) => e.Handled = true;
                folder.AddHandler(TreeViewItem.ExpandedEvent, holdBack);
                try
                {
                    folder.IsExpanded = true;
                    folder.UpdateLayout();
                }
                finally
                {
                    folder.RemoveHandler(TreeViewItem.ExpandedEvent, holdBack);
                }
                return (folder, Realize(folder, 0));
            }
            index++;
        }
        return (null, null);
    }

    private static ContextMenu? MenuOf(FrameworkElement container) =>
        container.ContextMenu ?? ContextMenuService.GetContextMenu(container) as ContextMenu;

    private static MenuItem? ItemOf(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(m =>
            (m.Header as string) == "Open in Explorer" ||
            (m.Command as RoutedUICommand)?.Name == "OpenInExplorer");

    // As ContextMenuService opens a row's menu on a right-click: its PlacementTarget, then IsOpen.
    private static ContextMenu? OpenMenu(FrameworkElement container)
    {
        if (MenuOf(container) is not { } menu)
            return null;
        menu.PlacementTarget = container;
        menu.IsOpen = true;
        return menu;
    }

    // A click on the menu item, as the mouse's raises it: MenuItem.OnClick, which runs the command only if it can
    // execute then.
    private static bool Click(MenuItem item)
    {
        var onClick = typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (onClick != null)
            onClick.Invoke(item, null);
        else
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        return true;
    }

    // Everything already queued on the UI thread, down to Background (layout, the popup's opening, a requery), has run.
    private static void Flush() => _app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static bool IsScanning(Window window) =>
        (window.FindName("ScanningPanel") as UIElement)?.Visibility == Visibility.Visible;

    // Waits up to 5 s for a reveal outcome newer than the one before the click and a status that has settled.
    private static (object? Outcome, string Status) WaitForOutcome(Window window, object? initialOutcome)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            (object? outcome, string status) = Ui(() =>
            {
                var oc = GetLastOutcome(window);
                var tb = window.FindName("RevealStatus") as TextBlock;
                return (oc, tb?.Text ?? "");
            });

            if (outcome != null && !ReferenceEquals(outcome, initialOutcome) && status != "Opening in Explorer…")
                return (outcome, status);

            Thread.Sleep(Poll);
        }
        return (null, "");
    }

    private static Dictionary<string, object?> OutcomeEntry(string surface, string name, object? outcome, string status, FrameworkElement? container)
    {
        string kind = outcome?.GetType().GetProperty("Kind")?.GetValue(outcome)?.ToString() ?? "";
        string shown = outcome?.GetType().GetProperty("Shown")?.GetValue(outcome)?.ToString() ?? "";
        string reason = outcome?.GetType().GetProperty("Reason")?.GetValue(outcome)?.ToString() ?? "";
        int hresult = outcome?.GetType().GetProperty("HResult")?.GetValue(outcome) as int? ?? 0;
        string requested = outcome?.GetType().GetProperty("Requested")?.GetValue(outcome)?.ToString() ?? "";
        // SL-091: the settings that hid the item ("None", "HiddenFiles", "ProtectedFiles" or both), and the Explorer
        // view the reveal was decided under, as <hidden>|<protected>, null when none was read; beside the view this
        // run injected (FSA_HOOK_EXPLORER_VIEW), null when none was.
        string hiddenBy = outcome?.GetType().GetProperty("HiddenBy")?.GetValue(outcome)?.ToString() ?? "";
        object? view = outcome?.GetType().GetProperty("View")?.GetValue(outcome);
        string? effective = view is null ? null : $"{Bit(view, "ShowsHidden")}|{Bit(view, "ShowsProtected")}";
        string? injected = Environment.GetEnvironmentVariable("FSA_HOOK_EXPLORER_VIEW") is { Length: > 0 } armed ? armed : null;

        if (string.IsNullOrEmpty(requested) && container != null)
        {
            requested = Ui(() =>
            {
                object? data = container.DataContext;
                return data?.GetType().GetProperty("Path")?.GetValue(data) as string
                    ?? data?.GetType().GetProperty("FullPath")?.GetValue(data) as string
                    ?? "";
            });
        }

        return new Dictionary<string, object?>
        {
            ["surface"] = surface,
            ["name"] = name,
            ["path"] = requested,
            ["kind"] = kind,
            ["shown"] = shown,
            ["reason"] = reason,
            ["hresult"] = hresult,
            ["status"] = status,
            ["hiddenBy"] = hiddenBy,
            ["view"] = effective,
            ["injected"] = injected,
            ["revealStartQpc"] = Interlocked.Read(ref _revealStartQpc),
            ["qpcFrequency"] = Stopwatch.Frequency
        };
    }

    private static string Bit(object view, string property) =>
        view.GetType().GetProperty(property)?.GetValue(view) is true ? "1" : "0";

    private static FrameworkElement? FindContainer(Window window, string surface, string name)
    {
        return surface.ToLowerInvariant() switch
        {
            "largest" => FindLargestContainer(window, name),
            "tree" => FindTreeContainer(window, name),
            "top" => FindTopContainer(window, name),
            "unreadable" => FindUnreadableContainer(window, name),
            _ => null
        };
    }

    private static FrameworkElement? FindLargestContainer(Window window, string name)
    {
        if (window.FindName("LargestFilesList") is not ListBox list)
            return null;

        list.UpdateLayout();
        foreach (object item in list.Items)
        {
            string? path = item.GetType().GetProperty("Path")?.GetValue(item) as string;
            if (path != null && (Path.GetFileName(path) == name || path == name || path.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                list.ScrollIntoView(item);
                list.UpdateLayout();
                return list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            }
        }
        return null;
    }

    private static FrameworkElement? FindTopContainer(Window window, string name)
    {
        if (window.FindName("TopLevelList") is not ListBox list)
            return null;

        list.UpdateLayout();
        foreach (object item in list.Items)
        {
            string? rowName = item.GetType().GetProperty("Name")?.GetValue(item) as string;
            string? path = item.GetType().GetProperty("Path")?.GetValue(item) as string;
            if (rowName == name || path == name || (path != null && path.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                list.ScrollIntoView(item);
                list.UpdateLayout();
                return list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            }
        }
        return null;
    }

    private static FrameworkElement? FindUnreadableContainer(Window window, string name)
    {
        // Their list is on screen only while their tab is selected (SL-065; before, while their expander was open).
        if (window.FindName("UnreadableTab") is TabItem tab)
        {
            tab.IsSelected = true;
            tab.UpdateLayout();
        }

        if (window.FindName("UnreadableList") is not ListBox list)
            return null;

        list.UpdateLayout();
        foreach (object item in list.Items)
        {
            string? path = item.GetType().GetProperty("Path")?.GetValue(item) as string;
            string? text = item.GetType().GetProperty("Text")?.GetValue(item) as string;
            if (path == name || (path != null && (Path.GetFileName(path) == name || path.EndsWith(name, StringComparison.OrdinalIgnoreCase))) || (text != null && text.Contains(name)))
            {
                list.ScrollIntoView(item);
                list.UpdateLayout();
                return list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            }
        }
        return null;
    }

    private static FrameworkElement? FindTreeContainer(Window window, string name)
    {
        if (window.FindName("FolderTree") is not TreeView tree)
            return null;

        tree.UpdateLayout();
        if (tree.Items.Count == 0)
            return null;

        object rootNode = tree.Items[0];
        string? rootName = rootNode.GetType().GetProperty("Name")?.GetValue(rootNode) as string;
        string? rootPath = rootNode.GetType().GetProperty("FullPath")?.GetValue(rootNode) as string;

        var rootContainer = tree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
        if (rootContainer != null)
        {
            rootContainer.IsExpanded = true;
            rootContainer.UpdateLayout();
        }

        if (name == "root" || rootName == name || rootPath == name || (rootPath != null && rootPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            return rootContainer;

        if (rootContainer == null)
            return null;

        return FindInTreeItem(rootContainer, rootNode, name);
    }

    private static FrameworkElement? FindInTreeItem(TreeViewItem parentContainer, object parentNode, string name)
    {
        parentNode.GetType().GetMethod("EnsureChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(parentNode, null);
        var children = parentNode.GetType().GetProperty("Children")?.GetValue(parentNode) as IEnumerable<object>;
        if (children == null)
            return null;

        // SL-110: a child of this folder that is the target, by its name or its whole path, is taken before any sibling's
        // subtree is opened. A target at the foot of a long list (a folder the stop came before sorts last, with nothing
        // on disk) would otherwise be reached only after every sibling above it was opened, c-prefix's 5,000 included.
        int direct = 0;
        foreach (object child in children)
        {
            string? childName = child.GetType().GetProperty("Name")?.GetValue(child) as string;
            string? childPath = child.GetType().GetProperty("FullPath")?.GetValue(child) as string;
            if (childName == name || childPath == name)
                return Realize(parentContainer, direct);
            direct++;
        }

        int index = 0;
        foreach (object child in children)
        {
            string? childName = child.GetType().GetProperty("Name")?.GetValue(child) as string;
            string? childPath = child.GetType().GetProperty("FullPath")?.GetValue(child) as string;

            if (childName == name || childPath == name || (childPath != null && childPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                return Realize(parentContainer, index);
            }

            var childContainer = Realize(parentContainer, index);
            if (childContainer != null)
            {
                childContainer.IsExpanded = true;
                childContainer.UpdateLayout();
                var found = FindInTreeItem(childContainer, child, name);
                if (found != null)
                    return found;
            }
            index++;
        }
        return null;
    }

    private static TreeViewItem? Realize(ItemsControl owner, int index)
    {
        if (index >= owner.Items.Count)
            return null;
        if (owner.ItemContainerGenerator.ContainerFromIndex(index) is TreeViewItem found)
            return found;
        if (ItemsHost(owner) is { } panel)
        {
            panel.BringIndexIntoViewPublic(index);
            owner.UpdateLayout();
        }
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
        owner is TreeViewItem item ? item.Template?.FindName("ItemsHost", item) as ItemsPresenter : FindPresenter(owner);

    private static ItemsPresenter? FindPresenter(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ItemsPresenter p)
                return p;
            if (FindPresenter(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static object? GetLastOutcome(Window window) =>
        window.GetType().GetProperty("LastRevealOutcome", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(window);

    private static Window WaitForWindow()
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < EndingDeadline)
        {
            if (Ui(() => _app.MainWindow is { IsLoaded: true } ? _app.MainWindow : null) is { } window)
                return window;
            Thread.Sleep(Poll);
        }
        throw new InvalidOperationException("Main window did not load.");
    }

    private static void WaitForScanToEnd(Window window)
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < EndingDeadline)
        {
            (bool scanning, bool outcome) = Ui(() => (
                (window.FindName("ScanningPanel") as UIElement)?.Visibility == Visibility.Visible,
                (window.FindName("OutcomePanel") as UIElement)?.Visibility == Visibility.Visible
            ));

            if (!scanning && outcome)
                return;

            Thread.Sleep(Poll);
        }
        throw new InvalidOperationException("Scan did not complete within deadline.");
    }

    private static void WaitForScanToStart(Window window)
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < EndingDeadline)
        {
            (bool scanning, bool stoppedOrDone) = Ui(() => (
                (window.FindName("ScanningPanel") as UIElement)?.Visibility == Visibility.Visible,
                (window.FindName("OutcomePanel") as UIElement)?.Visibility == Visibility.Visible
            ));
            if (scanning || stoppedOrDone)
                return;
            Thread.Sleep(10);
        }
    }

    private static void WaitForTopRow(Window window, string targetName)
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < EndingDeadline)
        {
            bool ready = Ui(() =>
            {
                if (window.FindName("TopLevelList") is not ListBox list)
                    return false;
                foreach (object item in list.Items)
                {
                    string? name = item.GetType().GetProperty("Name")?.GetValue(item) as string;
                    if (name == targetName || (name != null && name.EndsWith(targetName, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                return false;
            });
            if (ready)
                return;
            Thread.Sleep(10);
        }
    }

    private static void StopScan(Window window)
    {
        Ui(() =>
        {
            if (window.FindName("CancelButton") is Button button && button.IsEnabled && (window.FindName("ScanningPanel") as UIElement)?.Visibility == Visibility.Visible)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
        });
    }

    private static void WaitForScanToStop(Window window)
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < EndingDeadline)
        {
            bool scanning = Ui(() => (window.FindName("ScanningPanel") as UIElement)?.Visibility == Visibility.Visible);
            if (!scanning)
                return;
            Thread.Sleep(Poll);
        }
    }

    private static T Ui<T>(Func<T> work)
    {
        Exception? error = null;
        T result = _app.Dispatcher.Invoke(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                error = ex;
                return default!;
            }
        }, DispatcherPriority.Normal);

        if (error != null)
            throw new InvalidOperationException($"Error on UI thread: {error.Message}", error);

        return result;
    }

    private static void Ui(Action work) => Ui(() => { work(); return true; });
}
