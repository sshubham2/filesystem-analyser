using FsAnalyser.App;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-110: when a stopped scan's ending offers the folder tree (<see cref="FolderTreeSession.OffersStoppedTree"/>). The tree
/// is built over the store of a scan that was stopped, so it must have a root whose listing committed: a Stop before the root
/// was appended leaves an empty store (the tree's build throws on it), and a Stop in the root's own listing leaves a root
/// reading not listed (the tree would head it "is empty"). The scans are real, on a temp tree, with Cancel fed in at every
/// guarded call of the scan thread, before it and during it (lesson #98), as CancelledFolderTests and NotListedTests feed it.
/// </summary>
public class StoppedTreeOfferTests
{
    // A root holding enough names to span several listing buffers, so a Stop can land inside the root's own listing, then a
    // few folders: an empty one, one with a file, one denied its listing, and one last.
    private static TempTree Tree()
    {
        var tree = new TempTree();
        for (int i = 0; i < 1500; i++)
            tree.File($"{i:D4}{new string('n', 92)}.txt", 1);
        tree.Folder("a-empty");
        tree.Folder("b");
        tree.File(@"b\f.bin", 10);
        string denied = tree.Folder("denied");
        tree.File(@"denied\hidden.txt", 100);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);
        tree.Deny(denied, 0x1); // FILE_LIST_DIRECTORY
        return tree;
    }

    [Theory]
    [InlineData(false)] // before the call: the guard refuses it, as when Cancel lands between two listing buffers
    [InlineData(true)] // during the call, whose result is then discarded
    public void TheTreeIsOfferedExactlyWhenTheRootsListingCommitted(bool duringTheCall)
    {
        using TempTree tree = Tree();
        SynchronousIoCancel? guard = null;
        ScanResult baseline = Scans.WithGuard(g => guard = g, () => Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None));
        Assert.Equal(ScanStatus.Completed, baseline.Status);
        Assert.False(FolderTreeSession.OffersStoppedTree(baseline)); // a completed scan builds its tree unasked
        int calls = guard!.Calls;
        Assert.True(calls > 8, $"the scan made {calls} guarded calls");

        int offered = 0, emptyStores = 0, notListedRoots = 0;
        for (int k = 1; k <= calls; k++)
        {
            ScanResult result = Scans.RunCancellingAt(tree.Root, k, duringTheCall, (root, ct) => Scanner.Scan(root, new ScanProgress(), ct));
            string at = $"Stop {(duringTheCall ? "during" : "before")} call {k} of {calls}";
            ScanStore store = result.Store;
            Assert.True(result.Status == ScanStatus.Cancelled, $"{at}: {result.Status}");

            if (store.Count == 0)
            {
                emptyStores++;
                Assert.False(FolderTreeSession.OffersStoppedTree(result), $"{at}: the tree is offered over an empty store");
                continue;
            }
            if (store.NotListed(0))
            {
                notListedRoots++;
                Assert.False(FolderTreeSession.OffersStoppedTree(result), $"{at}: the tree is offered over a root nobody listed");
                continue;
            }

            // The root's listing committed: the tree is offered, and it has a root to open that is not "empty" by omission.
            Assert.True(FolderTreeSession.OffersStoppedTree(result), $"{at}: the root was listed, but no tree is offered");
            offered++;
            Assert.True(store.ChildCount(0) > 0, $"{at}: the offered root has no children");
            Assert.Equal(store.ChildCount(0), result.TopLevel!.Rows.Count);

            // The note arms rely on this: a folder is never both not listed and in the unreadable record, because Core
            // records a failure only for a listing it committed, and committing clears the mark.
            foreach (UnreadableFolder failed in result.Unreadable)
                Assert.False(store.NotListed(failed.Entry), $"{at}: {store.GetPath(failed.Entry)} is both not listed and unreadable");
        }
        Scans.WaitForAbortedCloses();

        // The sweep reached every shape, or it proved nothing about the predicate.
        Assert.True(emptyStores > 0, "no Stop left an empty store");
        Assert.True(notListedRoots > 0, "no Stop left a root nobody listed");
        Assert.True(offered > 0, "no Stop left a root whose listing committed");
        Assert.Equal(calls, emptyStores + notListedRoots + offered);
    }

    [Fact]
    public void ANeverStartedOrRefusedOrFailedScanOffersNoTree()
    {
        using var tree = new TempTree();
        string file = tree.File("one.txt", 5);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ScanResult before = Scanner.Scan(tree.Root, new ScanProgress(), cancelled.Token);
        Assert.Equal(ScanStatus.Cancelled, before.Status);
        Assert.False(FolderTreeSession.OffersStoppedTree(before)); // a token cancelled before the scan: an empty store

        ScanResult fileRoot = Scanner.Scan(file, new ScanProgress(), CancellationToken.None);
        Assert.Equal(ScanStatus.RootUnreadable, fileRoot.Status);
        Assert.False(FolderTreeSession.OffersStoppedTree(fileRoot)); // never a stopped tree: the window shows the problem
    }

    [Fact]
    public void AStopAfterEveryListingStillOffersTheTreeAndMarksNothingUnreadable()
    {
        using var tree = new TempTree();
        tree.Folder("a-empty");
        tree.Folder(@"b\c");
        tree.File(@"b\c\f.bin", 10);
        tree.Folder("z");
        tree.File(@"z\g.bin", 20);

        // Stopped in the size probes: every folder listed, so nothing reads not listed, and the tree is offered all the same.
        using var cts = new CancellationTokenSource();
        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, new ScanOptions
        {
            ProbeWorkers = 2,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                cts.Cancel();
                endOfFile = allocationSize = 1;
                return 0;
            },
        });
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.True(FolderTreeSession.OffersStoppedTree(result));
        for (int e = 0; e < result.Store.Count; e++)
            Assert.False(result.Store.NotListed(e), $"{result.Store.GetPath(e)} reads not listed after every folder was listed");
    }
}
