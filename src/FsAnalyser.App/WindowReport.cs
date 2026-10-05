using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Workarounds;

namespace FsAnalyser.App;

/// <summary>
/// Writes what the window shows at its ending to a file, so a check can read the shipped exe's own
/// window without UI Automation.
/// </summary>
/// <remarks>
/// <code>
///   FsAnalyser.App.exe &lt;root&gt; --report &lt;file&gt;
/// </code>
/// scans the root with the window shown as usual, writes the report once the first ending is on screen,
/// and exits. Exit 0: the report was written, whatever the ending (the ending is in the file). Exit 2:
/// --report has no value, there is no root, or there is any other argument. Exit 3: the report could not
/// be written. Exit 70: an unhandled exception on the UI thread, or any other end without a report, such
/// as the window closed before its ending. Every non-zero exit leaves no file at &lt;file&gt;, so an
/// absent file means this run has no report. A file already there is deleted before the run, read-only
/// or not; the one exception is a file another program holds open without sharing delete, which nothing
/// can delete: that run exits 3 and leaves it where it was. eng/read-window.ps1 runs the exe and prints the result; it
/// quotes the arguments, which Start-Process does not.
///
/// Schema 1, JSON, pure ASCII (non-ASCII is written as \uXXXX):
/// <code>
///   { "schema": 1,
///     "root": the root argument as the app took it,
///     "window": { "OutcomeTitle": Text, "OutcomeDetail": [Text split into lines], "CountersText": Text,
///                 "PickerPanel": { "actualHeight": number }, "DrivePanel": { "buttons": number },
///                 "OutcomePanel": { "visible": bool, "actualHeight": number },
///                 "OutcomeTexts": { "actualHeight": number, "extentHeight": number, "inView": bool },
///                 "TopLevelHeader": Text,
///                 "OutcomeTabs": { "visible": bool, "selected": x:Name or null, "actualHeight": number, "stripRows": number },
///                 "LargestFilesTab": { "visible": bool, "selected": bool, "inView": bool, "actualHeight": number, "header": Text },
///                 "UnreadableTab": { "visible": bool, "selected": bool, "inView": bool, "actualHeight": number, "header": Text },
///                 "LargestFilesPanel": { "visible": bool, "actualHeight": number },
///                 "LargestFilesCaption": Text,
///                 "LargestFilesList": { "actualHeight": number, "inView": bool, "realizedRows": number, "fullRows": number },
///                 "UnreadableExpander": { "visible": bool, "actualHeight": number, "inView": bool, "expanded": bool },
///                 "UnreadableList": { "actualHeight": number, "inView": bool, "fullRows": number },
///                 "TopLevelPanel": { "visible": bool, "actualHeight": number },
///                 "TopLevelList": { "actualHeight": number, "inView": bool, "realizedRows": number, "fullRows": number },
///                 "FolderTree": { "visible": bool, "actualHeight": number, "inView": bool, "realizedRows": number, "fullRows": number },
///                 "ShowKindsButton": { "visible": bool, "isChecked": bool },
///                 "KindsView": { "visible": bool, "actualHeight": number }, "KindsCaption": Text,
///                 "KindsList": { "rows": number, "actualHeight": number, "inView": bool, "realizedRows": number, "fullRows": number } },
///     "largestFilesShown": rows bound to LargestFilesList, "largestFileOnDisk": the first row's on-disk
///         bytes or null, "largestFilesMs": how long ranking them took or null (SL-040),
///     "core": { "status", "rootError", "files", "folders", "links", "bytes", "bytesOnDisk", "elapsedMs",
///               "unreadable", "skippedStores", "notScanned", "opensUnderExcludedStore" } or null,
///     "outcome": "completed" | "cancelled" | "root-unreadable" | "refused" or null, "refusal": "container-layer-store" or null,
///     "workarounds": [ { "id", "on" } ],   (SL-095)
///     "topLevel": { "rows", "reading", "notRead", "stoppedPartway", "unreadable", "notScanned", "bytes", "onDisk", "files",
///                   "final" },   (SL-039; absent when the results list shows nothing)
///     "kinds": [ { "id", "label", "files", "size", "onDisk" } ] }   (SL-046; absent when no kinds were computed)
/// </code>
/// SL-095 added, all additive: window "OutcomeNotes" (visible, lines) and "SettingsButton" (its text); "core"'s
/// "unreadable", how many folders the scan's failed-folder record holds, "skippedStores", how many folders it left out on
/// purpose (the Windows container layer store), kept apart from it so that nothing reading unreadable folders reaches the
/// store, "notScanned", the same count under the name the pool protocol reads, and "opensUnderExcludedStore", how many
/// listings under that store the scanner's backstop refused, which is 0 when the walk's own check held; "outcome" and
/// "refusal", a refused root being "refused" with "container-layer-store"; "workarounds", each precaution against an
/// outside defect (Core's KnownWorkarounds) by id with whether it was on; and "topLevel"'s "notScanned".
/// Counts and ids only, never a path (Q-016).
/// "kinds" is the files by kind that KindsList (SL-046) is written from, in its order, largest on disk first: each
/// kind's stable id ("disk-images"), its label as the list shows it, and its files, bytes and bytes on disk as Core
/// added them up. Its sums are the root's, so they are "core"'s files, bytes and bytesOnDisk, after a stop too. It
/// holds no extension, name or path (Q-016); the extensions are on screen only. It is absent from the moment a scan
/// starts until that scan's ending has computed it, and after an ending with no scan behind it (Could not read, Scan
/// failed); an empty array is a scan with no files. The kinds are one of the two views of the results cell, chosen on
/// its header line: ShowKindsButton is that choice, checked when the kinds are shown, and KindsView the view. KindsList
/// "rows" is how many rows it holds whether or not its view is shown; the rest reads it as the other lists are read.
/// "topLevel" counts the rows the results list (TopLevelList) holds: how many, how many in each unfinished
/// or unreadable state, and their bytes, on-disk bytes and files added up, with whether the snapshot they
/// came from is the scan's final one. It carries no name and no path, so the report stays a matter of
/// totals and the root (Q-016).
/// Every key under "window" is the x:Name of the control it was read from, and every value is what that
/// control shows: its Text as displayed, or for a panel whether it is on screen. "inView" says the
/// control is on screen with some height, and that neither it nor anything around it up to the window
/// was laid out larger than the room it was given, so nothing squeezed it out of sight or clipped it (a
/// control squeezed out of a DockPanel reads visible with actualHeight 0). "realizedRows" is how many
/// rows the list has built, which stays near what fits on screen while it virtualizes; "fullRows" is how many
/// of them lie wholly inside the list's viewport, readable without scrolling. OutcomeTexts is the scrolling
/// region of the outcome's texts: "extentHeight" is their whole height, and more than "actualHeight" means
/// the rest is a scroll away. PickerPanel's height is what the pickers take above everything else, and
/// DrivePanel's "buttons" how many drive buttons they hold; the height must not depend on that count.
/// OutcomeTabs (SL-065) holds the outcome's analysis sections, one tab each, and shows the selected one's alone:
/// "selected" is that tab's x:Name, "stripRows" how many rows the headers take (0 while the tabs are not on screen).
/// LargestFilesTab and UnreadableTab are the tabs themselves: "visible" and "inView" are about the header, and
/// "header" is its text. A section's controls are out of the visual tree while another tab is selected, so they read
/// visible false, inView false and fullRows 0 then, whatever actualHeight they kept from when they were last on
/// screen. UnreadableExpander is what the unreadable folders were before SL-065, kept with the meaning it had: it is
/// read from UnreadableTab, and "expanded" is whether that tab is selected, the one way their list is on screen.
/// FolderTree (SL-044) is the folder tree a completed scan shows in TopLevelList's place: its layout only, never
/// a row's name or figures (Q-016). Its "realizedRows" are the tree rows built at any depth, and a row is one of
/// its "fullRows" when its own line, not the folders opened under it, lies wholly inside the viewport. Later
/// screens ADD keys; they never rename one or change what it holds. "window" is the evidence a check
/// compares with the tree it built. The largestFiles* keys are read from the list's own rows and from the
/// window's timing of the one call that ranked them; they never hold a name or a path. "core" is the
/// ScanResult behind the ending, straight from the same process: it only says which side is wrong when the
/// window and the tree disagree, and is null when the ending came before any result. The last line of
/// OutcomeDetail is the elapsed time and differs on every run.
///
/// Nothing here formats a number or builds text from the result: a wrong or blank control must show up
/// in the report as wrong or blank (architecture rule in .taad/fitness.yaml).
/// </remarks>
internal static class WindowReport
{
    /// <summary>
    /// Waits until layout and render have settled, then reads the window, writes the report beside
    /// <paramref name="path"/> and moves it into place, so the file is either whole or absent.
    /// </summary>
    /// <exception cref="IOException">The report could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The report could not be written.</exception>
    public static async Task WriteAsync(MainWindow window, string root, string path)
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded); // below Render: layout and render have run

        ScanResult? result = window.LastResult;
        var largest = window.LargestFilesList.ItemsSource as IReadOnlyList<MainWindow.LargestFileRow> ?? [];
        var report = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["root"] = root,
            ["window"] = new
            {
                OutcomeTitle = window.OutcomeTitle.Text,
                OutcomeDetail = Regex.Split(window.OutcomeDetail.Text, @"\r?\n"),
                OutcomeNotes = new { visible = window.OutcomeNotes.IsVisible, lines = Regex.Split(window.OutcomeNotes.Text, @"\r?\n") },
                SettingsButton = window.SettingsButton.Content as string,
                CountersText = window.CountersText.Text,
                PickerPanel = new { actualHeight = window.PickerPanel.ActualHeight },
                DrivePanel = new { buttons = window.DrivePanel.Children.Count },
                OutcomePanel = new { visible = window.OutcomePanel.IsVisible, actualHeight = window.OutcomePanel.ActualHeight },
                OutcomeTexts = new
                {
                    actualHeight = window.OutcomeTexts.ActualHeight,
                    extentHeight = window.OutcomeTexts.ExtentHeight,
                    inView = InView(window.OutcomeTexts),
                },
                TopLevelHeader = window.TopLevelHeader.Text,
                OutcomeTabs = new
                {
                    visible = window.OutcomeTabs.IsVisible,
                    selected = (window.OutcomeTabs.SelectedItem as TabItem)?.Name,
                    actualHeight = window.OutcomeTabs.ActualHeight,
                    stripRows = StripRows(window.OutcomeTabs),
                },
                LargestFilesTab = Section(window.LargestFilesTab),
                UnreadableTab = Section(window.UnreadableTab),
                LargestFilesPanel = new { visible = window.LargestFilesPanel.IsVisible, actualHeight = window.LargestFilesPanel.ActualHeight },
                LargestFilesCaption = window.LargestFilesCaption.Text,
                LargestFilesList = new
                {
                    actualHeight = window.LargestFilesList.ActualHeight,
                    inView = InView(window.LargestFilesList),
                    realizedRows = Enumerable.Range(0, largest.Count).Count(i => window.LargestFilesList.ItemContainerGenerator.ContainerFromIndex(i) is not null),
                    fullRows = FullRows(window.LargestFilesList),
                },
                // The unreadable folders were an expander before SL-065: its key stays, read from their tab, open
                // when the tab is selected.
                UnreadableExpander = new
                {
                    visible = window.UnreadableTab.IsVisible,
                    actualHeight = window.UnreadableTab.ActualHeight,
                    inView = InView(window.UnreadableTab),
                    expanded = window.UnreadableTab.IsSelected,
                },
                UnreadableList = new
                {
                    actualHeight = window.UnreadableList.ActualHeight,
                    inView = InView(window.UnreadableList),
                    fullRows = FullRows(window.UnreadableList),
                },
                TopLevelPanel = new { visible = window.TopLevelPanel.IsVisible, actualHeight = window.TopLevelPanel.ActualHeight },
                TopLevelList = new
                {
                    actualHeight = window.TopLevelList.ActualHeight,
                    inView = InView(window.TopLevelList),
                    realizedRows = Enumerable.Range(0, window.TopLevelList.Items.Count).Count(i => window.TopLevelList.ItemContainerGenerator.ContainerFromIndex(i) is not null),
                    fullRows = FullRows(window.TopLevelList),
                },
                FolderTree = new
                {
                    visible = window.FolderTree.IsVisible,
                    actualHeight = window.FolderTree.ActualHeight,
                    inView = InView(window.FolderTree),
                    realizedRows = TreeRows(window.FolderTree).Count,
                    fullRows = FullRows(window.FolderTree),
                },
                ShowKindsButton = new { visible = window.ShowKindsButton.IsVisible, isChecked = window.ShowKindsButton.IsChecked == true },
                KindsView = new { visible = window.KindsView.IsVisible, actualHeight = window.KindsView.ActualHeight },
                KindsCaption = window.KindsCaption.Text,
                KindsList = new
                {
                    rows = window.KindsList.Items.Count,
                    actualHeight = window.KindsList.ActualHeight,
                    inView = InView(window.KindsList),
                    realizedRows = Enumerable.Range(0, window.KindsList.Items.Count).Count(i => window.KindsList.ItemContainerGenerator.ContainerFromIndex(i) is not null),
                    fullRows = FullRows(window.KindsList),
                },
            },
            ["largestFilesShown"] = largest.Count,
            ["largestFileOnDisk"] = largest.Count > 0 ? largest[0].OnDisk : (long?)null,
            ["largestFilesMs"] = window.LargestFilesMs,
            ["core"] = result is null ? null : new
            {
                status = result.Status.ToString(),
                rootError = result.Status == ScanStatus.RootUnreadable ? result.RootError : (int?)null,
                files = result.Totals.Files,
                folders = result.Totals.Folders,
                links = result.Totals.Links,
                bytes = result.Totals.Bytes,
                bytesOnDisk = result.Totals.BytesOnDisk,
                elapsedMs = (long)result.Elapsed.TotalMilliseconds,
                unreadable = result.Unreadable.Count,
                skippedStores = result.SkippedStores.Count,
                notScanned = result.SkippedStores.Count,
                opensUnderExcludedStore = result.OpensUnderExcludedStore,
            },
            // SL-095: how the scan ended in one word, and why a refused one was ("container-layer-store"); null with no result.
            ["outcome"] = result?.Status switch
            {
                null => null,
                ScanStatus.Completed => "completed",
                ScanStatus.Cancelled => "cancelled",
                ScanStatus.RootUnreadable => "root-unreadable",
                ScanStatus.Refused => "refused",
                { } other => other.ToString(),
            },
            ["refusal"] = result?.Refusal == ScanRefusal.ContainerLayerStore ? "container-layer-store" : null,
            // The precautions against outside defects as the window holds them, by id: on or off (SL-095). Rendered from
            // Core's list as it is, naming no entry.
            ["workarounds"] = KnownWorkarounds.All.Select(w => new { id = w.Id, on = window.EnabledWorkarounds.Contains(w.Id) }).ToArray(),
        };
        // The rows the results list holds, counted: never a name or a path (Q-016), only how many rows are in
        // each state and what they add up to. Absent when the list shows nothing.
        if (window.ShownTopLevel is { } shown && window.TopLevelList.ItemsSource is IEnumerable<TopLevelRow> listed)
        {
            List<TopLevelRow> rows = [.. listed];
            report["topLevel"] = new
            {
                rows = rows.Count,
                reading = rows.Count(r => r.State == TopLevelState.Reading),
                notRead = rows.Count(r => r.State == TopLevelState.NotRead),
                stoppedPartway = rows.Count(r => r.State == TopLevelState.StoppedPartway),
                unreadable = rows.Count(r => r.State == TopLevelState.Unreadable),
                notScanned = rows.Count(r => r.State == TopLevelState.NotScanned),
                bytes = rows.Sum(r => r.Bytes),
                onDisk = rows.Sum(r => r.OnDisk),
                files = rows.Sum(r => r.Files),
                final = shown.Final,
            };
        }
        // The files by kind the list is written from, as Core computed them (SL-046): per kind its id, label and totals,
        // never an extension, a name or a path (Q-016). Absent until an ending has computed them, and again from the
        // moment the next scan starts.
        if (window.Kinds is { } kinds)
        {
            report["kinds"] = kinds.Kinds
                .Select(k => new { id = k.Id, label = k.Label, files = k.Files, size = k.Size, onDisk = k.OnDisk })
                .ToArray();
        }

        // The default encoder escapes everything outside ASCII, so Windows PowerShell 5.1 and pwsh read
        // the same text whatever code page or byte-order mark they assume.
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(temporary);
            throw;
        }
    }

    // Whether the control is on screen with some height, and neither it nor anything around it up to the
    // window was laid out larger than its slot, which is how WPF clips what does not fit. A control squeezed
    // out of its DockPanel still reads IsVisible, with ActualHeight 0 (measured on SL-040's layout mutant).
    private static bool InView(FrameworkElement control)
    {
        if (!control.IsVisible)
            return false;
        for (DependencyObject? node = control; node is not null and not Window; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement element)
                continue;
            Rect slot = LayoutInformation.GetLayoutSlot(element);
            Thickness margin = element.Margin;
            double height = slot.Height - margin.Top - margin.Bottom, width = slot.Width - margin.Left - margin.Right;
            if (element.ActualHeight <= 0 || element.ActualHeight > height + 0.5 || element.ActualWidth > width + 0.5)
                return false;
        }
        return true;
    }

    // A section's tab: its header, whether it is shown and selected, and where the header is. What the section holds
    // is read from its own controls, which are out of the visual tree while another tab is selected (SL-065).
    private static object Section(TabItem tab) => new
    {
        visible = tab.IsVisible,
        selected = tab.IsSelected,
        inView = InView(tab),
        actualHeight = tab.ActualHeight,
        header = tab.Header as string,
    };

    // How many rows the tabs' headers take: the header panel's height over its tallest header's, rounded. A strip
    // wider than the tabs wraps into a second row, which takes that row's height from every section. 0 while the tabs
    // are not on screen.
    private static int StripRows(TabControl tabs)
    {
        if (!tabs.IsVisible || FindDescendant<TabPanel>(tabs) is not { } strip)
            return 0;
        double tallest = tabs.Items.OfType<TabItem>().Where(tab => tab.IsVisible).Select(tab => tab.ActualHeight).DefaultIfEmpty(0).Max();
        return tallest > 0 ? (int)Math.Round(strip.ActualHeight / tallest) : 0;
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if ((child as T ?? FindDescendant<T>(child)) is { } found)
                return found;
        }
        return null;
    }

    // How many of the list's rows lie wholly inside its viewport, the part of it that shows rows: the rows a person
    // can read there without scrolling. A list with none shows only slivers, however tall it is. A list in a tab that
    // is not selected is out of the visual tree, so it is not visible and has none, whatever height it last had.
    private static int FullRows(ListBox list)
    {
        if (!list.IsVisible || FindViewport(list) is not { } viewport)
            return 0;
        int full = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible || row.ActualHeight <= 0)
                continue;
            Rect box = row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            if (box.Top >= -0.5 && box.Bottom <= viewport.ActualHeight + 0.5)
                full++;
        }
        return full;
    }

    // The same for the folder tree: each built row's own line (its header), inside the tree's viewport.
    private static int FullRows(TreeView tree)
    {
        if (!tree.IsVisible || FindViewport(tree) is not { } viewport)
            return 0;
        int full = 0;
        foreach (TreeViewItem row in TreeRows(tree))
        {
            if (row.Template?.FindName("PART_Header", row) is not FrameworkElement line || !line.IsVisible || line.ActualHeight <= 0)
                continue;
            Rect box = line.TransformToAncestor(viewport).TransformBounds(new Rect(line.RenderSize));
            if (box.Top >= -0.5 && box.Bottom <= viewport.ActualHeight + 0.5)
                full++;
        }
        return full;
    }

    // The tree's built rows at every depth: the containers in its visual tree.
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

    private static ScrollContentPresenter? FindViewport(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if ((child as ScrollContentPresenter ?? FindViewport(child)) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Deletes the report and its temporary file, if they are there; never throws.</summary>
    public static void Discard(string path)
    {
        DeleteQuietly(path);
        DeleteQuietly(path + ".tmp");
    }

    private static void DeleteQuietly(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
