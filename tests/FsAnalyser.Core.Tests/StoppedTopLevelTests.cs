using System.Reflection;
using FsAnalyser.App;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-113: a stopped scan's ending shows the final top-level snapshot with the store's lower bound on every row the store reports
/// unfinished (<see cref="ScanResult.StoppedTopLevel"/>, <see cref="TopLevelRow.AtLeast"/>), under one guard
/// (<see cref="ScanResult.MarksAtLeast"/>) that the folder tree reads too. The scans here are real, on temp trees, stopped in the
/// listing (every guarded call of the scan thread, before it and during it) and in the size checks (inside a probe); the answers they
/// are compared with are a walk of the Parent chains (<see cref="UnfinishedTests.Oracle"/>), which reads none of the store's own
/// Unfinished or AtLeast.
/// </summary>
/// <remarks>
/// What each Stop must show: a row is marked exactly when its entry is unfinished and was listed, with the oracle's bound; every other
/// row is the very object the scanner published; every public property of a row and of the snapshot but the bound is the original's,
/// found by reflection so a property added to <see cref="TopLevelRow"/> that the rebuild forgets fails here; the snapshot is a new
/// object whenever any row is marked (the window redraws only a snapshot it has not shown) and the original when none is; the one the
/// scanner published is never changed; and a result with nothing to mark is returned as it is, its store unread (a store with no
/// entries throws on those reads).
/// </remarks>
public class StoppedTopLevelTests(ITestOutputHelper output)
{
    // ---- 1. A Stop in the listing: no size check has run ----

    /// <summary>
    /// Stop before and during every guarded call of the scan thread. Each result is judged whatever it holds: no entry, a root nobody
    /// listed, or a listed root with folders listed, queued, cut part-way and denied below it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStopInTheListingMarksExactlyTheUnfinishedListedRows(bool duringTheCall)
    {
        using TempTree tree = NotListedTests.SweepTree();
        NotListedTests.Baseline baseline = NotListedTests.RunBaseline(tree, planted: true);
        var tally = new Tally();
        for (int call = 1; call <= baseline.Calls; call++)
        {
            ScanResult r = NotListedTests.StopAt(tree, call, duringTheCall);
            Judge($"Stop {(duringTheCall ? "during" : "before")} call {call} of {baseline.Calls}", r, tally, sizeStop: false);
        }
        Scans.WaitForAbortedCloses();

        output.WriteLine($"listing stops: {tally}");
        Assert.True(tally.Stops == baseline.Calls && tally.NoStore >= 1, tally.ToString());
        Assert.True(tally.MarkedRows >= 20 && tally.FinalRows >= 20 && tally.UnreadRows >= 20, $"the sweep did not reach every kind of row: {tally}");
        Assert.True(tally.ReusedSnapshots >= 5 && tally.NewSnapshots >= 10, $"a snapshot was always new or always the original: {tally}");
    }

