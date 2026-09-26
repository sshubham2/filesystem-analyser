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
}
