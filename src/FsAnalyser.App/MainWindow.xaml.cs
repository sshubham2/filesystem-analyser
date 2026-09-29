using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FsAnalyser.Core;
using FsAnalyser.Core.Analysis;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using Microsoft.Win32;
using static FsAnalyser.App.FigureText;

namespace FsAnalyser.App;

/// <summary>
/// Pick a drive or folder, watch it being read, and see how the scan ended. Progress is polled from
/// the scanner's counters every 250 ms; there is one scan at a time. The results list below them fills in
/// from the scanner's published snapshots of the root's direct children (SL-039), and keeps the last one
/// after a stop. A scan that completes shows the folder tree in its place instead (SL-044), read from the
/// store the scan returned, which nothing writes any more. The same cell shows the files by kind instead when a
/// person chooses them on its header line (SL-046), computed once from that store at the ending.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly RoutedUICommand OpenInExplorer = new(
        "Open in Explorer",
        nameof(OpenInExplorer),
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Enter, ModifierKeys.Control) });

    internal RevealOutcome? LastRevealOutcome { get; private set; }
    private long _revealSequence;
    // A refresh (handler and layout) that ran longer than this makes the next tick skip its refresh, so a
    // busy machine gets the UI thread back (spike live-refresh-expanded-100k-folder, SL-039).
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromMilliseconds(100);

    private static readonly Comparison<TopLevelRow> LargestFirst = (a, b) =>
    {
        int bySize = b.Bytes.CompareTo(a.Bytes);
        return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    };

    private static readonly (Brush Border, Brush Background) Finished = (Solid("#FF5E9E4A"), Solid("#FFF2F9EE"));
    private static readonly (Brush Border, Brush Background) Stopped = (Solid("#FFD69A2D"), Solid("#FFFFF8E6"));
    private static readonly (Brush Border, Brush Background) CouldNotRead = (Solid("#FFC84B4B"), Solid("#FFFDF0F0"));

    // How many of the largest files the outcome lists (SL-040). Core takes the count as a parameter.
    private const int LargestFilesShown = 100;

    private readonly string? _launchPath;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cancel;
    private ScanProgress? _progress;
    private bool _scanning;
    private int _scanGeneration; // one more at every scan's start: a volume figure of an earlier scan is dropped
    private Stopwatch? _stoppingClock; // from Cancel until the scan returns; null otherwise
    private TopLevelSnapshot? _shownTopLevel; // what the results list shows, compared by reference
    private readonly Stopwatch _refreshClock = new();
    private bool _skipNextRefresh;
    private ScrollViewer? _topLevelScroll;
    private FolderTreeSession? _treeSession; // the completed scan the folder tree shows; closed as the next one starts

    public MainWindow(string? launchPath)
    {
        InitializeComponent();
        _launchPath = launchPath;
        _timer.Tick += (_, _) =>
        {
            ShowProgress();
            RefreshTopLevel();
            ShowStopping();
        };
    }

    /// <summary>
    /// Raised after every ending (Finished, Stopped, Could not read, Scan failed) has been put on the
    /// screen, once per scan. A Finished or Stopped ending is whole once its volume line is settled too:
    /// for a whole drive, when the scan's figure has landed or been given up (SL-043); an ending that another
    /// scan replaced before then raises nothing. The window does nothing else for whoever listens.
    /// </summary>
    internal event EventHandler? EndingShown;

    /// <summary>The result behind the ending on screen; null when the ending came before any result.</summary>
    internal ScanResult? LastResult { get; private set; }

    /// <summary>How long ranking the largest files took for the list on screen; null when no list was made.</summary>
    internal double? LargestFilesMs { get; private set; }

    /// <summary>
    /// The files by kind behind the list on screen (SL-046): computed once from the store of a scan that returned,
    /// Completed or Cancelled; null before that, from the moment the next scan starts, and after an ending with no
    /// scan behind it (Could not read, Scan failed). What the list shows is written from this and nothing else.
    /// </summary>
    internal KindBreakdownResult? Kinds { get; private set; }

    /// <summary>The snapshot the results list shows, its rows sorted into the list; null when it shows none.</summary>
    internal TopLevelSnapshot? ShownTopLevel => _shownTopLevel;

    /// <summary>How long the last refresh of the results list took, to the end of its layout.</summary>
    internal TimeSpan LastRefresh { get; private set; }

    /// <summary>Timer ticks of this scan that skipped their refresh because the one before ran long.</summary>
    internal int SkippedRefreshes { get; private set; }

    /// <summary>Whether the next tick with something new to show will skip it.</summary>
    internal bool SkipsNextRefresh => _skipNextRefresh;

    /// <summary>How long the last folder opened in the tree took to sort its children, in ms; null before any.</summary>
    internal double? LastFolderSortMs => _treeSession?.LastSortMs;

    /// <summary>How long the last folder opened in the tree took to build their rows, in ms; null before any.</summary>
    internal double? LastFolderBuildMs => _treeSession?.LastBuildMs;

    // Tall enough by default for the folder tree to show a screenful, but never taller than the work area; the
    // window keeps its MinHeight whatever the screen (SL-044, review M2). Windows places each new window a step lower
    // than the last, which put the foot of one this tall, where the folder tree is, up to 62 px below the work area
    // (measured at SL-044's build over 10 launches): it is moved up so that the whole of it is on screen. Both against
    // the work area of the monitor Windows placed it on, which its window handle, first here, tells (HF-019).
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Rect area = MonitorWorkArea();
        (Top, Height) = WorkAreaFit.Fit(Top, Height, area.Top, area.Bottom);
    }

    // In device-independent pixels, as the window's own position is; the primary's when Windows does not say.
    private Rect MonitorWorkArea()
    {
        if (Monitors.WorkAreaOf(new WindowInteropHelper(this).Handle) is not { } area
            || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        Matrix fromDevice = target.TransformFromDevice;
        return new Rect(fromDevice.Transform(new Point(area.Left, area.Top)), fromDevice.Transform(new Point(area.Right, area.Bottom)));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_launchPath is not null)
            StartScan(_launchPath);
        await LoadDrives();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _cancel?.Cancel();

    // ---- Picking ----

    private async Task LoadDrives()
    {
        List<DriveChoice> drives = await Task.Run(ReadDrives); // a network drive can block
        DrivePanel.Children.Clear();
        foreach (DriveChoice drive in drives)
        {
            var button = new Button
            {
                Content = drive.Label,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0), // one line of them, above DriveScroller's scroll bar
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = !_scanning,
            };
            button.Click += (_, _) => StartScan(drive.Root);
            DrivePanel.Children.Add(button);
        }
        DriveStatus.Text = drives.Count == 0 ? "No ready drives were found. Choose a folder instead." : "Drives";
    }

    private static List<DriveChoice> ReadDrives()
    {
        var drives = new List<DriveChoice>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                continue;
            try
            {
                if (!drive.IsReady)
                    continue;
                string label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel;
                drives.Add(new DriveChoice(drive.RootDirectory.FullName,
                    $"{drive.Name.TrimEnd('\\')}  {label} ({drive.DriveFormat})\n{FormatSize(drive.AvailableFreeSpace)} free of {FormatSize(drive.TotalSize)}"));
            }
            catch (IOException)
            {
                // it stopped being ready while we looked
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return drives;
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            StartScan(dialog.FolderName);
    }

    private void SetPickersEnabled(bool enabled)
    {
        ChooseFolderButton.IsEnabled = enabled;
        foreach (UIElement button in DrivePanel.Children)
            button.IsEnabled = enabled;
    }

    // ---- Scanning ----

    private async void StartScan(string path)
    {
        if (_scanning)
            return;
        _scanning = true;
        _scanGeneration++;
        LastResult = null;
        RevealStatus.Text = string.Empty;
        RevealStatus.Visibility = Visibility.Collapsed;
        LastRevealOutcome = null;
        HideUnreadable();
        HideLargestFiles(); // the last scan's list is never shown as this one's
        EnsureSelectedTabIsVisible(); // none is: the next ending opens on its own first section
        AwaitKinds(); // nor its kinds
        OutcomePanel.Visibility = Visibility.Collapsed;
        ClearFolderTree(); // and its store with it, before this scan builds another
        ClearTopLevel();
        SkippedRefreshes = 0;
        SetPickersEnabled(false);

        _progress = new ScanProgress();
        _cancel = new CancellationTokenSource();
        ScanProgress progress = _progress;
        CancellationToken token = _cancel.Token;
        ScanningTitle.Text = $"Reading {path}";
        CancelButton.IsEnabled = true;
        ScanningPanel.Visibility = Visibility.Visible;
        ShowProgress();
        _timer.Start();

        ScanResult? result = null;
        string? rejected = null;
        Exception? failure = null;
        try
        {
            result = await Task.Factory.StartNew(() => Scanner.Scan(path, progress, token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (ArgumentException e) when (e.ParamName == "root")
        {
            // Only the root's own rejection (empty or relative, from LongPath.NormalizeRoot). Any other
            // ArgumentException, such as an index out of range inside the store, is a bug: Scan failed.
            rejected = e.Message.Replace(" (Parameter 'root')", "");
        }
        catch (Exception e)
        {
            // A bug, OutOfMemory, anything the scanner did not expect: say so, and let a new scan start.
            failure = e;
        }
        finally
        {
            _timer.Stop();
            EndStopping(); // every ending: cancelled, failed, or finished before the cancel took effect
            ScanningPanel.Visibility = Visibility.Collapsed;
            _cancel.Dispose();
            _cancel = null;
            _progress = null;
            _scanning = false;
            SetPickersEnabled(true);
        }

        // Always the final snapshot, never subject to the tick's skip; with none (the root never listed, or
        // the scan failed) the list is cleared, so no row is left claiming a state that is no longer true. A
        // completed scan clears it too: the folder tree takes its place (ShowOutcome), so its final rows would
        // be sorted and bound only to be thrown away (HF-007, HF-020).
        ShowFinalTopLevel(failure is null && result?.Status != ScanStatus.Completed ? result?.TopLevel : null);
        if (failure is not null)
            ShowProblem($"Scan failed: {path}",
                $"The scan stopped on an error it did not expect, so nothing it read is shown.\n{failure.GetType().Name}: {failure.Message}");
        else if (result is null)
            ShowProblem($"Could not read {path}", rejected!);
        else
            ShowOutcome(result);
    }

    // Cancel does not abandon the scan: the window waits for it to return, however long the drive takes
    // (SL-038). On a share that stopped answering that is the SMB client's timeout, and the Stopping state
    // says so once 2 s have passed. The progress timer keeps running until the scan returns.
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        CancelButton.IsEnabled = false;
        ScanningTitle.Text = "Stopping...";
        _stoppingClock = Stopwatch.StartNew();
    }

    // Derived from the clock alone on every tick: nothing here can touch the drive the scan is waiting on.
    private void ShowStopping()
    {
        string? wait = _stoppingClock is { } clock ? StoppingWait.For(clock.Elapsed) : null;
        StoppingDetail.Text = wait ?? "";
        StoppingDetail.Visibility = wait is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EndStopping()
    {
        _stoppingClock = null;
        ShowStopping();
    }

    private void ShowProgress()
    {
        if (_progress is not { } p)
            return;
        string unreadable = p.Unreadable > 0 ? $", {p.Unreadable:N0} folders unreadable" : "";
        CountersText.Text = $"{p.Files:N0} files, {p.Folders:N0} folders, {FormatSize(p.Bytes)} ({FormatSize(p.BytesOnDisk)} on disk){unreadable}";
        // Once every folder is listed, the scan reads each file's own size (SL-056): say so, not the last folder.
        CurrentFolderText.Text = p.FilesToSize > 0
            ? $"Reading each file's own size: {p.FilesSized:N0} of {p.FilesToSize:N0}"
            : p.CurrentFolder ?? "";
    }

    // ---- Results so far (SL-039) ----

    // On a tick: the scanner's latest snapshot, if it is not the one shown. A tick after a refresh that ran
    // long skips it and leaves the next tick to show whatever is latest then.
    private void RefreshTopLevel()
    {
        if (_progress?.TopLevel is not { } snapshot || ReferenceEquals(snapshot, _shownTopLevel))
            return;
        if (_skipNextRefresh)
        {
            _skipNextRefresh = false;
            SkippedRefreshes++;
            return;
        }
        ShowTopLevel(snapshot);
    }

    // At the ending, whatever the last tick did.
    private void ShowFinalTopLevel(TopLevelSnapshot? final)
    {
        if (final is null)
            ClearTopLevel();
        else if (!ReferenceEquals(final, _shownTopLevel))
            ShowTopLevel(final);
    }

    // The rows are sorted on the UI thread into an array of their own and replace the list's items whole;
    // the scroll position, in rows, is put back once layout has run. The time to that point is the refresh's.
    private void ShowTopLevel(TopLevelSnapshot snapshot)
    {
        _refreshClock.Restart();
        ScrollViewer? scroll = TopLevelScroll();
        double offset = scroll?.VerticalOffset ?? 0;
        TopLevelRow[] rows = [.. snapshot.Rows];
        Array.Sort(rows, LargestFirst);
        _shownTopLevel = snapshot;
        TopLevelHeader.Text = !snapshot.Final
            ? $"Largest at the top of {snapshot.Root}, so far. Rows marked reading are still growing."
            : "What was read before the stop. Rows marked not read yet or stopped partway are short.";
        TopLevelList.ItemsSource = rows;
        TopLevelPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            scroll?.ScrollToVerticalOffset(offset);
            LastRefresh = _refreshClock.Elapsed;
            _skipNextRefresh = LastRefresh > SlowRefresh;
        });
    }

    private void ClearTopLevel()
    {
        TopLevelList.ItemsSource = null;
        TopLevelHeader.Text = "";
        TopLevelPanel.Visibility = Visibility.Collapsed;
        _shownTopLevel = null;
        _skipNextRefresh = false;
    }

    // Null until the list's template has been applied, which is once it has first been on screen.
    private ScrollViewer? TopLevelScroll() => _topLevelScroll ??= FindScrollViewer(TopLevelList);

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } found)
                return found;
        }
        return null;
    }

    // ---- The folder tree (SL-044) ----

    // At a completed ending, in the results list's place: the root, opened, over the store the scan returned,
    // which nothing writes any more. Its rows hold the session, never the store, so the next scan takes the store
    // away from every one of them at once, whatever WPF still holds on to.
    private void ShowFolderTree(ScanResult result)
    {
        var session = new FolderTreeSession(result.Store);
        FolderTreeNode root = FolderTreeNode.Root(session);
        root.EnsureChildren();
        root.IsExpanded = true;
        _treeSession = session;

        TopLevelList.ItemsSource = null;
        TopLevelList.Visibility = Visibility.Collapsed;
        _shownTopLevel = null;
        TopLevelHeader.Text = root.Children.Count == 0
            ? $"{result.Root} is empty."
            : $"Largest at the top of {result.Root}, by space on disk";
        FolderTreeHeadings.Visibility = Visibility.Visible;
        FolderTree.ItemsSource = new[] { root };
        FolderTree.Visibility = Visibility.Visible;
        TopLevelPanel.Visibility = Visibility.Visible;
    }

    // As a scan starts: the tree and its store go before the new scan builds another, so two stores are never held
    // at once (one at 5M entries is about 650 MiB, against a budget of 1 GiB).
    private void ClearFolderTree()
    {
        _treeSession?.Close();
        _treeSession = null;
        FolderTree.ItemsSource = null;
        FolderTree.Visibility = Visibility.Collapsed;
        FolderTreeHeadings.Visibility = Visibility.Collapsed;
        TopLevelList.Visibility = Visibility.Visible;
    }

    // A folder's rows are built here as it opens, not in the row's IsExpanded setter: that is reached through
    // bindings, and a binding turns an exception into a trace line, leaving an open folder that shows nothing
    // (review m4). The expander's own click reaches here through a binding too, so a fault is raised again on the
    // dispatcher, where the app's own failure handling sees it.
    private void OnFolderExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: FolderTreeNode node })
            return;
        try
        {
            node.EnsureChildren();
        }
        catch (Exception fault)
        {
            ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(fault);
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(captured.Throw));
        }
    }

    // The numpad's * on a row opens every folder below the selected one (TreeView's ExpandSubtree): on a drive's
    // root, rows for every folder of the scan, built on the UI thread and kept until the next scan. Folders open
    // one at a time (review M1).
    private void OnFolderTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Multiply)
            e.Handled = true;
    }

    // ---- Outcomes ----

    private void ShowOutcome(ScanResult result)
    {
        LastResult = result;
        if (result.Status == ScanStatus.RootUnreadable)
        {
            ShowProblem($"Could not read {result.Root}", $"{Win32Message(result.RootError)} (Windows error {result.RootError})");
            return;
        }

        bool finished = result.Status == ScanStatus.Completed;
        Paint(finished ? Finished : Stopped);
        OutcomeTitle.Text = finished
            ? $"Finished: {result.Root}"
            : $"Stopped: {result.Root}. This is only what was read before the stop, not the whole folder.";

        ScanTotals t = result.Totals;
        var detail = new OutcomeDetailText(
            $"{Count(t.Files, "file")} and {Count(t.Folders, "folder")}; {Count(t.Links, "link")} not followed\n" +
            $"{FormatSize(t.Bytes)} in files, taking {FormatSize(t.BytesOnDisk)} on disk\n",
            $"Read in {result.Elapsed.TotalSeconds:N1} s",
            finished, t.BytesOnDisk);
        OutcomeDetail.Text = detail.Counted + detail.Elapsed;

        var notes = new List<string>();
        if (!result.FileIdsAvailable)
            notes.Add("This drive does not report file IDs, so a file with more than one name (a hardlink) may be counted more than once.");
        if (result.ProbeFailures > 0)
            notes.Add($"{result.ProbeFailures:N0} files could not be opened to read their true size on disk; their listed size was used.");
        if (result.VanishedBeforeProbe > 0)
            notes.Add($"{result.VanishedBeforeProbe:N0} files were deleted before their true size could be read; their listed size was used.");
        OutcomeNotes.Text = string.Join("\n", notes);
        OutcomeNotes.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // One row per failed folder in the scan's record, in path order, and the header counts those rows (SL-087).
        IReadOnlyList<UnreadableRow> unreadable = UnreadableRows.Build(result.Unreadable, result.Store.GetPath);
        if (unreadable.Count > 0)
        {
            UnreadableTab.Header = $"Unreadable folders ({unreadable.Count:N0})";
            UnreadableList.ItemsSource = unreadable;
            UnreadableTab.Visibility = Visibility.Visible;
        }
        else
        {
            HideUnreadable();
        }

        // Last, so that a failure here cannot take away what is already filled in above.
        ShowLargestFiles(result);
        EnsureSelectedTabIsVisible(); // every section of this ending is in: its first is selected
        if (finished)
            ShowFolderTree(result); // a stopped scan keeps its list: a share would be of an incomplete whole
        ShowKinds(result); // after the tree, which a failure here cannot take away; a stopped scan's kinds too
        OutcomePanel.Visibility = Visibility.Visible;
        ShowVolumeInUse(result.Store.VolumeUsed, _scanGeneration, detail); // and then EndingShown
    }

    // The volume line comes from the scan's own read of the volume, which may still be running when the
    // scan returns (SL-043). The ending is on screen without it and never waits for it; the line goes in when
    // the figure lands, and EndingShown is raised then, so whoever listens reads the whole ending. Null (a
    // folder or a share, or no answer within the read's bound) adds no line. What waits for the figure holds
    // the window, this scan's generation and the texts, never the result or its store, so a read stuck on a
    // dead drive keeps no scan the window has moved on from in memory (M1). A figure that lands after another
    // scan started is dropped. A figure that has already landed takes the same way, one dispatcher turn later,
    // so every case that reaches this reaches the path a slow volume takes (SL-043, m2).
    private void ShowVolumeInUse(Task<long?> figure, int generation, OutcomeDetailText detail) =>
        figure.ContinueWith(landed => Dispatcher.InvokeAsync(() => AddVolumeInUse(landed, generation, detail)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    // On the UI thread, with the figure's task completed: nothing here blocks.
    private void AddVolumeInUse(Task<long?> landed, int generation, OutcomeDetailText detail)
    {
        if (generation != _scanGeneration)
            return;
        if (landed.IsCompletedSuccessfully && landed.Result is long used)
            OutcomeDetail.Text = detail.Counted + Coverage(detail.Finished, detail.SeenOnDisk, used) + "\n" + detail.Elapsed;
        EndingShown?.Invoke(this, EventArgs.Empty);
    }

    // The largest single files anywhere under the root, from the store of a scan that has returned, so
    // nothing writes it any more. The caption says when the list cannot be the whole tree: a stopped scan,
    // and folders the scan could not read in full.
    private void ShowLargestFiles(ScanResult result)
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<LargestFile> top = LargestFiles.Top(result.Store, LargestFilesShown);
        LargestFilesMs = clock.Elapsed.TotalMilliseconds;

        string part = result.Status == ScanStatus.Completed ? "" : " in the part read before the scan stopped";
        string caption = top.Count == 0
            ? $"No non-empty files found{part}."
            : $"Top {top.Count:N0} of {Count(result.Totals.Files, "file")} scanned{part}.";
        if (result.Unreadable.Count > 0)
            caption += UnreadableCaption(result.Unreadable.Count);
        LargestFilesCaption.Text = caption;

        // Its tab may not be the selected one, so the list may be out of the visual tree, never laid out: nothing here
        // reads its layout, and it builds its rows when its tab is next selected.
        LargestFilesList.ItemsSource = top
            .Select(f => new LargestFileRow(f.ChargedOnDisk, FormatSize(f.ChargedOnDisk), FormatSize(f.ChargedSize), f.Path))
            .ToList();
        Visibility rows = top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LargestFilesHeadings.Visibility = rows;
        LargestFilesList.Visibility = rows;
        LargestFilesPanel.Visibility = Visibility.Visible;
        LargestFilesTab.Visibility = Visibility.Visible;
    }

    // What the unreadable folders leave out of a list of files, for the largest files and the kinds alike. Not "not
    // included": a folder whose listing failed partway kept what it listed, and those files are in (HF-002). Worded as
    // the rows word such a folder, and a folder that could not be opened may have held nothing.
    private static string UnreadableCaption(int folders) =>
        $" Files in {Count(folders, "folder")} that could not be read (in full) may be missing.";

    private void HideLargestFiles()
    {
        HideSection(LargestFilesTab);
        LargestFilesPanel.Visibility = Visibility.Collapsed;
        LargestFilesList.Visibility = Visibility.Collapsed;
        LargestFilesList.ItemsSource = null;
        LargestFilesCaption.Text = "";
        LargestFilesMs = null;
    }

    private void HideUnreadable()
    {
        HideSection(UnreadableTab);
        UnreadableList.ItemsSource = null;
    }

    // ---- The outcome's sections (SL-065) ----

    // A section goes: the selection first, if it is on it, and then the tab, because a tab collapsed while it is selected
    // keeps its content on screen under no header. EnsureSelectedTabIsVisible, once every section of the change is set,
    // selects another.
    private void HideSection(TabItem section)
    {
        if (ReferenceEquals(OutcomeTabs.SelectedItem, section))
            OutcomeTabs.SelectedItem = null;
        section.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Puts the selection on a section that is on screen, and shows the sections only while there is one: the selected
    /// section if it still is, or else the first shown, in the order of OutcomeTabs; with none, no selection and no tabs,
    /// so the outcome's texts take the whole panel (MainWindow.xaml). Called once a change has set every section's
    /// visibility: at a scan's start, where none is left, and at each ending, which so opens on its first section and
    /// never on the unreadable folders while the largest files are shown. It names no section, so a section added
    /// later calls it after its own visibility changes and adds nothing here.
    /// </summary>
    internal void EnsureSelectedTabIsVisible()
    {
        TabItem? first = OutcomeTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Visibility == Visibility.Visible);
        if (OutcomeTabs.SelectedItem is not TabItem { Visibility: Visibility.Visible })
            OutcomeTabs.SelectedItem = first;
        OutcomeTabs.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- The files by kind (SL-046) ----

    // The results cell shows one of two views, as the choice on its header line says: the folders (the results list
    // while a scan runs and after a stop, the folder tree once a scan completes), or the files by kind. The choice is the
    // person's and outlasts a scan; the kinds view says, while a scan runs, when its rows come.
    private void OnResultsViewChanged(object sender, RoutedEventArgs e)
    {
        bool kinds = ShowKindsButton.IsChecked == true;
        FoldersView.Visibility = kinds ? Visibility.Collapsed : Visibility.Visible;
        KindsView.Visibility = kinds ? Visibility.Visible : Visibility.Collapsed;
    }

    // Every file of the store of a scan that has returned, so nothing writes it any more, Completed or Cancelled: one
    // pass on the UI thread (118 ms over the 3.6 million files of this PC's C:\), once per scan. Which kind a file is,
    // and every figure, is Core's; the rows only put them into words. The caption says when the kinds cannot be the
    // whole tree, as the largest files' does.
    private void ShowKinds(ScanResult result)
    {
        KindBreakdownResult kinds = KindBreakdown.Compute(result.Store);
        Kinds = kinds;

        string part = result.Status == ScanStatus.Completed ? "" : " in the part read before the scan stopped";
        string caption = kinds.Files == 0
            ? $"No files{part}."
            : $"{Count(kinds.Files, "file")} scanned{part}, sorted by extension.";
        if (result.Unreadable.Count > 0)
            caption += UnreadableCaption(result.Unreadable.Count);
        KindsCaption.Text = caption;

        KindRow[] rows = KindRowText.Rows(kinds, FormatSize);
        KindsList.ItemsSource = rows;
        Visibility shown = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        KindsHeadings.Visibility = shown;
        KindsList.Visibility = shown;
    }

    // As a scan starts: the last scan's kinds go, and until this one's ending the view says when they come.
    private void AwaitKinds()
    {
        HideKinds();
        KindsCaption.Text = "Shown when the scan finishes.";
    }

    // An ending with no scan behind it (Could not read, Scan failed) has no kinds to show.
    private void HideKinds()
    {
        Kinds = null;
        KindsList.ItemsSource = null;
        KindsList.Visibility = Visibility.Collapsed;
        KindsHeadings.Visibility = Visibility.Collapsed;
        KindsCaption.Text = "";
    }

    // What the scan saw against what the volume says is in use. The gap is not explained here yet:
    // metafiles, unreadable folders and the rest belong to the coverage panel of a later slice.
    private static string Coverage(bool finished, long seenOnDisk, long volumeInUse)
    {
        long unseen = volumeInUse - seenOnDisk;
        if (unseen < 0)
            return $"The volume reports {FormatSize(volumeInUse)} in use; this scan counted {FormatSize(-unseen)} more than that.";
        return finished
            ? $"The volume reports {FormatSize(volumeInUse)} in use, so {FormatSize(unseen)} of it was not seen by this scan " +
              "(file-system metadata, folders it could not read, and anything else an ordinary account cannot list)."
            : $"The volume reports {FormatSize(volumeInUse)} in use; {FormatSize(unseen)} of it is not in this partial scan.";
    }

    // Could not read, and Scan failed: nothing to show but why.
    private void ShowProblem(string title, string why)
    {
        Paint(CouldNotRead);
        OutcomeTitle.Text = title;
        OutcomeDetail.Text = why;
        OutcomeNotes.Visibility = Visibility.Collapsed;
        HideUnreadable();
        HideLargestFiles();
        EnsureSelectedTabIsVisible(); // no section: the texts take the whole panel
        HideKinds();
        OutcomePanel.Visibility = Visibility.Visible;
        EndingShown?.Invoke(this, EventArgs.Empty);
    }

    private void Paint((Brush Border, Brush Background) look)
    {
        OutcomePanel.BorderBrush = look.Border;
        OutcomePanel.Background = look.Background;
    }

    private static string Win32Message(int error) => new Win32Exception(error).Message;

    private static Brush Solid(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed record DriveChoice(string Root, string Label);

    /// <summary>The outcome's detail around the volume line: its first two lines, its last, and what the line needs.</summary>
    private sealed record OutcomeDetailText(string Counted, string Elapsed, bool Finished, long SeenOnDisk);

    /// <summary>One row of the largest-files list: the texts it shows, and the on-disk bytes it was ranked by.</summary>
    internal sealed record LargestFileRow(long OnDisk, string OnDiskText, string SizeText, string Path);

    private static string PathOf(object? row) => row switch
    {
        LargestFileRow file => file.Path,
        FolderTreeNode node => node.FullPath,
        TopLevelRow top => top.Path,
        UnreadableRow unreadable => unreadable.Path,
        _ => string.Empty
    };

    // Stands in for a row the menu could not capture: PathOf gives it no path, so the entry is disabled, and it is
    // never null, so the command does not fall back to whatever row has the keyboard focus.
    private static readonly object NoRow = new();

    // The row a menu acts on is captured as the menu opens (review m1, B3-1, R2-B4-2). While a scan runs every refresh
    // replaces the results list's items, which detaches the container the menu was opened on: its DataContext turns
    // into the disconnected sentinel and a command routed from it never reaches the window's binding. So the command
    // is sent to the window itself, with the captured row as its parameter. A row already detached as the menu opens
    // is not captured, and the entry is disabled rather than acting on the row an earlier opening left behind.
    private void OnRowContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            item.CommandTarget = this;
        object? row = (menu.PlacementTarget as FrameworkElement)?.DataContext;
        menu.Tag = row is null || ReferenceEquals(row, BindingOperations.DisconnectedSource) ? NoRow : row;
    }

    private void OnCanOpenInExplorer(object sender, CanExecuteRoutedEventArgs e)
    {
        object? row = e.Parameter ?? (Keyboard.FocusedElement as FrameworkElement)?.DataContext ?? (e.OriginalSource as FrameworkElement)?.DataContext;
        e.CanExecute = !string.IsNullOrEmpty(PathOf(row));
        e.Handled = true;
    }

    private async void OnOpenInExplorer(object sender, ExecutedRoutedEventArgs e)
    {
        object? row = e.Parameter ?? (Keyboard.FocusedElement as FrameworkElement)?.DataContext ?? (e.OriginalSource as FrameworkElement)?.DataContext;
        string path = PathOf(row);
        if (string.IsNullOrEmpty(path))
            return;

        long seq = Interlocked.Increment(ref _revealSequence);
        RevealStatus.Text = "Opening in Explorer\u2026";
        RevealStatus.Visibility = Visibility.Visible;

        try
        {
            RevealOutcome outcome = await ShellReveal.RevealAsync(path);
            if (Interlocked.Read(ref _revealSequence) != seq)
                return;

            LastRevealOutcome = outcome;
            RevealStatus.Text = FormatRevealStatus(outcome);
            RevealStatus.Visibility = string.IsNullOrEmpty(RevealStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _revealSequence) != seq)
                return;

            int hr = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
            LastRevealOutcome = new RevealOutcome(
                RevealKind.Failed,
                path,
                null,
                RevealReason.None,
                null,
                false,
                false,
                hr);
            RevealStatus.Text = FormatRevealStatus(LastRevealOutcome);
            RevealStatus.Visibility = Visibility.Visible;
        }
    }

    // A reveal lands or is refused (SL-088): a refusal says so on the status line and that nothing was opened, since no
    // other folder is shown in the item's place.
    private static string FormatRevealStatus(RevealOutcome outcome)
    {
        return outcome.Kind switch
        {
            RevealKind.Exact when outcome.Hidden =>
                $"'{GetLeafName(outcome.Requested)}' is hidden, so Explorer only highlights it if it is set to show hidden files (and protected operating system files, for system items).",
            RevealKind.Exact =>
                string.Empty,
            _ when outcome.Reason == RevealReason.NoLongerExists =>
                $"'{GetLeafName(outcome.Requested)}' no longer exists where the scan found it, so Explorer was not opened. Rescan to refresh.",
            _ =>
                $"Couldn't show '{GetLeafName(outcome.Requested)}' in Explorer ({RevealFailure(outcome)}). Nothing was opened."
        };
    }

    // Windows' message for a FACILITY_WIN32 HRESULT, and the HRESULT itself for any other.
    private static string RevealFailure(RevealOutcome outcome)
    {
        if (outcome.Reason == RevealReason.NameUnaddressable)
            return $"Explorer can't select '{outcome.UnaddressableName}' by name: it ends in a dot or space, or is a reserved device name";
        if (outcome.Reason == RevealReason.ParsedAsAnotherItem)
            return "Explorer reads its path as another item";
        if ((outcome.HResult & 0xFFFF0000) == 0x80070000)
            return Win32Message(outcome.HResult & 0xFFFF).TrimEnd('.');
        return $"0x{outcome.HResult:X8}";
    }

    private static string GetLeafName(string path)
    {
        string name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
