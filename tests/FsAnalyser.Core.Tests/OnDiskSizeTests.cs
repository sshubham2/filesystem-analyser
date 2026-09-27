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
        // Every file is probed (SL-056); for a closed file the probe agrees with the listing and corrects nothing.
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(sparse));
        Assert.Equal(listing["sparse.bin"].AllocationSize, store.OnDisk(sparse));

        int lznt = Scans.Find(result, tree.PathOf("lznt.txt"));
        Assert.Equal(4 * MiB, store.Size(lznt));
        Assert.InRange(store.OnDisk(lznt), 1, store.Size(lznt) / 2);

        int plain = Scans.Find(result, tree.PathOf("plain.bin"));
        Assert.Equal(10_000, store.Size(plain));
        Assert.True(store.OnDisk(plain) >= 10_000, $"plain.bin takes {store.OnDisk(plain)} bytes on disk");

        foreach ((int entry, string name) in new[] { (lznt, "lznt.txt"), (plain, "plain.bin") })
        {
            Assert.Equal(EntryFlags.SizeProbed, store.Flags(entry));
            Assert.Equal(listing[name].AllocationSize, store.OnDisk(entry));
        }
        Assert.Equal(3, result.Probes); // the small plain file too: no listing figure says when it can be trusted
        Assert.Equal(0, result.ProbeCorrections);
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
        using var io = new SynchronousIoCancel(CancellationToken.None);
        Assert.Equal(0, SizeProbe.Probe(io, LongPath.ToExtended(path), out long endOfFile, out long allocationSize));

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
        // Whether the entry had caught up by the time it was listed is timing-dependent, so ProbeCorrected is not asserted.
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(entry) & (EntryFlags.SizeProbed | EntryFlags.ProbeFailed));
        Assert.Equal(128 * MiB, store.Size(entry));
        Assert.True(store.OnDisk(entry) >= 128 * MiB, $"growing.vhdx takes {store.OnDisk(entry)} bytes on disk");
        Assert.Equal(128 * MiB, result.Totals.Bytes);
        Assert.Equal(1, result.Probes);
    }

    /// <summary>
    /// The shape SL-056 exists for, at its smallest: a file well under 64 MiB, not a WOF suspect, that its
    /// directory entry still lists at 0 B because it was created and written through a handle still open. The rule
    /// SL-056 replaced trusted that listing and charged 0; every file is now charged at what it reports itself.
    /// </summary>
    [Fact]
    public void ASmallFileWhoseListingSaysNothingIsChargedAtItsOwnSize()
    {
        using var tree = new TempTree();
        string path = tree.PathOf("small-open.bin");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        TempTree.Write(writer, 3 * MiB); // never flushed: the directory entry keeps the size it was created with
        RawEntry listed = Scans.Listing(tree.Root)["small-open.bin"];
        Assert.True(listed.EndOfFile == 0 && listed.AllocationSize == 0,
            $"precondition: small-open.bin should still be listed at 0 B, but shows {listed.EndOfFile} ({listed.AllocationSize} allocated)");

        ScanResult result = Scans.Run(tree.Root);

        ScanStore store = result.Store;
        int entry = Scans.Find(result, path);
        Assert.Equal(EntryFlags.SizeProbed | EntryFlags.ProbeCorrected, store.Flags(entry));
        Assert.Equal(3 * MiB, store.Size(entry));
        Assert.True(store.OnDisk(entry) >= 3 * MiB, $"small-open.bin takes {store.OnDisk(entry)} bytes on disk");
        Assert.Equal(new ScanTotals(Files: 1, Folders: 0, Links: 0, Bytes: 3 * MiB, BytesOnDisk: store.OnDisk(entry)), result.Totals);
        Assert.Equal((1, 1, 0), (result.Probes, result.ProbeCorrections, result.ProbeFailures));
    }

    /// <summary>
    /// The probe guard (SL-056: every file is probed, and the only guard left is cloud-only). The listing says 0
    /// allocated for all of the first four. The WOF file is probed and corrected. The all-hole sparse file and the
    /// resident file are probed too, and the probe agrees that nothing is allocated. OFFLINE stands in for a cloud
    /// placeholder, which is never opened, whether it is small or listed at 64 MiB. A file whose probe is refused
    /// keeps its listing sizes (criterion 5).
    /// </summary>
    [Fact]
    public void AFileWhoseListingSaysNothingIsAllocatedIsProbedUnlessItIsCloudOnly()
    {
        using var tree = new TempTree();
        string wof = tree.TextFile("wof.txt", 4 * MiB);
        TempTree.WofCompress(wof);
        tree.SparseFile("all-hole.bin", 16 * MiB); // really nothing allocated: the probe must agree, not "correct" it
        string offline = tree.TextFile("offline.txt", 1 * MiB);
        TempTree.WofCompress(offline); // 0 allocated and not sparse, like the WOF file, but OFFLINE
        TempTree.AddAttributes(offline, FileAttributes.Offline);
        // A placeholder listed at 64 MiB with nothing allocated, like an online-only OneDrive file: the size the
        // old large-file rule probed at, which must not reach the probe either.
        string offlineLarge = tree.SparseFile("offline-large.bin", 64 * MiB);
        TempTree.AddAttributes(offlineLarge, FileAttributes.Offline);
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
        RawEntry large = listing["offline-large.bin"];
        Assert.True(large.EndOfFile >= 64 * MiB && (large.Attributes & Offline) != 0,
            $"precondition: offline-large.bin should be listed OFFLINE at 64 MiB or more, but shows {large.EndOfFile} with 0x{large.Attributes:X}");

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        ScanStore store = result.Store;
        Assert.Equal(4, result.Probes); // wof.txt, all-hole.bin, resident.bin and wof-denied.txt; never the two OFFLINE files
        Assert.Equal(1, result.ProbeFailures);
        Assert.Equal(1, result.ProbeCorrections); // wof.txt only
        Assert.Equal(0, result.VanishedBeforeProbe);

        int probed = Scans.Find(result, wof);
        Assert.Equal(EntryFlags.SizeProbed | EntryFlags.ProbeCorrected, store.Flags(probed));
        Assert.Equal(4 * MiB, store.Size(probed));
        Assert.InRange(store.OnDisk(probed), 1, 2 * MiB);

        int hole = Scans.Find(result, tree.PathOf("all-hole.bin"));
        Assert.Equal(16 * MiB, store.Size(hole));
        Assert.Equal(0, store.OnDisk(hole));
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(hole)); // probed, and it agreed: nothing allocated

        int cloud = Scans.Find(result, offline);
        Assert.Equal(EntryFlags.CloudOnly, store.Flags(cloud)); // never opened
        Assert.Equal(0, store.OnDisk(cloud));

        int cloudLarge = Scans.Find(result, offlineLarge);
        Assert.Equal(EntryFlags.CloudOnly, store.Flags(cloudLarge)); // CloudOnly, not SizeProbed: never opened
        Assert.Equal(64 * MiB, store.Size(cloudLarge));
        Assert.Equal(0, store.OnDisk(cloudLarge));

        int resident = Scans.Find(result, tree.PathOf("resident.bin"));
        Assert.Equal(200, store.Size(resident));
        Assert.Equal(listing["resident.bin"].AllocationSize, store.OnDisk(resident));
        Assert.True(store.OnDisk(resident) < 4096, $"resident.bin takes {store.OnDisk(resident)} bytes on disk");
        Assert.Equal(EntryFlags.SizeProbed, store.Flags(resident)); // probed, and it agreed with its listing

        int failed = Scans.Find(result, denied);
        Assert.Equal(EntryFlags.ProbeFailed, store.Flags(failed)); // criterion 5: refused, so neither SizeProbed nor corrected
        Assert.Equal(1 * MiB, store.Size(failed));
        Assert.Equal(0, store.OnDisk(failed)); // the listing value, kept

        Assert.Equal(store.OnDisk(probed) + store.OnDisk(resident), result.Totals.BytesOnDisk);
    }
}
