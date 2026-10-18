using System.Diagnostics;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;
using Xunit.Sdk;
using Figures = FsAnalyser.Core.AtLeastFigures;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-112: after Stop, the store says which folders a size check still pending at Stop could change
/// (<see cref="ScanStore.Unfinished"/>) and what each holds for certain (<see cref="ScanStore.AtLeast"/>). A hardlink's
/// charge is its first name's own check, so the same read covers criterion 2. The scans here are real, on one temp tree
/// whose hardlink first names are listed stale in both directions, and every stopped store is compared with the scans
/// of the unchanged tree that ran to the end.
/// </summary>
/// <remarks>
/// What was listed and what was checked never comes from the watermark under test: the oracle reads each file's flags
/// (a checked file is SizeProbed or ProbeFailed) and walks every entry's Parent chain. Each case command of the design
/// is bounded by the engine's 120 s limit (Q-222), so the tree is built once for the class, a group is made stale again by
/// setting lengths through its names and never by making links again, and each Fact's stops are sampled.
/// </remarks>
public class UnfinishedTests(ITestOutputHelper output, UnfinishedTests.Rig rig) : IClassFixture<UnfinishedTests.Rig>
{
    private const int Batch = 8;
    private const uint FileListDirectory = 0x1;

    // ---- The scans ----

    internal static ScanOptions Options(Action<int>? afterFolder = null, Func<UInt128, UInt128>? listedId = null,
        ProbeFile? probe = null, ProbeRule? rule = null, int workers = 1, TimeSpan? abandon = null) => new()
    {
        ContainerLayerStorePath = "", // a PC with no layer store: the walk is the tree's alone
        AfterFolder = afterFolder,
        ListedFileId = listedId,
        Probe = probe,
        ProbeRule = rule,
        ProbeWorkers = workers,
        ProbeBatch = Batch,
        ProbeAbandonAfter = abandon ?? ProbePass.DefaultAbandonAfter,
    };

    // ---- 1. A Stop in the listing: SizeFiles never ran ----

    /// <summary>
    /// Stop after each folder's listing committed, at sampled calls of the scan thread (the first two, which leave no root, the
    /// folder cut mid-listing, and the rest), and at entries of a commit that Cancel reached part-way. No size check ran, so every
    /// file of a sized-from-the-file kind is pending and every folder above one is unfinished, a folder at the last AfterFolder
    /// included, which has every folder listed and its Stop header the listing's.
    /// </summary>
    [Fact]
    public void StopsInTheListingMarkWhatAPendingCheckCouldChange()
    {
        var clock = Stopwatch.StartNew();
        StopTree tree = rig.Tree;
        Reference reference = rig.Reference;
        var tally = new Tally();

        for (int n = 1; n <= reference.FoldersListed; n++)
        {
            tree.Restale();
            using var cts = new CancellationTokenSource();
            ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, Options(afterFolder: k =>
            {
                if (k == n)
                    cts.Cancel();
            }));
            Check($"Stop after folder {n} of {reference.FoldersListed}", r, reference, tree, tally, sizeStop: false, probes: 0);
        }

        (int calls, List<(string Folder, int LastCall)> ends) = Baseline(tree);
        int wide = ends.FindIndex(f => f.Folder == tree.Tree.PathOf("wide"));
        Assert.True(wide > 0, "the folder spanning several listing buffers was not met");
        var sampled = new SortedSet<int>(Enumerable.Range(1, 6)) { calls };
        for (int k = 7; k < calls; k += 7)
            sampled.Add(k);
        for (int k = ends[wide - 1].LastCall + 1; k <= ends[wide].LastCall; k++)
            sampled.Add(k);
        Assert.True(ends[wide].LastCall - ends[wide - 1].LastCall > 4, "the wide folder was listed in a single buffer: the cut-mid-listing case needs several");
        foreach (int k in sampled)
        {
            foreach (bool during in new[] { false, true })
            {
                tree.Restale();
                ScanResult r = Scans.RunCancellingAt(tree.Root, k, during, (root, ct) => Scanner.Scan(root, new ScanProgress(), ct, Options()));
                Check($"Stop {(during ? "during" : "before")} call {k} of {calls}", r, reference, tree, tally, sizeStop: false, probes: 0);
            }
        }

