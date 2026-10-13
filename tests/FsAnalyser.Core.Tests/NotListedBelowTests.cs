using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-111: a stopped scan's store says, for any folder, whether some folder at or below it was never listed
/// (<see cref="ScanStore.NotListedAtOrBelow"/>). The read is derived from the marks and from the order a scan registers
/// folders in, so these tests do two jobs: hold the answer to a full walk of everything below the folder at every point a
/// Stop can land (criterion 1, and 4 for the scans that run to the end), and fail when that order changes (criterion 2).
/// </summary>
/// <remarks>
/// The walk (<see cref="Walked"/>) reads the same marks but looks at every child of every folder, so it shares no logic with
/// the read, which follows only the last folder of each block. The scans are real, on temp trees; the replays feed a store
/// the way a differently ordered scan would, one whole block and one SetChildren per folder. The oracle's own feeder
/// (<see cref="RollupOracle.Feed"/>) is not used for that: it registers every child as it goes, which clears a folder's mark
/// at its first child (SL-109, lesson #485).
/// </remarks>
public class NotListedBelowTests
{
    /// <summary>The ragged tree's folders in the order a scan lists them: depth-first, subfolders in name order.</summary>
    internal static readonly string[] RaggedOrder =
    [
        "", "a", @"a\a1", @"a\a1\a11", @"a\a1\a11\a111", @"a\a2", "b", "c", @"c\c1", @"c\c1\c11", @"c\c2", @"c\c2\c21",
        @"c\c2\c21\c211", @"c\c2\c21\c211\c2111", "e",
    ];

    /// <summary>
    /// Fifteen folders of ragged depth. A uniform tree cannot tell a queue from a stack (spike 121: no mismatch on a 3-ary,
    /// 5-level tree), so this one has: a folder whose first subfolder is deep and whose last is empty (a), an empty folder
    /// (b), a folder whose last subfolder is the deepest, five levels down (c), a last folder holding only files (e), and
    /// blocks that end in files and in a junction after their last subfolder (the root; c), which the backward scan has to
    /// skip.
    /// </summary>
    internal static TempTree RaggedTree()
    {
        var tree = new TempTree();
        tree.Folder(@"a\a1\a11\a111");
        tree.File(@"a\a1\a11\a111\leaf.txt", 3);
        tree.Folder(@"a\a2");
        tree.Folder("b");
        tree.Folder(@"c\c1\c11");
        tree.File(@"c\c1\c11\x.txt", 5);
        tree.Folder(@"c\c2\c21\c211\c2111");
        tree.File(@"c\c2\c21\c211\c2111\y.txt", 7);
        tree.File(@"c\c3.txt", 9);
        tree.Junction(@"c\zj", tree.Outside("target"));
        tree.Folder("e");
        tree.File(@"e\f1.txt", 11);
        tree.File(@"e\f2.txt", 13);
        tree.File("z.txt", 15);
        return tree;
    }

    // ---- The oracle and the order pin ----

    /// <summary>
    /// Whether each entry is not listed itself or has a not-listed folder below it, by looking at every registered child of
    /// every entry from the root, with an explicit stack (a deep chain must not overflow the test thread's). Every entry is
    /// reached exactly once, or the store is not a tree and nothing here can be judged.
    /// </summary>
    internal static bool[] Walked(ScanStore store)
    {
        int n = store.Count;
        var answer = new bool[n];
        if (n == 0)
            return answer;

        var parent = new int[n];
        var reached = new bool[n];
        var preorder = new List<int>(n);
        var stack = new Stack<int>();
        stack.Push(0);
        reached[0] = true;
        parent[0] = -1;
        while (stack.Count > 0)
        {
            int e = stack.Pop();
            preorder.Add(e);
            int first = store.FirstChild(e), count = store.ChildCount(e);
            for (int c = first; c < first + count; c++)
            {
                Assert.True(!reached[c], $"entry {c} is in more than one child block");
                reached[c] = true;
                parent[c] = e;
                stack.Push(c);
            }
        }
        Assert.True(preorder.Count == n, $"{n - preorder.Count} entries are in no registered child block");

        for (int k = n - 1; k >= 0; k--) // every parent came before its children, so this meets children first
        {
            int e = preorder[k];
            if (store.NotListed(e))
                answer[e] = true;
            if (answer[e] && parent[e] >= 0)
                answer[parent[e]] = true;
        }
        return answer;
    }

