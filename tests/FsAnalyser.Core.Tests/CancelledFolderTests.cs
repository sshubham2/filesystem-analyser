using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A folder that Cancel aborts commits nothing, and every folder committed before it is exactly as an
/// uncancelled scan has it. Cancel is fed in at each guarded call of the scan thread in turn: every listing
/// buffer of a folder that needs several, and every open and query. Files are probed on worker threads after
/// the walk (SL-056), so a cancel anywhere in the walk leaves every file at its listing sizes; a cancel during
/// the probes is ProbePassTests'.
/// </summary>
public class CancelledFolderTests
{
    [Theory]
    [InlineData(false)] // before the call: Enter refuses it, as when Cancel lands between two listing buffers
    [InlineData(true)] // during the call, which then returns SUCCESS, 18 or a handle: its result is discarded
    public void CancelAtAnyCallCommitsOnlyWholeFolders(bool duringTheCall)
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 11);
        tree.Folder("big");
        for (int i = 0; i < 2000; i++)
            tree.File($@"big\{i:D4}{new string('n', 92)}.txt", 1); // 100-character names
        tree.Folder("empty"); // its listing's first call returns SUCCESS with nothing in it
        tree.Folder("h1");
        string charged = tree.File(@"h1\x.bin", 1000);
        tree.Folder("h2");
        TempTree.Hardlink(tree.PathOf(@"h2\y.bin"), charged); // a later name: charges nothing and is never probed
        tree.Folder("probes");
        tree.SparseFile(@"probes\large.bin", 64 * TempTree.MiB);
        tree.File(@"probes\small.txt", 22);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);

        // The uncancelled scan, with a live token, and the last call of each folder it commits.
        var progress = new ScanProgress();
        var folderEnds = new List<(string Folder, int LastCall)>();
        SynchronousIoCancel? guard = null;
        ScanResult baseline;
        using (var live = new CancellationTokenSource())
        {
            baseline = Scans.WithGuard(g => guard = g, () => Scanner.Scan(tree.Root, progress, live.Token, new ScanOptions
            {
                AfterFolder = _ => folderEnds.Add((progress.CurrentFolder!, guard!.Calls)),
            }));
        }
        Assert.Equal(ScanStatus.Completed, baseline.Status);
        Assert.Equal(2005, baseline.Probes); // every file but y.bin, on worker threads after the walk
        Assert.Equal(0, baseline.ProbeCorrections); // every file closed: each probe agreed with its listing
        string[] order = ["", "a", "big", "empty", "h1", "h2", "probes", "z"];
        Assert.Equal(order.Select(tree.PathOf), folderEnds.Select(f => f.Folder));
        int big = Array.IndexOf(order, "big");
        int calls = guard!.Calls;
        Assert.Equal(calls, folderEnds[^1].LastCall); // the probes are not the scan thread's calls
        int bigBuffers = folderEnds[big].LastCall - folderEnds[big - 1].LastCall - 2; // less its open and the final ERROR_NO_MORE_FILES
        Assert.True(bigBuffers > 1, $"big was listed in {bigBuffers} buffer(s): the between-buffers case needs several");
        Dictionary<string, Scans.EntryState> expected = Scans.Snapshot(baseline.Store);
        Assert.Equal(EntryFlags.SizeProbed, expected[tree.PathOf(@"h1\x.bin")].Flags);
        Assert.Equal(EntryFlags.SizeProbed, expected[tree.PathOf(@"probes\large.bin")].Flags);
        Assert.Equal(EntryFlags.HardlinkDuplicate, expected[tree.PathOf(@"h2\y.bin")].Flags);

        for (int k = 1; k <= calls; k++)
        {
            var runProgress = new ScanProgress();
            ScanResult result = Scans.RunCancellingAt(tree.Root, k, duringTheCall, (root, ct) => Scanner.Scan(root, runProgress, ct));

            int inProgress = folderEnds.FindIndex(f => f.LastCall >= k);
            int committedCount = inProgress;
            string at = $"Cancel {(duringTheCall ? "during" : "before")} call {k} of {calls}, in {folderEnds[inProgress].Folder}";

            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            Assert.True(result.Unreadable.Count == 0, $"{at}: an aborted folder was reported unreadable");
            Assert.True(result.Probes == 0, $"{at}: {result.Probes} files were probed, though the walk never finished");
            Scans.AssertChargedBytesAreTheTotals(result);
            if (result.Store.Count > 0)
                TraversalTests.AssertChildrenAreContiguous(result.Store); // no entry of an aborted folder left behind

            // The last folder reported listed is the last one committed: an aborted folder, even one whose
            // listing said SUCCESS with nothing in it, is never reported.
            Assert.True(runProgress.CurrentFolder == (committedCount == 0 ? null : folderEnds[committedCount - 1].Folder),
                $"{at}: the last folder reported listed is {runProgress.CurrentFolder ?? "none"}");

            Dictionary<string, Scans.EntryState> actual = Scans.Snapshot(result.Store);
            var committed = new HashSet<string>(folderEnds.Take(committedCount).Select(f => f.Folder), StringComparer.Ordinal);
            foreach ((string folder, _) in folderEnds)
            {
                if (committed.Contains(folder))
                    Assert.True(actual.ContainsKey(folder), $"{at}: {folder} was committed but is missing");
            }
            foreach ((string path, Scans.EntryState state) in actual)
            {
                Assert.True(expected.TryGetValue(path, out Scans.EntryState want), $"{at}: {path} is not in the uncancelled scan");
                if (want.Kind == EntryKind.Folder && !committed.Contains(path))
                    want = want with { ChildCount = 0 }; // aborted, or never reached
                // Never probed: the sizing comes after the walk. The sizes are the listing's, which every probe agreed with.
                want = want with { Flags = want.Flags & ~EntryFlags.SizeProbed };
                Assert.True(want == state, $"{at}: {path} is {state}, expected {want}");
            }
        }

        Scans.WaitForAbortedCloses();
    }

    /// <summary>
    /// The reader's own half of the rule: a listing aborted after some buffers were read keeps none of
    /// them, so nothing downstream can commit a folder half read.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAbortedListingKeepsNothingOfTheBuffersBeforeIt(bool duringTheCall)
    {
        using var tree = new TempTree();
        for (int i = 0; i < 2000; i++)
            tree.File($"{i:D4}{new string('n', 92)}.txt", 1);
        using var cts = new CancellationTokenSource();
        using var guard = new SynchronousIoCancel(cts.Token);
        using var reader = new DirectoryReader(guard);
        Assert.Equal(0, guard.OpenDirectory(tree.ExtendedRoot, keepLastAccess: true, out var handle));
        const int ThirdBuffer = 4; // call 1 was the open
        if (duringTheCall)
        {
            guard.AfterCall = call =>
            {
                if (call != ThirdBuffer)
                    return;
                cts.Cancel();
                Assert.True(SpinWait.SpinUntil(() => guard.CancelIsIssued, TimeSpan.FromSeconds(5)));
            };
        }
        else
        {
            guard.BeforeCall = call =>
            {
                if (call == ThirdBuffer)
                    cts.Cancel();
            };
        }

        int error = reader.List(handle!, fullClass: false);
        guard.Close(handle!);

        Assert.Equal(SynchronousIoCancel.Aborted, error);
        Assert.Equal(ThirdBuffer, guard.Calls); // two buffers were read, and parsed, before the abort
        Assert.True(guard.LastCallAborted);
        Assert.Equal(0, reader.Count);
        Scans.WaitForAbortedCloses();
    }

    /// <summary>
    /// A 995 that Windows returns by itself, with no cancel requested, is not the guard's abort. It is an
    /// error like any other, and the entries read before it are kept.
    /// </summary>
    [Fact]
    public void A995WindowsReturnsWithNoCancelKeepsWhatWasListed()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 2000; i++)
            tree.File($"{i:D4}{new string('n', 92)}.txt", 1);
        using var cts = new CancellationTokenSource(); // live, never cancelled
        using var guard = new SynchronousIoCancel(cts.Token);
        using var reader = new DirectoryReader(guard);
        Assert.Equal(0, guard.OpenDirectory(tree.ExtendedRoot, keepLastAccess: true, out var handle));
        const int ThirdBuffer = 4; // call 1 was the open
        guard.OverrideQueryError = (call, error) => call == ThirdBuffer ? SynchronousIoCancel.Aborted : error;

        int listed = reader.List(handle!, fullClass: false);
        guard.Close(handle!);

        Assert.Equal(SynchronousIoCancel.Aborted, listed);
        Assert.Equal(ThirdBuffer, guard.Calls);
        Assert.False(guard.LastCallAborted);
        Assert.InRange(reader.Count, 1, 1999); // the first two buffers, kept
        Assert.Equal(0, guard.ClosesHandedOff); // nothing was aborted, so the close ran inline
    }

    /// <summary>
    /// The same 995, through the real scanner: the folder is Unreadable(995) with what was listed before
    /// it, and the scan carries on, because no folder is dropped silently and nobody asked to stop.
    /// </summary>
    [Fact]
    public void A995WindowsReturnsWithNoCancelIsAnUnreadableFolderAndTheScanCarriesOn()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 11);
        tree.Folder("big");
        for (int i = 0; i < 2000; i++)
            tree.File($@"big\{i:D4}{new string('n', 92)}.txt", 1);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);

        // Where big's listing calls fall, from a scan with nothing overridden.
        var progress = new ScanProgress();
        var folderEnds = new List<(string Folder, int LastCall)>();
        SynchronousIoCancel? guard = null;
        using var live = new CancellationTokenSource();
        SynchronousIoCancel.Created = g => guard = g;
        try
        {
            Scanner.Scan(tree.Root, progress, live.Token, new ScanOptions
            {
                AfterFolder = _ => folderEnds.Add((progress.CurrentFolder!, guard!.Calls)),
            });
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }
        Assert.Equal(tree.PathOf("big"), folderEnds[2].Folder);
        int bigThirdBuffer = folderEnds[1].LastCall + 4; // big's open, then its first two buffers

        SynchronousIoCancel.Created = g => g.OverrideQueryError = (call, error) => call == bigThirdBuffer ? SynchronousIoCancel.Aborted : error;
        ScanResult result;
        try
        {
            result = Scanner.Scan(tree.Root, new ScanProgress(), live.Token);
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }

        Assert.Equal(ScanStatus.Completed, result.Status);
        int big = Scans.Find(result, tree.PathOf("big"));
        Assert.Equal(new UnreadableFolder(big, SynchronousIoCancel.Aborted), Assert.Single(result.Unreadable));
        Assert.Equal(EntryFlags.Unreadable, result.Store.Flags(big));
        Assert.InRange(result.Store.ChildCount(big), 1, 1999); // what the first two buffers listed
        Assert.Equal(1, result.Store.ChildCount(Scans.Find(result, tree.PathOf("z")))); // the scan carried on
        Scans.AssertChargedBytesAreTheTotals(result);
        TraversalTests.AssertChildrenAreContiguous(result.Store);
    }
}