        // Cancel reached part-way through a commit: the listing returned cleanly, so the folder is committed whole and the scan stops after it.
        int entries = reference.Entries;
        foreach (int k in new[] { 1, 2, 3, 12, 150, 250, 400, 550, entries - 2, entries - 1 })
        {
            tree.Restale();
            using var cts = new CancellationTokenSource();
            int seen = 0;
            ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, Options(listedId: id =>
            {
                if (++seen == k)
                    cts.Cancel();
                return id;
            }));
            Check($"Cancel at entry {k} of a commit", r, reference, tree, tally, sizeStop: false, probes: 0);
            ScanStore s = r.Store;
            int folder = s.Parent(k);
            Assert.False(s.NotListed(folder), $"entry {k}'s folder {s.GetPath(folder)} was committed whole after Cancel, yet reads not listed");
            for (int c = 0; c < s.ChildCount(folder); c++)
            {
                int child = s.FirstChild(folder) + c;
                bool eligible = s.Kind(child) == EntryKind.File && (s.Flags(child) & (EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly)) == 0;
                Assert.True(s.SizeCheckPending(child) == eligible, $"{s.GetPath(child)}: pending {s.SizeCheckPending(child)}, expected {eligible}");
            }
            tally.MidCommit++;
        }

        output.WriteLine($"listing stops: {tally} in {clock.Elapsed.TotalSeconds:F1} s");
        Assert.True(tally.Stops >= 90, $"{tally.Stops} stops");
        Assert.True(tally.EmptyStores >= 2 && tally.MidCommit >= 8, tally.ToString());
        Assert.True(tally.ShrinkAboveWhileAtLeastHolds >= 1, $"no Stop had a stale-large first name listed above its true size: the teeth are missing. {tally}");
        Assert.True(tally.FinalCompared >= 200 && tally.UnfinishedCompared >= 400, tally.ToString());
        Assert.True(clock.Elapsed < Tally.Bound, $"{clock.Elapsed.TotalSeconds:F1} s: past the {Tally.Bound.TotalSeconds:F0} s bound set for one case command");
    }

    // ---- 2. A Stop in the size checks ----

    /// <summary>
    /// Stop in the size checks, ProbeBatch 8: (i) between batches, from the probe rule, which the scan thread runs while it
    /// builds the next batch, so the batch that comes back Cancelled never starts a probe; (ii) inside a probe call, before the
    /// real probe and after it has returned, so readings exist and the batch is discarded; (iii) with one probe blocked in a
    /// call nothing interrupts, so the scan returns by abandoning its worker. The cutoff must be the first entry of the
    /// cancelled batch every time: that many checks were applied and no more.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void StopsInTheSizeChecksMarkWhatAPendingCheckCouldChange(int workers)
    {
        var clock = Stopwatch.StartNew();
        StopTree tree = rig.Tree;
        Reference reference = rig.Reference;
        int due = reference.Probes;
        var tally = new Tally();

        // (i) Between batches. The rule is asked once of every entry to count the due files, then again as each batch is built;
        // the k-th due file of the second round is met on the scan thread, after the batch before it was applied.
        foreach (int k in new[] { 1, 2, 7, 8, 9, 16, 17, 24, 25, 100, 101, 320, 321, due - 1, due })
        {
            tree.Restale();
            var prober = new Prober();
            using var cts = new CancellationTokenSource();
            int asked = 0, met = 0;
            int counting = reference.Entries - 1;
            ProbeRule rule = (kind, flags, attributes, size, onDisk) =>
            {
                bool eligible = SizeProbe.IsEligible(kind, flags);
                if (++asked > counting && eligible && ++met == k)
                    cts.Cancel();
                return eligible;
            };
            ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, Options(probe: prober.AsDelegate, rule: rule, workers: workers));
            int ordinal = (k - 1) / Batch * Batch;
            Landing landing = Check($"Stop between batches at due file {k} ({workers} workers)", r, reference, tree, tally, sizeStop: true, probes: ordinal);
            Assert.Equal(ordinal, landing.Ordinal);
            Assert.True(prober.Calls == ordinal, $"due file {k}: {prober.Calls} probes were made, but the batch Cancel reached starts none (expected {ordinal})");
            tally.Queued++;
        }

        // (ii) Inside a probe call, before the real probe and once it has returned.
        foreach (int n in new[] { 1, 2, 7, 8, 9, 15, 16, 17, 50, 101, 250, 400, due - 1, due })
        {
            foreach (bool afterTheProbe in new[] { false, true })
            {
                tree.Restale();
                using var cts = new CancellationTokenSource();
                Action<int> cancel = call =>
                {
                    if (call == n)
                        cts.Cancel();
                };
                var prober = new Prober { Before = afterTheProbe ? null : cancel, After = afterTheProbe ? cancel : null };
                ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, Options(probe: prober.AsDelegate, workers: workers));
                int ordinal = (n - 1) / Batch * Batch;
                Landing landing = Check($"Stop {(afterTheProbe ? "after" : "before")} the real probe of call {n} ({workers} workers)", r, reference, tree, tally, sizeStop: true, probes: ordinal);
                Assert.Equal(ordinal, landing.Ordinal);
                Assert.True(prober.Calls > ordinal, $"call {n}: no probe was made in the batch Cancel reached");
                tally.MidBatch++;
            }
        }

        // (iii) One probe blocked where Cancel cannot reach it.
        foreach (int n in new[] { 1, 9, 41, due - 3 })
        {
            StuckStop(tree, reference, workers, n, tally);
            tally.Stuck++;
        }

        output.WriteLine($"size-check stops, {workers} workers: {tally} in {clock.Elapsed.TotalSeconds:F1} s");
        Assert.True(tally.Queued >= 10 && tally.MidBatch >= 20 && tally.Stuck >= 3, tally.ToString());
        Assert.True(tally.ShrinkAboveWhileAtLeastHolds >= 1, $"no Stop had a stale-large first name listed above its true size: the teeth are missing. {tally}");
        Assert.True(tally.LaterNameFolderFinalFirstPending >= 1, $"no Stop had a later name's folder final while its first name's check was pending. {tally}");
        Assert.True(tally.CorrectedFirstFolderFinal >= 1, $"no Stop had a corrected first name's folder final. {tally}");
        Assert.True(tally.RefusedCheckFolderFinal >= 1, $"no Stop had a refused check before the cutoff in a final folder. {tally}");
        Assert.True(tally.FinalCompared >= 500 && tally.UnfinishedCompared >= 500, tally.ToString());
        Assert.True(clock.Elapsed < Tally.Bound, $"{clock.Elapsed.TotalSeconds:F1} s: past the {Tally.Bound.TotalSeconds:F0} s bound set for one case command");
    }

    // ---- 3. Nothing changes once Stop has taken effect ----

    /// <summary>
    /// Stop has taken effect when Scan has returned. What the store holds then is what stays: with every probe worker stuck in a
    /// call nothing interrupts, the store is dumped at the return and again after the workers have been let go and have left, and
    /// nothing differs. And a folder whose listing returned cleanly after Cancel is committed before the return, whole: it
    /// reads listed, and its checked-kind files read pending.
    /// </summary>
    [Fact]
    public void NothingChangesOnceTheScanHasReturned()
    {
        StopTree tree = rig.Tree;
        Reference reference = rig.Reference;
        var tally = new Tally();

        foreach (int workers in new[] { 1, 4 })
            StuckStop(tree, reference, workers, n: 12, tally);

        // A listing that returned cleanly after Cancel: the commit is not cut short.
        tree.Restale();
        using var cts = new CancellationTokenSource();
        int seen = 0, target = reference.Entries - 3;
        ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, Options(listedId: id =>
        {
            if (++seen == target)
                cts.Cancel();
            return id;
        }));
        Check("Cancel inside a commit", r, reference, tree, tally, sizeStop: false, probes: 0);
        ScanStore s = r.Store;
        int folder = s.Parent(target);
        Assert.False(s.NotListed(folder), $"{s.GetPath(folder)} was committed whole after Cancel, yet reads not listed");
        Assert.Equal(s.ChildCount(folder) > 0, s.FirstChild(folder) >= 0);
        for (int c = 0; c < s.ChildCount(folder); c++)
        {
            int child = s.FirstChild(folder) + c;
            bool eligible = s.Kind(child) == EntryKind.File && (s.Flags(child) & (EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly)) == 0;
            Assert.True(s.SizeCheckPending(child) == eligible, $"{s.GetPath(child)}: pending {s.SizeCheckPending(child)}, expected {eligible}");
        }
    }

    // ---- 4. A scan that runs to the end marks nothing ----

    /// <summary>
    /// The tree run to the end; the sweep tree with five listed files deleted before their check (VanishedBeforeProbe 5), which
    /// leave neither SizeProbed nor ProbeFailed and so are the trap for a rule keyed on flags; and a tree of folders alone, where
    /// no file is due and the sizing pass returns Completed without running a batch.
    /// </summary>
    [Fact]
    public void AScanThatRunsToTheEndMarksNothing()
    {
        StopTree tree = rig.Tree;
        tree.Restale();
        ScanResult whole = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None, Options(probe: new Prober().AsDelegate, workers: 4));
        Assert.Equal(ScanStatus.Completed, whole.Status);
        Assert.True(whole.Probes > 600);
        CompletedMarksNothing(whole);

        using TempTree sweep = NotListedTests.SweepTree();
        var progress = new ScanProgress();
        bool deleted = false;
        string big = sweep.PathOf("big");
        ScanResult vanished = Scanner.Scan(sweep.Root, progress, CancellationToken.None, new ScanOptions
        {
            ContainerLayerStorePath = sweep.PathOf(NotListedTests.Store),
            AfterFolder = _ =>
            {
                if (deleted || progress.CurrentFolder != big)
                    return;
                deleted = true;
                for (int i = 0; i < 5; i++)
                    File.Delete(sweep.PathOf($@"big\{i:D4}{new string('n', 92)}.txt"));
            },
        });
        Assert.True(deleted);
        Assert.Equal(ScanStatus.Completed, vanished.Status);
        Assert.Equal(5, vanished.VanishedBeforeProbe);
        CompletedMarksNothing(vanished);

        using var folders = new TempTree();
        folders.Folder("a");
        folders.Folder(@"b\c");
        folders.Folder(@"b\d\e");
        ScanResult empty = Scanner.Scan(folders.Root, new ScanProgress(), CancellationToken.None, Options());
        Assert.Equal(ScanStatus.Completed, empty.Status);
        Assert.Equal(0, empty.Probes);
        CompletedMarksNothing(empty);
    }

    // ---- 5. The read on every shape a scan registers ----

    /// <summary>
    /// Random trees registered the way the scan registers them (one stack, subfolders pushed last-first, each folder's children
    /// one block and one SetChildren), cut at random folders, with cloud-only, repeated, no-ID and link entries, empty and not
    /// listed last subfolders, and one folder whose block of more than 65,536 children straddles a page. Each is read at the
    /// watermark 0, at the entry count and at random points, and every answer must be a brute-force walk's.
    /// </summary>
    [Fact]
    public void TheReadAnswersAsAParentWalkOnEveryScannerShapedStore()
    {
        var empty = new ScanStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Unfinished(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.AtLeast(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.SizeCheckPending(0));
        empty.EndSizeChecks(0); // a scan stopped before it had a root
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.EndSizeChecks(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.EndSizeChecks(-1));
        empty.AppendRoot(@"C:\", 0x10);
        empty.EndSizeChecks(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.EndSizeChecks(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.EndSizeChecks(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Unfinished(1));

        int stores = 0, straddling = 0, entries = 0;
        for (int seed = 1; seed <= 140; seed++)
        {
            var rng = new Random(seed);
            ScanStore s = Synthetic(rng, foldersToList: rng.Next(1, 60), bigBlock: false);
            CheckEveryWatermark(s, rng);
            stores++;
            entries += s.Count;
        }
        foreach (int listed in new[] { 3, 9, 40 })
        {
            var rng = new Random(900 + listed);
            ScanStore s = Synthetic(rng, foldersToList: listed, bigBlock: true);
            straddling++;
            Assert.True(s.ChildCount(s.FirstChild(0)) >= 70_000, "the big block was not built");
            Assert.True((s.FirstChild(s.FirstChild(0)) >> ScanStore.PageShift) != ((s.FirstChild(s.FirstChild(0)) + s.ChildCount(s.FirstChild(0)) - 1) >> ScanStore.PageShift),
                "the block does not straddle a page");
            CheckEveryWatermark(s, rng);
            entries += s.Count;
        }
        output.WriteLine($"{stores} random stores and {straddling} with a block over a page: {entries:N0} entries read");
        Assert.True(entries > 40_000);
    }

    // ---- The tree ----

    /// <summary>The temp tree: built once for the class, a folder is final or unfinished in a stopped store by what it holds.</summary>
    public sealed class Rig : IDisposable
    {
        private readonly Lazy<StopTree> _tree = new(() => new StopTree());
        private Reference? _reference;

        internal StopTree Tree => _tree.Value;

        internal Reference Reference => _reference ??= Reference.Take(Tree);

        public void Dispose()
        {
            if (!_tree.IsValueCreated)
                return;
            Scans.WaitForAbortedCloses();
            _tree.Value.Dispose();
        }
    }

    /// <summary>
    /// A ragged tree under one root, in the order NTFS lists it. Eight hardlinks of a file with two names each, whose first name (the
    /// one the scan meets, and charges) is listed stale in a direction: a grow group lists 4 KiB and is 1 MiB, a shrink group lists
    /// 1 MiB and is 4 KiB. The first name sits in an ancestor of the later one, beside it, in an earlier branch, deep in a chain;
    /// one group lists its later name stale instead, which charges nothing. Two files are refused by the probe seam, a folder
    /// spans several listing buffers, the last subfolder of one is empty and of another denied, and a junction is never entered.
    /// </summary>
    internal sealed class StopTree : IDisposable
    {
        public const long Small = 4096, Large = TempTree.MiB;

        internal sealed record Group(string Id, bool Grows, string First, string Later, bool LaterIsStale)
        {
            public string Stale => LaterIsStale ? Later : First;

            public string Fresh => LaterIsStale ? First : Later;

            public long Listed => Grows ? Small : Large;

            public long True => Grows ? Large : Small;
        }

        public StopTree()
        {
            Tree = new TempTree();
            Tree.Folder("a-empty");
            Tree.Folder(@"chain\c1\c2\c3\c4\c5\c6\c7\c8");
            Tree.File(@"chain\c1\one.txt", 11);
            Tree.File(@"chain\c1\c2\c3\c4\four.txt", 44);
            Tree.File(@"chain\c1\c2\c3\c4\c5\c6\c7\c8\eight.txt", 88);

            var groups = new List<Group>();
            Group Add(string id, bool grows, string first, string later, bool laterIsStale = false)
            {
                Tree.Folder(Path.GetDirectoryName(first)!);
                Tree.Folder(Path.GetDirectoryName(later)!);
                string written = Tree.File(first, Large);
                TempTree.Hardlink(Tree.PathOf(later), written);
                var group = new Group(id, grows, first, later, laterIsStale);
                groups.Add(group);
                return group;
            }
            Add("A", grows: true, @"ga\a.bin", @"ga\sub\a2.bin"); // the first name in an ancestor of the later one
            Add("B", grows: false, @"gb\b.bin", @"gb\sub\b2.bin");
            Add("C", grows: true, @"gc\c.bin", @"gc\c2.bin"); // beside it
            Add("D", grows: false, @"gd\d.bin", @"gd\d2.bin");
            Add("E", grows: true, @"ge1\e.bin", @"ge2\sub\e2.bin"); // in an earlier branch than the later name
            Add("F", grows: false, @"gf1\f.bin", @"gf2\sub\f2.bin");
            Add("G", grows: true, @"chain\c1\c2\c3\c4\c5\c6\c7\c8\g.bin", @"gg\g2.bin"); // deep, the later name in a later branch
            Add("H", grows: false, @"gh\h.bin", @"gh\sub\h2.bin", laterIsStale: true); // the later name is the stale one
            Groups = groups;

            Tree.Folder("pf1");
            Tree.File(@"pf1\x.bin", 10);
            Tree.Folder("pf2");
            Tree.File(@"pf2\y.bin", 20);
            Tree.Folder("plain");
            for (int i = 0; i < 4; i++)
                Tree.File($@"plain\p{i}.txt", 300 + 1700 * i);
            Tree.Folder(@"tail\z-empty");
            Tree.Folder(@"tail\a-full");
            Tree.File(@"tail\a-full\one.txt", 5);
            Tree.Folder(@"tail2\a-full");
            Tree.File(@"tail2\a-full\x.txt", 6);
            string denied = Tree.Folder(@"tail2\z-denied");
            Tree.File(@"tail2\z-denied\hidden.txt", 1000);
            Tree.Folder("wide");
            for (int i = 0; i < 600; i++)
                Tree.File($@"wide\{i:D4}{new string('w', 92)}.txt", 1 + i % 7);
            Tree.Folder("z-last");
            Tree.File(@"z-last\last.txt", 33);
            Tree.Junction("j", Tree.Outside("target"));
            Tree.Deny(denied, FileListDirectory);
            Restale();
        }

        public TempTree Tree { get; }

        public string Root => Tree.Root;

        public IReadOnlyList<Group> Groups { get; }

        /// <summary>
        /// Makes every group stale again, before every scan the references included, since a size probe of a name refreshes its
        /// directory entry: the stale name has the length it lists set through it, then the file is given its true length through the
        /// other name. No link is made again. A group whose stale name does not list stale makes the run void, and fails it.
        /// </summary>
        public void Restale()
        {
            foreach (Group g in Groups)
            {
                SetLength(Tree.PathOf(g.Stale), g.Listed);
                SetLength(Tree.PathOf(g.Fresh), g.True);
                // (making the link again here, as the CancellationTests recipe does, costs a cmd.exe per link per scan: Tally.Bound)
            }
            foreach (Group g in Groups)
            {
                RawEntry listed = Scans.Listing(Path.GetDirectoryName(Tree.PathOf(g.Stale))!)[Path.GetFileName(g.Stale)];
                Assert.True(listed.EndOfFile == g.Listed,
                    $"void: group {g.Id}'s stale name {g.Stale} lists {listed.EndOfFile} bytes, not the {g.Listed} it was made stale at");
            }
        }

        private static void SetLength(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.SetLength(length);
        }

        public void Dispose() => Tree.Dispose();
    }

    /// <summary>
    /// The probe each scan here runs: the real one, counting its calls, refusing two files with error 5 (a file-level deny cannot
    /// refuse a probe's open, so the seam does), and calling the hooks before the real probe and once it has returned.
    /// </summary>
    internal sealed class Prober
    {
        private int _calls, _active;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Probe calls that have begun and not yet left.</summary>
        public int Active => Volatile.Read(ref _active);

        public Action<int>? Before { get; init; }

        public Action<int>? After { get; init; }

        public ProbeFile AsDelegate => Probe;

        private int Probe(SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize)
        {
            int n = Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _active);
            try
            {
                Before?.Invoke(n);
                int error;
                if (path.EndsWith(@"\pf1\x.bin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\pf2\y.bin", StringComparison.OrdinalIgnoreCase))
                {
                    endOfFile = allocationSize = 0;
                    error = 5; // ERROR_ACCESS_DENIED: the listing's figures stay and the file is ProbeFailed
                }
                else
                {
                    error = SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
                }
                After?.Invoke(n);
                return error;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    /// <summary>What two scans of the unchanged tree that ran to the end agree on, folder by path.</summary>
    internal sealed class Reference
    {
        public required Dictionary<string, Figures> Folders { get; init; }

        public required Dictionary<string, EntryFlags> Flags { get; init; }

        public int Entries { get; init; }

        public int FoldersListed { get; init; }

        public int Probes { get; init; }

        public static Reference Take(StopTree tree)
        {
            Reference? first = null;
            for (int pass = 0; pass < 2; pass++)
            {
                tree.Restale();
                int folders = 0;
                ScanResult r = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None,
                    Options(afterFolder: _ => folders++, probe: new Prober().AsDelegate, workers: 4));
                Assert.Equal(ScanStatus.Completed, r.Status);
                Assert.Equal(2, r.ProbeFailures);
                int stale = tree.Groups.Count(g => !g.LaterIsStale);
                Assert.True(r.ProbeCorrections >= stale, $"void: {r.ProbeCorrections} corrections, and {stale} first names were made stale");
                CompletedMarksNothing(r);

                ScanStore s = r.Store;
                var current = new Reference
                {
                    Folders = [],
                    Flags = [],
                    Entries = s.Count,
                    FoldersListed = folders,
                    Probes = r.Probes,
                };
                for (int e = 0; e < s.Count; e++)
                {
                    string path = s.GetPath(e);
                    current.Flags[path] = s.Flags(e);
                    if (s.Kind(e) == EntryKind.Folder)
                        current.Folders[path] = new Figures(s.RolledSize(e), s.RolledOnDisk(e), s.FilesBelow(e));
                }
                if (first is null)
                {
                    first = current;
                    continue;
                }
                Assert.Equal(first.Entries, current.Entries);
                Assert.Equal(first.Probes, current.Probes);
                foreach ((string path, Figures figures) in first.Folders)
                    Assert.True(current.Folders[path] == figures, $"{path}: two scans of the unchanged tree gave {figures} and {current.Folders[path]}");
            }
            return first!;
        }
    }

    /// <summary>What a set of Stops met, so a Fact can say it was not vacuous.</summary>
    internal sealed class Tally
    {
        /// <summary>
        /// What one Fact may take (Q-222): the engine stops a case command at 120 s, build included, and a command selects up to three
        /// rows (the listing Fact and the sizing Theory's two). Each takes 1.6 to 2.6 s here; a link made again before every scan
        /// (a cmd.exe per link, eight groups, some 250 scans) takes 24 s. Asked at every Stop, so a Fact that has become slow fails
        /// at the bound and not minutes later.
        /// </summary>
        public static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

        public readonly Stopwatch Clock = Stopwatch.StartNew();

        public void Within(string at) => Assert.True(Clock.Elapsed < Bound,
            $"{at}: {Clock.Elapsed.TotalSeconds:F1} s into the Fact, past the {Bound.TotalSeconds:F0} s bound set for one case command ({Stops} stops so far)");

        public int Stops, EmptyStores, MidCommit, Queued, MidBatch, Stuck;
        public int FinalCompared, UnfinishedCompared;
        public int ShrinkAboveWhileAtLeastHolds, LaterNameFolderFinalFirstPending, CorrectedFirstFolderFinal, RefusedCheckFolderFinal, FirstNamePending;

        public override string ToString() =>
            $"{Stops} stops ({EmptyStores} empty stores, {MidCommit} mid-commit, {Queued} queued, {MidBatch} mid-batch, {Stuck} stuck); " +
            $"{FinalCompared} final and {UnfinishedCompared} unfinished folders compared; shrink-above {ShrinkAboveWhileAtLeastHolds}, " +
            $"later-name-final {LaterNameFolderFinalFirstPending}, corrected-final {CorrectedFirstFolderFinal}, refused-final {RefusedCheckFolderFinal}, first-name-pending {FirstNamePending}";
    }

    /// <summary>Where a Stop in the size checks left the cutoff: the first pending entry and how many checks were applied before it.</summary>
    internal readonly record struct Landing(int FirstPending, int Ordinal);

    /// <summary>
    /// The answers a brute-force walk gives for every entry: each file's Parent chain is climbed and credited to each folder above
    /// it. A file is pending when it is of a sized-from-the-file kind and (by flags, for a real scan) neither SizeProbed nor
    /// ProbeFailed, or (by watermark, for a synthetic store) at or after the watermark. It reads no answer of the store's under test.
    /// </summary>
    internal sealed class Oracle
    {
        public required bool[] Pending { get; init; }

        public required bool[] Unfinished { get; init; }

        public required Figures[] AtLeast { get; init; }

        public static Oracle ByFlags(ScanStore s) => Of(s, e => (s.Flags(e) & (EntryFlags.SizeProbed | EntryFlags.ProbeFailed)) == 0);

        public static Oracle ByWatermark(ScanStore s, int watermark) => Of(s, e => e >= watermark);

        private static Oracle Of(ScanStore s, Func<int, bool> notChecked)
        {
            int n = s.Count;
            var pending = new bool[n];
            var unfinished = new bool[n];
            var size = new long[n];
            var onDisk = new long[n];
            var files = new int[n];
            for (int e = 0; e < n; e++)
            {
                EntryKind kind = s.Kind(e);
                EntryFlags flags = s.Flags(e);
                bool file = kind == EntryKind.File;
                pending[e] = file && (flags & (EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly)) == 0 && notChecked(e);
                long certainSize = file && !pending[e] ? s.ChargedSize(e) : 0, certainOnDisk = file && !pending[e] ? s.ChargedOnDisk(e) : 0;
                bool unlisted = kind == EntryKind.Folder && s.NotListed(e);
                for (int a = s.Parent(e); a >= 0; a = s.Parent(a))
                {
                    size[a] += certainSize;
                    onDisk[a] += certainOnDisk;
                    files[a] += file ? 1 : 0;
                    unfinished[a] |= pending[e] || unlisted;
                }
                unfinished[e] |= pending[e] || unlisted;
                if (file)
                {
                    size[e] = certainSize;
                    onDisk[e] = certainOnDisk;
                    files[e] = 1;
                }
            }
            var atLeast = new Figures[n];
            for (int e = 0; e < n; e++)
                atLeast[e] = new Figures(size[e], onDisk[e], files[e]);
            return new Oracle { Pending = pending, Unfinished = unfinished, AtLeast = atLeast };
        }
    }

    // ---- Checks ----

    /// <summary>
    /// One Stop's store against the oracle, entry by entry, and its folders against the scans that ran to the end: every folder not
    /// unfinished holds the reference's figures, and every unfinished one's at-least figures are no greater; each group's charged
    /// name is the reference's; an unchecked first name makes its folder and every folder above it unfinished.
    /// </summary>
    private static Landing Check(string at, ScanResult r, Reference reference, StopTree tree, Tally tally, bool sizeStop, int? probes)
    {
        ScanStore s = r.Store;
        tally.Stops++;
        tally.Within(at);
        Assert.True(r.Status == ScanStatus.Cancelled, $"{at}: the Stop did not land, the scan {r.Status}");
        if (probes is { } expected)
            Assert.True(r.Probes == expected, $"{at}: {r.Probes} checks were applied, expected {expected}");
        bool flagged = r.TopLevel?.StoppedInSizeChecks ?? false;
        Assert.True(flagged == sizeStop, $"{at}: StoppedInSizeChecks is {flagged}, expected {sizeStop}");
        if (r.TopLevel is { } top)
            Assert.True(top.Final && !top.Completed, $"{at}: the last snapshot is not a stopped one");

        if (s.Count == 0)
        {
            tally.EmptyStores++;
            Assert.Throws<ArgumentOutOfRangeException>(() => s.Unfinished(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => s.AtLeast(0));
            return new Landing(-1, 0);
        }
        Scans.AssertChargedBytesAreTheTotals(r);

        Oracle o = Oracle.ByFlags(s);
        int firstPending = -1, ordinal = 0;
        for (int e = 0; e < s.Count; e++)
        {
            if (s.SizeCheckPending(e) != o.Pending[e])
                throw new XunitException($"{at}: {s.GetPath(e)} reads SizeCheckPending {s.SizeCheckPending(e)}, the flags say {o.Pending[e]}");
            if (s.Unfinished(e) != o.Unfinished[e])
                throw new XunitException($"{at}: {s.GetPath(e)} reads Unfinished {s.Unfinished(e)}, a walk of its subtree says {o.Unfinished[e]}");
            if (s.AtLeast(e) != o.AtLeast[e])
                throw new XunitException($"{at}: {s.GetPath(e)} holds at least {s.AtLeast(e)}, a walk of its subtree says {o.AtLeast[e]}");
            if (s.Kind(e) == EntryKind.File && (s.Flags(e) & (EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly)) == 0 && firstPending < 0)
            {
                if (o.Pending[e])
                    firstPending = e;
                else
                    ordinal++;
            }
        }
        if (sizeStop)
        {
            Assert.True(firstPending > 0, $"{at}: a Stop in the size checks left nothing pending");
            Assert.True(ordinal % Batch == 0, $"{at}: {ordinal} checks were applied before the cutoff, which is not a whole number of batches of {Batch}");
        }

        // Against the scans that ran to the end: the stopped store holds a prefix of their entries, each under the same path.
        var byPath = Scans.ByPath(s);
        foreach ((string path, int e) in byPath)
        {
            if (!reference.Flags.TryGetValue(path, out EntryFlags expectedFlags))
                throw new XunitException($"{at}: {path} is in the stopped store and not in the scans that ran to the end");
            if ((s.Flags(e) & EntryFlags.HardlinkDuplicate) != (expectedFlags & EntryFlags.HardlinkDuplicate))
                throw new XunitException($"{at}: {path} is charged differently from the scans that ran to the end (flags {s.Flags(e)}, expected {expectedFlags})");
            if (s.Kind(e) != EntryKind.Folder)
                continue;

            Figures figures = reference.Folders[path];
            Figures atLeast = s.AtLeast(e);
            Figures rolled = new(s.RolledSize(e), s.RolledOnDisk(e), s.FilesBelow(e));
            if (!s.Unfinished(e))
            {
                if (rolled != figures || atLeast != figures)
                    throw new XunitException($"{at}: {path} is final, with rolled {rolled} and at least {atLeast}, and the scans that ran to the end give {figures}");
                tally.FinalCompared++;
                continue;
            }
            tally.UnfinishedCompared++;
            if (atLeast.Size > figures.Size || atLeast.OnDisk > figures.OnDisk || atLeast.Files > figures.Files)
                throw new XunitException($"{at}: {path} is unfinished and holds at least {atLeast}, above the {figures} the scans that ran to the end give");
            if (rolled.Size > figures.Size || rolled.OnDisk > figures.OnDisk)
                tally.ShrinkAboveWhileAtLeastHolds++;
        }

        // The hardlinks: an unchecked first name makes its folder and every ancestor unfinished; the later name's folder may be final.
        bool anyFirstPending = false;
        foreach (StopTree.Group g in tree.Groups)
        {
            if (!byPath.TryGetValue(tree.Tree.PathOf(g.First), out int first))
                continue;
            if (g.LaterIsStale)
                Assert.False(s.SizeCheckPending(byPath.GetValueOrDefault(tree.Tree.PathOf(g.Later), 0)), $"{at}: {g.Id}'s later name is pending, and it charges nothing");
            if (s.SizeCheckPending(first))
            {
                anyFirstPending = true;
                for (int a = first; a >= 0; a = s.Parent(a))
                    Assert.True(s.Unfinished(a), $"{at}: {s.GetPath(a)} is at or above {g.Id}'s first name, whose check is pending, and is not unfinished");
                if (byPath.TryGetValue(tree.Tree.PathOf(g.Later), out int later) && !s.Unfinished(s.Parent(later)))
                    tally.LaterNameFolderFinalFirstPending++;
            }
            else if (!g.LaterIsStale && (s.Flags(first) & EntryFlags.ProbeCorrected) != 0 && !s.Unfinished(s.Parent(first)))
            {
                tally.CorrectedFirstFolderFinal++;
            }
        }
        if (anyFirstPending)
            tally.FirstNamePending++;
        foreach (string refused in new[] { @"pf1\x.bin", @"pf2\y.bin" })
        {
            if (byPath.TryGetValue(tree.Tree.PathOf(refused), out int e) && (s.Flags(e) & EntryFlags.ProbeFailed) != 0 && !s.Unfinished(s.Parent(e)))
                tally.RefusedCheckFolderFinal++;
        }
        return new Landing(firstPending, ordinal);
    }

    /// <summary>A scan that ran to the end: nothing is pending or unfinished, and every at-least figure is the rolled one.</summary>
    private static void CompletedMarksNothing(ScanResult r)
    {
        ScanStore s = r.Store;
        Assert.False(r.TopLevel?.StoppedInSizeChecks ?? false);
        for (int e = 0; e < s.Count; e++)
        {
            string path = s.GetPath(e);
            Assert.False(s.SizeCheckPending(e), $"{path} reads pending after a scan that ran to the end");
            Assert.False(s.Unfinished(e), $"{path} reads unfinished after a scan that ran to the end");
            Figures expected = s.Kind(e) switch
            {
                EntryKind.Folder => new Figures(s.RolledSize(e), s.RolledOnDisk(e), s.FilesBelow(e)),
                EntryKind.File => new Figures(s.ChargedSize(e), s.ChargedOnDisk(e), 1),
                _ => default,
            };
            Assert.True(s.AtLeast(e) == expected, $"{path} holds at least {s.AtLeast(e)}, not {expected}");
        }
    }

    /// <summary>One scan with every probe worker's call blocked, stopped by its caller, and the store before and after the workers go.</summary>
    private void StuckStop(StopTree tree, Reference reference, int workers, int n, Tally tally)
    {
        tree.Restale();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var prober = new Prober
        {
            Before = call =>
            {
                if (call != n)
                    return;
                cts.CancelAfter(TimeSpan.FromMilliseconds(30));
                release.Wait(); // no cancel reaches this
            },
        };
        var clock = Stopwatch.StartNew();
        ScanResult r;
        try
        {
            r = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, Options(probe: prober.AsDelegate, workers: workers, abandon: TimeSpan.FromMilliseconds(200)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the scan took {clock.Elapsed.TotalSeconds:F1} s to return, with a worker stuck");
        }
        catch
        {
            release.Set();
            throw;
        }

        string[] atReturn;
        try
        {
            int ordinal = (n - 1) / Batch * Batch;
            Landing landing = Check($"Stop with the probe of call {n} stuck ({workers} workers)", r, reference, tree, tally, sizeStop: true, probes: ordinal);
            Assert.Equal(ordinal, landing.Ordinal);
            atReturn = Dump(r.Store);
        }
        finally
        {
            release.Set();
        }
        Assert.True(SpinWait.SpinUntil(() => prober.Active == 0, TimeSpan.FromSeconds(10)), "the stuck worker never left");
        Thread.Sleep(50);
        string[] after = Dump(r.Store);
        int differences = atReturn.Zip(after).Count(p => p.First != p.Second) + Math.Abs(atReturn.Length - after.Length);
        Assert.True(differences == 0, $"{differences} entries differ between the store at the return and after the stuck worker left, e.g. {atReturn.Zip(after).FirstOrDefault(p => p.First != p.Second)}");
        Scans.WaitForAbortedCloses();
    }

    /// <summary>Every figure and mark the store holds for every entry, one line each, to be compared with another dump.</summary>
    private static string[] Dump(ScanStore s)
    {
        var lines = new string[s.Count];
        for (int e = 0; e < s.Count; e++)
        {
            lines[e] = $"{e} {s.Kind(e)} {s.Flags(e)} notListed={s.NotListed(e)} below={s.NotListedAtOrBelow(e)} size={s.Size(e)} onDisk={s.OnDisk(e)} " +
                $"rolled=({s.RolledSize(e)},{s.RolledOnDisk(e)},{s.FilesBelow(e)}) children={s.FirstChild(e)}+{s.ChildCount(e)} " +
                $"pending={s.SizeCheckPending(e)} unfinished={s.Unfinished(e)} atLeast={s.AtLeast(e)}";
        }
        return lines;
    }

    /// <summary>One scan of the tree to the end, counting the scan thread's guarded calls, with the last call of each folder it committed.</summary>
    private static (int Calls, List<(string Folder, int LastCall)> Ends) Baseline(StopTree tree)
    {
        tree.Restale();
        var progress = new ScanProgress();
        var ends = new List<(string, int)>();
        SynchronousIoCancel? guard = null;
        ScanResult r = Scans.WithGuard(g => guard ??= g, () => Scanner.Scan(tree.Root, progress, CancellationToken.None,
            Options(afterFolder: _ => ends.Add((progress.CurrentFolder!, guard!.Calls)), probe: new Prober().AsDelegate)));
        Assert.Equal(ScanStatus.Completed, r.Status);
        return (ends[^1].Item2, ends);
    }

    // ---- Synthetic stores ----

    /// <summary>
    /// A store registered as a scan registers one: an explicit stack, each folder's children appended as one block and registered by one
    /// SetChildren, subfolders pushed last-first, and the walk cut after <paramref name="foldersToList"/> listings. With
    /// <paramref name="bigBlock"/>, the first folder listed under the root holds 70,000 children, which straddle a page, with folders at
    /// both sides of the boundary and at its end.
    /// </summary>
    private static ScanStore Synthetic(Random rng, int foldersToList, bool bigBlock)
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        var pending = new Stack<int>();
        var ids = new List<ulong>();
        ulong nextId = 100;
        int listed = 0;
        bool bigDone = !bigBlock;

        void Append(int parent, int slot, bool forceFolder, bool forceFile)
        {
            double p = rng.NextDouble();
            EntryKind kind = forceFolder || (!forceFile && p < 0.30) ? EntryKind.Folder : !forceFile && p < 0.38 ? EntryKind.Link : EntryKind.File;
            EntryFlags flags = EntryFlags.None;
            UInt128 id = UInt128.Zero;
            long size = kind == EntryKind.File ? rng.Next(0, 6000) : 0;
            if (kind == EntryKind.File)
            {
                double q = rng.NextDouble();
                if (q < 0.10)
                {
                    flags = EntryFlags.CloudOnly;
                    id = new UInt128(0, nextId++);
                }
                else if (q < 0.25 && ids.Count > 0)
                {
                    id = new UInt128(0, ids[rng.Next(ids.Count)]); // another name of a file already met
                }
                else if (q < 0.32)
                {
                    id = UInt128.Zero; // no file ID: never a later name
                }
                else
                {
                    ids.Add(nextId);
                    id = new UInt128(0, nextId++);
                }
            }
            store.Append(parent, $"n{store.Count}-{slot}", kind, flags, kind == EntryKind.Folder ? 0x10u : 0x20u, size, (size + 4095) / 4096 * 4096, id);
        }

        void List(int folder, int children, Func<int, (bool Folder, bool File)>? shape)
        {
            int first = store.Count;
            for (int k = 0; k < children; k++)
            {
                (bool asFolder, bool asFile) = shape?.Invoke(k) ?? default;
                Append(folder, k, asFolder, asFile);
            }
            store.SetChildren(folder, first, children);
            for (int k = children - 1; k >= 0; k--)
            {
                if (store.Kind(first + k) == EntryKind.Folder)
                    pending.Push(first + k);
            }
            listed++;
        }

        List(0, bigBlock ? 3 : rng.Next(1, 8), bigBlock ? _ => (true, false) : null);
        while (pending.Count > 0 && listed < foldersToList)
        {
            int folder = pending.Pop();
            if (!bigDone)
            {
                bigDone = true;
                List(folder, 70_000, k => (k % 5000 == 4999 || (k >= 65_530 && k < 65_546), k % 5000 != 4999 && !(k >= 65_530 && k < 65_546)));
                continue;
            }
            List(folder, rng.Next(0, 7), null);
        }
        return store;
    }

    /// <summary>Reads every entry at watermarks that cover the ends and random points, the corrections a scan would have applied made first.</summary>
    private static void CheckEveryWatermark(ScanStore s, Random rng)
    {
        var marks = new SortedSet<int> { 0, s.Count };
        for (int i = 0; i < 3; i++)
            marks.Add(rng.Next(0, s.Count + 1));
        int firstFile = Enumerable.Range(1, s.Count - 1).FirstOrDefault(e => s.Kind(e) == EntryKind.File);
        if (firstFile > 0)
        {
            marks.Add(firstFile);
            marks.Add(firstFile + 1);
        }
        if (s.Count > 65_540)
            marks.Add(65_536);

        int applied = 1;
        foreach (int w in marks)
        {
            for (; applied < w; applied++)
            {
                if (EntryRules.ChecksOwnSize(s.Kind(applied), s.Flags(applied)))
                    s.CorrectSizes(applied, rng.Next(0, 9000), rng.Next(0, 3) * 4096);
            }
            s.EndSizeChecks(w);
            Oracle o = Oracle.ByWatermark(s, w);
            for (int e = 0; e < s.Count; e++)
            {
                if (s.SizeCheckPending(e) != o.Pending[e])
                    throw new XunitException($"watermark {w}: entry {e} ({s.Kind(e)}) reads SizeCheckPending {s.SizeCheckPending(e)}, expected {o.Pending[e]}");
                if (s.Unfinished(e) != o.Unfinished[e])
                    throw new XunitException($"watermark {w} of {s.Count}: entry {e} ({s.Kind(e)}, {s.ChildCount(e)} children) reads Unfinished {s.Unfinished(e)}, a walk says {o.Unfinished[e]}");
                if (s.AtLeast(e) != o.AtLeast[e])
                    throw new XunitException($"watermark {w} of {s.Count}: entry {e} ({s.Kind(e)}) holds at least {s.AtLeast(e)}, a walk says {o.AtLeast[e]}");
            }
        }
    }
}