    /// <summary>
    /// Null when the folders that have children were registered in depth-first order: visiting the registered tree in
    /// preorder, with each folder's subfolders in block order, the first child of each folder with children is strictly
    /// greater than the one before. That holds for every prefix of a depth-first registration, so it holds of any store a
    /// Stop left. It cannot see folders listed empty, which have no block: the listing-order assertions beside it cover those.
    /// </summary>
    internal static string? DepthFirstViolation(ScanStore store)
    {
        if (store.Count == 0)
            return null;
        var stack = new Stack<int>();
        stack.Push(0);
        int previous = -1, previousFolder = -1;
        while (stack.Count > 0)
        {
            int e = stack.Pop();
            int count = store.ChildCount(e);
            if (count == 0)
                continue;
            int first = store.FirstChild(e);
            if (first <= previous)
                return $"folder {e} ({store.GetPath(e)}) has its children at {first}, but folder {previousFolder} before it in preorder has them at {previous}";
            previous = first;
            previousFolder = e;
            for (int c = first + count - 1; c >= first; c--)
            {
                if (store.Kind(c) == EntryKind.Folder)
                    stack.Push(c);
            }
        }
        return null;
    }

    private static void AssertListedDepthFirst(ScanStore store) =>
        Assert.True(DepthFirstViolation(store) is null, DepthFirstViolation(store));

    private static void AssertAnswersAsAWalk(ScanStore store, string at)
    {
        bool[] walked = Walked(store);
        for (int e = 0; e < store.Count; e++)
        {
            Assert.True(store.NotListedAtOrBelow(e) == walked[e],
                $"{at}: {store.GetPath(e)} reads {store.NotListedAtOrBelow(e)}, a walk of everything below it says {walked[e]}");
            if (store.Kind(e) != EntryKind.Folder)
                Assert.False(store.NotListedAtOrBelow(e), $"{at}: {store.GetPath(e)}, a {store.Kind(e)}, reads true");
        }
    }

    // ---- Criterion 1: the answer is a walk's, at every point a Stop can land ----

    /// <summary>
    /// Stop at every guarded call of the sweep tree, before it and during it, with the container layer store planted under
    /// m (so Layers is skipped, committed with no children, when m is). For every store a Stop left, every entry's answer
    /// is what a walk of everything below it says, and every file and link reads false. The cut folder (big, part-way
    /// through its buffers) committed nothing and is on no pending stack, yet it and the root must read true.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAtAnyCallOfTheSweepTreeAnswersAsAWalk(bool duringTheCall)
    {
        using TempTree tree = NotListedTests.SweepTree();
        NotListedTests.Baseline baseline = NotListedTests.RunBaseline(tree, planted: true);
        Assert.Equal(NotListedTests.SweepOrder.Select(tree.PathOf), baseline.FolderEnds.Select(f => f.Folder));
        int big = Array.IndexOf(NotListedTests.SweepOrder, "big");
        int bigBuffers = baseline.FolderEnds[big].LastCall - baseline.FolderEnds[big - 1].LastCall - 2;
        Assert.True(bigBuffers > 1, $"big was listed in {bigBuffers} buffer(s): the between-buffers case needs several");
        string layers = tree.PathOf(NotListedTests.Store);

        int stores = 0, empty = 0, cutInsideBig = 0, rootsNotListed = 0, layersSkipped = 0, listedTrue = 0;
        for (int k = 1; k <= baseline.Calls; k++)
        {
            ScanResult result = NotListedTests.StopAt(tree, k, duringTheCall);
            ScanStore store = result.Store;
            int inProgress = baseline.FolderEnds.FindIndex(f => f.LastCall >= k);
            string at = $"Stop {(duringTheCall ? "during" : "before")} call {k} of {baseline.Calls}, in {baseline.FolderEnds[inProgress].Folder}";
            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            if (store.Count == 0)
            {
                empty++; // calls 1 and 2: the root is not yet known to be a folder, and there is nothing to ask
                continue;
            }
            stores++;
            AssertAnswersAsAWalk(store, at);
            AssertListedDepthFirst(store);

            Dictionary<string, int> byPath = Scans.ByPath(store);
            if (store.NotListed(0))
            {
                rootsNotListed++;
                Assert.True(store.NotListedAtOrBelow(0), $"{at}: the root, itself not listed, reads false");
            }
            if (baseline.FolderEnds[inProgress].Folder == tree.PathOf("big") && k > baseline.FolderEnds[inProgress - 1].LastCall + 1)
            {
                cutInsideBig++;
                Assert.True(store.NotListedAtOrBelow(byPath[tree.PathOf("big")]), $"{at}: big, cut part-way, reads false");
                Assert.True(store.NotListedAtOrBelow(0), $"{at}: the root reads false while big is cut");
            }
            if (byPath.TryGetValue(layers, out int skipped) && store.Flags(skipped) == EntryFlags.NotScanned)
            {
                layersSkipped++;
                Assert.False(store.NotListedAtOrBelow(skipped), $"{at}: the skipped container store reads true");
                Assert.False(store.NotListedAtOrBelow(byPath[tree.PathOf("m")]), $"{at}: m, whose only subfolder is the skipped store, reads true");
            }
            for (int e = 0; e < store.Count; e++)
            {
                if (!store.NotListed(e) && store.Kind(e) == EntryKind.Folder && store.NotListedAtOrBelow(e))
                    listedTrue++;
            }
        }

        // The sweep was not vacuous: it met stops inside big's buffers, roots that were cut, the skipped store, and listed folders over unlisted ones.
        Assert.True(empty == 2, $"{empty} stops left an empty store, expected the first two calls' only");
        Assert.True(cutInsideBig >= bigBuffers, $"only {cutInsideBig} stops landed inside big");
        Assert.True(rootsNotListed >= 4, $"only {rootsNotListed} stops left the root not listed");
        Assert.True(layersSkipped >= 1, "no stop saw the container layer store committed as not scanned");
        Assert.True(listedTrue >= stores - rootsNotListed, $"only {listedTrue} listed folders read true over {stores} stores");
        Scans.WaitForAbortedCloses();
    }

