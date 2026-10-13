using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-109: a stopped scan's store tells a folder it never listed from one it listed and found empty
/// (<see cref="ScanStore.NotListed"/>). A folder is appended not listed and only registering its children clears
/// that. The scans here are real, on temp trees; the store tests need no disk.
/// </summary>
/// <remarks>
/// The record of what was listed before the Stop never comes from the mark: it is the AfterFolder hook of the very run
/// (a folder it fires for had its children registered), and the call numbers of an uncancelled run of the same tree
/// (the sweep of CancelledFolderTests, lesson #98). The mark is then compared with it, folder by folder.
/// </remarks>
public class NotListedTests
{
    private const uint FileListDirectory = 0x1;

    // The root's open (call 1) and its attribute query (call 2): until the second answers, nothing is known to be a
    // folder, so a Stop in either leaves no root to mark. Every later call of the root's handling has a root.
    private const int RootOpenAndQuery = 2;

    private const string Store = @"m\Layers";

    // ---- The scan's stop points ----

    /// <summary>
    /// A tree of every shape a stopped store holds, listed in this order: an empty folder first, a folder spanning several
    /// listing buffers, nested folders, a folder denied its listing, a hardlink pair, a junction, a folder whose only
    /// subfolder is the planted container layer store, and a last folder. The store is m's only subfolder, so it is skipped
    /// (committed) exactly when m is: skipping it makes no guarded call and fires no AfterFolder.
    /// </summary>
    private static TempTree SweepTree()
    {
        var tree = new TempTree();
        tree.Folder("a-empty");
        tree.Folder("big");
        for (int i = 0; i < 2000; i++)
            tree.File($@"big\{i:D4}{new string('n', 92)}.txt", 1); // 100-character names: several buffers
        tree.Folder(@"deep\inner");
        tree.File(@"deep\inner\leaf.txt", 5);
        string denied = tree.Folder("denied");
        tree.File(@"denied\hidden.txt", 1000);
        tree.Folder("h1");
        string charged = tree.File(@"h1\x.bin", 1000);
        tree.Folder("h2");
        TempTree.Hardlink(tree.PathOf(@"h2\y.bin"), charged); // a later name: charges nothing
        tree.Junction("j", tree.Outside("target"));
        tree.Folder(@"m\Layers\L1");
        tree.File(@"m\Layers\L1\inside.bin", 700);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);
        tree.Deny(denied, FileListDirectory);
        return tree;
    }

    private static readonly string[] SweepOrder = ["", "a-empty", "big", "deep", @"deep\inner", "denied", "h1", "h2", "m", "z"];

    /// <summary>One uncancelled scan with the last guarded call of each folder it committed.</summary>
    private sealed record Baseline(ScanResult Result, List<(string Folder, int LastCall)> FolderEnds, int Calls)
    {
        public int LastCallOf(TempTree tree, string relative) =>
            FolderEnds.Single(f => f.Folder == tree.PathOf(relative)).LastCall;
    }

    private static ScanOptions Options(TempTree tree, ScanProgress? progress = null, List<string>? listed = null) => new()
    {
        ContainerLayerStorePath = tree.PathOf(Store),
        AfterFolder = listed is null ? null : _ => listed.Add(progress!.CurrentFolder!),
    };

    private static Baseline RunBaseline(TempTree tree, bool planted)
    {
        var progress = new ScanProgress();
        var ends = new List<(string, int)>();
        SynchronousIoCancel? guard = null;
        ScanResult result;
        using (var live = new CancellationTokenSource())
        {
            result = Scans.WithGuard(g => guard = g, () => Scanner.Scan(tree.Root, progress, live.Token, new ScanOptions
            {
                ContainerLayerStorePath = planted ? tree.PathOf(Store) : null,
                AfterFolder = _ => ends.Add((progress.CurrentFolder!, guard!.Calls)),
            }));
        }
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(guard!.Calls, ends[^1].Item2); // the probes are not the scan thread's calls
        return new Baseline(result, ends, guard.Calls);
    }

    private static ScanResult StopAt(TempTree tree, int call, bool during, List<string>? listed = null)
    {
        var progress = new ScanProgress();
        return Scans.RunCancellingAt(tree.Root, call, during,
            (root, ct) => Scanner.Scan(root, progress, ct, Options(tree, progress, listed)));
    }

    private static ScanResult StopAfterFolders(string root, int folders)
    {
        using var cts = new CancellationTokenSource();
        return Scanner.Scan(root, new ScanProgress(), cts.Token, new ScanOptions
        {
            AfterFolder = n =>
            {
                if (n == folders)
                    cts.Cancel();
            },
        });
    }

    // ---- Criterion 1: folders not listed at the Stop ----

    /// <summary>
    /// Stop at every guarded call of the scan thread, before it and during it. A folder reads not listed exactly when its
    /// children were not registered before the Stop: folders still queued (appended by a listing that committed, never
    /// listed themselves), the folder cut part-way (its call refused or discarded, which commits nothing), the root while
    /// its own handling is cut. A listed empty folder, an Unreadable one and the skipped container store read listed.
    /// </summary>
    [Theory]
    [InlineData(false)] // before the call: the guard refuses it, as when Cancel lands between two listing buffers
    [InlineData(true)] // during the call, whose result is then discarded
    public void CancelAtAnyCallMarksExactlyTheFoldersNotCommitted(bool duringTheCall)
    {
        using TempTree tree = SweepTree();
        Baseline baseline = RunBaseline(tree, planted: true);
        Assert.Equal(SweepOrder.Select(tree.PathOf), baseline.FolderEnds.Select(f => f.Folder));
        int big = Array.IndexOf(SweepOrder, "big");
        int bigBuffers = baseline.FolderEnds[big].LastCall - baseline.FolderEnds[big - 1].LastCall - 2; // less its open and the final ERROR_NO_MORE_FILES
        Assert.True(bigBuffers > 1, $"big was listed in {bigBuffers} buffer(s): the between-buffers case needs several");
        string layers = tree.PathOf(Store), m = tree.PathOf("m");
        Assert.Single(baseline.Result.SkippedStores);

        var allEntries = Scans.ByPath(baseline.Result.Store);
        int notListedFolders = 0, cutInsideBig = 0, rootsMarked = 0;
        for (int k = 1; k <= baseline.Calls; k++)
        {
            var listed = new List<string>();
            ScanResult result = StopAt(tree, k, duringTheCall, listed);
            ScanStore store = result.Store;
            int inProgress = baseline.FolderEnds.FindIndex(f => f.LastCall >= k);
            string at = $"Stop {(duringTheCall ? "during" : "before")} call {k} of {baseline.Calls}, in {baseline.FolderEnds[inProgress].Folder}";

            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            Assert.True(result.RootError == 0, $"{at}: root error {result.RootError}");
            Assert.True(result.Probes == 0, $"{at}: {result.Probes} files were probed, though the walk never finished");
            // The record of what was listed: the folders AfterFolder fired for. It must be the uncancelled run's own prefix.
            Assert.True(baseline.FolderEnds.Take(inProgress).Select(f => f.Folder).SequenceEqual(listed),
                $"{at}: the folders listed were {string.Join(" | ", listed)}");
            var committed = new HashSet<string>(listed, StringComparer.Ordinal);
            if (committed.Contains(m))
                committed.Add(layers); // popped right after m, skipped with no call of its own
            Scans.AssertChargedBytesAreTheTotals(result);
            if (store.Count > 0)
                TraversalTests.AssertChildrenAreContiguous(store);

            if (k <= RootOpenAndQuery)
            {
                Assert.True(store.Count == 0, $"{at}: the root was not yet known to be a folder, yet the store holds {store.Count}");
                continue;
            }
            Assert.True(store.Count >= 1, $"{at}: the store holds no root");

            Dictionary<string, int> byPath = Scans.ByPath(store);
            foreach ((string path, int e) in byPath)
            {
                bool folder = store.Kind(e) == EntryKind.Folder;
                bool expectNotListed = folder && !committed.Contains(path);
                Assert.True(store.NotListed(e) == expectNotListed, $"{at}: {path} reads NotListed {store.NotListed(e)}, expected {expectNotListed}");
                Assert.True(((byte)store.Flags(e) & 0x80) == 0, $"{at}: {path} shows the mark through Flags");
                if (!store.NotListed(e))
                    continue;
                notListedFolders++;
                Assert.True(store.ChildCount(e) == 0 && store.FirstChild(e) == -1, $"{at}: {path} is not listed but holds children");
                Assert.True(Rolled.Of(store, e) == default, $"{at}: {path} is not listed but rolls {Rolled.Of(store, e)}");
                Assert.True((store.Flags(e) & (EntryFlags.Unreadable | EntryFlags.NotScanned)) == 0, $"{at}: {path} is not listed and also {store.Flags(e)}");
                Assert.Empty(store.ChildrenLargestFirst(e));
            }
            foreach (string path in committed)
                Assert.True(byPath.ContainsKey(path), $"{at}: {path} was listed but is missing");
            // Every child of a folder listed before the Stop is in the store: the queued folders are there to be marked.
            foreach ((string path, int _) in allEntries)
            {
                string? parent = Path.GetDirectoryName(path);
                if (parent is not null && committed.Contains(parent))
                    Assert.True(byPath.ContainsKey(path), $"{at}: {path}, a child of the listed {parent}, is missing");
            }

            Assert.True(store.NotListed(0) == !committed.Contains(tree.Root), $"{at}: the root reads NotListed {store.NotListed(0)}");
            if (store.NotListed(0))
                rootsMarked++;
            string[] unreadable = result.Unreadable.Select(u => store.GetPath(u.Entry)).ToArray();
            Assert.True(committed.Contains(tree.PathOf("denied")) ? unreadable.SequenceEqual(new[] { tree.PathOf("denied") }) : unreadable.Length == 0,
                $"{at}: unreadable {string.Join(" | ", unreadable)}");
            Assert.True(result.SkippedStores.Count == (committed.Contains(layers) ? 1 : 0), $"{at}: {result.SkippedStores.Count} skipped stores");
            if (byPath.TryGetValue(layers, out int skipped) && committed.Contains(layers))
                Assert.True(store.Flags(skipped) == EntryFlags.NotScanned && !store.NotListed(skipped), $"{at}: the skipped store reads {store.Flags(skipped)}, NotListed {store.NotListed(skipped)}");
            if (baseline.FolderEnds[inProgress].Folder == tree.PathOf("big") && k > baseline.FolderEnds[inProgress - 1].LastCall + 1)
            {
                cutInsideBig++;
                Assert.True(store.NotListed(byPath[tree.PathOf("big")]), $"{at}: big, cut part-way, reads listed");
            }
        }

        // The sweep was not vacuous: it met queued folders, the cut folder and a root that was cut.
        Assert.True(notListedFolders > baseline.Calls, $"only {notListedFolders} not-listed folders were met");
        Assert.True(cutInsideBig >= bigBuffers, $"only {cutInsideBig} stops landed inside big");
        Assert.True(rootsMarked >= 4, $"only {rootsMarked} stops left the root not listed");
        Scans.WaitForAbortedCloses();
    }

    /// <summary>
    /// The folder cut part-way through a listing that spans several 64 KiB buffers: Stop between two buffers or during
    /// one. Nothing of it is committed, so it reads not listed, with none of the buffers it had read; the folders around it
    /// read as they should.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFolderCutPartwayThroughItsBuffersReadsNotListed(bool duringTheCall)
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 11);
        tree.Folder("big");
        for (int i = 0; i < 2000; i++)
            tree.File($@"big\{i:D4}{new string('n', 92)}.txt", 1);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);

        var progress = new ScanProgress();
        var ends = new List<(string Folder, int LastCall)>();
        SynchronousIoCancel? guard = null;
        using (var live = new CancellationTokenSource())
        {
            Scans.WithGuard(g => guard = g, () => Scanner.Scan(tree.Root, progress, live.Token,
                new ScanOptions { AfterFolder = _ => ends.Add((progress.CurrentFolder!, guard!.Calls)) }));
        }
        Assert.Equal(new[] { "", "a", "big", "z" }.Select(tree.PathOf), ends.Select(f => f.Folder));
        int open = ends[1].LastCall + 1, last = ends[2].LastCall;
        int buffers = last - open - 1; // less its open and the final ERROR_NO_MORE_FILES
        Assert.True(buffers > 1, $"big was listed in {buffers} buffer(s)");

        // Every call of big's own: its open, each buffer, the end of the listing.
        for (int k = open; k <= last; k++)
        {
            ScanResult result = Scans.RunCancellingAt(tree.Root, k, duringTheCall, (root, ct) => Scanner.Scan(root, new ScanProgress(), ct));
            ScanStore store = result.Store;
            string at = $"Stop {(duringTheCall ? "during" : "before")} call {k}, big's call {k - open + 1} of {last - open + 1}";

            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            int a = Scans.Find(result, tree.PathOf("a")), bigEntry = Scans.Find(result, tree.PathOf("big")), zEntry = Scans.Find(result, tree.PathOf("z"));
            Assert.True(store.NotListed(bigEntry), $"{at}: big reads listed");
            Assert.True(store.NotListed(zEntry), $"{at}: z, still queued, reads listed");
            Assert.False(store.NotListed(0));
            Assert.False(store.NotListed(a));
            Assert.Equal(1, store.ChildCount(a));
            Assert.True(store.ChildCount(bigEntry) == 0 && store.FirstChild(bigEntry) == -1, $"{at}: big holds entries of the buffers it read");
            Assert.True(store.Count == 5, $"{at}: the store holds {store.Count} entries: no entry of a cut folder is left behind");
            TraversalTests.AssertChildrenAreContiguous(store);
        }
        Scans.WaitForAbortedCloses();
    }

    // ---- Criterion 2: a listed empty folder is listed ----

    [Fact]
    public void AnEmptyFolderListedBeforeTheStopReadsListed()
    {
        using var tree = new TempTree();
        tree.Folder("a-empty");
        tree.Folder(@"b\c");
        tree.File(@"b\c\f.bin", 10);
        tree.Folder("z");
        tree.File(@"z\g.bin", 20);

        // Listed in this order: the root, a-empty, b, b\c, z.
        ScanResult afterTwo = StopAfterFolders(tree.Root, 2);
        Assert.Equal(ScanStatus.Cancelled, afterTwo.Status);
        ScanStore two = afterTwo.Store;
        int root = 0, empty = Scans.Find(afterTwo, tree.PathOf("a-empty")), b = Scans.Find(afterTwo, tree.PathOf("b")), z = Scans.Find(afterTwo, tree.PathOf("z"));
        Assert.False(two.NotListed(root));
        Assert.False(two.NotListed(empty)); // listed, and found empty
        Assert.True(two.NotListed(b)); // queued behind it
        Assert.True(two.NotListed(z));
        Assert.Equal(4, two.Count); // b's own children were never read
        // The empty folder and the queued ones look alike in every other read; only the mark tells them apart.
        foreach (int unread in new[] { b, z })
        {
            Assert.Equal((two.Kind(empty), two.Flags(empty), two.FirstChild(empty), two.ChildCount(empty)),
                (two.Kind(unread), two.Flags(unread), two.FirstChild(unread), two.ChildCount(unread)));
            Assert.Equal(Rolled.Of(two, empty), Rolled.Of(two, unread));
            Assert.Equal(two.ChildrenLargestFirst(empty), two.ChildrenLargestFirst(unread));
        }

        ScanResult afterThree = StopAfterFolders(tree.Root, 3);
        ScanStore three = afterThree.Store;
        Assert.Equal(ScanStatus.Cancelled, afterThree.Status);
        Assert.False(three.NotListed(Scans.Find(afterThree, tree.PathOf("a-empty"))));
        Assert.False(three.NotListed(Scans.Find(afterThree, tree.PathOf("b")))); // listed, with a child
        Assert.True(three.NotListed(Scans.Find(afterThree, tree.PathOf(@"b\c")))); // appended by b's listing, itself unread
        Assert.True(three.NotListed(Scans.Find(afterThree, tree.PathOf("z"))));

        ScanResult completed = Scans.Run(tree.Root);
        Assert.Equal(ScanStatus.Completed, completed.Status);
        for (int e = 0; e < completed.Store.Count; e++)
            Assert.False(completed.Store.NotListed(e), $"{completed.Store.GetPath(e)} reads NotListed in a completed scan");
        Assert.False(completed.Store.NotListed(Scans.Find(completed, tree.PathOf("a-empty"))));
    }

    // ---- Criterion 1, the root ----

    /// <summary>
    /// Stop at every call of a 2,000-entry root's handling, before it and during it. The first two (its open and its
    /// attribute query) leave no root: until the second answers nothing is known to be a folder. From the third on
    /// (the last-access queries, the keep reopen, each listing buffer) the store holds the root, reading not listed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStopInTheRootsListingLeavesTheRootNotListed(bool duringTheCall)
    {
        using var tree = new TempTree();
        for (int i = 0; i < 2000; i++)
            tree.File($"{i:D4}{new string('n', 92)}.txt", 1);
        SynchronousIoCancel? guard = null;
        ScanResult baseline = Scans.WithGuard(g => guard = g, () => Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None));
        Assert.Equal(ScanStatus.Completed, baseline.Status);
        int calls = guard!.Calls;
        Assert.True(calls > RootOpenAndQuery + 4, $"the root's handling made {calls} calls");

        for (int k = 1; k <= calls; k++)
        {
            ScanResult result = Scans.RunCancellingAt(tree.Root, k, duringTheCall, (root, ct) => Scanner.Scan(root, new ScanProgress(), ct));
            ScanStore store = result.Store;
            string at = $"Stop {(duringTheCall ? "during" : "before")} root call {k} of {calls}";

            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            Assert.True(result.RootError == 0, $"{at}: root error {result.RootError}");
            if (k <= RootOpenAndQuery)
            {
                Assert.True(store.Count == 0, $"{at}: the store holds {store.Count} entries");
                continue;
            }
            Assert.True(store.Count == 1, $"{at}: the store holds {store.Count} entries, expected the root alone");
            Assert.True(store.NotListed(0), $"{at}: the root reads listed");
            Assert.True(store.Kind(0) == EntryKind.Folder && store.Parent(0) == -1, $"{at}: entry 0 is not the root folder");
            Assert.True(store.ChildCount(0) == 0 && store.FirstChild(0) == -1, $"{at}: the root holds children");
            Assert.True(Rolled.Of(store, 0) == default, $"{at}: the root rolls {Rolled.Of(store, 0)}");
            Assert.Empty(result.Unreadable);
            Scans.AssertChargedBytesAreTheTotals(result);
        }
        Scans.WaitForAbortedCloses();
    }

    /// <summary>
    /// The boundary of the early append: a Stop in the root's open or attribute query, a token cancelled before the scan
    /// starts, and a file picked as the root all leave no root entry, with the status the scan always gave them.
    /// </summary>
    [Fact]
    public void ARootNotYetKnownToBeAFolderLeavesNoRootToMark()
    {
        using var tree = new TempTree();
        string file = tree.File("one.txt", 5);

        foreach (bool during in new[] { false, true })
        {
            for (int k = 1; k <= RootOpenAndQuery; k++)
            {
                ScanResult stopped = Scans.RunCancellingAt(tree.Root, k, during, (root, ct) => Scanner.Scan(root, new ScanProgress(), ct));
                Assert.True(ScanStatus.Cancelled == stopped.Status, $"call {k} {(during ? "during" : "before")}: {stopped.Status}");
                Assert.True(stopped.Store.Count == 0, $"call {k} {(during ? "during" : "before")}: the store holds {stopped.Store.Count} entries");
                Assert.Equal(0, stopped.RootError);
            }
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ScanResult before = Scanner.Scan(tree.Root, new ScanProgress(), cancelled.Token);
        Assert.Equal(ScanStatus.Cancelled, before.Status);
        Assert.Equal(0, before.Store.Count);

        ScanResult fileRoot = Scanner.Scan(file, new ScanProgress(), CancellationToken.None);
        Assert.Equal(ScanStatus.RootUnreadable, fileRoot.Status);
        Assert.Equal(267, fileRoot.RootError); // ERROR_DIRECTORY
        Assert.Equal(0, fileRoot.Store.Count);
        Scans.WaitForAbortedCloses();
    }

    // ---- Criterion 1, the size probes ----

    /// <summary>
    /// Stop during the size probes, after every folder's listing committed. The status is Cancelled, but nothing is unread:
    /// no folder reads not listed.
    /// </summary>
    [Fact]
    public void AStopDuringSizingLeavesEveryFolderListed()
    {
        using var tree = new TempTree();
        tree.Folder("a-empty");
        tree.Folder(@"b\c");
        tree.File(@"b\one.txt", 11);
        tree.File(@"b\c\two.txt", 22);
        tree.Folder("z");
        tree.File(@"z\three.txt", 33);
        using var cts = new CancellationTokenSource();
        int probed = 0;

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, new ScanOptions
        {
            ProbeWorkers = 2,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                Interlocked.Increment(ref probed);
                cts.Cancel();
                endOfFile = allocationSize = 1;
                return 0;
            },
        });

        Assert.True(probed > 0, "no probe ran: the Stop did not land in the sizing");
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(0, result.Probes); // the batch the Stop landed in commits nothing
        Assert.Equal(5, Scans.ByPath(result.Store).Count(p => result.Store.Kind(p.Value) == EntryKind.Folder));
        for (int e = 0; e < result.Store.Count; e++)
            Assert.False(result.Store.NotListed(e), $"{result.Store.GetPath(e)} reads not listed, though every listing committed");
        Scans.AssertChargedBytesAreTheTotals(result);
    }

    // ---- Criterion 3: only an append sets the mark, only SetChildren clears it ----

    private static readonly EntryFlags[] NamedFlags =
        [.. Enum.GetValues<EntryFlags>().Where(f => f != EntryFlags.None)];

    private static ScanStore StoreWithRoot()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\synthetic", 0x10);
        return store;
    }

    [Theory]
    [InlineData(EntryKind.Folder)]
    [InlineData(EntryKind.File)]
    [InlineData(EntryKind.Link)]
    public void ACallerCannotSetTheMark(EntryKind kind)
    {
        ScanStore store = StoreWithRoot();
        const EntryFlags mark = (EntryFlags)0x80;
        foreach (EntryFlags extra in NamedFlags.Prepend(EntryFlags.None))
        {
            int count = store.Count;
            Assert.Throws<ArgumentException>(() => store.Append(0, "x", kind, mark | extra, 0x20, 5, 5, new UInt128(0, 77)));
            Assert.Equal(count, store.Count); // refused before the count was published
            Assert.Equal(-1, store.FirstChild(0));
        }
        // A refused append left no half-written entry: the next append is entry 1, unmarked unless it is a folder.
        int next = store.Append(0, "ok", kind, EntryFlags.None, 0x20, 5, 5, new UInt128(0, 78));
        Assert.Equal(1, next);
        Assert.Equal(kind == EntryKind.Folder, store.NotListed(next));
        Assert.Equal(EntryFlags.None, store.Flags(next));
    }

    [Fact]
    public void ACallerCannotSetTheMarkThroughAddFlags()
    {
        const EntryFlags mark = (EntryFlags)0x80;
        ScanStore store = StoreWithRoot();
        int marked = store.Append(0, "marked", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int listed = store.Append(0, "listed", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int file = store.Append(0, "file", EntryKind.File, EntryFlags.None, 0x20, 5, 5, new UInt128(0, 9));
        int link = store.Append(0, "link", EntryKind.Link, EntryFlags.None, 0x400, 0, 0, UInt128.Zero);
        store.SetChildren(listed, store.Count, 0);
        Assert.True(store.NotListed(marked));
        Assert.False(store.NotListed(listed));

        foreach (int entry in new[] { marked, listed, file, link, 0 })
        {
            bool was = store.NotListed(entry);
            EntryFlags flagsBefore = store.Flags(entry);
            foreach (EntryFlags extra in NamedFlags.Where(f => f != EntryFlags.HardlinkDuplicate).Prepend(EntryFlags.None))
            {
                Assert.Throws<ArgumentException>(() => store.AddFlags(entry, mark | extra));
                Assert.Equal(was, store.NotListed(entry));
                Assert.Equal(flagsBefore, store.Flags(entry)); // the named flag beside the mark was not applied either
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AMarkedFolderTakesItsChildrenAndSetChildrenClearsIt(int children)
    {
        ScanStore store = StoreWithRoot();
        int folder = store.Append(0, "f", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        Assert.True(store.NotListed(folder));

        // As FolderDone appends a listing: the children first, under a parent that is still marked.
        int first = store.Count;
        for (int k = 0; k < children; k++)
            store.Append(folder, "c" + k, k % 2 == 0 ? EntryKind.File : EntryKind.Folder, EntryFlags.None, 0x20, 10, 10, new UInt128(0, (ulong)(100 + k)));
        Assert.True(store.NotListed(folder), "the children's appends cleared the mark");
        Assert.Equal(-1, store.FirstChild(folder));

        store.SetChildren(folder, first, children);
        Assert.False(store.NotListed(folder));
        Assert.Equal(children, store.ChildCount(folder));
        Assert.Equal(children == 0 ? -1 : first, store.FirstChild(folder));
        Assert.Equal(EntryFlags.None, store.Flags(folder));
        for (int k = 0; k < children; k++)
            Assert.Equal(k % 2 == 1, store.NotListed(first + k)); // the child folders are born marked, the files are not
        Assert.True(store.NotListed(0)); // the root, appended and never registered
        Assert.False(store.NotListed(folder));
    }

    [Fact]
    public void EveryOtherFlagWriteLeavesTheMarkAlone()
    {
        ScanStore store = StoreWithRoot();
        Assert.True(store.NotListed(0)); // AppendRoot goes through Append

        // Every named flag AddFlags accepts, on a marked and on a listed folder, in a file and in a link.
        foreach (EntryFlags flag in NamedFlags.Where(f => f != EntryFlags.HardlinkDuplicate))
        {
            int marked = store.Append(0, "m", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
            int listed = store.Append(0, "l", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
            store.SetChildren(listed, store.Count, 0);
            int file = store.Append(0, "f", EntryKind.File, EntryFlags.None, 0x20, 1, 1, UInt128.Zero);
            int link = store.Append(0, "k", EntryKind.Link, EntryFlags.None, 0x400, 0, 0, UInt128.Zero);

            foreach (int entry in new[] { marked, listed, file, link })
                store.AddFlags(entry, flag);
            Assert.True(store.NotListed(marked), $"AddFlags({flag}) cleared the mark");
            Assert.False(store.NotListed(listed), $"AddFlags({flag}) set the mark on a listed folder");
            Assert.False(store.NotListed(file), $"AddFlags({flag}) set the mark on a file");
            Assert.False(store.NotListed(link), $"AddFlags({flag}) set the mark on a link");
            foreach (int entry in new[] { marked, listed, file, link })
                Assert.Equal(flag, store.Flags(entry));
        }

        // A flag handed to Append beside a folder's kind keeps its meaning and gains the mark.
        foreach (EntryFlags flag in NamedFlags.Where(f => f != EntryFlags.HardlinkDuplicate))
        {
            int folder = store.Append(0, "g", EntryKind.Folder, flag, 0x10, 0, 0, UInt128.Zero);
            Assert.True(store.NotListed(folder));
            Assert.Equal(flag, store.Flags(folder));
        }

        // A later name of a file: flagged HardlinkDuplicate by the append, never marked.
        int firstName = store.Append(0, "h1", EntryKind.File, EntryFlags.None, 0x20, 40, 40, new UInt128(5, 5));
        int laterName = store.Append(0, "h2", EntryKind.File, EntryFlags.None, 0x20, 40, 40, new UInt128(5, 5));
        Assert.Equal(EntryFlags.None, store.Flags(firstName));
        Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(laterName));
        Assert.False(store.NotListed(firstName) || store.NotListed(laterName));

        // A correction under a chain of marked folders posts up it and clears nothing.
        int a = store.Append(0, "a", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int b = store.Append(a, "b", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int c = store.Append(b, "c", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        int deepFile = store.Append(c, "d", EntryKind.File, EntryFlags.None, 0x20, 100, 4096, new UInt128(0, 321));
        long rootBefore = store.RolledSize(0);
        ScanStore.Posted posted = store.CorrectSizes(deepFile, 150, 8192);
        Assert.Equal(new ScanStore.Posted(50, 4096, 0), posted);
        foreach (int folder in new[] { 0, a, b, c })
            Assert.True(store.NotListed(folder), $"a correction cleared the mark of {store.Name(folder).ToString()}");
        Assert.Equal(rootBefore + 50, store.RolledSize(0));
        Assert.Equal(150, store.RolledSize(c));
        Assert.False(store.NotListed(deepFile));
        Assert.Equal(EntryFlags.None, store.Flags(deepFile));
    }

    // ---- Criteria 4 and 5: a marked folder is still a folder, and classifies as it would without the mark ----

    /// <summary>The stop points of the sweep tree the figures are read at: inside big, between h1 and h2, in the root's listing, just after m.</summary>
    private static (string Name, int Call, bool During)[] StopPoints(TempTree tree, Baseline b)
    {
        int insideBig = (b.LastCallOf(tree, "a-empty") + b.LastCallOf(tree, "big")) / 2;
        return
        [
            ("inside big", insideBig, false),
            ("inside big, during", insideBig, true),
            ("between h1 and h2", b.LastCallOf(tree, "h1") + 1, false),
            ("inside the root's listing", b.LastCallOf(tree, "") - 1, true),
            ("just after m", b.LastCallOf(tree, "m") + 1, false),
        ];
    }

    /// <summary>The entries of a store as a synthetic append stream, as the recount reads them: raw columns only.</summary>
    private static List<Fed> Stream(ScanStore store)
    {
        var stream = new List<Fed>(store.Count);
        for (int e = 0; e < store.Count; e++)
        {
            stream.Add(new Fed(store.Parent(e), store.Kind(e), store.Size(e), store.OnDisk(e), store.FileId(e),
                store.Flags(e) & ~(EntryFlags.HardlinkDuplicate | EntryFlags.Unreadable), store.Name(e).ToString()));
        }
        return stream;
    }

    [Fact]
    public void AStoppedStoreRecountsWithNoDifference()
    {
        using TempTree tree = SweepTree();
        Baseline baseline = RunBaseline(tree, planted: true);
        int storesRead = 0, markedSeen = 0;

        foreach ((string name, int call, bool during) in StopPoints(tree, baseline))
        {
            ScanResult result = StopAt(tree, call, during);
            ScanStore store = result.Store;
            Assert.True(ScanStatus.Cancelled == result.Status, $"{name}: {result.Status}");
            Assert.True(store.Count >= 1, $"{name}: the store holds no root");

            // The recount: from the raw columns alone, with its own first-seen ID set and its own rule for what charges.
            List<Fed> stream = Stream(store);
            Rolled[] expected = RollupOracle.Expected(stream);
            RollupOracle.AssertMatches(store, expected);
            RollupOracle.CheckStructure(store);
            Scans.AssertChargedBytesAreTheTotals(result);

            for (int e = 0; e < store.Count; e++)
            {
                if (store.NotListed(e))
                {
                    markedSeen++;
                    Assert.True(store.Kind(e) == EntryKind.Folder, $"{name}: entry {e} is marked and a {store.Kind(e)}");
                    Assert.True(expected[e] == default, $"{name}: {store.GetPath(e)} is not listed and recounts {expected[e]}");
                }
                if (store.Kind(e) != EntryKind.Folder || store.ChildCount(e) == 0)
                {
                    Assert.Empty(store.ChildrenLargestFirst(e));
                    continue;
                }
                // Largest first, from the recount alone: the child's on-disk figure, then listing order.
                int first = store.FirstChild(e);
                long total = expected[e].OnDisk;
                ChildRow[] want = Enumerable.Range(first, store.ChildCount(e))
                    .OrderByDescending(c => expected[c].OnDisk).ThenBy(c => c)
                    .Select(c => new ChildRow(c, expected[c].OnDisk, total == 0 ? 0.0 : (double)expected[c].OnDisk / total))
                    .ToArray();
                Assert.True(want.SequenceEqual(store.ChildrenLargestFirst(e)), $"{name}: the children of {store.GetPath(e)} are not in the recounted order");
            }
            storesRead++;
        }
        Assert.Equal(5, storesRead);
        Assert.True(markedSeen > 5, $"only {markedSeen} marked folders were recounted");
        Scans.WaitForAbortedCloses();
    }

    [Fact]
    public void AStoppedStoreClassifiesAsItsMaskedTwin()
    {
        using TempTree tree = SweepTree();
        Baseline baseline = RunBaseline(tree, planted: true);
        string denied = tree.PathOf("denied"), layers = tree.PathOf(Store), h2 = tree.PathOf(@"h2\y.bin");
        int twinsCompared = 0;

        // Every named flag of EntryFlags, and the mark is none of them.
        Assert.DoesNotContain(Enum.GetValues<EntryFlags>(), f => ((byte)f & 0x80) != 0);
        Assert.DoesNotContain(Enum.GetNames<EntryFlags>(), n => ((byte)Enum.Parse<EntryFlags>(n) & 0x80) != 0);

        foreach ((string name, int call, bool during) in StopPoints(tree, baseline))
        {
            ScanResult result = StopAt(tree, call, during);
            ScanStore store = result.Store;
            Dictionary<string, int> byPath = Scans.ByPath(store);

            // The same store with no mark: replayed through the append path, every folder registered, then Unreadable added.
            ScanStore twin = RollupOracle.Feed(Stream(store));
            for (int e = 0; e < twin.Count; e++)
            {
                if (twin.Kind(e) == EntryKind.Folder && store.ChildCount(e) == 0)
                    twin.SetChildren(e, twin.Count, 0);
                if ((store.Flags(e) & EntryFlags.Unreadable) != 0)
                    twin.AddFlags(e, EntryFlags.Unreadable);
            }
            for (int e = 0; e < twin.Count; e++)
                Assert.False(twin.NotListed(e), $"{name}: the twin still carries a mark on {twin.GetPath(e)}");

            Assert.Equal(twin.Count, store.Count);
            for (int e = 0; e < store.Count; e++)
            {
                string at = $"{name}: {store.GetPath(e)}";
                Assert.True(store.Kind(e) == twin.Kind(e), $"{at}: kind");
                Assert.True(store.Flags(e) == twin.Flags(e), $"{at}: flags {store.Flags(e)}, the twin's {twin.Flags(e)}");
                Assert.True(((byte)store.Flags(e) & 0x80) == 0, $"{at}: Flags shows the mark");
                Assert.True(store.ChargedSize(e) == twin.ChargedSize(e) && store.ChargedOnDisk(e) == twin.ChargedOnDisk(e), $"{at}: charged bytes");
                Assert.True(Rolled.Of(store, e) == Rolled.Of(twin, e), $"{at}: rolled {Rolled.Of(store, e)}, the twin's {Rolled.Of(twin, e)}");
                Assert.True(store.FirstChild(e) == twin.FirstChild(e) && store.ChildCount(e) == twin.ChildCount(e), $"{at}: children");
                Assert.True(store.ChildrenLargestFirst(e).SequenceEqual(twin.ChildrenLargestFirst(e)), $"{at}: children largest first");
            }

            // What the classifications are, by name, in the stopped store itself.
            if (byPath.TryGetValue(denied, out int d) && !store.NotListed(d))
            {
                Assert.Equal(EntryFlags.Unreadable, store.Flags(d));
                Assert.Contains(result.Unreadable, u => u.Entry == d);
            }
            if (byPath.TryGetValue(layers, out int s) && !store.NotListed(s))
                Assert.Equal(EntryFlags.NotScanned, store.Flags(s));
            if (byPath.TryGetValue(h2, out int dup))
                Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(dup));
            twinsCompared++;
        }
        Assert.Equal(5, twinsCompared);

        // Just after m: the denied folder, the skipped store, the hardlink pair and the queued z are all in one stopped store.
        ScanResult afterM = StopAt(tree, baseline.LastCallOf(tree, "m") + 1, false);
        ScanStore stopped = afterM.Store;
        Dictionary<string, int> paths = Scans.ByPath(stopped);
        Assert.Equal(EntryFlags.Unreadable, stopped.Flags(paths[denied]));
        Assert.False(stopped.NotListed(paths[denied]));
        Assert.Equal(EntryFlags.NotScanned, stopped.Flags(paths[layers]));
        Assert.False(stopped.NotListed(paths[layers]));
        Assert.Equal(EntryFlags.HardlinkDuplicate, stopped.Flags(paths[h2]));
        Assert.True(stopped.NotListed(paths[tree.PathOf("z")]));
        Assert.Equal(EntryFlags.None, stopped.Flags(paths[tree.PathOf("z")]));
        Scans.WaitForAbortedCloses();
    }

    // ---- Criterion 6: a scan that runs to the end ----

    [Fact]
    public void ACompletedScanLeavesNothingNotListed()
    {
        using TempTree sweep = SweepTree();
        ScanResult first = Scans.Run(sweep.Root, Options(sweep));
        AssertNothingNotListed(first, "the sweep tree");
        Assert.Single(first.SkippedStores);
        Assert.Single(first.Unreadable);

        using var tree = new TempTree();
        string component = new string('L', 120);
        string deep = $@"{component}\{new string('m', 120)}\{new string('n', 60)}";
        tree.Folder(deep);
        tree.File($@"{deep}\far.txt", 7);
        tree.Folder("plain");
        tree.Folder("empty");
        tree.Junction("j-outside", tree.Outside("target"));
        Assert.True(tree.PathOf(deep).Length > 260, "the path should pass MAX_PATH");
        ScanResult second = Scans.Run(tree.Root);
        AssertNothingNotListed(second, "the tree with a junction and a long path");
        Assert.Contains(Scans.ByPath(second.Store).Values, e => second.Store.Kind(e) == EntryKind.Link);
    }

    private static void AssertNothingNotListed(ScanResult result, string what)
    {
        Assert.Equal(ScanStatus.Completed, result.Status);
        for (int e = 0; e < result.Store.Count; e++)
        {
            Assert.False(result.Store.NotListed(e), $"{what}: {result.Store.GetPath(e)} reads not listed after a scan that ran to the end");
            Assert.True(((byte)result.Store.Flags(e) & 0x80) == 0, $"{what}: {result.Store.GetPath(e)} shows the mark");
        }
    }
}
