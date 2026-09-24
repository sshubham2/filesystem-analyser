using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FsAnalyser.Core.Scanning;
using Microsoft.Win32;

namespace FsAnalyser.App;

/// <summary>
/// Pick a drive or folder, watch it being read, and see how the scan ended. Progress is polled from
/// the scanner's counters every 250 ms; there is one scan at a time.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly (Brush Border, Brush Background) Finished = (Solid("#FF5E9E4A"), Solid("#FFF2F9EE"));
    private static readonly (Brush Border, Brush Background) Stopped = (Solid("#FFD69A2D"), Solid("#FFFFF8E6"));
    private static readonly (Brush Border, Brush Background) CouldNotRead = (Solid("#FFC84B4B"), Solid("#FFFDF0F0"));

    private readonly string? _launchPath;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cancel;
    private ScanProgress? _progress;
    private ScanResult? _result;
    private bool _scanning;

    public MainWindow(string? launchPath)
    {
        InitializeComponent();
        _launchPath = launchPath;
        _timer.Tick += (_, _) => ShowProgress();
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
                Margin = new Thickness(0, 0, 8, 8),
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
        _result = null; // let the previous scan's store be collected
        UnreadableList.ItemsSource = null;
        OutcomePanel.Visibility = Visibility.Collapsed;
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
        try
        {
            result = await Task.Factory.StartNew(() => Scanner.Scan(path, progress, token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (ArgumentException e)
        {
            rejected = e.ParamName is null ? e.Message : e.Message.Replace($" (Parameter '{e.ParamName}')", "");
        }
        finally
        {
            _timer.Stop();
            ScanningPanel.Visibility = Visibility.Collapsed;
            _cancel.Dispose();
            _cancel = null;
            _progress = null;
            _scanning = false;
            SetPickersEnabled(true);
        }

        if (result is null)
            ShowCouldNotRead(path, rejected!);
        else
            ShowOutcome(result);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        CancelButton.IsEnabled = false;
        ScanningTitle.Text = "Stopping...";
    }

    private void ShowProgress()
    {
        if (_progress is not { } p)
            return;
        string unreadable = p.Unreadable > 0 ? $", {p.Unreadable:N0} folders unreadable" : "";
        CountersText.Text = $"{p.Files:N0} files, {p.Folders:N0} folders, {FormatSize(p.Bytes)} ({FormatSize(p.BytesOnDisk)} on disk){unreadable}";
        CurrentFolderText.Text = p.CurrentFolder ?? "";
    }

    // ---- Outcomes ----

    private void ShowOutcome(ScanResult result)
    {
        if (result.Status == ScanStatus.RootUnreadable)
        {
            ShowCouldNotRead(result.Root, $"{Win32Message(result.RootError)} (Windows error {result.RootError})");
            return;
        }

        _result = result;
        bool finished = result.Status == ScanStatus.Completed;
        Paint(finished ? Finished : Stopped);
        OutcomeTitle.Text = finished
            ? $"Finished: {result.Root}"
            : $"Stopped: {result.Root}. This is only what was read before the stop, not the whole folder.";

        ScanTotals t = result.Totals;
        OutcomeDetail.Text =
            $"{Count(t.Files, "file")} and {Count(t.Folders, "folder")}; {Count(t.Links, "link")} not followed\n" +
            $"{FormatSize(t.Bytes)} in files, taking {FormatSize(t.BytesOnDisk)} on disk\n" +
            $"Read in {result.Elapsed.TotalSeconds:N1} s";

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
        OutcomePanel.Visibility = Visibility.Visible;
    }

    private void ShowCouldNotRead(string root, string why)
    {
        Paint(CouldNotRead);
        OutcomeTitle.Text = $"Could not read {root}";
        OutcomeDetail.Text = why;
        OutcomeNotes.Visibility = Visibility.Collapsed;
        UnreadableExpander.Visibility = Visibility.Collapsed;
        OutcomePanel.Visibility = Visibility.Visible;
    }

    private void Paint((Brush Border, Brush Background) look)
    {
        OutcomePanel.BorderBrush = look.Border;
        OutcomePanel.Background = look.Background;
    }

    private static string Win32Message(int error) => new Win32Exception(error).Message;

    private static string Count(long n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    private static string FormatSize(long bytes)
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
}
