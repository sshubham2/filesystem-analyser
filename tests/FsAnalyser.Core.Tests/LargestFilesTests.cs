using System.Diagnostics;
using FsAnalyser.Core.Analysis;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The largest-files list (SL-040) over real trees: each builds one under %TEMP%, scans it, and ranks what
/// the scan stored. Sizes are whole 4 KiB clusters, so a plain file takes exactly its size on disk.
/// </summary>
public class LargestFilesTests
{
    private const long MiB = TempTree.MiB;
    private const long KiB4 = 4096;
    private const uint FileListDirectory = 0x1;

    [Fact]
    public void RanksFilesAtAnyDepthAndNeverAFolder()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\b\c\d\e\f");
        string deep = tree.File(@"a\b\c\d\e\f\deep.bin", 3 * MiB);
        string top = tree.File("top.bin", 2 * MiB);
        tree.Folder("many");
        string[] many = Enumerable.Range(1, 5).Select(k => tree.File($@"many\m{k}.bin", 1 * MiB)).ToArray(); // 5 MiB together
        string small = tree.File("small.txt", 100);

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        Assert.Equal(ScanStatus.Completed, result.Status);
        AssertRanked(result.Store, list);
        Assert.Equal(8, list.Count);
        Assert.Equal(deep, list[0].Path); // six folders down, and the largest single file
        Assert.Equal(3 * MiB, list[0].ChargedSize);
        Assert.Equal(3 * MiB, list[0].ChargedOnDisk);
        Assert.Equal(top, list[1].Path);
        Assert.Equal(many.Order(StringComparer.Ordinal), list.Skip(2).Take(5).Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(small, list[7].Path);
        Assert.DoesNotContain(list, f => f.Path == tree.PathOf("many")); // 5 MiB, more than any file, and not a file
    }