    /// <summary>
    /// The same on the ragged tree, where the shapes the chain has to get right are: an unlisted folder five levels down
    /// under a chain of listed last subfolders (c), a folder whose deep first subfolder is still being listed while its
    /// empty last one is not yet (a, before and after a2), and blocks ending in files and a junction.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAtAnyCallOfTheRaggedTreeAnswersAsAWalk(bool duringTheCall)
    {
        using TempTree tree = RaggedTree();
        NotListedTests.Baseline baseline = NotListedTests.RunBaseline(tree, planted: false);
        Assert.Equal(RaggedOrder.Select(tree.PathOf), baseline.FolderEnds.Select(f => f.Folder));

        int stores = 0, empty = 0, rootsNotListed = 0, listedTrue = 0, deepChain = 0, aBeforeA2 = 0, aAfterA2 = 0, listedFalseOverUnlisted = 0, junctions = 0;
        for (int k = 1; k <= baseline.Calls; k++)
        {
            ScanResult result = Scans.RunCancellingAt(tree.Root, k, duringTheCall, (root, ct) => Scanner.Scan(root, new ScanProgress(), ct));
            ScanStore store = result.Store;
            string at = $"Stop {(duringTheCall ? "during" : "before")} call {k} of {baseline.Calls}";
            Assert.True(ScanStatus.Cancelled == result.Status, $"{at}: {result.Status}");
            if (store.Count == 0)
            {
                empty++;
                continue;
            }
            stores++;
            AssertAnswersAsAWalk(store, at);
            AssertListedDepthFirst(store);

            Dictionary<string, int> byPath = Scans.ByPath(store);
            int Entry(string relative) => byPath.TryGetValue(tree.PathOf(relative), out int e) ? e : -1;
            if (store.NotListed(0))
                rootsNotListed++;
            for (int e = 0; e < store.Count; e++)
            {
                if (!store.NotListed(e) && store.Kind(e) == EntryKind.Folder && store.NotListedAtOrBelow(e))
                    listedTrue++;
                if (store.Kind(e) == EntryKind.Link)
                    junctions++;
            }

            // c > c2 > c21 > c211 > c2111: the unlisted folder at the end of four listed last subfolders.
            int c2111 = Entry(@"c\c2\c21\c211\c2111");
            if (c2111 >= 0 && store.NotListed(c2111) && !store.NotListed(Entry(@"c\c2\c21\c211")))
            {
                deepChain++;
                foreach (string up in new[] { "c", @"c\c2", @"c\c2\c21", @"c\c2\c21\c211" })
                {
                    Assert.False(store.NotListed(Entry(up)), $"{at}: {up} is listed");
                    Assert.True(store.NotListedAtOrBelow(Entry(up)), $"{at}: {up} reads false over the unlisted c2111");
                }
            }

            // a: a1\a11\a111 first, a2 (empty) last. While a2 is queued something is unlisted below a; once a2 is listed, a1 is finished.
            int a = Entry("a"), a2 = Entry(@"a\a2");
            if (a >= 0 && !store.NotListed(a) && a2 >= 0)
            {
                if (store.NotListed(a2))
                {
                    aBeforeA2++;
                    Assert.True(store.NotListedAtOrBelow(a), $"{at}: a reads false while a2 is not yet listed");
                }
                else
                {
                    aAfterA2++;
                    Assert.False(store.NotListedAtOrBelow(a), $"{at}: a reads true though a2 is listed and so a1 is finished");
                    if (store.NotListedAtOrBelow(0))
                        listedFalseOverUnlisted++;
                }
            }
        }

        Assert.True(empty == 2, $"{empty} stops left an empty store");
        Assert.True(deepChain >= 1, "no stop left c2111 unlisted under listed c, c2, c21 and c211");
        Assert.True(aBeforeA2 >= 1 && aAfterA2 >= 1, $"a was met with a2 unlisted {aBeforeA2} times and listed {aAfterA2} times");
        Assert.True(listedFalseOverUnlisted >= 1, "no stop left a finished folder under a root that still had something unlisted");
        Assert.True(junctions >= 1, "no store held the junction");
        Assert.True(rootsNotListed >= 1, "no stop left the root not listed");
        Assert.True(listedTrue >= stores - rootsNotListed, $"only {listedTrue} listed folders read true over {stores} stores");
        Scans.WaitForAbortedCloses();
    }

