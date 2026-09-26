using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// Folder totals, hardlinks counted once, leaves that add nothing, and children read largest first, on
/// synthetic append streams with no disk (SL-042). Every store is built through the append path only, and
/// no test sets HardlinkDuplicate by hand: the store is what these tests exist to prove sets it.
/// </summary>
public class ScanStoreRollupTests
{
    private const long K = 4096;

    // ---- Folder totals (C1) ----

    [Fact]
    public void AChainTenThousandFoldersDeepRollsEveryFileUpToEveryAncestor()
    {
        const int depth = 10_000;
        var stream = new List<Fed> { Root() };
        var chain = new List<int> { 0 }; // chain[d]: the folder at depth d
        for (int level = 0; level < depth; level++)
        {
            int parent = chain[^1];
            chain.Add(stream.Count);
            stream.Add(FolderAt(parent));
            stream.Add(FileAt(parent, level + 1, K * (level + 1), Id(level + 1)));
        }
        stream.Add(FileAt(chain[^1], depth + 1, K * (depth + 1), Id(depth + 1)));

        ScanStore store = RollupOracle.Feed(stream);

        RollupOracle.AssertMatches(store, RollupOracle.Expected(stream));
        RollupOracle.CheckStructure(store);
        // Hand-computed: the folder at depth d holds the files at depths d+1 to 10,001, sized d+1 to 10,001.
        for (int d = 0; d <= depth; d++)
        {
            long files = depth + 1 - d;
            long bytes = Triangle(depth + 1) - Triangle(d);
            Assert.Equal(new Rolled(bytes, K * bytes, files), Rolled.Of(store, chain[d]));
        }
    }

    [Fact]
    public void AFolderWithAHundredThousandChildrenRollsUpAcrossPageBoundaries()
    {
        const int children = 100_000, subfolders = 1_000, filesPerSubfolder = 100;
        var stream = new List<Fed> { Root(), FolderAt(0) };
        const int wide = 1;
        var subs = new List<int>();
        for (int k = 0; k < children; k++)
        {
            if (k % (children / subfolders) == 37)
            {
                subs.Add(stream.Count);
                stream.Add(FolderAt(wide));
            }
            else
            {
                stream.Add(FileAt(wide, 1_000 + k % 977, K * (1 + k % 13), Id(stream.Count)));
            }
        }
        foreach (int sub in subs)
        {
            for (int f = 0; f < filesPerSubfolder; f++)
                stream.Add(FileAt(sub, 10 + f, K, Id(stream.Count)));
        }
        Assert.True(stream.Count > 3 * ScanStore.PageSize, $"{stream.Count} entries should cross three page boundaries");

        ScanStore store = RollupOracle.Feed(stream);

        RollupOracle.AssertMatches(store, RollupOracle.Expected(stream));
        RollupOracle.CheckStructure(store);
        Assert.Equal(children, store.ChildCount(wide));
        Assert.Equal(children - subfolders + subfolders * filesPerSubfolder, store.FilesBelow(wide));
        Assert.Equal(Rolled.Of(store, wide), Rolled.Of(store, 0));
        foreach (int sub in subs)
            Assert.Equal(new Rolled(Triangle(filesPerSubfolder + 9) - Triangle(9), K * filesPerSubfolder, filesPerSubfolder), Rolled.Of(store, sub));
    }

    /// <summary>
    /// A folder with nothing below it never receives a posting and reads 0/0/0. The small tree is checked by
    /// both oracles after every single append, which is every state a cancelled scan can leave behind.
    /// </summary>
    [Theory]
    [InlineData("an empty root")]
    [InlineData("empty and nested empty folders")]
    [InlineData("a small mixed tree, checked after every append")]
    [InlineData("a seeded 50,000-entry mixed tree")]
    public void EmptyAndMixedTreesMatchTheOracleAtEveryCut(string shape)
    {
        List<Fed> stream;
        switch (shape)
        {
            case "an empty root":
                stream = [Root()];
                break;
            case "empty and nested empty folders":
                // root: a, b, c; b: b1, b2; b1: b1a; c: an empty file
                stream = [Root(), FolderAt(0), FolderAt(0), FolderAt(0), FolderAt(2), FolderAt(2), FileAt(3, 0, 0, Id(1)), FolderAt(4)];
                break;
            case "a small mixed tree, checked after every append":
                stream = MixedTree(seed: 7, entries: 400);
                Assert.Equal(400, stream.Count);
                RollupOracle.Feed(stream, (store, k) =>
                {
                    RollupOracle.AssertMatches(store, RollupOracle.Expected(stream, k + 1));
                    RollupOracle.CheckStructure(store);
                });
                break;
            default:
                stream = MixedTree(seed: 42, entries: 50_000);
                Assert.Equal(50_000, stream.Count);
                break;
        }

        ScanStore built = RollupOracle.Feed(stream);

        RollupOracle.AssertMatches(built, RollupOracle.Expected(stream));
        RollupOracle.CheckStructure(built);
        for (int e = 0; e < built.Count; e++)
        {
            if (built.Kind(e) == EntryKind.Folder && built.ChildCount(e) == 0)
                Assert.Equal(default, Rolled.Of(built, e));
        }
        if (shape.Contains("mixed"))
            AssertHasEveryShape(stream);
    }

