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

            string[] targets = System.Text.RegularExpressions.Regex.Split(revealEnv, @",(?=(?:largest|tree|top|unreadable):)");
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
        FrameworkElement? container = Ui(() => FindContainer(window, surface, name));

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

        bool clicked = Ui(() =>
        {
            ContextMenu? menu = container.ContextMenu ?? ContextMenuService.GetContextMenu(container) as ContextMenu;
            if (menu == null)
                return false;

            menu.PlacementTarget = container;
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

            // Click the menu item exercising Command and CommandParameter binding
            var onClick = typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (onClick != null)
            {
                onClick.Invoke(menuItem, null);
            }
            else
            {
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }

            var tb = window.FindName("RevealStatus") as TextBlock;
            if (tb?.Text != "Opening in Explorer\u2026" && GetLastOutcome(window) == initialOutcome)
            {
                if (menuItem.Command is RoutedCommand routed)
                {
                    IInputElement target = menuItem.CommandTarget ?? container;
                    if (routed.CanExecute(menuItem.CommandParameter, target))
                    {
                        routed.Execute(menuItem.CommandParameter, target);
                    }
                }
            }

            return true;
        });

        if (!clicked)
        {
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["name"] = name,
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
            ["status"] = statusText
        };

        StartupHook.Log(JsonSerializer.Serialize(entry));
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

        if (rootName == name || rootPath == name || (rootPath != null && rootPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            return rootContainer;

        // Ensure children are generated
        rootNode.GetType().GetMethod("EnsureChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(rootNode, null);
        rootContainer?.UpdateLayout();

        var children = rootNode.GetType().GetProperty("Children")?.GetValue(rootNode) as IEnumerable<object>;
        if (children == null || rootContainer == null)
            return null;

        int index = 0;
        foreach (object child in children)
        {
            string? childName = child.GetType().GetProperty("Name")?.GetValue(child) as string;
            string? childPath = child.GetType().GetProperty("FullPath")?.GetValue(child) as string;

            if (childName == name || childPath == name || (childPath != null && childPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                return Realize(rootContainer, index);
            }

            // Check if nested child matches
            child.GetType().GetMethod("EnsureChildren", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(child, null);
            var nestedContainer = Realize(rootContainer, index);
            if (nestedContainer != null)
            {
                nestedContainer.IsExpanded = true;
                nestedContainer.UpdateLayout();
                var grandchildren = child.GetType().GetProperty("Children")?.GetValue(child) as IEnumerable<object>;
                if (grandchildren != null)
                {
                    int gIndex = 0;
                    foreach (object gc in grandchildren)
                    {
                        string? gcName = gc.GetType().GetProperty("Name")?.GetValue(gc) as string;
                        string? gcPath = gc.GetType().GetProperty("FullPath")?.GetValue(gc) as string;
                        if (gcName == name || gcPath == name || (gcPath != null && gcPath.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            return Realize(nestedContainer, gIndex);
                        }
                        gIndex++;
                    }
                }
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
            bool scanning = Ui(() => (window.FindName("ScanningPanel") as UIElement)?.Visibility == Visibility.Visible);
            if (scanning)
                return;
            Thread.Sleep(Poll);
        }
    }

    private static void StopScan(Window window)
    {
        Ui(() =>
        {
            if (window.FindName("CancelButton") is Button button && button.IsEnabled)
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
