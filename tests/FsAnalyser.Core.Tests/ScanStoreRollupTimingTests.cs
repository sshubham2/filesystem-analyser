using System.Diagnostics;
using System.Reflection;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// Criterion 6, a measured number: the children of a folder holding 100,000 entries read largest first in
/// under 0.25 s. The order is sorted on every read and never stored, so this is what stands behind that
/// choice. Runs alone, in the memory-budget collection, so no other test's allocations or threads share the
/// machine with the measurement.
/// </summary>
[Collection(nameof(MemoryBudget))]
public class ScanStoreRollupTimingTests(ITestOutputHelper output)
{
    private const double BudgetMs = 250;
    private const int Reads = 10;

    [Fact]
    public void TheOrderedChildrenOfAHundredThousandChildFolderReadInUnderAQuarterSecond()
    {
        const int children = 100_000, subfolders = 1_000;
        var store = new ScanStore();
        store.AppendRoot(@"C:\synthetic", 0x10);
        int wide = store.Append(0, "wide", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        store.SetChildren(0, wide, 1);

        // Seeded on-disk sizes from a short list, so most children tie with thousands of others, plus 1,000
        // subfolders ranked by what they hold.
        var random = new Random(6);
        long[] sizes = [0, 4096, 8192, 65_536, 1 << 20];
        int first = store.Count;
        var subs = new List<int>();
        for (int k = 0; k < children; k++)
        {
            if (k % (children / subfolders) == 0)
            {
                subs.Add(store.Append(wide, "sub", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero));
                continue;
            }
            long onDisk = sizes[random.Next(sizes.Length)];
            store.Append(wide, "file", EntryKind.File, EntryFlags.None, 0x20, onDisk, onDisk, new UInt128(1, (ulong)k));
        }
        store.SetChildren(wide, first, children);
        foreach (int sub in subs)
        {
            int block = store.Count;
            long onDisk = sizes[random.Next(sizes.Length)];
            store.Append(sub, "inner", EntryKind.File, EntryFlags.None, 0x20, onDisk, onDisk, new UInt128(2, (ulong)sub));
            store.SetChildren(sub, block, 1);
        }

        var times = new double[Reads];
        ChildRow[]? firstRead = null;
        for (int r = 0; r < Reads; r++)
        {
            long start = Stopwatch.GetTimestamp();
            ChildRow[] rows = store.ChildrenLargestFirst(wide);
            times[r] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            firstRead ??= rows;
            Assert.Equal(firstRead, rows);
        }

        string configuration = typeof(ScanStore).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        output.WriteLine($"{configuration} build, high-resolution clock {Stopwatch.IsHighResolution}. {Reads} reads of {children:N0} children, " +
            $"the first cold, in ms: {string.Join(", ", times.Select(t => t.ToString("F2")))}; slowest {times.Max():F2}, budget {BudgetMs}");

        Assert.Equal(children, firstRead!.Length);
        for (int k = 1; k < firstRead.Length; k++)
        {
            ChildRow before = firstRead[k - 1], row = firstRead[k];
            Assert.True(before.OnDisk > row.OnDisk || (before.OnDisk == row.OnDisk && before.Entry < row.Entry),
                $"row {k} ({row}) is out of order after {before}");
        }
        Assert.Equal(1.0, firstRead.Sum(r => r.Share), 1e-9);
        for (int r = 0; r < Reads; r++)
            Assert.True(times[r] < BudgetMs, $"read {r + 1} of {Reads} took {times[r]:F2} ms, over the {BudgetMs} ms budget");
    }

    /// <summary>
    /// SL-111 criterion 3: opening a folder of 100,000 children in a 5,000,000-entry store a Stop left late, asking each
    /// row whether anything at or below it is unlisted (<see cref="ScanStore.NotListedAtOrBelow"/>) beside the largest-first
    /// read, takes under 0.25 s, the first pass cold: Core's share of the window's 0.5 s open (the sort alone is about
    /// 40 ms of it). The shape is the read's worst: S, at depth 2, has 100,000 children, every tenth a folder heading a chain
    /// 48 levels deep, each level's block the next folder first and nine files after it, so the backward scan of every block
    /// reads all of it. The chains are complete but the last, which stops right after its 24th block is registered, so S is on
    /// the path of the one unlisted folder. The file sizes are seeded from the entry's index, so largest-first order
    /// scrambles which chain the next row reads.
    /// </summary>
    /// <remarks>
    /// The read visits each entry below S at most once over all its rows, so no other shape at or under 5,000,000 entries
    /// costs more. Driven as the scanner drives a store: an explicit stack, each folder's children one block, SetChildren
    /// once the block is whole, subfolders pushed last-first. No per-entry state is kept by the generator.
    /// </remarks>
    [Fact]
    public void AnsweringEveryRowOfAHundredThousandChildFolderInAStoppedFiveMillionStoreFitsTheBudget()
    {
        const int total = 5_000_000, rows = 100_000, chains = rows / 10, levels = 48, listedLevels = 24, filesPerLevel = 9;
        const int chainEntries = (levels - 1) * (1 + filesPerLevel) + filesPerLevel; // the deepest block holds files only
        const int lastChainEntries = listedLevels * (1 + filesPerLevel);
        // The root, A, Pad and S, the 100,000 children of S, every chain but the last whole and the last half listed: Pad's
        // files are the rest, so the store is 5,000,000 entries exactly.
        const int padFiles = total - (4 + rows + (chains - 1) * chainEntries + lastChainEntries);
        Assert.InRange(padFiles, 1, 400_000);

        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);

        // One listing as FolderDone commits it: the children appended as one block, then SetChildren. Returns the block's first entry.
        int Block(int folder, int count, Func<int, EntryKind> kindOf)
        {
            int first = store.Count;
            for (int k = 0; k < count; k++)
            {
                if (kindOf(k) == EntryKind.Folder)
                {
                    store.Append(folder, "d", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
                }
                else
                {
                    long size = 1 + (uint)(store.Count * 2654435761u) % 4096;
                    store.Append(folder, "f", EntryKind.File, EntryFlags.None, 0x20, size, size, UInt128.Zero);
                }
            }
            store.SetChildren(folder, first, count);
            return first;
        }

        // The root lists A; A lists Pad then S (S last, so it is popped last); Pad lists its files; S lists its 100,000 children.
        int a = Block(0, 1, _ => EntryKind.Folder);
        int pad = Block(a, 2, _ => EntryKind.Folder), s = pad + 1;
        Block(pad, padFiles, _ => EntryKind.File);
        int firstChild = Block(s, rows, k => k % 10 == 0 ? EntryKind.Folder : EntryKind.File);

        // The chain heads are walked in listing order, each chain to its end before the next head is popped.
        var pending = new Stack<(int Entry, int Level, bool Last)>();
        for (int k = rows - 1; k >= 0; k--)
        {
            if (k % 10 == 0)
                pending.Push((firstChild + k, 1, k == rows - 10));
        }
        while (pending.Count > 0)
        {
            (int folder, int level, bool last) = pending.Pop();
            if (level == levels)
            {
                Block(folder, filesPerLevel, _ => EntryKind.File);
                continue;
            }
            int first = Block(folder, 1 + filesPerLevel, k => k == 0 ? EntryKind.Folder : EntryKind.File);
            pending.Push((first, level + 1, last));
            if (last && level == listedLevels)
                break; // the Stop: this block is registered and the folder it opens is queued, not listed
        }

        Assert.Equal(total, store.Count);
        Assert.Equal(rows, store.ChildCount(s));
        Assert.Single(pending);
        int notListed = 0;
        for (int e = 0; e < store.Count; e++)
        {
            if (store.NotListed(e))
                notListed++;
        }
        Assert.Equal(1, notListed);
        Assert.True(store.NotListed(pending.Peek().Entry));
        Assert.True(store.NotListedAtOrBelow(0) && store.NotListedAtOrBelow(a) && store.NotListedAtOrBelow(s));
        Assert.False(store.NotListedAtOrBelow(pad));

        var times = new double[Reads];
        var answerOnly = new double[Reads];
        for (int r = 0; r < Reads; r++)
        {
            long start = Stopwatch.GetTimestamp();
            ChildRow[] children = store.ChildrenLargestFirst(s);
            long sorted = Stopwatch.GetTimestamp();
            int unlisted = 0;
            foreach (ChildRow row in children)
            {
                if (store.NotListedAtOrBelow(row.Entry))
                    unlisted++;
            }
            long end = Stopwatch.GetTimestamp();
            times[r] = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
            answerOnly[r] = Stopwatch.GetElapsedTime(sorted, end).TotalMilliseconds;
            Assert.Equal(rows, children.Length);
            Assert.True(unlisted == 1, $"pass {r + 1}: {unlisted} rows read unlisted below them, expected the one half-listed chain");
        }

        string configuration = typeof(ScanStore).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        output.WriteLine($"{configuration} build, {store.Count:N0} entries, {rows:N0} rows of S, {padFiles:N0} files in Pad. {Reads} passes of the sort plus every row's answer, " +
            $"the first cold, in ms: {string.Join(", ", times.Select(t => t.ToString("F2")))}; slowest {times.Max():F2}, budget {BudgetMs}. " +
            $"The answers alone: {string.Join(", ", answerOnly.Select(t => t.ToString("F2")))}; slowest {answerOnly.Max():F2}");
        for (int r = 0; r < Reads; r++)
            Assert.True(times[r] < BudgetMs, $"pass {r + 1} of {Reads} took {times[r]:F2} ms, over the {BudgetMs} ms budget");
        GC.KeepAlive(store);
    }
}