    // ---- Hardlinks counted once (C2, C3) ----

    [Fact]
    public void OneIdUnderThreeNamesInThreeFoldersIsChargedOnceToTheFirstNamesFolder()
    {
        var id = new UInt128(7, 42);
        var store = new ScanStore();
        store.AppendRoot(@"C:\synthetic", 0x10);
        int top = Folder(store, 0);
        store.SetChildren(0, top, 1);
        int a = Folder(store, top), b = Folder(store, top), c = Folder(store, top);
        store.SetChildren(top, a, 3);

        int x = store.Append(a, "x", EntryKind.File, EntryFlags.None, 0x20, 1_000_000, 1_003_520, id, out int xHolder, out var xPosted);
        store.SetChildren(a, x, 1);
        int y = store.Append(b, "y", EntryKind.File, EntryFlags.None, 0x20, 1_000_000, 1_003_520, id, out int yHolder, out var yPosted);
        store.SetChildren(b, y, 1);
        int z = store.Append(c, "z", EntryKind.File, EntryFlags.None, 0x20, 1_000_000, 1_003_520, id, out int zHolder, out var zPosted);
        store.SetChildren(c, z, 1);

        Assert.Equal((x, x, x), (xHolder, yHolder, zHolder));
        Assert.Equal(new ScanStore.Posted(1_000_000, 1_003_520, 1), xPosted);
        Assert.Equal(new ScanStore.Posted(0, 0, 1), yPosted);
        Assert.Equal(new ScanStore.Posted(0, 0, 1), zPosted);
        Assert.Equal(EntryFlags.None, store.Flags(x));
        Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(y));
        Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(z));

        var copy = new Rolled(1_000_000, 1_003_520, 1);
        Assert.Equal(copy, Rolled.Of(store, x));
        Assert.Equal(copy, Rolled.Of(store, a));
        Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, y));
        Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, b));
        Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, z));
        Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, c));
        Assert.Equal(copy with { Files = 3 }, Rolled.Of(store, top));
        Assert.Equal(copy with { Files = 3 }, Rolled.Of(store, 0));
        RollupOracle.CheckStructure(store);
    }

    [Fact]
    public void NamesThatShareAnAncestorOrAParentChargeTheBytesOnceAndCountEveryName()
    {
        UInt128 first = Id(1), second = Id(2);
        // root: a, d; a: x, b; b: y (x and y are one file); d: p, q (one file, twice in one folder)
        List<Fed> stream =
        [
            Root(), FolderAt(0), FolderAt(0),
            FileAt(1, 300, 4 * K, first), FolderAt(1),
            FileAt(4, 300, 4 * K, first),
            FileAt(2, 500, 2 * K, second), FileAt(2, 500, 2 * K, second),
        ];

        ScanStore store = RollupOracle.Feed(stream);

        RollupOracle.AssertMatches(store, RollupOracle.Expected(stream));
        RollupOracle.CheckStructure(store);
        Assert.Equal(new Rolled(300, 4 * K, 2), Rolled.Of(store, 1));
        Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, 4));
        Assert.Equal(new Rolled(500, 2 * K, 2), Rolled.Of(store, 2));
        Assert.Equal(new Rolled(800, 6 * K, 4), Rolled.Of(store, 0));
        Assert.Equal([EntryFlags.None, EntryFlags.HardlinkDuplicate, EntryFlags.None, EntryFlags.HardlinkDuplicate],
            new[] { 3, 5, 6, 7 }.Select(store.Flags));
    }

    /// <summary>
    /// 0 is no ID (FAT, some SMB volumes, the fallback listing class), and so are all ones and the 64-bit
    /// all-ones widened (MS-FSCC: no unique ID could be established). Two files carrying any of them are two
    /// files. A match needs both 64-bit halves.
    /// </summary>
    [Fact]
    public void IdsThatMustNotMatchAreEachCharged()
    {
        UInt128[] ids =
        [
            UInt128.Zero, UInt128.Zero,
            UInt128.MaxValue, UInt128.MaxValue,
            new(0, ulong.MaxValue), new(0, ulong.MaxValue),
            new(1, 5), new(2, 5),
        ];
        var stream = new List<Fed> { Root() };
        foreach (UInt128 id in ids)
            stream.Add(FileAt(0, 1000, K, id));

        ScanStore store = RollupOracle.Feed(stream);

        for (int e = 1; e < store.Count; e++)
        {
            Assert.Equal(EntryFlags.None, store.Flags(e));
            Assert.Equal((1000L, K), (store.ChargedSize(e), store.ChargedOnDisk(e)));
        }
        Assert.Equal(new Rolled(1000 * ids.Length, K * ids.Length, ids.Length), Rolled.Of(store, 0));
        RollupOracle.AssertMatches(store, RollupOracle.Expected(stream));
        Assert.Equal([false, false, false, true, true], new[] { UInt128.Zero, UInt128.MaxValue, new UInt128(0, ulong.MaxValue), new UInt128(1, 5), new UInt128(0, 1) }
            .Select(ScanStore.HasFileId));
    }

    /// <summary>The store is the only source of the flag: one passed in on append is cleared, and adding it later is refused.</summary>
    [Fact]
    public void OnlyTheStoreFlagsAHardlinkDuplicate()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\synthetic", 0x10);
        int x = store.Append(0, "x", EntryKind.File, EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly, 0x20, 100, K, Id(1));
        store.SetChildren(0, x, 1);

        Assert.Equal(EntryFlags.CloudOnly, store.Flags(x));
        Assert.Equal(new Rolled(100, K, 1), Rolled.Of(store, 0));
        Assert.Throws<ArgumentException>(() => store.AddFlags(x, EntryFlags.HardlinkDuplicate));
        Assert.Equal(EntryFlags.CloudOnly, store.Flags(x));
        Assert.Equal(100, store.ChargedSize(x));
    }

    // ---- Leaves that add nothing (C4) ----

    [Fact]
    public void ALinkAndAnUnreadableFolderAreLeavesThatAddNothing()
    {
        // root: p; p: a link and an unreadable folder, both stating sizes, and one normal file
        List<Fed> stream =
        [
            Root(), FolderAt(0),
            new(1, EntryKind.Link, 5000, 8192),
            new(1, EntryKind.Folder, 4096, 4096, Flags: EntryFlags.Unreadable),
            FileAt(1, 100, K, Id(1)),
        ];
        const int p = 1, link = 2, unreadable = 3, file = 4;

        ScanStore store = RollupOracle.Feed(stream);

        var fileAlone = new Rolled(100, K, 1);
        Assert.Equal(fileAlone, Rolled.Of(store, p));
        Assert.Equal(fileAlone, Rolled.Of(store, 0));
        foreach (int leaf in new[] { link, unreadable })
        {
            Assert.Equal((0, -1), (store.ChildCount(leaf), store.FirstChild(leaf)));
            Assert.Equal((0L, 0L), (store.ChargedSize(leaf), store.ChargedOnDisk(leaf)));
            Assert.Equal(default, Rolled.Of(store, leaf));
            Assert.Empty(store.ChildrenLargestFirst(leaf));
        }
        Assert.Equal((5000L, 8192L), (store.Size(link), store.OnDisk(link))); // stated, kept, charged nowhere
        Assert.Equal((4096L, 4096L), (store.Size(unreadable), store.OnDisk(unreadable)));
        RollupOracle.AssertMatches(store, RollupOracle.Expected(stream));

        foreach (int parent in new[] { file, link, unreadable })
        {
            Assert.Throws<InvalidOperationException>(() =>
                store.Append(parent, "under", EntryKind.File, EntryFlags.None, 0x20, 7, K, Id(9)));
        }
        Assert.Equal(stream.Count, store.Count);
        Assert.Equal(fileAlone, Rolled.Of(store, p));
        RollupOracle.CheckStructure(store);
    }

    /// <summary>
    /// A folder whose listing failed partway keeps what was listed: its children were appended before the
    /// folder was flagged, so their bytes stay in every total above them (the root still equals the scan's
    /// totals) and the ordered read returns them. Nothing more can be appended under it.
    /// </summary>
    [Fact]
    public void AFolderListedInPartBeforeItsErrorKeepsWhatWasListed()
    {
        List<Fed> stream = [Root(), FolderAt(0), FolderAt(0), FileAt(1, 100, K, Id(1)), FileAt(1, 200, 3 * K, Id(2))];
        ScanStore store = RollupOracle.Feed(stream);
        const int partial = 1, empty = 2;

        store.AddFlags(partial, EntryFlags.Unreadable);
        store.AddFlags(empty, EntryFlags.Unreadable);

        Assert.Equal(new Rolled(300, 4 * K, 2), Rolled.Of(store, partial));
        Assert.Equal(new Rolled(300, 4 * K, 2), Rolled.Of(store, 0));
        Assert.Equal([new ChildRow(4, 3 * K, 0.75), new ChildRow(3, K, 0.25)], store.ChildrenLargestFirst(partial));
        Assert.Equal(default, Rolled.Of(store, empty));
        Assert.Empty(store.ChildrenLargestFirst(empty));
        Assert.Throws<InvalidOperationException>(() => store.Append(partial, "late", EntryKind.File, EntryFlags.None, 0x20, 1, 1, Id(3)));
        RollupOracle.CheckStructure(store);
    }

    /// <summary>A parent is an entry already in the store, so no Parent chain can loop. Only the first entry, the root, has none.</summary>
    [Fact]
    public void AParentMustAlreadyBeInTheStoreAndOnlyTheRootHasNone()
    {
        var store = new ScanStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Append(0, "first", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero));
        Assert.Equal(0, store.Count);
        store.AppendRoot(@"C:\synthetic", 0x10);

        foreach (int parent in new[] { -1, 1, 2, 1000, int.MaxValue, -2 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                store.Append(parent, "x", EntryKind.File, EntryFlags.None, 0x20, 1, 1, Id(1)));
        }
        Assert.Throws<InvalidOperationException>(() => store.AppendRoot(@"C:\again", 0x10));
        Assert.Equal(1, store.Count);
        Assert.Equal(default, Rolled.Of(store, 0));
    }

    // ---- A folder's own size (C1, C4) ----

    [Fact]
    public void AFolderThatStatesItsOwnSizeAddsNothingOfIt()
    {
        // root: d (states 4096/4096), e (states 4096/4096, empty); d: one file
        List<Fed> stream =
        [
            Root(), new(0, EntryKind.Folder, 4096, 4096), new(0, EntryKind.Folder, 4096, 4096),
            FileAt(1, 100, 512, Id(1)),
        ];

        ScanStore store = RollupOracle.Feed(stream);

        Assert.Equal(new Rolled(100, 512, 1), Rolled.Of(store, 1));
        Assert.Equal(default, Rolled.Of(store, 2));
        Assert.Equal(new Rolled(100, 512, 1), Rolled.Of(store, 0));
        Assert.Equal((0L, 0L, 4096L), (store.ChargedSize(1), store.ChargedOnDisk(1), store.Size(1)));
        RollupOracle.AssertMatches(store, RollupOracle.Expected(stream));
        RollupOracle.CheckStructure(store);
    }

    // ---- Children largest first, with shares (C5) ----

    /// <summary>
    /// A folder ranks by what is below it, not by the size it states; equal sizes come back in listing
    /// order although the sort itself is unstable; a later name of a hardlinked file ranks at 0, last. The
    /// folder holds more than 16 children because a sort of 16 or fewer is an insertion sort, which is
    /// stable, and would keep listing order with no tiebreak at all.
    /// </summary>
    [Fact]
    public void ChildrenAreReadLargestFirstWithEqualSizesInListingOrder()
    {
        const int ties = 60;
        UInt128 shared = Id(1);
        List<Fed> stream =
        [
            Root(), FolderAt(0), FolderAt(0), // 1 other, 2 p
            FileAt(1, 10, K, shared), // 3: the first name, in another folder
            // p's listing: 4 A, 5 S, 6 B, 7 C, 8 D (the later name), 9 T, 10 E, then 11 to 70 all of K
            FileAt(2, 1, K, Id(2)),
            new(2, EntryKind.Folder, 1 << 20, 1 << 20), // S states 1 MiB, holds 2 * K
            FileAt(2, 2, 2 * K, Id(3)),
            FileAt(2, 3, K, Id(4)),
            FileAt(2, 10, K, shared),
            FolderAt(2),
            FileAt(2, 4, 3 * K, Id(5)),
        ];
        for (int t = 0; t < ties; t++)
            stream.Add(FileAt(2, 100 + t, K, Id(100 + t)));
        stream.Add(FileAt(5, 5, 2 * K, Id(6))); // S
        stream.AddRange([FileAt(9, 6, K, Id(7)), FileAt(9, 7, K, Id(8))]); // T

        ScanStore store = RollupOracle.Feed(stream);

        int[] expected = [10, 5, 6, 9, 4, 7, .. Enumerable.Range(11, ties), 8];
        ChildRow[] firstRead = store.ChildrenLargestFirst(2);
        Assert.Equal(expected, firstRead.Select(r => r.Entry));
        Assert.Equal([3 * K, 2 * K, 2 * K, 2 * K, .. Enumerable.Repeat(K, ties + 2), 0], firstRead.Select(r => r.OnDisk));
        for (int read = 0; read < 2; read++)
            Assert.Equal(firstRead, store.ChildrenLargestFirst(2));
        Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(8));
    }

    [Fact]
    public void EachChildsShareIsItsOnDiskBytesOverTheFolders()
    {
        // root: q, t; q: 3K and 1K on disk, logical sizes the other way round; t: a file, a folder, a file of K each
        List<Fed> stream =
        [
            Root(), FolderAt(0), FolderAt(0),
            FileAt(1, 100, 3 * K, Id(1)), FileAt(1, 9_000, K, Id(2)),
            FileAt(2, 1, K, Id(3)), FolderAt(2), FileAt(2, 1, K, Id(4)),
            FileAt(6, 1, K, Id(5)),
        ];

        ScanStore store = RollupOracle.Feed(stream);

        ChildRow[] quarters = store.ChildrenLargestFirst(1);
        Assert.Equal([new ChildRow(3, 3 * K, 0.75), new ChildRow(4, K, 0.25)], quarters);
        Assert.Equal(1.0, quarters.Sum(r => r.Share));

        ChildRow[] thirds = store.ChildrenLargestFirst(2);
        Assert.Equal([5, 6, 7], thirds.Select(r => r.Entry));
        Assert.All(thirds, r => Assert.Equal(1.0 / 3, r.Share, 1e-12));
        Assert.Equal(1.0, thirds.Sum(r => r.Share), 1e-12);
        Assert.Equal(store.RolledOnDisk(2), thirds.Sum(r => r.OnDisk));

        ChildRow[] top = store.ChildrenLargestFirst(0);
        Assert.Equal(1.0, top.Sum(r => r.Share), 1e-12);
    }

    /// <summary>
    /// 0 over 0 is NaN in double and throws in long; neither may reach a reader. Online-only files (U-019)
    /// take a real folder there: logical bytes above 0, nothing on disk.
    /// </summary>
    [Fact]
    public void AFolderWithNothingOnDiskGivesEveryChildAZeroShare()
    {
        UInt128 shared = Id(1);
        List<Fed> stream =
        [
            Root(), FolderAt(0), FolderAt(0), FolderAt(0), FolderAt(0), // 1 empty, 2 other, 3 duplicates, 4 cloud
            new(0, EntryKind.Link, 5000, 8192), // 5
            FileAt(0, 7, K, Id(2)), // 6
            new(0, EntryKind.Folder, Flags: EntryFlags.Unreadable), // 7
            FileAt(2, 10, K, shared), // 8
            FileAt(3, 10, K, shared), FileAt(3, 10, K, shared), // 9, 10
            new(4, EntryKind.File, 5_000_000, 0, Id(3), EntryFlags.CloudOnly), new(4, EntryKind.File, 10, 0, Id(4), EntryFlags.CloudOnly), // 11, 12
        ];

        ScanStore store = RollupOracle.Feed(stream);

        Assert.Empty(store.ChildrenLargestFirst(1));
        Assert.Equal(0, store.RolledOnDisk(3));
        Assert.Equal([new ChildRow(9, 0, 0.0), new ChildRow(10, 0, 0.0)], store.ChildrenLargestFirst(3));
        Assert.Equal((5_000_010L, 0L), (store.RolledSize(4), store.RolledOnDisk(4)));
        ChildRow[] cloud = store.ChildrenLargestFirst(4);
        Assert.Equal([new ChildRow(11, 0, 0.0), new ChildRow(12, 0, 0.0)], cloud);
        Assert.All(cloud, r => Assert.False(double.IsNaN(r.Share)));
        foreach (int notAFolder in new[] { 5, 6, 7 })
            Assert.Empty(store.ChildrenLargestFirst(notAFolder));
        RollupOracle.CheckStructure(store);
    }

    // ---- Building streams ----

    private static Fed Root() => new(-1, EntryKind.Folder, Name: @"C:\synthetic");

    private static Fed FolderAt(int parent) => new(parent, EntryKind.Folder);

    private static Fed FileAt(int parent, long size, long onDisk, UInt128 id) => new(parent, EntryKind.File, size, onDisk, id);

    private static UInt128 Id(long n) => new(0x0001_0000, (ulong)n);

    private static int Folder(ScanStore store, int parent) =>
        store.Append(parent, "d", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);

    private static long Triangle(long n) => n * (n + 1) / 2;

    /// <summary>
    /// A seeded tree in the scanner's order: depth first, each folder's listing one block. Files of every
    /// size including 0, IDs that repeat anywhere (several names of one file) or are 0 or all ones, links
    /// and unreadable folders stating sizes of their own, readable folders stating one, and empty folders.
    /// </summary>
    private static List<Fed> MixedTree(int seed, int entries)
    {
        var random = new Random(seed);
        var stream = new List<Fed> { Root() };
        var ids = new List<UInt128>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0 && stream.Count < entries)
        {
            int folder = pending.Pop();
            int count = random.Next(0, 4) == 0 ? 0 : random.Next(1, 14);
            var subfolders = new List<int>();
            for (int k = 0; k < count && stream.Count < entries; k++)
            {
                int roll = random.Next(100);
                if (roll < 22)
                {
                    subfolders.Add(stream.Count);
                    stream.Add(random.Next(10) == 0 ? new(folder, EntryKind.Folder, 4096, 4096) : FolderAt(folder));
                }
                else if (roll < 26)
                {
                    stream.Add(new(folder, EntryKind.Folder, 4096, 4096, Flags: EntryFlags.Unreadable));
                }
                else if (roll < 30)
                {
                    stream.Add(new(folder, EntryKind.Link, 5000, 8192));
                }
                else
                {
                    UInt128 id = random.Next(100) switch
                    {
                        < 15 when ids.Count > 0 => ids[random.Next(ids.Count)],
                        < 18 => UInt128.Zero,
                        < 19 => UInt128.MaxValue,
                        _ => Id(stream.Count),
                    };
                    if (id != UInt128.Zero && id != UInt128.MaxValue)
                        ids.Add(id);
                    long size = random.Next(10) == 0 ? 0 : random.NextInt64(1, 1L << 32);
                    stream.Add(FileAt(folder, size, (size + K - 1) / K * K, id));
                }
            }
            // Pushed last-first, so the next block listed is this one's first subfolder's, as the scanner does.
            for (int s = subfolders.Count - 1; s >= 0; s--)
                pending.Push(subfolders[s]);
        }
        return stream;
    }

    private static void AssertHasEveryShape(List<Fed> stream)
    {
        var parents = stream.Skip(1).Select(e => e.Parent).ToHashSet();
        var ids = stream.Where(e => e.Kind == EntryKind.File && e.Id != UInt128.Zero && e.Id != UInt128.MaxValue)
            .GroupBy(e => e.Id).Count(g => g.Count() > 1);
        Assert.True(ids > 0, "no ID is reused");
        Assert.Contains(stream, e => e.Kind == EntryKind.Link);
        Assert.Contains(stream, e => e.Flags.HasFlag(EntryFlags.Unreadable) && e.Size > 0);
        Assert.Contains(stream, e => e.Kind == EntryKind.File && e.Size == 0);
        Assert.Contains(stream, e => e.Kind == EntryKind.File && e.Id == UInt128.Zero);
        Assert.Contains(Enumerable.Range(0, stream.Count), i => stream[i].Kind == EntryKind.Folder && !stream[i].Flags.HasFlag(EntryFlags.Unreadable) && !parents.Contains(i));
    }
}
