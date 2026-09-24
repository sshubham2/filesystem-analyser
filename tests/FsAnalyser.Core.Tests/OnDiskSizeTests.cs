using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class OnDiskSizeTests
{
    private const long MiB = TempTree.MiB;
    private const uint Offline = 0x1000;

    [Fact]
    public void SparseCompressedAndPlainFilesTakeTheListingsOnDiskSize()
    {
        using var tree = new TempTree();
        tree.SparseFile("sparse.bin", 64 * MiB, writeOffset: 32 * MiB, writeBytes: 4096);
        TempTree.Compress(tree.TextFile("lznt.txt", 4 * MiB));
        tree.File("plain.bin", 10_000);
        Dictionary<string, RawEntry> listing = Scans.Listing(tree.Root);

        ScanResult result = Scans.Run(tree.Root);

        ScanStore store = result.Store;
        int sparse = Scans.Find(result, tree.PathOf("sparse.bin"));
        Assert.Equal(64 * MiB, store.Size(sparse));
        Assert.InRange(store.OnDisk(sparse), 4096, 1 * MiB);
        // At 64 MiB the sparse file meets the large-file rule and is probed; the probe agrees with the listing.
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(sparse));
        Assert.Equal(listing["sparse.bin"].AllocationSize, store.OnDisk(sparse));

        int lznt = Scans.Find(result, tree.PathOf("lznt.txt"));
        Assert.Equal(4 * MiB, store.Size(lznt));
        Assert.InRange(store.OnDisk(lznt), 1, store.Size(lznt) / 2);

        int plain = Scans.Find(result, tree.PathOf("plain.bin"));
        Assert.Equal(10_000, store.Size(plain));
        Assert.True(store.OnDisk(plain) >= 10_000, $"plain.bin takes {store.OnDisk(plain)} bytes on disk");

        foreach (int entry in new[] { lznt, plain })
            Assert.Equal(EntryFlags.None, store.Flags(entry));
        Assert.Equal(1, result.Probes); // the listing is trusted for the compressed and the plain file
        Assert.Equal(store.OnDisk(sparse) + store.OnDisk(lznt) + store.OnDisk(plain), result.Totals.BytesOnDisk);
    }

    /// <summary>
    /// Documents what the large-file rule rests on. Measured on this PC: once a writer flushes, the
    /// directory entry of a file it still holds open shows the same EndOfFile and AllocationSize as the
    /// file itself. (Between an unflushed write and the lazy writer, the entry lagged: 1 MiB listed
    /// against 64 MiB real, caught up within 2 s. That window is timing-dependent and not asserted.)
    /// </summary>
    [Fact]
    public void TheListingOfAFileHeldOpenIsCurrentOnceTheWriterFlushes()
    {
        using var tree = new TempTree();
        string path = tree.PathOf("open.bin");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        TempTree.Write(writer, 1 * MiB);
        writer.Flush(flushToDisk: true);
        TempTree.Write(writer, 63 * MiB);
        writer.Flush(flushToDisk: true);

        RawEntry listed = Scans.Listing(tree.Root)["open.bin"];
        Assert.Equal(0, SizeProbe.Probe(LongPath.ToExtended(path), out long endOfFile, out long allocationSize));

        Assert.Equal(64 * MiB, endOfFile);
        Assert.Equal(endOfFile, listed.EndOfFile);
        Assert.Equal(allocationSize, listed.AllocationSize);
    }

    /// <summary>
    /// A large file held open and still growing is charged at the file's own size, whatever its
    /// directory entry says at that moment.
    /// </summary>
    [Fact]
    public void ALargeFileHeldOpenAndGrowingIsChargedAtItsTrueSize()
    {
        using var tree = new TempTree();
        string path = tree.PathOf("growing.vhdx");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        TempTree.Write(writer, 64 * MiB);
        writer.Flush(flushToDisk: true);
        TempTree.Write(writer, 64 * MiB); // not flushed: the directory entry may still say 64 MiB

        ScanResult result = Scans.Run(tree.Root);

        ScanStore store = result.Store;
        int entry = Scans.Find(result, path);
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(entry));
        Assert.Equal(128 * MiB, store.Size(entry));
        Assert.True(store.OnDisk(entry) >= 128 * MiB, $"growing.vhdx takes {store.OnDisk(entry)} bytes on disk");
        Assert.Equal(128 * MiB, result.Totals.Bytes);
        Assert.Equal(1, result.Probes);
    }

    /// <summary>
    /// The probe guard. The listing says 0 allocated for all of the first four; only the WOF files are
    /// probed. An all-hole sparse file really has nothing allocated, OFFLINE stands in for a cloud
    /// placeholder that must never be opened, and a resident file takes no clusters.
    /// </summary>
    [Fact]
    public void AFileWhoseListingSaysNothingIsAllocatedIsProbedOnlyWhenItIsWof()
    {
        using var tree = new TempTree();
        string wof = tree.TextFile("wof.txt", 4 * MiB);
        TempTree.WofCompress(wof);
        tree.SparseFile("all-hole.bin", 16 * MiB); // under the large-file threshold, so only the sparse guard decides
        string offline = tree.TextFile("offline.txt", 1 * MiB);
        TempTree.WofCompress(offline); // 0 allocated and not sparse: a WOF suspect but for OFFLINE
        TempTree.AddAttributes(offline, FileAttributes.Offline);
        tree.File("resident.bin", 200);
        string denied = tree.TextFile("wof-denied.txt", 1 * MiB);
        TempTree.WofCompress(denied);
        tree.Deny(denied, 0x100080); // SYNCHRONIZE | FILE_READ_ATTRIBUTES: the probe's open fails

        Dictionary<string, RawEntry> listing = Scans.Listing(tree.Root);
        foreach (string name in new[] { "wof.txt", "all-hole.bin", "offline.txt", "wof-denied.txt" })
        {
            RawEntry listed = listing[name];
            Assert.True(listed.AllocationSize == 0 && listed.EndOfFile >= 4096,
                $"precondition: {name} should be listed as 0 allocated, but shows {listed.AllocationSize} of {listed.EndOfFile}");
        }
        Assert.NotEqual(0u, listing["offline.txt"].Attributes & Offline);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        ScanStore store = result.Store;
        Assert.Equal(2, result.Probes); // wof.txt and wof-denied.txt
        Assert.Equal(1, result.ProbeFailures);

        int probed = Scans.Find(result, wof);
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(probed));
        Assert.Equal(4 * MiB, store.Size(probed));
        Assert.InRange(store.OnDisk(probed), 1, 2 * MiB);

        int hole = Scans.Find(result, tree.PathOf("all-hole.bin"));
        Assert.Equal(16 * MiB, store.Size(hole));
        Assert.Equal(0, store.OnDisk(hole));
        Assert.Equal(EntryFlags.None, store.Flags(hole));

        int cloud = Scans.Find(result, offline);
        Assert.Equal(EntryFlags.CloudOnly, store.Flags(cloud)); // never opened
        Assert.Equal(0, store.OnDisk(cloud));

        int resident = Scans.Find(result, tree.PathOf("resident.bin"));
        Assert.Equal(200, store.Size(resident));
        Assert.Equal(listing["resident.bin"].AllocationSize, store.OnDisk(resident));
        Assert.True(store.OnDisk(resident) < 4096, $"resident.bin takes {store.OnDisk(resident)} bytes on disk");
        Assert.Equal(EntryFlags.None, store.Flags(resident));

        int failed = Scans.Find(result, denied);
        Assert.Equal(EntryFlags.ProbeFailed, store.Flags(failed));
        Assert.Equal(1 * MiB, store.Size(failed));
        Assert.Equal(0, store.OnDisk(failed)); // the listing value, kept

        Assert.Equal(store.OnDisk(probed) + store.OnDisk(resident), result.Totals.BytesOnDisk);
    }
}
