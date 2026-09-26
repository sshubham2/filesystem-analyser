using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FsAnalyser.Core;
using FsAnalyser.Core.Analysis;
using FsAnalyser.Core.Scanning;
using Microsoft.Win32;

namespace FsAnalyser.App;

/// <summary>
/// Pick a drive or folder, watch it being read, and see how the scan ended. Progress is polled from
/// the scanner's counters every 250 ms; there is one scan at a time. The results list below them fills in
/// from the scanner's published snapshots of the root's direct children (SL-039), and keeps the last one
/// after the scan ends.
/// </summary>
public partial class MainWindow : Window
{
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
    private ScanResult? _result;
    private bool _scanning;
    private int _scanGeneration; // one more at every scan's start: a volume figure of an earlier scan is dropped
    private Stopwatch? _stoppingClock; // from Cancel until the scan returns; null otherwise
    private TopLevelSnapshot? _shownTopLevel; // what the results list shows, compared by reference
    private readonly Stopwatch _refreshClock = new();
    private bool _skipNextRefresh;
    private ScrollViewer? _topLevelScroll;

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

    /// <summary>The snapshot the results list shows, its rows sorted into the list; null when it shows none.</summary>
    internal TopLevelSnapshot? ShownTopLevel => _shownTopLevel;

    /// <summary>How long the last refresh of the results list took, to the end of its layout.</summary>
    internal TimeSpan LastRefresh { get; private set; }

    /// <summary>Timer ticks of this scan that skipped their refresh because the one before ran long.</summary>
    internal int SkippedRefreshes { get; private set; }

    /// <summary>Whether the next tick with something new to show will skip it.</summary>
    internal bool SkipsNextRefresh => _skipNextRefresh;

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
        _result = null; // let the previous scan's store be collected
        LastResult = null;
        UnreadableList.ItemsSource = null;
        HideLargestFiles(); // the last scan's list is never shown as this one's
        OutcomePanel.Visibility = Visibility.Collapsed;
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
        // the scan failed) the list is cleared, so no row is left claiming a state that is no longer true.
        ShowFinalTopLevel(failure is null ? result?.TopLevel : null);
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
        CurrentFolderText.Text = p.CurrentFolder ?? "";
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
            : !snapshot.Completed
                ? "What was read before the stop. Rows marked not read yet or stopped partway are short."
                : rows.Length == 0 ? $"{snapshot.Root} is empty." : $"Largest at the top of {snapshot.Root}";
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

    // ---- Outcomes ----

    private void ShowOutcome(ScanResult result)
    {
        LastResult = result;
        if (result.Status == ScanStatus.RootUnreadable)
        {
            ShowProblem($"Could not read {result.Root}", $"{Win32Message(result.RootError)} (Windows error {result.RootError})");
            return;
        }

        _result = result;
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
        OutcomeNotes.Text = string.Join("\n", notes);
        OutcomeNotes.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (result.Unreadable.Count > 0)
        {
            UnreadableExpander.Header = $"{result.Unreadable.Count:N0} folders could not be read";
            UnreadableList.ItemsSource = result.Unreadable
                .Select(u => $"{result.Store.GetPath(u.Entry)}  ({Win32Message(u.Win32Error)})")
                .ToList();
            UnreadableExpander.IsExpanded = false;
            UnreadableExpander.Visibility = Visibility.Visible;
        }
        else
        {
            UnreadableExpander.Visibility = Visibility.Collapsed;
        }

        // Last, so that a failure here cannot take away what is already filled in above.
        ShowLargestFiles(result);
        OutcomePanel.Visibility = Visibility.Visible;
        ShowVolumeInUse(result.Store.VolumeUsed, _scanGeneration, detail); // and then EndingShown
    }

    // The volume line comes from the scan's own read of the volume, which may still be running when the
    // scan returns (SL-043). The ending is on screen without it and never waits for it; the line goes in when
    // the figure lands, and EndingShown is raised then, so whoever listens reads the whole ending. Null (a
    // folder or a share, or no answer within the read's bound) adds no line. What waits for the figure holds
    // the window, this scan's generation and the texts, never the result or its store, so a read stuck on a
    // dead drive keeps no scan the window has moved on from in memory (M1). A figure that lands after another
    // scan started is dropped.
    private void ShowVolumeInUse(Task<long?> figure, int generation, OutcomeDetailText detail)
    {
        if (figure.IsCompleted)
            AddVolumeInUse(figure, generation, detail);
        else
            figure.ContinueWith(landed => Dispatcher.InvokeAsync(() => AddVolumeInUse(landed, generation, detail)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

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
    // and folders whose files the scan never saw.
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
            caption += $" Files in {Count(result.Unreadable.Count, "unreadable folder")} are not included.";
        LargestFilesCaption.Text = caption;

        LargestFilesList.ItemsSource = top
            .Select(f => new LargestFileRow(f.ChargedOnDisk, FormatSize(f.ChargedOnDisk), FormatSize(f.ChargedSize), f.Path))
            .ToList();
        Visibility rows = top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LargestFilesHeadings.Visibility = rows;
        LargestFilesList.Visibility = rows;
        LargestFilesPanel.Visibility = Visibility.Visible;
    }

    // The outcome's texts row takes their natural height at most, so the lists get the rest (MainWindow.xaml).
    // Set once the texts are laid out, and never under the row's MinHeight: before that their height is 0.
    private void OnOutcomeTextsSizeChanged(object sender, SizeChangedEventArgs e) =>
        OutcomeTextsRow.MaxHeight = Math.Max(e.NewSize.Height, OutcomeTextsRow.MinHeight);

    private void HideLargestFiles()
    {
        LargestFilesPanel.Visibility = Visibility.Collapsed;
        LargestFilesList.Visibility = Visibility.Collapsed; // its own row, below the heading's
        LargestFilesList.ItemsSource = null;
        LargestFilesCaption.Text = "";
        LargestFilesMs = null;
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
        UnreadableExpander.Visibility = Visibility.Collapsed;
        HideLargestFiles();
        OutcomePanel.Visibility = Visibility.Visible;
        EndingShown?.Invoke(this, EventArgs.Empty);
    }

    private void Paint((Brush Border, Brush Background) look)
    {
        OutcomePanel.BorderBrush = look.Border;
        OutcomePanel.Background = look.Background;
    }

    private static string Win32Message(int error) => new Win32Exception(error).Message;

    internal static string Count(long n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:N1} {units[unit]}";
    }

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
}
