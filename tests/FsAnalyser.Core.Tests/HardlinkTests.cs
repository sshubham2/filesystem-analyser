using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A file with several names is charged once, at its true size. The fixture writes 1 MiB, adds a
/// second name, then appends 63 MiB through the first name only, which leaves the other name's
/// directory entry showing a stale 1 MiB.
/// </summary>
public class HardlinkTests
{
    private const long MiB = TempTree.MiB;

    /// <param name="names">How many names the file has, one per folder, visited in folder order.</param>
    /// <param name="writtenThrough">Which name the 63 MiB were appended through; every other name is stale.</param>
    [Theory]
    [InlineData(2, 1)] // the stale name is seen first: its own probe corrects it, and the repeat is never probed
    [InlineData(3, 2)] // three names in three folders: each later name adds a file and 0 bytes, and is not probed
    [InlineData(2, 0)] // the reverse order: the current name is seen first, and its probe agrees with its listing
    public void AFileWithSeveralNamesIsChargedOnceAtItsTrueSize(int names, int writtenThrough)
    {
        using var tree = new TempTree();
        var paths = new string[names];
        for (int i = 0; i < names; i++)
        {
            string folder = $"{i + 1}-folder";
            tree.Folder(folder);
            paths[i] = tree.PathOf($@"{folder}\name{i}.bin");
        }
        TempTree.WriteFile(paths[writtenThrough], 1 * MiB);
        for (int i = 0; i < names; i++)
        {
            if (i != writtenThrough)
                TempTree.Hardlink(paths[i], paths[writtenThrough]);
        }
        TempTree.Append(paths[writtenThrough], 63 * MiB);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        ScanStore store = result.Store;
        int charged = Scans.Find(result, paths[0]); // the first name the scan meets charges the bytes
        Assert.Equal(64 * MiB, store.Size(charged));
        Assert.True(store.OnDisk(charged) >= 64 * MiB, $"the charged name takes {store.OnDisk(charged)} bytes on disk");
        // The probe, not the listing, supplied the figure exactly when the first name met was a stale one.
        Assert.Equal(writtenThrough > 0 ? EntryFlags.SizeProbed | EntryFlags.ProbeCorrected : EntryFlags.SizeProbed, store.Flags(charged));
        Assert.Equal(64 * MiB, store.ChargedSize(charged));
        for (int i = 1; i < names; i++)
        {
            int repeat = Scans.Find(result, paths[i]);
            Assert.True(charged < repeat);
            Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(repeat));
            Assert.Equal(store.FileId(charged), store.FileId(repeat));
            Assert.Equal(0, store.ChargedSize(repeat));
            Assert.Equal(0, store.ChargedOnDisk(repeat));
        }
        Assert.Equal(new ScanTotals(Files: names, Folders: names, Links: 0, Bytes: 64 * MiB, BytesOnDisk: store.OnDisk(charged)), result.Totals);
        // One probe per file, through the first name met: every name reads the same handle-reported sizes
        // (U-010), so a later name is never probed (SL-056, review M4).
        Assert.Equal(1, result.Probes);
        Assert.Equal(0, result.ProbeFailures);

        // Per folder (SL-042). The first name's folder carries the file's true size, the one read through a
        // handle, even when that name's own listing was the stale one (writtenThrough > 0): the correction
        // lands on an entry appended earlier and is posted up its folders. Every other name's folder
        // carries no bytes and one file.
        var copy = new Rolled(64 * MiB, store.OnDisk(charged), 1);
        Assert.Equal(copy, Rolled.Of(store, store.Parent(charged)));
        for (int i = 1; i < names; i++)
            Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, store.Parent(Scans.Find(result, paths[i]))));
        Assert.Equal(copy with { Files = names }, Rolled.Of(store, 0));
    }

    /// <summary>
    /// Documents the operating-system behaviour the probe exists for. If Windows ever keeps every
    /// name's directory entry current, this test fails and the counting tests above still pass.
    /// </summary>
    [Fact]
    public void TheListingOfTheNameNotWrittenThroughIsStale()
    {
        using var tree = new TempTree();
        tree.Folder("stale");
        string written = tree.File("a.bin", 1 * MiB);
        TempTree.Hardlink(tree.PathOf(@"stale\b.bin"), written);
        TempTree.Append(written, 63 * MiB);

        Assert.Equal(64 * MiB, Scans.Listing(tree.Root)["a.bin"].EndOfFile);
        Assert.Equal(1 * MiB, Scans.Listing(tree.PathOf("stale"))["b.bin"].EndOfFile);
    }

    /// <summary>
    /// Once the recorded limitation, now fixed by probing every file (SL-056): the listing has no link count, so
    /// a name whose other names all lie outside the root looks like an ordinary small file, listed here at a
    /// stale 1 MiB, well under the 64 MiB and not a WOF suspect. The rule SL-056 replaced trusted that listing and
    /// charged 1 MiB with no probe; the file's own size is 64 MiB.
    /// </summary>
    [Fact]
    public void ALoneNameUnderTheRootIsChargedAtItsOwnSize()
    {
        using var tree = new TempTree();
        string outside = tree.Outside("other-name");
        string written = TempTree.WriteFile(Path.Combine(outside, "a.bin"), 1 * MiB);
        string lone = TempTree.Hardlink(tree.PathOf("b.bin"), written);
        TempTree.Append(written, 63 * MiB);
        RawEntry listed = Scans.Listing(tree.Root)["b.bin"];
        Assert.True(listed.EndOfFile == 1 * MiB, $"precondition: b.bin should be listed at a stale 1 MiB, but shows {listed.EndOfFile}");

        ScanResult result = Scans.Run(tree.Root);

        int entry = Scans.Find(result, lone);
        Assert.Equal(EntryFlags.SizeProbed | EntryFlags.ProbeCorrected, result.Store.Flags(entry));
        Assert.Equal(64 * MiB, result.Store.Size(entry));
        Assert.Equal(64 * MiB, result.Totals.Bytes);
        Assert.Equal((1, 1), (result.Probes, result.ProbeCorrections));
    }
}
