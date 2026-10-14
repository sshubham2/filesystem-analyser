using System.Diagnostics;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-112 criterion 7: what a Stop leaves to say which folders a pending size check could still change is one number and
/// reads of the columns that exist, so a store stopped with work pending fits the bounds a finished one does. U-201's shape
/// (about 4,880,000 entries, 90% files and 10% folders, 300,000 names sharing 100,000 file IDs, W with 60,000 subfolders listed
/// last) driven the way the scanner drives a store (an explicit stack, each folder's children one contiguous block, SetChildren
/// once the block is whole, subfolders pushed last-first), with one folder of 100,000 children. Two rows: a Stop in the listing
/// at the unlisted peak, where no check has run (the watermark 0), and a scan listed to its end and stopped late in the size
/// checks, with the first 97% of the entries checked.
/// </summary>
/// <remarks>
/// Runs alone with <see cref="ScanStoreBudgetTests"/> and <see cref="NotListedBudgetTests"/>, because it reads the process's GC
/// heap. The reads are made on every entry, folders and files, between two full collections, and are required to allocate
/// nothing and to leave nothing retained. The brute-force answer to compare them with is made after the measurement, from
/// the Parent column alone, so the arrays it needs are not charged to the store.
/// </remarks>
[Collection(nameof(MemoryBudget))]
public class UnfinishedBudgetTests(ITestOutputHelper output)
{
    private const long StoreBudget = 800L * 1024 * 1024;