    // ---- Criterion 2: the answer relies on the shipped traversal ----

    /// <summary>
    /// Completed real scans of both trees: the folders were listed in the planned depth-first order, folders with children
    /// have their blocks in increasing order along it, and nothing reads unlisted below any folder. This is the pin on the
    /// shipped traversal: a queue, a first-first push or a lister that commits out of order fails here, on real output.
    /// </summary>
    [Fact]
    public void TheScannerRegistersFoldersDepthFirstInListingOrder()
    {
        using TempTree ragged = RaggedTree();
        NotListedTests.Baseline raggedScan = NotListedTests.RunBaseline(ragged, planted: false);
        Assert.Equal(RaggedOrder.Select(ragged.PathOf), raggedScan.FolderEnds.Select(f => f.Folder));
        AssertListedDepthFirst(raggedScan.Result.Store);
        AssertNothingUnlistedBelow(raggedScan.Result.Store, "the ragged tree");

        using TempTree sweep = NotListedTests.SweepTree();
        NotListedTests.Baseline sweepScan = NotListedTests.RunBaseline(sweep, planted: true);
        Assert.Equal(NotListedTests.SweepOrder.Select(sweep.PathOf), sweepScan.FolderEnds.Select(f => f.Folder));
        AssertListedDepthFirst(sweepScan.Result.Store);
        AssertNothingUnlistedBelow(sweepScan.Result.Store, "the sweep tree");
    }

    private static void AssertNothingUnlistedBelow(ScanStore store, string what)
    {
        for (int e = 0; e < store.Count; e++)
            Assert.False(store.NotListedAtOrBelow(e), $"{what}: {store.GetPath(e)} reads something unlisted below it after a scan that ran to the end");
    }

    private enum Order { StackLastFirst, Queue, TwoAtATime, StackFirstFirst }

    private sealed record Replayed(ScanStore Store, int States, int Differing);

