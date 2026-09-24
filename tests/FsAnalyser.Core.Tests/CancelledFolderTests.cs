using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A folder that Cancel aborts commits nothing, and every folder committed before it is exactly as an
/// uncancelled scan has it. Cancel is fed in at each guarded call of a real scan in turn: every listing
/// buffer of a folder that needs several, every open, and every probe.
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
        TempTree.Hardlink(tree.PathOf(@"h2\y.bin"), charged); // the first repeat: probed while h2 is appended
        tree.Folder("probes");
        tree.SparseFile(@"probes\large.bin", 64 * TempTree.MiB); // due a probe, which runs before the append
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
            SynchronousIoCancel.Created = g => guard = g;
            try
            {
                baseline = Scanner.Scan(tree.Root, progress, live.Token, new ScanOptions
                {
                    AfterFolder = _ => folderEnds.Add((progress.CurrentFolder!, guard!.Calls)),
                });
            }
            finally
            {
                SynchronousIoCancel.Created = null;
            }
        }
        Assert.Equal(ScanStatus.Completed, baseline.Status);
        Assert.Equal(2, baseline.Probes); // large.bin, and x.bin through its repeat y.bin
        string[] order = ["", "a", "big", "empty", "h1", "h2", "probes", "z"];
        Assert.Equal(order.Select(tree.PathOf), folderEnds.Select(f => f.Folder));
        int big = Array.IndexOf(order, "big"), h2 = Array.IndexOf(order, "h2");
        int calls = guard!.Calls;
        Assert.Equal(calls, folderEnds[^1].LastCall);
        int bigBuffers = folderEnds[big].LastCall - folderEnds[big - 1].LastCall - 2; // less its open and the final ERROR_NO_MORE_FILES
        Assert.True(bigBuffers > 1, $"big was listed in {bigBuffers} buffer(s): the between-buffers case needs several");
        Dictionary<string, Scans.EntryState> expected = Scans.Snapshot(baseline.Store);
        string repeatCharged = tree.PathOf(@"h1\x.bin");
        Assert.Equal(EntryFlags.SizeProbed, expected[repeatCharged].Flags);
        Assert.Equal(EntryFlags.SizeProbed, expected[tree.PathOf(@"probes\large.bin")].Flags);

        for (int k = 1; k <= calls; k++)
        {
            using var cts = new CancellationTokenSource();
            int cancelAt = k;
            SynchronousIoCancel.Created = g =>
            {
                if (duringTheCall)
                {
                    g.AfterCall = call =>
                    {
                        if (call != cancelAt)
                            return;
                        cts.Cancel();
                        Assert.True(SpinWait.SpinUntil(() => g.CancelIsIssued, TimeSpan.FromSeconds(5)), "the canceller never marked the call");
                    };
                }
                else
                {
                    g.BeforeCall = call =>
                    {
                        if (call == cancelAt)
                            cts.Cancel();
                    };
                }
            };
            var runProgress = new ScanProgress();
            ScanResult result;
            try
            {
                result = Scanner.Scan(tree.Root, runProgress, cts.Token);
            }
            finally
            {
                SynchronousIoCancel.Created = null;
            }

            int inProgress = folderEnds.FindIndex(f => f.LastCall >= k);
            // x.bin is probed through its repeat y.bin, in the last two calls of h2, after h2's listing was
            // appended. Cancelled anywhere in h2, x.bin stays as if y.bin had never been reached; cancelled
            // in that probe, h2 is committed whole.
            bool repeatNotProbed = inProgress == h2;
            bool repeatProbe = repeatNotProbed && k > folderEnds[h2].LastCall - 2;
            int committedCount = repeatProbe ? inProgress + 1 : inProgress;
            string at = $"Cancel {(duringTheCall ? "during" : "before")} call {k} of {calls}, in {folderEnds[inProgress].Folder}";

            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            Assert.True(result.Unreadable.Count == 0, $"{at}: an aborted folder was reported unreadable");
            Assert.True(result.ProbeFailures == 0, $"{at}: an aborted probe was counted as failed");
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
                if (repeatNotProbed && path == repeatCharged)
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
        Assert.Equal(0, guard.OpenDirectory(tree.ExtendedRoot, out var handle));
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
        Assert.Equal(0, reader.Count);
        Scans.WaitForAbortedCloses();
    }
}
