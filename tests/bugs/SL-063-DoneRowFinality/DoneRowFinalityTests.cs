using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FsAnalyser.App;
using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace SL063.DoneRowFinality.Tests;

/// <summary>
/// Bug (SL-063, raised by the SL-039 reflection): a top-level row's TopLevelState.Done does not promise the
/// figures are final. A hardlinked file is charged to the first name the scan's listing meets; once every
/// folder is listed, a size probe reads the file's own true size and can correct that first name's record in
/// place (TopLevelTracker.Moved), even though its row already reported Done (SL-056, and TopLevelTests'
/// AnInPlaceCorrectionOfAnEarlierNameIsCreditedToThatNamesRow). Validate saw this for real on C:\Windows: one
/// Done row changed its bytes after Done, with 47,409 later hardlink names in a different row from the one
/// charged. The SL-039 reflection records that "the window shows no suffix for a Done row, so 'Done' still
/// implies a finality the data cannot keep": TopLevelRowText.TitleOf(row) returns exactly row.Name for
/// TopLevelState.Done, with nothing to tell a corrected row apart from a row that never changed.
///
/// Expected: a Done row whose figures an in-place hardlink correction revised after it settled must read as
/// something other than its bare name, so the person is not told an unqualified "Done" for a figure that was
/// corrected after that point.
///
/// Actual: TopLevelRowText.Convert returns exactly the row's bare name, identical to any other Done row,
/// including one that was never corrected.
/// </summary>
public class DoneRowFinalityTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void ADoneRowCorrectedInPlaceAfterItSettledReadsDifferentlyFromItsBareName()
    {
        string root = Path.Combine(Path.GetTempPath(), "fsa-bug-sl063", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "1-folder"));
        Directory.CreateDirectory(Path.Combine(root, "2-folder"));
        try
        {
            // The file is written and grown only through "2-folder"'s name; "1-folder"'s name is a hardlink
            // added afterwards, so its own directory entry keeps reporting the pre-growth size until the
            // scan's own probe reads the file itself (HardlinkTests.TheListingOfTheNameNotWrittenThroughIsStale).
            string current = Path.Combine(root, "2-folder", "current.bin");
            File.WriteAllBytes(current, new byte[1 * MiB]);
            string stale = Path.Combine(root, "1-folder", "stale.bin");
            Hardlink(stale, current);
            using (var append = new FileStream(current, FileMode.Append, FileAccess.Write))
                append.Write(new byte[63 * MiB]);

            var progress = new ScanProgress();
            ScanResult result = Scanner.Scan(root, progress, CancellationToken.None);

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.NotNull(result.TopLevel);
            // "1-folder" is listed first (it sorts before "2-folder"), so the scan charges the file's stale
            // 1 MiB to "1-folder" and marks that row Done before "2-folder" is even read. The probe that runs
            // once every folder is listed then corrects "1-folder"'s record to the file's true 64 MiB in
            // place: exactly the shape the SL-039 reflection describes, not an ordinary settled Done row.
            TopLevelRow correctedAfterDone = result.TopLevel!.Rows.Single(r => r.Name == "1-folder");
            Assert.Equal(TopLevelState.Done, correctedAfterDone.State);
            Assert.Equal(64 * MiB, correctedAfterDone.Bytes);

            var converter = new TopLevelRowText();
            var title = (string)converter.Convert(correctedAfterDone, typeof(string), null!, CultureInfo.InvariantCulture);

            Assert.NotEqual(correctedAfterDone.Name, title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- The fix's own cases (SL-063's build) ----

    /// <summary>
    /// The control: a Done row that nothing corrected still reads its bare name, so the fix is not a suffix on
    /// every Done row. The scan must have corrected nothing, or the control would prove nothing.
    /// </summary>
    [Fact]
    public void ADoneRowNothingCorrectedStillReadsItsBareName()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "1-folder", "plain.bin"), new byte[1 * MiB]);
            File.WriteAllBytes(Path.Combine(root, "2-folder", "other.bin"), new byte[1000]);

            ScanResult result = Scanner.Scan(root, new ScanProgress(), CancellationToken.None);

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.DoesNotContain(Enumerable.Range(0, result.Store.Count), e => (result.Store.Flags(e) & EntryFlags.ProbeCorrected) != 0);
            Assert.All(result.TopLevel!.Rows, row =>
            {
                Assert.Equal((TopLevelState.Done, false), (row.State, row.Revised));
                Assert.Equal(row.Name, Title(row));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The converter on its own, over every state with the flag both ways: the mark shows on a Done row and
    /// only there, and every other state keeps its own words whatever the flag says. The tracker only ever
    /// sets it on a Done row, but the converter must not rely on that.
    /// </summary>
    [Theory]
    [InlineData(TopLevelState.Done, "a-row  (revised)")]
    [InlineData(TopLevelState.Reading, "a-row  (reading...)")]
    [InlineData(TopLevelState.NotRead, "a-row  (not read yet)")]
    [InlineData(TopLevelState.StoppedPartway, "a-row  (stopped partway)")]
    [InlineData(TopLevelState.Unreadable, "a-row  (could not be opened)")]
    [InlineData(TopLevelState.NotFollowed, "a-row  (link, not followed)")]
    public void TheMarkShowsOnlyOnADoneRow(TopLevelState state, string revisedTitle)
    {
        Assert.Equal(revisedTitle, Title(MadeRow(state, revised: true)));
        Assert.Equal(state == TopLevelState.Done ? "a-row" : revisedTitle, Title(MadeRow(state, revised: false)));
    }

    /// <summary>
    /// The mark reaches the window, not only the converter: the results list is filled on a timer tick
    /// (RefreshTopLevel, reading the scan's progress) and at the ending (ShowFinalTopLevel), and both must show
    /// it on the corrected row and not on the other. Read from the rows the list realises, in MainWindows that
    /// are built and laid out but never shown.
    /// </summary>
    [Fact]
    public void TheResultsListShowsTheMarkOnATickAndAtTheEnding()
    {
        string root = NewRoot();
        try
        {
            BuildStaleTree(root, grownBy: 63 * MiB);
            var progress = new ScanProgress();
            ScanResult result = Scanner.Scan(root, progress, CancellationToken.None);
            Assert.Equal(ScanStatus.Completed, result.Status);

            (Dictionary<string, string> onTick, Dictionary<string, string> atEnding) = OnStaThread(() =>
            {
                var ticked = new MainWindow(null);
                typeof(MainWindow).GetField("_progress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ticked, progress);
                CallPrivate(ticked, "RefreshTopLevel");
                var ended = new MainWindow(null);
                CallPrivate(ended, "ShowFinalTopLevel", result.TopLevel);
                return (ShownTitles(ticked), ShownTitles(ended));
            });

            var expected = new Dictionary<string, string> { ["1-folder"] = "1-folder  (revised)", ["2-folder"] = "2-folder" };
            Assert.Equal(expected, onTick);
            Assert.Equal(expected, atEnding);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The acceptance above is not a race. The probe that corrects the stale name runs only once every folder
    /// is listed (SL-056: Scanner.Execute sizes files after the listing loop has emptied), so "1-folder" is
    /// Done before any correction can reach it, in every run, and Revised is true exactly when its figures moved
    /// after that. Twenty scans of fresh copies of its tree; the file grows by 3 MiB here rather than 63, since
    /// the sizes decide nothing about the order.
    /// </summary>
    [Fact]
    public void TheCorrectionLandsAfterTheRowSettledInEveryRun()
    {
        for (int run = 1; run <= 20; run++)
        {
            string root = NewRoot();
            try
            {
                BuildStaleTree(root, grownBy: 3 * MiB);

                ScanResult result = Scanner.Scan(root, new ScanProgress(), CancellationToken.None);

                TopLevelRow row = result.TopLevel!.Rows.Single(r => r.Name == "1-folder");
                Assert.True((result.Status, row.State, row.Bytes, row.Revised) == (ScanStatus.Completed, TopLevelState.Done, 4 * MiB, true),
                    $"run {run}: {result.Status}, 1-folder {row.State} at {row.Bytes} bytes, Revised {row.Revised}");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "fsa-bug-sl063", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "1-folder"));
        Directory.CreateDirectory(Path.Combine(root, "2-folder"));
        return root;
    }

    // The acceptance's tree: a 1 MiB file written and grown only through 2-folder's name, and a hardlink to it in
    // 1-folder made before it grew, whose directory entry keeps the stale size.
    private static void BuildStaleTree(string root, long grownBy)
    {
        string current = Path.Combine(root, "2-folder", "current.bin");
        File.WriteAllBytes(current, new byte[1 * MiB]);
        Hardlink(Path.Combine(root, "1-folder", "stale.bin"), current);
        using var append = new FileStream(current, FileMode.Append, FileAccess.Write);
        append.Write(new byte[grownBy]);
    }

    private static string Title(TopLevelRow row) =>
        (string)new TopLevelRowText().Convert(row, typeof(string), null!, CultureInfo.InvariantCulture);

    // TopLevelRow is built only by Core's tracker, and this assembly sees none of Core's internals.
    private static TopLevelRow MadeRow(TopLevelState state, bool revised)
    {
        ConstructorInfo ctor = typeof(TopLevelRow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        EntryKind kind = state == TopLevelState.NotFollowed ? EntryKind.Link : EntryKind.Folder;
        var row = (TopLevelRow)ctor.Invoke(["a-row", @"C:\made\a-row", kind, state, 1L, 1L, 0L, 1L, 0L]);
        typeof(TopLevelRow).GetProperty(nameof(TopLevelRow.Revised))!.SetValue(row, revised);
        // An Unreadable row always carries the kind the scanner stamped (SL-106): a refused open reads 'could not be opened'.
        if (state == TopLevelState.Unreadable)
            typeof(TopLevelRow).GetProperty(nameof(TopLevelRow.FailedKind))!.SetValue(row, UnreadableKind.NothingListed);
        return row;
    }

    private static void CallPrivate(MainWindow window, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), name);
        method.Invoke(window, BindingFlags.DoNotWrapExceptions, null, args, null);
    }

    // Each realised row's first line, the title, by row name. The list is laid out on its own, at a size that
    // realises every row of these two-row roots.
    private static Dictionary<string, string> ShownTitles(MainWindow window)
    {
        var list = (ListBox)window.FindName("TopLevelList");
        list.Measure(new Size(800, 600));
        list.Arrange(new Rect(0, 0, 800, 600));
        list.UpdateLayout();
        var titles = new Dictionary<string, string>();
        foreach (TopLevelRow row in list.Items)
        {
            var item = list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem
                ?? throw new InvalidOperationException($"row {row.Name} was not realised");
            titles[row.Name] = Descendants(item).OfType<TextBlock>().First().Text;
        }
        window.Close();
        return titles;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject below in Descendants(child))
                yield return below;
        }
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T value = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = work();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return value;
    }

    private static void Hardlink(string newName, string existing)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "/c", "mklink", "/H", newName, existing })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start cmd.exe");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /H failed ({process.ExitCode}): {output.Trim()}");
    }
}