    /// <summary>
    /// A folder with no file anywhere below it is unfinished only because something below it was never listed: no size check pends
    /// under it, so a read of the pending checks alone would call it final. The Stop lands while the chain q\a is being listed, after
    /// q and before or among a's empty subfolders, and q reads unfinished through the unread ones, with the bound (0, 0, 0).
    /// </summary>
    [Fact]
    public void AFolderWithNoFileBelowIsMarkedThroughAnUnreadFolderBelowIt()
    {
        using var tree = new TempTree();
        tree.Folder("a-files");
        tree.File(@"a-files\one.txt", 7);
        for (int i = 0; i < 30; i++)
            tree.Folder($@"q\a\s{i:D2}");
        tree.Folder("r");
        tree.File(@"r\two.txt", 9);

        var progress = new ScanProgress();
        var ends = new List<(string Folder, int LastCall)>();
        SynchronousIoCancel? guard = null;
        ScanResult whole;
        using (var live = new CancellationTokenSource())
        {
            whole = Scans.WithGuard(g => guard = g, () => Scanner.Scan(tree.Root, progress, live.Token, new ScanOptions
            {
                ContainerLayerStorePath = "",
                AfterFolder = _ => ends.Add((progress.CurrentFolder!, guard!.Calls)),
            }));
        }
        Assert.Equal(ScanStatus.Completed, whole.Status);

        var tally = new Tally();
        int throughUnread = 0;
        for (int call = 1; call <= ends[^1].LastCall; call++)
        {
            ScanResult r = Scans.RunCancellingAt(tree.Root, call, during: false,
                (root, ct) => Scanner.Scan(root, new ScanProgress(), ct, new ScanOptions { ContainerLayerStorePath = "" }));
            Judge($"Stop before call {call}", r, tally, sizeStop: false);
            if (r.TopLevel is null || r.Store.Count == 0)
                continue;

            ScanStore s = r.Store;
            TopLevelRow row = r.StoppedTopLevel()!.Rows.Single(x => x.Name == "q");
            int q = s.FirstChild(0) + r.TopLevel.Rows.ToList().FindIndex(x => x.Name == "q");
            if (s.NotListed(q) || !s.NotListedAtOrBelow(q))
            {
                Assert.Null(row.AtLeast); // listed in full, or never listed: not marked as a bound
                continue;
            }
            // q was listed and something below it was not: no file lies below it, so only the unread folder makes it unfinished.
            throughUnread++;
            Assert.NotNull(row.AtLeast);
            Assert.Equal(new AtLeastFigures(0, 0, 0), row.AtLeast!.Value);
            Assert.False(Enumerable.Range(q + 1, s.Count - q - 1).Any(e => IsUnder(s, e, q) && s.Kind(e) == EntryKind.File), "a file lies under q");
        }
        Scans.WaitForAbortedCloses();
        Assert.True(throughUnread >= 5, $"only {throughUnread} Stops left q listed with an unread folder below it: {tally}");
    }

    private static bool IsUnder(ScanStore s, int entry, int ancestor)
    {
        for (int a = s.Parent(entry); a >= 0; a = s.Parent(a))
        {
            if (a == ancestor)
                return true;
        }
        return false;
    }

    // ---- 2. A Stop in the size checks: some checks have run ----

    /// <summary>
    /// Stop inside a probe call, at calls from the first to the last, on the ragged tree with stale hardlink first names. Every folder was
    /// listed, so nothing is unread, and the rows marked are the ones with a check still pending below them while the rest are final.
    /// </summary>
    [Fact]
    public void AStopInTheSizeChecksMarksOnlyTheRowsWithAPendingCheck()
    {
        using var tree = new UnfinishedTests.StopTree();
        tree.Restale();
        ScanResult whole = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None,
            UnfinishedTests.Options(probe: new UnfinishedTests.Prober().AsDelegate, workers: 4));
        Assert.Equal(ScanStatus.Completed, whole.Status);
        int due = whole.Probes;
        Assert.True(due > 100, $"{due} probes");

