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
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPath(1));
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

    [Fact]
    public void ARepeatedFileIdIsFoundAndZeroIsNeverDeduplicated()
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        const int unique = 200_000; // enough to make the table rehash many times
        for (int i = 1; i <= unique; i++)
        {
            int e = store.Append(0, "f", EntryKind.File, EntryFlags.None, 0, 0, 0, new UInt128((ulong)(i % 7), (ulong)i));
            Assert.True(store.TryAddFileId(e, out int charged));
            Assert.Equal(e, charged);
        }
        for (int i = 1; i <= unique; i++)
        {
            int e = store.Append(0, "g", EntryKind.File, EntryFlags.None, 0, 0, 0, new UInt128((ulong)(i % 7), (ulong)i));
            Assert.False(store.TryAddFileId(e, out int charged));
            Assert.Equal(i, charged);
        }

        int zeroA = store.Append(0, "z", EntryKind.File, EntryFlags.None, 0, 0, 0, UInt128.Zero);
        int zeroB = store.Append(0, "z", EntryKind.File, EntryFlags.None, 0, 0, 0, UInt128.Zero);
        Assert.True(store.TryAddFileId(zeroA, out _));
        Assert.True(store.TryAddFileId(zeroB, out _));
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
/// averaging about 27 characters, 4.3M distinct FileIds with 300,000 names sharing 100,000 of them).
/// Runs alone, because it reads the process's GC heap.
/// </summary>
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

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;

        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        Span<char> name = stackalloc char[64];
        int folder = 0, files = 0, duplicates = 0;
        for (int i = 1; i < entries; i++)
        {
            int length = 5 + (int)((uint)(i * 2654435761u) % 44u); // 5..48, mean 26.5
            name[..length].Fill((char)('a' + i % 26));
            if (i % 10 == 0)
            {
                folder = store.Append(folder, name[..length], EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, new UInt128(1, (ulong)i));
                continue;
            }

            ulong id = files < sharedNames ? (ulong)(files / 3) + 1 : (ulong)files + 1_000_000;
            files++;
            int e = store.Append(folder, name[..length], EntryKind.File, EntryFlags.None, 0x20, i, i, new UInt128(0, id));
            if (!store.TryAddFileId(e, out _))
                duplicates++;

            if ((i & 0xFFFF) == 0)
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false) - before);
        }

        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        peak = Math.Max(peak, retained);
        output.WriteLine($"entries {store.Count:N0}, files {files:N0}, duplicate names {duplicates:N0}, FileId slots {store.FileIdTableSlots:N0}");
        output.WriteLine($"retained {retained / 1048576.0:N1} MiB, sampled peak {peak / 1048576.0:N1} MiB, budget {StoreBudget / 1048576:N0} MiB");

        Assert.Equal(entries, store.Count);
        Assert.Equal(200_000, duplicates);
        Assert.True(retained < StoreBudget, $"the store retained {retained / 1048576.0:N1} MiB");
        Assert.True(peak < StoreBudget, $"the store peaked at {peak / 1048576.0:N1} MiB");
        GC.KeepAlive(store);
    }
}