    /// <summary>
    /// Registers the shape of <paramref name="shape"/> (a completed scan's store) into a new store the way a scan ordered as
    /// <paramref name="order"/> would: each folder's children appended as one block, then one SetChildren, and after every
    /// SetChildren the read is held to the walk. Counts the states where it differs.
    /// </summary>
    private static Replayed Replay(ScanStore shape, Order order)
    {
        var store = new ScanStore();
        var fed = new int[shape.Count];
        fed[0] = store.AppendRoot(shape.Name(0).ToString(), (uint)shape.Attributes(0));
        int states = 0, differing = 0;

        void Commit(int node)
        {
            int first = store.Count, count = shape.ChildCount(node);
            for (int k = 0; k < count; k++)
            {
                int child = shape.FirstChild(node) + k;
                fed[child] = store.Append(fed[node], shape.Name(child), shape.Kind(child), EntryFlags.None, (uint)shape.Attributes(child), 1, 1, UInt128.Zero);
            }
            store.SetChildren(fed[node], first, count);
            states++;
            bool[] walked = Walked(store);
            for (int e = 0; e < store.Count; e++)
            {
                if (store.NotListedAtOrBelow(e) != walked[e])
                {
                    differing++;
                    break;
                }
            }
        }

        List<int> Subfolders(int node) =>
            [.. Enumerable.Range(shape.ChildCount(node) == 0 ? 0 : shape.FirstChild(node), shape.ChildCount(node)).Where(c => shape.Kind(c) == EntryKind.Folder)];

        Commit(0);
        switch (order)
        {
            case Order.Queue:
                var queue = new Queue<int>(Subfolders(0));
                while (queue.Count > 0)
                {
                    int node = queue.Dequeue();
                    Commit(node);
                    foreach (int sub in Subfolders(node))
                        queue.Enqueue(sub);
                }
                break;

            case Order.TwoAtATime:
                // Two listers each take a folder before either has committed: both are popped, then both are listed, then
                // each one's subfolders are pushed.
                var pair = new Stack<int>();
                foreach (int sub in Enumerable.Reverse(Subfolders(0)))
                    pair.Push(sub);
                while (pair.Count > 0)
                {
                    int x = pair.Pop();
                    int y = pair.Count > 0 ? pair.Pop() : -1;
                    Commit(x);
                    if (y >= 0)
                        Commit(y);
                    foreach (int sub in Enumerable.Reverse(Subfolders(x)))
                        pair.Push(sub);
                    if (y >= 0)
                    {
                        foreach (int sub in Enumerable.Reverse(Subfolders(y)))
                            pair.Push(sub);
                    }
                }
                break;

            default:
                bool lastFirst = order == Order.StackLastFirst;
                var stack = new Stack<int>();
                foreach (int sub in lastFirst ? Enumerable.Reverse(Subfolders(0)) : Subfolders(0))
                    stack.Push(sub);
                while (stack.Count > 0)
                {
                    int node = stack.Pop();
                    Commit(node);
                    foreach (int sub in lastFirst ? Enumerable.Reverse(Subfolders(node)) : Subfolders(node))
                        stack.Push(sub);
                }
                break;
        }
        return new Replayed(store, states, differing);
    }

    private static void AssertSameStore(ScanStore expected, ScanStore actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int e = 0; e < expected.Count; e++)
        {
            string at = $"entry {e} ({expected.GetPath(e)})";
            Assert.True(expected.Parent(e) == actual.Parent(e), $"{at}: parent");
            Assert.True(expected.Name(e).SequenceEqual(actual.Name(e)), $"{at}: name");
            Assert.True(expected.Kind(e) == actual.Kind(e), $"{at}: kind");
            Assert.True(expected.FirstChild(e) == actual.FirstChild(e) && expected.ChildCount(e) == actual.ChildCount(e), $"{at}: children");
            Assert.True(expected.NotListed(e) == actual.NotListed(e), $"{at}: mark");
        }
    }

    /// <summary>
    /// The ragged tree's shape replayed in queue order: the read then differs from the walk in some states, and the
    /// depth-first pin throws on the finished store. The same shape replayed in the scanner's own order is the control: no
    /// state differs, the store comes out entry for entry as the real scan's, and the pin passes. So the replay models the
    /// scan, and a passing guard is not vacuous.
    /// </summary>
    [Fact]
    public void AQueueOrderedStoreBreaksTheAnswerAndTheGuardSeesIt()
    {
        using TempTree tree = RaggedTree();
        ScanStore scanned = NotListedTests.RunBaseline(tree, planted: false).Result.Store;

        Replayed control = Replay(scanned, Order.StackLastFirst);
        Assert.Equal(RaggedOrder.Length, control.States);
        Assert.Equal(0, control.Differing);
        AssertSameStore(scanned, control.Store);
        AssertListedDepthFirst(control.Store);

        Replayed queue = Replay(scanned, Order.Queue);
        Assert.Equal(RaggedOrder.Length, queue.States);
        Assert.True(queue.Differing >= 1, "a queue-ordered store never read differently from a walk: the tree cannot tell the orders apart");
        Assert.NotNull(DepthFirstViolation(queue.Store));
    }

    /// <summary>
    /// The same shape registered as two listers would: both take a folder before either commits, and with the subfolders
    /// pushed first-first. Folders are then committed out of preorder, so the read differs from the walk in some states and
    /// the pin throws.
    /// </summary>
    [Fact]
    public void AParallelOrderedStoreBreaksTheAnswerAndTheGuardSeesIt()
    {
        using TempTree tree = RaggedTree();
        ScanStore scanned = NotListedTests.RunBaseline(tree, planted: false).Result.Store;

        foreach (Order order in new[] { Order.TwoAtATime, Order.StackFirstFirst })
        {
            Replayed replayed = Replay(scanned, order);
            Assert.Equal(RaggedOrder.Length, replayed.States);
            Assert.True(replayed.Differing >= 1, $"{order}: the store never read differently from a walk");
            Assert.True(DepthFirstViolation(replayed.Store) is not null, $"{order}: the depth-first pin did not fire");
        }
    }
}