    private const int PerListing = 30; // 3 folders (every tenth child) and 27 files
    private const int MaxDepth = 12;
    private const int EntriesBeforeW = 4_820_000;
    private const int WSubfolders = 60_000;
    private const int SharedNames = 300_000;
    private const int TopFolders = 8;
    private const int WideChildren = 100_000;
    private const int WideDepth = 4;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStoreStoppedWithChecksPendingFitsTheBudget(bool listedToTheEnd)
    {
        CommitAfterFullCollection();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;

        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        var name = new char[64];
        var pending = new Stack<(int Entry, int Depth)>();
        int files = 0, duplicates = 0, w = -1, wide = -1;

        int Add(int parent, EntryKind kind)
        {
            int i = store.Count;
            int length = 5 + (int)((uint)(i * 2654435761u) % 44u); // 5..48, mean 26.5
            Array.Fill(name, (char)('a' + i % 26), 0, length);
            int entry;
            if (kind == EntryKind.Folder)
            {
                entry = store.Append(parent, name.AsSpan(0, length), EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, new UInt128(1, (ulong)i));
            }
            else
            {
                ulong id = files < SharedNames ? (ulong)(files / 3) + 1 : (ulong)files + 1_000_000;
                files++;
                entry = store.Append(parent, name.AsSpan(0, length), EntryKind.File, EntryFlags.None, 0x20, i, i, new UInt128(0, id), out int charged, out _);
                if (charged != entry)
                    duplicates++;
            }
            if ((i & 0xFFFF) == 0)
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
            return entry;
        }

        void List(int folder, int depth, int children, bool onlyFolders)
        {
            int first = store.Count;
            for (int k = 0; k < children; k++)
                Add(folder, onlyFolders || k % 10 == 9 ? EntryKind.Folder : EntryKind.File);
            store.SetChildren(folder, first, children);
            for (int k = children - 1; k >= 0; k--)
            {
                if (store.Kind(first + k) == EntryKind.Folder)
                    pending.Push((first + k, depth + 1));
            }
        }

        List(0, 0, TopFolders + 1, onlyFolders: true);
        w = store.FirstChild(0) + TopFolders;
        while (pending.Count > 0)
        {
            (int folder, int depth) = pending.Pop();
            if (folder == w)
            {
                List(folder, depth, WSubfolders, onlyFolders: true);
                if (!listedToTheEnd)
                    break; // the Stop: W's block is registered and its 60,000 subfolders are queued
                continue;
            }
            if (wide < 0 && depth == WideDepth)
            {
                wide = folder;
                List(folder, depth, WideChildren, onlyFolders: false);
                continue;
            }
            int children = depth < MaxDepth && store.Count + PerListing <= EntriesBeforeW ? PerListing : 0;
            List(folder, depth, children, onlyFolders: false);
        }
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
        Assert.True(wide > 0 && store.ChildCount(wide) == WideChildren, "the 100,000-child folder was not built");

        // What the scan would say as it returns: 0 for a Stop in the listing, else where the checks stopped. Entries before it
        // were checked, some of them corrected.
        int watermark = 0;
        if (listedToTheEnd)
        {
            watermark = (int)(store.Count * 97L / 100);
            for (int e = 1, due = 0; e < watermark; e++)
            {
                if (EntryRules.ChecksOwnSize(store.Kind(e), store.Flags(e)) && ++due % 5 == 0)
                    store.CorrectSizes(e, store.Size(e) + 7, store.OnDisk(e) + 4096);
            }
        }
        store.EndSizeChecks(watermark);

        // Every entry's two reads, between two full collections. The first calls are made before the counter is read, so JIT and one-off
        // runtime work are not charged to them. Locals only: nothing here may allocate.
        _ = store.Unfinished(0);
        _ = store.AtLeast(0);
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        int unfinishedFolders = 0, unfinishedFiles = 0, folders = 0;
        long atLeastSize = 0;
        for (int e = 0; e < store.Count; e++)
        {
            bool unfinished = store.Unfinished(e);
            ScanStore.AtLeastFigures figures = store.AtLeast(e);
            if (store.Kind(e) == EntryKind.Folder)
            {
                folders++;
                if (unfinished)
                    unfinishedFolders++;
            }
            else if (unfinished)
            {
                unfinishedFiles++;
            }
            if (e == 0)
                atLeastSize = figures.Size;
        }
        TimeSpan wholeStore = Stopwatch.GetElapsedTime(start);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // The 100,000-child folder opened: every row's mark and at-least figure.
        start = Stopwatch.GetTimestamp();
        int firstChild = store.FirstChild(wide), rowsUnfinished = 0;
        long rowsSize = 0;
        for (int c = 0; c < WideChildren; c++)
        {
            if (store.Unfinished(firstChild + c))
                rowsUnfinished++;
            rowsSize += store.AtLeast(firstChild + c).Size;
        }
        TimeSpan wideRows = Stopwatch.GetElapsedTime(start);
        long rowsAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore - allocated;

        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        long retained = heapAfter - before;
        peak = Math.Max(peak, retained);

        output.WriteLine($"{(listedToTheEnd ? "listed to the end, checks 97% done" : "stopped at the unlisted peak, no check run")}: entries {store.Count:N0}, " +
            $"files {files:N0}, duplicate names {duplicates:N0}, folders {folders:N0}, watermark {watermark:N0}");
        output.WriteLine($"store: retained {retained / 1048576.0:N1} MiB, sampled peak {peak / 1048576.0:N1} MiB, budget {StoreBudget / 1048576:N0} MiB; " +
            $"the reads left {(heapAfter - heapBefore) / 1024.0:N1} KiB and allocated {allocated:N0} bytes");
        output.WriteLine($"both reads of every entry: {wholeStore.TotalMilliseconds:N1} ms ({unfinishedFolders:N0} folders and {unfinishedFiles:N0} files unfinished); " +
            $"the {WideChildren:N0}-child folder's rows: {wideRows.TotalMilliseconds:N1} ms, {rowsUnfinished:N0} unfinished, {rowsAllocated:N0} bytes allocated");

        // The reads keep and allocate nothing.
        Assert.True(heapAfter - heapBefore < 1024 * 1024, $"the reads left {(heapAfter - heapBefore) / 1024.0:N1} KiB retained");
        Assert.Equal(0, allocated);
        Assert.Equal(0, rowsAllocated);
        Assert.True(retained < StoreBudget, $"the store retained {retained / 1048576.0:N1} MiB");
        Assert.True(peak < StoreBudget, $"the store peaked at {peak / 1048576.0:N1} MiB");
        Assert.True(wholeStore < TimeSpan.FromSeconds(3), $"the reads of every entry took {wholeStore.TotalMilliseconds:N0} ms");
        Assert.True(wideRows < TimeSpan.FromMilliseconds(250), $"the 100,000-child folder's rows took {wideRows.TotalMilliseconds:N0} ms");
        Assert.True(store.Count is >= 4_800_000 and <= 5_000_000, $"the build ended at {store.Count:N0} entries, not about 4.88M");
        Assert.True(duplicates > 0 && files > SharedNames, "the shared-ID names were not exercised");
        Assert.True(unfinishedFolders > 0 && unfinishedFolders < folders, $"{unfinishedFolders:N0} of {folders:N0} folders unfinished");
        Assert.True(rowsUnfinished > 0, "no row of the 100,000-child folder was unfinished");
        output.WriteLine($"the root holds at least {atLeastSize:N0} bytes; the wide folder's rows hold at least {rowsSize:N0}");

        // Against a walk of the Parent column alone (children are appended after their parent, so one pass from the end sees every
        // child before its parent): the same two answers for every entry. Made after the measurement.
        int n = store.Count;
        var unfinishedBelow = new bool[n];
        var size = new long[n];
        var onDisk = new long[n];
        var count = new int[n];
        for (int e = n - 1; e > 0; e--)
        {
            EntryKind kind = store.Kind(e);
            int parent = store.Parent(e);
            if (kind == EntryKind.File)
            {
                bool isPending = e >= watermark && EntryRules.ChecksOwnSize(kind, store.Flags(e));
                if (!isPending)
                {
                    size[parent] += store.ChargedSize(e);
                    onDisk[parent] += store.ChargedOnDisk(e);
                }
                count[parent]++;
                unfinishedBelow[parent] |= isPending;
            }
            else
            {
                unfinishedBelow[e] |= store.NotListed(e);
                size[parent] += size[e];
                onDisk[parent] += onDisk[e];
                count[parent] += count[e];
                unfinishedBelow[parent] |= unfinishedBelow[e];
            }
        }
        for (int e = 0; e < n; e++)
        {
            if (store.Kind(e) == EntryKind.Folder)
            {
                Assert.True(store.Unfinished(e) == unfinishedBelow[e], $"folder {e} reads Unfinished {store.Unfinished(e)}, the walk says {unfinishedBelow[e]}");
                Assert.True(store.AtLeast(e) == new ScanStore.AtLeastFigures(size[e], onDisk[e], count[e]), $"folder {e} holds at least {store.AtLeast(e)}, the walk says ({size[e]}, {onDisk[e]}, {count[e]})");
            }
            else
            {
                bool isPending = e >= watermark && EntryRules.ChecksOwnSize(store.Kind(e), store.Flags(e));
                Assert.True(store.Unfinished(e) == isPending && store.SizeCheckPending(e) == isPending, $"file {e}");
            }
        }
        GC.KeepAlive(store);
    }

    // A blocking, compacting, aggressive full collection, so the heap before the build is not the previous Fact's garbage.
    private static void CommitAfterFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