        var tally = new Tally();
        foreach (int n in new[] { 1, 9, 100, due / 2, due - 3, due })
        {
            tree.Restale();
            using var cts = new CancellationTokenSource();
            var prober = new UnfinishedTests.Prober { Before = call => { if (call == n) cts.Cancel(); } };
            ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, UnfinishedTests.Options(probe: prober.AsDelegate, workers: 1));
            Assert.Equal(ScanStatus.Cancelled, r.Status);
            Judge($"Stop inside probe call {n} of {due}", r, tally, sizeStop: true);
        }
        Scans.WaitForAbortedCloses();

        output.WriteLine($"size-check stops: {tally}");
        Assert.True(tally.Stops == 6 && tally.UnreadRows == 0, tally.ToString());
        Assert.True(tally.MarkedRows >= 6 && tally.FinalRows >= 10, $"the stops did not leave both marked and final rows: {tally}");
        Assert.True(tally.MarkedAndFinalInOneStop >= 1, $"no single Stop left a marked row beside a final one: {tally}");
    }

    // ---- 3. The rebuild keeps every field ----

    /// <summary>
    /// A row carrying every field the tracker can set (Revised, FailedKind, unreadable counts) is rebuilt with the bound and loses none:
    /// the rebuild takes the nine constructor arguments and two init properties by hand, and a hand copy is where a field is forgotten.
    /// The store is built by hand, a root with a folder listed with a file pending, a folder listed empty, a folder never listed and a
    /// file pending, none of which a real scan can be made to give a row Revised and unfinished at once.
    /// </summary>
    [Fact]
    public void ARebuiltRowKeepsEveryPropertyItHad()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\r", 0x10);
        int a = store.Append(0, "a", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int b = store.Append(0, "b", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int c = store.Append(0, "c", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int d = store.Append(0, "d.bin", EntryKind.File, EntryFlags.None, 0x20, 4096, 4096, new UInt128(0, 1));
        store.SetChildren(0, a, 4);
        int f = store.Append(a, "f.bin", EntryKind.File, EntryFlags.None, 0x20, 1000, 4096, new UInt128(0, 2));
        store.SetChildren(a, f, 1);
        store.SetChildren(b, store.Count, 0);
        // c is never listed. The watermark is 0: nothing was checked.

        var rows = new[]
        {
            new TopLevelRow("a", @"C:\r\a", EntryKind.Folder, TopLevelState.Unreadable, 1000, 4096, 1, 1, 3) { Revised = true, FailedKind = UnreadableKind.ListedPartway },
            new TopLevelRow("b", @"C:\r\b", EntryKind.Folder, TopLevelState.Done, 0, 0, 0, 1, 0) { Revised = true },
            new TopLevelRow("c", @"C:\r\c", EntryKind.Folder, TopLevelState.NotRead, 0, 0, 0, 1, 0),
            new TopLevelRow("d.bin", @"C:\r\d.bin", EntryKind.File, TopLevelState.Done, 4096, 4096, 1, 0, 0) { Revised = true },
        };
        var original = new TopLevelSnapshot(@"C:\r", 9, final: true, completed: false, rows, new ScanTotals(2, 3, 0, 5096, 8192));

        TopLevelSnapshot marked = TopLevelTracker.WithAtLeast(original, store);

        Assert.NotSame(original, marked);
        AssertSnapshotsAgree(original, marked, "the rebuilt snapshot");
        Assert.Equal(new AtLeastFigures(0, 0, 1), marked.Rows[0].AtLeast!.Value); // a: its one file pending
        Assert.Null(marked.Rows[1].AtLeast); // b: listed and empty, final
        Assert.Null(marked.Rows[2].AtLeast); // c: never listed
        Assert.Equal(new AtLeastFigures(0, 0, 1), marked.Rows[3].AtLeast!.Value); // d.bin: a file whose check is pending
        Assert.True(marked.Rows[0].Revised && marked.Rows[0].FailedKind == UnreadableKind.ListedPartway && marked.Rows[0].UnreadableBelow == 3);
        Assert.True(marked.Rows[3].Revised);
        Assert.Same(original.Rows[1], marked.Rows[1]); // a row the store reports final is the very object it was
        Assert.Same(original.Rows[2], marked.Rows[2]);
        Assert.All(original.Rows, row => Assert.Null(row.AtLeast)); // the published snapshot is not changed

        // The rows and the root's children must agree: a snapshot the store does not hold is a bug, not a shape to mark around.
        var short1 = new TopLevelSnapshot(@"C:\r", 9, final: true, completed: false, rows[..3], new ScanTotals(2, 3, 0, 5096, 8192));
        Assert.Throws<InvalidOperationException>(() => TopLevelTracker.WithAtLeast(short1, store));

        // After the checks have run (the watermark at the end of the store) a, b and d.bin are final, and c was never listed: nothing is
        // marked, and the snapshot comes back as the original, so the window is not made to redraw rows that did not change.
        store.EndSizeChecks(store.Count);
        Assert.Same(original, TopLevelTracker.WithAtLeast(original, store));
    }

    // ---- 4. Nothing to mark: the store is not read ----

    /// <summary>
    /// A scan that ran to the end, a root that could not be read, a scan refused, a Stop before the root was appended and a store with
    /// nothing in it all have nothing to mark: <see cref="ScanResult.MarksAtLeast"/> is false and the snapshot comes back as it is,
    /// without a read (a read of a store with no entries throws).
    /// </summary>
    [Fact]
    public void ANoStoreToMarkIsReturnedAsItIsAndNeverRead()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 5);
        string file = tree.File("two.txt", 6);

        ScanResult completed = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None, new ScanOptions { ContainerLayerStorePath = "" });
        Assert.Equal(ScanStatus.Completed, completed.Status);
        Assert.NotNull(completed.TopLevel);
        AssertNothingToMark(completed);

        ScanResult unreadable = Scanner.Scan(file, new ScanProgress(), CancellationToken.None);
        Assert.Equal(ScanStatus.RootUnreadable, unreadable.Status);
        AssertNothingToMark(unreadable);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ScanResult before = Scanner.Scan(tree.Root, new ScanProgress(), cancelled.Token);
        Assert.Equal(ScanStatus.Cancelled, before.Status);
        Assert.Equal(0, before.Store.Count);
        AssertNothingToMark(before);

        using TempTree planted = new();
        planted.Folder(@"m\Layers\L1");
        ScanResult refused = Scanner.Scan(planted.PathOf(@"m\Layers"), new ScanProgress(), CancellationToken.None,
            new ScanOptions { ContainerLayerStorePath = planted.PathOf(@"m\Layers") });
        Assert.Equal(ScanStatus.Refused, refused.Status);
        AssertNothingToMark(refused);

        // A scan that ran to the end whose store still holds a folder nobody listed (which no real scan leaves, and no spike has yet
        // shown a real completed drive to be free of): the store would call that folder and every folder above it unfinished, so the
        // guard is the status, not the store's answers. Nothing is marked and the snapshot is the scanner's own.
        var unread = new ScanStore();
        unread.AppendRoot(@"C:\r", 0x10);
        int folder = unread.Append(0, "ghost", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        unread.SetChildren(0, folder, 1);
        Assert.True(unread.NotListed(folder) && unread.Unfinished(0), "the hand-built store does not read unfinished: the case proves nothing");
        var wholeSnapshot = new TopLevelSnapshot(@"C:\r", 2, final: true, completed: true,
            [new TopLevelRow("ghost", @"C:\r\ghost", EntryKind.Folder, TopLevelState.NotRead, 0, 0, 0, 1, 0)], new ScanTotals());
        var completedWithAnUnreadFolder = new ScanResult { Status = ScanStatus.Completed, Root = @"C:\r", Store = unread, TopLevel = wholeSnapshot };
        AssertNothingToMark(completedWithAnUnreadFolder);

        // A stopped result whose last snapshot exists but whose store holds nothing: the guard is the entry count, not only the snapshot.
        var emptyStore = new ScanStore();
        var snapshot = new TopLevelSnapshot(@"C:\r", 1, final: true, completed: false, [], new ScanTotals());
        var handBuilt = new ScanResult { Status = ScanStatus.Cancelled, Root = @"C:\r", Store = emptyStore, TopLevel = snapshot };
        AssertNothingToMark(handBuilt);
        Assert.Throws<ArgumentOutOfRangeException>(() => emptyStore.Unfinished(0)); // the read this guard keeps off
    }

    private static void AssertNothingToMark(ScanResult r)
    {
        Assert.False(r.MarksAtLeast);
        Assert.Same(r.TopLevel, r.StoppedTopLevel());
    }

    // ---- The judge ----

    /// <summary>One Stop's result against the oracle, row by row.</summary>
    private void Judge(string at, ScanResult r, Tally tally, bool sizeStop)
    {
        Assert.True(r.Status == ScanStatus.Cancelled, $"{at}: the Stop did not land, the scan {r.Status}");
        ScanStore s = r.Store;
        tally.Stops++;
        if (s.Count == 0 || r.TopLevel is null)
        {
            tally.NoStore++;
            AssertNothingToMark(r);
            return;
        }

        Assert.True(r.MarksAtLeast, $"{at}: a stopped scan with a committed root is not marked");
        TopLevelSnapshot original = r.TopLevel;
        Assert.Equal(sizeStop, original.StoppedInSizeChecks);
        TopLevelSnapshot stopped = r.StoppedTopLevel()!;
        UnfinishedTests.Oracle o = UnfinishedTests.Oracle.ByFlags(s);
        AssertSnapshotsAgree(original, stopped, at);
        Assert.Equal(s.ChildCount(0), stopped.Rows.Count);

        int first = s.FirstChild(0), marked = 0, finalRows = 0;
        for (int i = 0; i < stopped.Rows.Count; i++)
        {
            int entry = first + i;
            TopLevelRow row = stopped.Rows[i];
            if (!s.NotListed(entry) && o.Unfinished[entry])
            {
                marked++;
                Assert.True(row.AtLeast is { } bound && bound == o.AtLeast[entry],
                    $"{at}: {s.GetPath(entry)} holds {row.AtLeast}, a walk of its subtree says {o.AtLeast[entry]}");
                string figures = TopLevelRowText.FiguresOf(row);
                AtLeastFigures b = row.AtLeast!.Value;
                Assert.StartsWith($"at least {FigureText.FormatSize(b.Size)} (at least {FigureText.FormatSize(b.OnDisk)} on disk)", figures);
                if (row.IsFolder)
                    Assert.Contains($" in at least {FigureText.Count(b.Files, "file")} and at least {FigureText.Count(row.Folders - 1, "folder")}", figures);
                tally.MarkedRows++;
            }
            else
            {
                Assert.True(row.AtLeast is null, $"{at}: {s.GetPath(entry)} is {(s.NotListed(entry) ? "unread" : "final")} and holds {row.AtLeast}");
                Assert.Same(original.Rows[i], row);
                Assert.DoesNotContain("at least", TopLevelRowText.FiguresOf(row));
                Assert.DoesNotContain("at least", TopLevelRowText.TitleOf(row));
                if (s.NotListed(entry))
                    tally.UnreadRows++;
                else
                    finalRows++;
            }
        }
        tally.FinalRows += finalRows;
        if (marked > 0 && finalRows > 0)
            tally.MarkedAndFinalInOneStop++;
        if (marked > 0)
        {
            Assert.NotSame(original, stopped);
            tally.NewSnapshots++;
        }
        else
        {
            Assert.Same(original, stopped);
            tally.ReusedSnapshots++;
        }
        Assert.All(original.Rows, row => Assert.Null(row.AtLeast)); // what the scanner published is untouched

        // Criterion 2 at its source: every entry that is unread or unfinished has an unfinished parent, up to the root.
        for (int e = 1; e < s.Count; e++)
        {
            if (s.NotListed(e) || s.Unfinished(e))
                Assert.True(s.Unfinished(s.Parent(e)), $"{at}: {s.GetPath(e)} is unread or unfinished and its folder {s.GetPath(s.Parent(e))} is not");
        }
    }

    /// <summary>
    /// Every public property of the snapshot and of each row but the bound is the original's, found by reflection; and the properties
    /// are the ones this test knows, so a new one must be named here on purpose.
    /// </summary>
    private static void AssertSnapshotsAgree(TopLevelSnapshot original, TopLevelSnapshot other, string at)
    {
        string[] snapshotKnown = ["Root", "Sequence", "Final", "Completed", "StoppedInSizeChecks", "Rows", "Bytes", "OnDisk", "Files", "Folders"];
        string[] rowKnown = ["Name", "Path", "Kind", "IsFolder", "State", "Bytes", "OnDisk", "Files", "Folders", "UnreadableBelow", "Revised", "FailedKind", "AtLeast"];
        PropertyInfo[] snapshotProperties = typeof(TopLevelSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo[] rowProperties = typeof(TopLevelRow).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(snapshotKnown.Order(), snapshotProperties.Select(p => p.Name).Order());
        Assert.Equal(rowKnown.Order(), rowProperties.Select(p => p.Name).Order());

        foreach (PropertyInfo p in snapshotProperties.Where(p => p.Name != nameof(TopLevelSnapshot.Rows)))
            Assert.True(Equals(p.GetValue(original), p.GetValue(other)), $"{at}: the snapshot's {p.Name} is {p.GetValue(other)}, not {p.GetValue(original)}");
        Assert.Equal(original.Rows.Count, other.Rows.Count);
        for (int i = 0; i < original.Rows.Count; i++)
        {
            foreach (PropertyInfo p in rowProperties.Where(p => p.Name != nameof(TopLevelRow.AtLeast)))
                Assert.True(Equals(p.GetValue(original.Rows[i]), p.GetValue(other.Rows[i])),
                    $"{at}: row {original.Rows[i].Name}'s {p.Name} is {p.GetValue(other.Rows[i])}, not {p.GetValue(original.Rows[i])}");
        }
    }

    private sealed class Tally
    {
        public int Stops, NoStore, MarkedRows, FinalRows, UnreadRows, NewSnapshots, ReusedSnapshots, MarkedAndFinalInOneStop;

        public override string ToString() =>
            $"{Stops} stops ({NoStore} with no store to mark); rows: {MarkedRows} marked, {FinalRows} final, {UnreadRows} unread; " +
            $"{NewSnapshots} new snapshots, {ReusedSnapshots} the original; {MarkedAndFinalInOneStop} stops with a marked row beside a final one";
    }
}
