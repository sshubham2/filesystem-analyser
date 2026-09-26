using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>The store on synthetic entries, with no disk.</summary>
public class ScanStoreTests
{
    [Fact]
    public void AppendsPastOnePageAndRollsNamesOntoASecondPage()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\synthetic", 0x10);
        int entries = ScanStore.PageSize + 5_000;

        long namesUsed = @"C:\synthetic".Length;
        int straddler = -1;
        for (int i = 1; i <= entries; i++)
        {
            string name = NameOf(i);
            if (straddler < 0 && namesUsed + name.Length > ScanStore.NamePageChars)
                straddler = i; // this name would have crossed the first name page's end
            namesUsed += name.Length;
            int index = store.Append(0, name, EntryKind.File, EntryFlags.None, (uint)i, i, 2L * i, new UInt128((ulong)i, (ulong)i * 3));
            Assert.Equal(i, index);
        }
        store.SetChildren(0, 1, entries);

        Assert.True(straddler > 0, "the names should fill more than one name page");
        Assert.Equal(entries + 1, store.Count);
        Assert.Equal(@"C:\synthetic", store.Name(0).ToString());
        for (int i = 1; i <= entries; i++)
        {
            Assert.Equal(NameOf(i), store.Name(i).ToString());
            Assert.Equal(0, store.Parent(i));
            Assert.Equal((FileAttributes)i, store.Attributes(i));
            Assert.Equal(i, store.Size(i));
            Assert.Equal(2L * i, store.OnDisk(i));
            Assert.Equal(new UInt128((ulong)i, (ulong)i * 3), store.FileId(i));
            Assert.Equal(-1, store.FirstChild(i));
            Assert.Equal(0, store.ChildCount(i));
        }
        Assert.Equal(NameOf(straddler), store.Name(straddler).ToString());
        Assert.Equal(@"C:\synthetic\" + NameOf(ScanStore.PageSize), store.GetPath(ScanStore.PageSize));
        Assert.Equal(1, store.FirstChild(0));
        Assert.Equal(entries, store.ChildCount(0));
    }

    [Fact]
    public void AnIndexOutsideTheStoreThrows()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Name(1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Parent(-1));
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPath(1));
        // Not "root": an indexing bug during a scan must reach the window's Scan failed ending, not Could not read.
        Assert.NotEqual("root", e.ParamName);
    }

    [Fact]
    public void GetPathJoinsNamesExactlyAsListed()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        int dot = store.Append(0, "dot.", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        store.SetChildren(0, dot, 1);
        int nul = store.Append(dot, "NUL", EntryKind.File, EntryFlags.None, 0x20, 0, 0, UInt128.Zero);
        store.SetChildren(dot, nul, 1);

        Assert.Equal(@"C:\dot.", store.GetPath(dot));
        Assert.Equal(@"C:\dot.\NUL", store.GetPath(nul));
    }

    /// <summary>The ID table on the append path, which is the only way into it (SL-042).</summary>
    [Fact]
    public void ARepeatedFileIdIsFoundAndZeroIsNeverDeduplicated()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        const int unique = 200_000; // enough to make the table rehash many times
        for (int i = 1; i <= unique; i++)
        {
            int e = store.Append(0, "f", EntryKind.File, EntryFlags.None, 0, 0, 0, new UInt128((ulong)(i % 7), (ulong)i), out int charged, out _);
            Assert.Equal(e, charged);
            Assert.Equal(EntryFlags.None, store.Flags(e));
        }
        for (int i = 1; i <= unique; i++)
        {
            int e = store.Append(0, "g", EntryKind.File, EntryFlags.None, 0, 0, 0, new UInt128((ulong)(i % 7), (ulong)i), out int charged, out _);
            Assert.Equal(i, charged);
            Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(e));
        }

        int zeroA = store.Append(0, "z", EntryKind.File, EntryFlags.None, 0, 0, 0, UInt128.Zero, out int chargedA, out _);
        int zeroB = store.Append(0, "z", EntryKind.File, EntryFlags.None, 0, 0, 0, UInt128.Zero, out int chargedB, out _);
        Assert.Equal((zeroA, zeroB), (chargedA, chargedB));
        Assert.Equal(EntryFlags.None, store.Flags(zeroB));
    }

    internal static string NameOf(int i) =>
        string.Create(8 + (i * 7919 % 41), i, static (span, i) =>
        {
            span.Fill((char)('a' + i % 26));
            i.TryFormat(span, out _);
        });
}

