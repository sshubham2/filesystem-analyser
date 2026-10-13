using System.Diagnostics;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-109 criterion 7: the not-listed mark adds no per-entry storage, so a store stopped where the most folders are
/// appended and not yet listed fits the same bounds a finished one does. U-001's shape (about 5,000,000 entries,
/// 90% files and 10% folders, names of 5 to 48 characters, 300,000 names sharing 100,000 file IDs, every row carrying
/// the folder-total columns) driven the way the scanner drives a store: an explicit stack, each folder's children one
/// contiguous block, SetChildren once the block is whole, subfolders pushed last-first. The root's last folder W holds
/// 60,000 subfolders and is listed last (U-201's shape), and the run stops right after W's block is registered: the store
/// is then about 97.6% full with those 60,000 folders marked.
/// </summary>
/// <remarks>
/// Runs alone with <see cref="ScanStoreBudgetTests"/>, because it reads the process's GC heap and commit. The generator
/// holds no per-entry state: names and IDs come from the entry's index, and the only memory it keeps besides the store is
/// the stack of folders waiting to be listed, which is also the count the store's marks must equal.
/// <para>The commit measured is the test host's own, which has no window. The published app adds its WPF window, about
/// 106 MB of commit (U-001), and builds no tree on a stopped ending, so this is the app's figure less that baseline; the
/// app itself is not driven here (SL-109 design, filed for later). The test host's commit is printed as such.</para>
/// </remarks>
[Collection(nameof(MemoryBudget))]
public class NotListedBudgetTests(ITestOutputHelper output)
{
    private const long StoreBudget = 800L * 1024 * 1024;
    private const long AppBudget = 1024L * 1024 * 1024;

    private const int PerListing = 30; // 3 folders (every tenth child) and 27 files
    private const int MaxDepth = 12;
    private const int EntriesBeforeW = 4_820_000;
    private const int WSubfolders = 60_000;
    private const int SharedNames = 300_000;
    private const int TopFolders = 8;

    [Fact]
    public void AStoreStoppedAtItsUnlistedPeakFitsTheBudget()
    {
        long commitBefore = CommitAfterFullCollection(out long committedBefore);
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;

        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        var name = new char[64];
        var pending = new Stack<(int Entry, int Depth)>();
        int files = 0, duplicates = 0, w = -1, foldersListed = 0;
        long bytes = 0;

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
                else
                    bytes += i;
            }
            if ((i & 0xFFFF) == 0)
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
            return entry;
        }

        // One listing, as FolderDone commits it: the children appended as one block, SetChildren, then the subfolders
        // pushed last-first so they are visited in listing order.
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
            foldersListed++;
        }

        // The root: eight top folders to be walked, and W last, so it is the last folder listed.
        List(0, 0, TopFolders + 1, onlyFolders: true);
        w = store.FirstChild(0) + TopFolders;
        while (pending.Count > 0)
        {
            (int folder, int depth) = pending.Pop();
            if (folder == w)
            {
                List(folder, depth, WSubfolders, onlyFolders: true);
                break; // the Stop: W's block is registered and its 60,000 subfolders are queued
            }
            // Full listings until the entries before W are in, at most MaxDepth deep; every folder still queued then lists empty, so the stack drains.
            int children = depth < MaxDepth && store.Count + PerListing <= EntriesBeforeW ? PerListing : 0;
            List(folder, depth, children, onlyFolders: false);
        }
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);

        // The mark is read on every entry now, at the stop, before anything is collected.
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long commitAtStop = process.PrivateMemorySize64;
        int marked = 0;
        for (int e = 0; e < store.Count; e++)
        {
            if (!store.NotListed(e))
                continue;
            marked++;
            Assert.Equal(EntryKind.Folder, store.Kind(e));
        }
        int pendingFolders = pending.Count;

        // SL-111: the subtree read, asked of every entry at the stop and before anything is measured. It builds no array and
        // keeps no result but a count, so nothing it does can count against `retained`; and it must allocate nothing per entry.
        // The first call is made before the allocation counter is read, so JIT and one-off runtime work are not charged to it.
        _ = store.NotListedAtOrBelow(0);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long readStart = Stopwatch.GetTimestamp();
        int unlistedBelow = 0;
        for (int e = 0; e < store.Count; e++)
        {
            if (store.NotListedAtOrBelow(e))
                unlistedBelow++;
        }
        TimeSpan readTime = Stopwatch.GetElapsedTime(readStart);
        long readAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        peak = Math.Max(peak, retained);
        output.WriteLine($"entries {store.Count:N0}, files {files:N0}, duplicate names {duplicates:N0}, folders listed {foldersListed:N0}, " +
            $"marked (not listed) {marked:N0}, queued {pendingFolders:N0}");
        output.WriteLine($"store: retained {retained / 1048576.0:N1} MiB, sampled peak {peak / 1048576.0:N1} MiB, budget {StoreBudget / 1048576:N0} MiB");
        output.WriteLine($"test-host commit: {commitBefore / 1048576.0:N1} MiB before the build (GC committed {committedBefore / 1048576.0:N1} MiB), " +
            $"{commitAtStop / 1048576.0:N1} MiB at the stop, bound {AppBudget / 1048576:N0} MiB for the app (the host has no WPF window; the published app adds about 106 MB)");

        output.WriteLine($"subtree read of every entry: {readTime.TotalMilliseconds:N1} ms, {readAllocated:N0} bytes allocated, {unlistedBelow:N0} entries with something not listed at or below");

        Assert.True(store.Count is >= 4_800_000 and <= 5_000_000, $"the stop landed at {store.Count:N0} entries, not about 4.88M");
        // The 60,000 queued folders, W (whose last subfolder is queued) and the root (whose last folder is W): the eight top
        // folders were listed to their ends, so nothing below them is unlisted. One byte per entry would be 4.6 MiB.
        Assert.Equal(WSubfolders + 2, unlistedBelow);
        Assert.True(store.NotListedAtOrBelow(0) && store.NotListedAtOrBelow(w));
        for (int top = 0; top < TopFolders; top++)
            Assert.False(store.NotListedAtOrBelow(store.FirstChild(0) + top), $"top folder {top} reads something unlisted below it");
        Assert.True(readAllocated < 64 * 1024, $"the subtree read allocated {readAllocated:N0} bytes over {store.Count:N0} entries");
        Assert.Equal(WSubfolders, marked);
        Assert.Equal(pendingFolders, marked); // the marks are exactly the folders the stack still holds
        Assert.False(store.NotListed(w)); // W was listed, with 60,000 subfolders
        Assert.Equal(WSubfolders, store.ChildCount(w));
        Assert.Equal((bytes, bytes, files), (store.RolledSize(0), store.RolledOnDisk(0), store.FilesBelow(0)));
        Assert.True(duplicates > 0 && files > SharedNames, "the shared-ID names were not exercised");
        Assert.True(retained < StoreBudget, $"the store retained {retained / 1048576.0:N1} MiB");
        Assert.True(peak < StoreBudget, $"the store peaked at {peak / 1048576.0:N1} MiB");
        Assert.True(commitAtStop < AppBudget, $"the test host held {commitAtStop / 1048576.0:N1} MiB of commit at the stop");
        GC.KeepAlive(store);
    }

    // A blocking, compacting, aggressive full collection, so the commit before the build is not the previous Fact's garbage.
    private static long CommitAfterFullCollection(out long gcCommitted)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }
}
