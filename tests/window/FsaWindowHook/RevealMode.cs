using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class RevealMode
{
    private static Application _app = null!;
    private static readonly TimeSpan EndingDeadline = TimeSpan.FromSeconds(30);
    private const int Poll = 50;

    internal static void Drive()
    {
        if (StartupHook.WaitForApplication("reveal") is not { } app)
            return;
        _app = app;

        Window? window = null;
        try
        {
            string? revealEnv = Environment.GetEnvironmentVariable("FSA_HOOK_REVEAL");
            if (string.IsNullOrWhiteSpace(revealEnv))
            {
                StartupHook.Log("{\"error\":\"FSA_HOOK_REVEAL is empty\"}");
                return;
            }

            string[] targets = System.Text.RegularExpressions.Regex.Split(revealEnv, @",(?=(?:largest|tree|top|unreadable|gesture|tree-competing|placeholder):)");
            window = WaitForWindow();

            bool hasTopTarget = targets.Any(t => t.Trim().StartsWith("top:", StringComparison.OrdinalIgnoreCase));

            if (hasTopTarget)
            {
                // Stop the scan mid-scan so TopLevelList remains on screen
                WaitForScanToStart(window);
                StopScan(window);
                WaitForScanToStop(window);
            }
            else
            {
                // Wait for scan to complete normally
                WaitForScanToEnd(window);
            }

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

    private static void ProcessTarget(Window window, string surface, string name)
    {
        if (surface.Equals("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            ProcessPlaceholder(window, name);
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

        bool executed = Ui(() =>
        {
            if (via == "gesture")
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
            }
            else
            {
                ContextMenu? menu = container.ContextMenu ?? ContextMenuService.GetContextMenu(container) as ContextMenu;
                if (menu == null)
                    return false;

                menu.PlacementTarget = container;
                menu.Tag = container.DataContext;
                menu.IsOpen = true;
                container.UpdateLayout();

                MenuItem? menuItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m =>
                    (m.Header as string) == "Open in Explorer" ||
                    (m.Command as RoutedUICommand)?.Name == "OpenInExplorer");

                if (menuItem == null)
                {
                    menu.IsOpen = false;
                    return false;
                }

                var onClick = typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (onClick != null)
                {
                    onClick.Invoke(menuItem, null);
                }
                else
                {
                    menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }

                return true;
            }
        });

        if (!executed)
        {
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["name"] = name,
                ["via"] = via,
                ["error"] = "click-failed"
            }));
            return;
        }

        // Wait for RevealStatus to settle
        var sw = Stopwatch.StartNew();
        object? finalOutcome = null;
        string statusText = "";

        while (sw.ElapsedMilliseconds < 5000)
        {
            (object? outcome, string status) = Ui(() =>
            {
                var oc = GetLastOutcome(window);
                var tb = window.FindName("RevealStatus") as TextBlock;
                return (oc, tb?.Text ?? "");
            });

            if (outcome != null && !ReferenceEquals(outcome, initialOutcome) && status != "Opening in Explorer\u2026")
            {
                finalOutcome = outcome;
                statusText = status;
                break;
            }

            Thread.Sleep(Poll);
        }

        Ui(() =>
        {
            ContextMenu? menu = container.ContextMenu ?? ContextMenuService.GetContextMenu(container) as ContextMenu;
            if (menu != null)
                menu.IsOpen = false;
        });

        string kind = finalOutcome?.GetType().GetProperty("Kind")?.GetValue(finalOutcome)?.ToString() ?? "";
        string shown = finalOutcome?.GetType().GetProperty("Shown")?.GetValue(finalOutcome)?.ToString() ?? "";
        string reason = finalOutcome?.GetType().GetProperty("Reason")?.GetValue(finalOutcome)?.ToString() ?? "";
        int hresult = finalOutcome?.GetType().GetProperty("HResult")?.GetValue(finalOutcome) as int? ?? 0;
        string requested = finalOutcome?.GetType().GetProperty("Requested")?.GetValue(finalOutcome)?.ToString() ?? "";

        if (string.IsNullOrEmpty(requested))
        {
            requested = Ui(() =>
            {
                object? data = container.DataContext;
                return data?.GetType().GetProperty("Path")?.GetValue(data) as string
                    ?? data?.GetType().GetProperty("FullPath")?.GetValue(data) as string
                    ?? "";
            });
        }

        var entry = new Dictionary<string, object?>
        {
            ["surface"] = surface,
            ["name"] = name,
            ["path"] = requested,
            ["kind"] = kind,
            ["shown"] = shown,
            ["reason"] = reason,
            ["hresult"] = hresult,
            ["status"] = statusText,
            ["via"] = via
        };

        StartupHook.Log(JsonSerializer.Serialize(entry));
    }

    private static void ProcessPlaceholder(Window window, string name)
    {
        bool disabled = Ui(() =>
        {
            var cmd = window.GetType().GetField("OpenInExplorer", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as RoutedUICommand;
            if (cmd == null)
                return false;

            object? placeholderNode = null;
            if (window.FindName("FolderTree") is TreeView tree && tree.Items.Count > 0)
            {
                object rootNode = tree.Items[0];
                var children = rootNode.GetType().GetProperty("Children")?.GetValue(rootNode) as IEnumerable<object>;
                if (children != null)
                {
                    foreach (object child in children)
                    {
                        var childChildren = child.GetType().GetProperty("Children")?.GetValue(child) as IEnumerable<object>;
                        if (childChildren != null)
                        {
                            object? first = childChildren.FirstOrDefault();
                            if (first != null && (first.GetType().GetProperty("FullPath")?.GetValue(first) as string == ""))
                            {
                                placeholderNode = first;
                                break;
                            }
                        }
                    }
                }
            }

            var menu = new ContextMenu();
            var mi = new MenuItem
            {
                Header = "Open in Explorer",
                Command = cmd,
                CommandParameter = placeholderNode
            };
            menu.Items.Add(mi);
            menu.PlacementTarget = window;
            menu.IsOpen = true;
            window.UpdateLayout();
            bool isEnabled = mi.IsEnabled;
            menu.IsOpen = false;
            return !isEnabled;
        });

        StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["surface"] = "placeholder",
            ["name"] = name,
            ["disabled"] = disabled,
            ["enabled"] = !disabled
        }));
    }

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
        if (window.FindName("UnreadableExpander") is Expander exp)
        {
            exp.IsExpanded = true;
            exp.UpdateLayout();
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