[CollectionDefinition(nameof(MemoryBudget), DisableParallelization = true)]
public class MemoryBudget;

/// <summary>
/// Whether a full drive fits: U-001's shape (5,000,000 entries, 4.5M files and 0.5M folders, names
/// averaging about 27 characters, 4.3M distinct FileIds with 300,000 names sharing 100,000 of them), with
/// every row carrying the folder-total columns (SL-042) and one folder of 100,000 entries read largest
/// first. Runs alone, because it reads the process's GC heap.
/// </summary>
/// <remarks>
/// Folders sit at most 16 deep. Until SL-042 each folder was appended under the one before, a chain about
/// 500,000 deep, which cost nothing while appends posted nothing up the chain; posting every file's bytes
/// up it would be about 10^12 additions. Entry count, names, IDs, duplicate count and budget are as they
/// were; 100,000 entries in a row go into one folder, which turns about 10,000 folders into files.
/// </remarks>
[Collection(nameof(MemoryBudget))]
public class ScanStoreBudgetTests(ITestOutputHelper output)
{
    // The app must stay under 1 GiB. U-001 measured the WPF window alone at about 106 MB of commit and
    // the WPF tree over the store at about 160 MB, so the store gets what is left with room to spare.
    private const long StoreBudget = 800L * 1024 * 1024;

    [Fact]
    public void FiveMillionEntriesFitTheMemoryBudget()
    {
        const int entries = 5_000_000;
        const int sharedNames = 300_000;
        const int maxDepth = 16;
        const int wideAt = 2_000_000, wideChildren = 100_000;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;

        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        Span<char> name = stackalloc char[64];
        Span<int> path = stackalloc int[maxDepth + 1]; // path[d]: the folder files now go into at depth d
        int depth = 0, files = 0, duplicates = 0, wide = -1;
        long bytes = 0;
        for (int i = 1; i < entries; i++)
        {
            int length = 5 + (int)((uint)(i * 2654435761u) % 44u); // 5..48, mean 26.5
            name[..length].Fill((char)('a' + i % 26));
            bool inWide = wide >= 0 && i <= wideAt + wideChildren;
            if (i == wideAt || (i % 10 == 0 && !inWide))
            {
                // One level below any folder on the current path, at most maxDepth deep.
                int d = i == wideAt ? depth + 1 : Math.Min(depth + 1, 1 + (int)((uint)(i * 2246822519u) % maxDepth));
                d = Math.Min(d, maxDepth);
                path[d] = store.Append(path[d - 1], name[..length], EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, new UInt128(1, (ulong)i));
                depth = d;
                if (i == wideAt)
                    wide = path[d];
                continue;
            }

            ulong id = files < sharedNames ? (ulong)(files / 3) + 1 : (ulong)files + 1_000_000;
            files++;
            int e = store.Append(path[depth], name[..length], EntryKind.File, EntryFlags.None, 0x20, i, i, new UInt128(0, id), out int charged, out _);
            if (charged != e)
                duplicates++;
            else
                bytes += i;

            if ((i & 0xFFFF) == 0)
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
        }
        store.SetChildren(wide, wide + 1, wideChildren);

        ChildRow[] ordered = store.ChildrenLargestFirst(wide);
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
        Assert.Equal(wideChildren, ordered.Length);
        Assert.Equal(wideAt + wideChildren, ordered[0].Entry); // sizes rise with the index
        ordered = [];

        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        peak = Math.Max(peak, retained);
        output.WriteLine($"entries {store.Count:N0}, files {files:N0}, duplicate names {duplicates:N0}, FileId slots {store.FileIdTableSlots:N0}, " +
            $"folder totals on every row, one folder of {wideChildren:N0} read largest first");
        output.WriteLine($"retained {retained / 1048576.0:N1} MiB, sampled peak {peak / 1048576.0:N1} MiB, budget {StoreBudget / 1048576:N0} MiB");

        Assert.Equal(entries, store.Count);
        Assert.Equal(200_000, duplicates);
        Assert.Equal((bytes, bytes, files), (store.RolledSize(0), store.RolledOnDisk(0), store.FilesBelow(0)));
        Assert.True(retained < StoreBudget, $"the store retained {retained / 1048576.0:N1} MiB");
        Assert.True(peak < StoreBudget, $"the store peaked at {peak / 1048576.0:N1} MiB");
        GC.KeepAlive(store);
    }
}