    [Fact]
    public void KeepsExactlyTheCountLargestWhenSizesTieAtTheCut()
    {
        using var tree = new TempTree();
        var distinct = new List<string>();
        for (int k = 0; k < 95; k++)
            distinct.Add(tree.File($"d{k:D3}.bin", (200 - k) * KiB4));
        var tied = new List<string>();
        for (int k = 0; k < 10; k++)
            tied.Add(tree.File($"t{k:D3}.bin", 50 * KiB4)); // ranks 96 to 105: five make the cut

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        AssertRanked(result.Store, list);
        Assert.Equal(100, list.Count);
        Assert.Equal(list.Count, list.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(distinct, list.Take(95).Select(f => f.Path));
        // Among equal sizes the earlier entry ranks first, so the five tied files kept are the five the
        // scan stored first, in that order.
        int[] keptTied = tied.Select(p => Scans.Find(result, p)).Order().Take(5).ToArray();
        Assert.Equal(keptTied, list.Skip(95).Select(f => f.Index));
        Assert.All(list.Skip(95), f => Assert.Equal(50 * KiB4, f.ChargedSize));

        // The same list as sorting every file and taking the first 100.
        Assert.Equal(SortedFiles(result.Store).Take(100), list);
    }

    [Fact]
    public void ATreeWithNothingToRankGivesAnEmptyList()
    {
        using var tree = new TempTree();
        string empty = tree.Folder("empty");
        string zeros = tree.Folder("zeros");
        for (int k = 0; k < 5; k++)
            tree.File($@"zeros\z{k}.txt", 0);
        tree.Folder(@"zeros\nested");

        ScanResult emptyScan = Scans.Run(empty);
        ScanResult zeroScan = Scans.Run(zeros);

        Assert.Equal(ScanStatus.Completed, emptyScan.Status);
        Assert.Empty(LargestFiles.Top(emptyScan.Store, 100));
        Assert.Equal(5, zeroScan.Totals.Files);
        Assert.Empty(LargestFiles.Top(zeroScan.Store, 100));

        Assert.Empty(LargestFiles.Top(zeroScan.Store, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LargestFiles.Top(zeroScan.Store, -1));
        Assert.Throws<ArgumentNullException>(() => LargestFiles.Top(null!, 100));
    }

    [Fact]
    public void AFileWithTwoNamesIsListedOnce()
    {
        using var tree = new TempTree();
        tree.Folder("1-first");
        tree.Folder("2-second");
        string first = tree.File(@"1-first\three.bin", 3 * MiB);
        string second = TempTree.Hardlink(tree.PathOf(@"2-second\three-again.bin"), first);
        string other = tree.File("one.bin", 1 * MiB);

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        Assert.Equal(EntryFlags.HardlinkDuplicate, result.Store.Flags(Scans.Find(result, second))); // the shape was fed in
        AssertRanked(result.Store, list);
        Assert.Equal([first, other], list.Select(f => f.Path));
        Assert.Equal(3 * MiB, list[0].ChargedSize);
        Assert.Equal(3 * MiB, list[0].ChargedOnDisk);
    }

    [Fact]
    public void NoRowComesFromALink()
    {
        using var tree = new TempTree();
        string outside = tree.Outside("target");
        TempTree.WriteFile(Path.Combine(outside, "outside.bin"), 4 * MiB);
        string target = tree.File("target.bin", 2 * MiB);
        string[] links =
        [
            tree.FileSymlink("file-link.bin", target),
            tree.DirectorySymlink("folder-link", outside),
            tree.Junction("junction", outside),
        ];

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        Assert.All(links, link => Assert.Equal(EntryKind.Link, result.Store.Kind(Scans.Find(result, link))));
        AssertRanked(result.Store, list);
        LargestFile only = Assert.Single(list);
        Assert.Equal(target, only.Path);
        Assert.Equal(2 * MiB, only.ChargedSize);
    }

    [Fact]
    public void RanksBySpaceOnDiskAndKeepsBothSizes()
    {
        using var tree = new TempTree();
        string sparse = tree.SparseFile("sparse.bin", 64 * MiB); // nothing written: nothing allocated
        string dense = tree.File("dense.bin", 1 * MiB);

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        AssertRanked(result.Store, list);
        Assert.Equal([dense, sparse], list.Select(f => f.Path));
        Assert.Equal(1 * MiB, list[0].ChargedSize);
        Assert.Equal(1 * MiB, list[0].ChargedOnDisk);
        Assert.Equal(64 * MiB, list[1].ChargedSize);
        Assert.InRange(list[1].ChargedOnDisk, 0, KiB4); // still listed with nothing on disk, as an online-only file is

        // With room for one, the file that takes the space is the one kept.
        Assert.Equal(dense, Assert.Single(LargestFiles.Top(result.Store, 1)).Path);
    }

    [Fact]
    public void EachPathIsTheFilesExactPath()
    {
        using var tree = new TempTree();
        string ext = tree.ExtendedRoot;
        string folders = $@"{new string('L', 120)}\{new string('m', 120)}";
        Directory.CreateDirectory($@"{ext}\{folders}");
        string[] names =
        [
            $@"{folders}\long-{new string('n', 40)}.bin",
            "ends-in-a-dot.",
            "ends-in-a-space ",
            "a\uD800b.bin", // an unpaired high surrogate
        ];
        for (int k = 0; k < names.Length; k++)
            TempTree.WriteFile($@"{ext}\{names[k]}", (4 - k) * MiB);
        tree.File("filler.bin", KiB4);

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        Assert.Empty(result.Unreadable);
        AssertRanked(result.Store, list);
        Assert.Equal(names.Select(n => $@"{tree.Root}\{n}").Append(tree.PathOf("filler.bin")), list.Select(f => f.Path));
        Assert.True(list[0].Path.Length > 260, $"the long path is {list[0].Path.Length} characters");
        Assert.All(list, f => Assert.DoesNotContain('\uFFFD', f.Path));
        Assert.Equal('\uD800', list[3].Path[^6]);
        Assert.All(list, f => Assert.True(File.Exists(@"\\?\" + f.Path), $"no file at {f.Path}"));
    }

    [Fact]
    public void AStoppedScanListsOnlyWhatItRead()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 10; i++)
        {
            tree.Folder($"f{i}");
            tree.File($@"f{i}\file.bin", (i + 1) * 64 * KiB4); // the later the folder, the larger its file
        }
        using var cts = new CancellationTokenSource();
        var options = new ScanOptions
        {
            AfterFolder = n =>
            {
                if (n == 3)
                    cts.Cancel();
            },
        };

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, options);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        AssertRanked(result.Store, list);
        Assert.Equal([tree.PathOf(@"f1\file.bin"), tree.PathOf(@"f0\file.bin")], list.Select(f => f.Path)); // the root, f0 and f1 were read
    }

    [Fact]
    public void AFileInAFolderTheScanCouldNotReadIsNotListed()
    {
        using var tree = new TempTree();
        string denied = tree.Folder("denied");
        tree.File(@"denied\biggest.bin", 4 * MiB);
        string readable = tree.File("readable.bin", 1 * MiB);
        tree.Deny(denied, FileListDirectory);

        ScanResult result = Scans.Run(tree.Root);
        IReadOnlyList<LargestFile> list = LargestFiles.Top(result.Store, 100);

        Assert.Equal(Scans.Find(result, denied), Assert.Single(result.Unreadable).Entry);
        Assert.Equal(readable, Assert.Single(list).Path);
    }

    /// <summary>
    /// Every row is a file of the store charged some bytes, at its own path and charged sizes, and the rows
    /// run on-disk descending, then logical descending, then store index ascending.
    /// </summary>
    internal static void AssertRanked(ScanStore store, IReadOnlyList<LargestFile> list)
    {
        for (int k = 0; k < list.Count; k++)
        {
            LargestFile row = list[k];
            Assert.Equal(EntryKind.File, store.Kind(row.Index));
            Assert.Equal(store.GetPath(row.Index), row.Path);
            Assert.Equal(store.ChargedSize(row.Index), row.ChargedSize);
            Assert.Equal(store.ChargedOnDisk(row.Index), row.ChargedOnDisk);
            Assert.True(row.ChargedSize > 0 || row.ChargedOnDisk > 0, $"{row.Path} is charged nothing");
            if (k == 0)
                continue;
            LargestFile before = list[k - 1];
            int order = (before.ChargedOnDisk, before.ChargedSize, row.Index).CompareTo((row.ChargedOnDisk, row.ChargedSize, before.Index));
            Assert.True(order > 0, $"row {k} ({row.Path}) is out of order after {before.Path}");
        }
    }

    /// <summary>Every file charged some bytes, fully sorted: the list Top must equal a prefix of.</summary>
    internal static IEnumerable<LargestFile> SortedFiles(ScanStore store) =>
        Enumerable.Range(0, store.Count)
            .Where(i => store.Kind(i) == EntryKind.File && (store.ChargedSize(i) > 0 || store.ChargedOnDisk(i) > 0))
            .Select(i => new LargestFile(i, store.GetPath(i), store.ChargedSize(i), store.ChargedOnDisk(i)))
            .OrderByDescending(f => f.ChargedOnDisk).ThenByDescending(f => f.ChargedSize).ThenBy(f => f.Index);
}

/// <summary>
/// The list over a store the size of the real C:\ scan (4,417,469 entries, 3,570,999 files), built in memory.
/// Runs alone, in the memory-budget collection, so its timing is not another test's.
/// </summary>
[Collection(nameof(MemoryBudget))]
public class LargestFilesScaleTests(ITestOutputHelper output)
{
    private const int Entries = 4_417_469;
    private const long Terabyte = 1L << 40;

    [Fact]
    public void RanksAStoreOfFourMillionEntriesInOnePassWithoutCollectingIt()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        Span<char> name = stackalloc char[16];
        int level1 = 0, level2 = 0, files = 0;
        var planted = new List<int>();
        for (int i = 1; i < Entries; i++)
        {
            i.TryFormat(name, out int length);
            ReadOnlySpan<char> n = name[..length];
            if (i % 5000 == 1)
            {
                level1 = store.Append(0, n, EntryKind.Folder, EntryFlags.None, 0x10, Terabyte, Terabyte, UInt128.Zero);
                level2 = level1;
                continue;
            }
            if (i % 5 == 0)
            {
                level2 = store.Append(level1, n, EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
                continue;
            }
            files++;
            if (i % 44_000 == 3 && planted.Count < 100)
            {
                // The 100 largest, spread through the store and in no size order.
                long size = Terabyte / 4 - (planted.Count * 7919 % 100) * 4096;
                planted.Add(store.Append(level2, n, EntryKind.File, EntryFlags.None, 0x20, size, size, UInt128.Zero));
            }
            else if (i % 1000 == 7)
            {
                // Larger than any planted file, and charged nothing.
                store.Append(level2, n, EntryKind.Link, EntryFlags.None, 0x20, Terabyte, Terabyte, UInt128.Zero);
            }
            else if (i % 1000 == 9)
            {
                store.Append(level2, n, EntryKind.File, EntryFlags.HardlinkDuplicate, 0x20, Terabyte, Terabyte, UInt128.Zero);
            }
            else
            {
                long size = (long)((uint)(i * 2654435761u) % 1_000_000_000u);
                store.Append(level2, n, EntryKind.File, EntryFlags.None, 0x20, size, size, UInt128.Zero);
            }
        }
        Assert.Equal(Entries, store.Count);
        Assert.Equal(100, planted.Count);
        LargestFiles.Top(store, 100); // compiled and warmed before it is measured

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        IReadOnlyList<LargestFile> list = LargestFiles.Top(store, 100);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        output.WriteLine($"entries {store.Count:N0}, files {files:N0}: Top(100) took {clock.Elapsed.TotalMilliseconds:N1} ms and allocated {allocated:N0} bytes");
        Assert.Equal(planted.Order(), list.Select(f => f.Index).Order());
        LargestFilesTests.AssertRanked(store, list);
        // A heap of 100 and 100 paths. Collecting the files to sort them would take 14 MB for the indices alone.
        Assert.True(allocated < 1024 * 1024, $"Top allocated {allocated:N0} bytes over {Entries:N0} entries");
        GC.KeepAlive(store);
    }
}
