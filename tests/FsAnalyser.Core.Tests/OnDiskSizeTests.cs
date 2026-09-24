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

        ScanResult result = Scans.Run(tree.Root);

        ScanStore store = result.Store;
        int sparse = Scans.Find(result, tree.PathOf("sparse.bin"));
        Assert.Equal(64 * MiB, store.Size(sparse));
        Assert.InRange(store.OnDisk(sparse), 4096, 1 * MiB);

        int lznt = Scans.Find(result, tree.PathOf("lznt.txt"));
        Assert.Equal(4 * MiB, store.Size(lznt));
        Assert.InRange(store.OnDisk(lznt), 1, store.Size(lznt) / 2);

        int plain = Scans.Find(result, tree.PathOf("plain.bin"));
        Assert.Equal(10_000, store.Size(plain));
        Assert.True(store.OnDisk(plain) >= 10_000, $"plain.bin takes {store.OnDisk(plain)} bytes on disk");

        foreach (int entry in new[] { sparse, lznt, plain })
            Assert.Equal(EntryFlags.None, store.Flags(entry));
        Assert.Equal(0, result.Probes); // the listing is trusted for these
        Assert.Equal(store.OnDisk(sparse) + store.OnDisk(lznt) + store.OnDisk(plain), result.Totals.BytesOnDisk);
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
        tree.SparseFile("all-hole.bin", 64 * MiB);
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
        Assert.Equal(64 * MiB, store.Size(hole));
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
