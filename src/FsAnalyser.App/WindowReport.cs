using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;

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
/// absent file means this run has no report. eng/read-window.ps1 runs the exe and prints the result; it
/// quotes the arguments, which Start-Process does not.
///
/// Schema 1, JSON, pure ASCII (non-ASCII is written as \uXXXX):
/// <code>
///   { "schema": 1,
///     "root": the root argument as the app took it,
///     "window": { "OutcomeTitle": Text, "OutcomeDetail": [Text split into lines], "CountersText": Text,
///                 "OutcomePanel": { "visible": bool, "actualHeight": number }, "TopLevelHeader": Text },
///     "core": { "status", "rootError", "files", "folders", "links", "bytes", "bytesOnDisk", "elapsedMs" } or null,
///     "topLevel": { "rows", "reading", "notRead", "stoppedPartway", "unreadable", "bytes", "onDisk", "files",
///                   "final" } }   (SL-039; absent when the results list shows nothing)
/// </code>
/// "topLevel" counts the rows the results list (TopLevelList) holds: how many, how many in each unfinished
/// or unreadable state, and their bytes, on-disk bytes and files added up, with whether the snapshot they
/// came from is the scan's final one. It carries no name and no path, so the report stays a matter of
/// totals and the root (Q-016).
/// Every key under "window" is the x:Name of the control it was read from, and every value is what that
/// control shows: its Text as displayed, or for OutcomePanel whether it is on screen. Later screens ADD
/// keys; they never rename one or change what it holds. "window" is the evidence a check compares with
/// the tree it built. "core" is the ScanResult behind the ending, straight from the same process: it only
/// says which side is wrong when the window and the tree disagree, and is null when the ending came
/// before any result. The last line of OutcomeDetail is the elapsed time and differs on every run.
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
        var report = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["root"] = root,
            ["window"] = new
            {
                OutcomeTitle = window.OutcomeTitle.Text,
                OutcomeDetail = Regex.Split(window.OutcomeDetail.Text, @"\r?\n"),
                CountersText = window.CountersText.Text,
                OutcomePanel = new { visible = window.OutcomePanel.IsVisible, actualHeight = window.OutcomePanel.ActualHeight },
                TopLevelHeader = window.TopLevelHeader.Text,
            },
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
            },
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
                bytes = rows.Sum(r => r.Bytes),
                onDisk = rows.Sum(r => r.OnDisk),
                files = rows.Sum(r => r.Files),
                final = shown.Final,
            };
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
