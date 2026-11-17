using FsAnalyser.Core.Analysis;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The files whose own size the scan could not read, as the coverage panel reports them apart from the unseen space (SL-001): the
/// two 64 KiB files whose size probe is refused (deny SYNCHRONIZE and FILE_READ_ATTRIBUTES), and a clean tree where every size
/// was read.
/// </summary>
public class SizeNotReadTests
{
    private const long Kib64 = 64 * 1024;
    private const uint SynchronizeAndReadAttributes = 0x100080; // the probe's open fails: the listing size is kept

    [Fact]
    public void TwoFilesWhoseProbeIsRefusedAreCountedAtTheirListingSizeInsideWhatTheScanSaw()
    {
        using var tree = new TempTree();
        string a = tree.File("denied-a.bin", Kib64);
        string b = tree.File("denied-b.bin", Kib64);
        tree.File("read.bin", Kib64);
        tree.Deny(a, SynchronizeAndReadAttributes);
        tree.Deny(b, SynchronizeAndReadAttributes);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(2, result.ProbeFailures);
        Assert.Equal(2, ProbeFailedFiles(result.Store));
        Assert.Equal(2 * Kib64, SizeNotRead.OnDisk(result.Store));
        // Those bytes are inside what the scan counted, so they cannot be inside the space it did not see.
        Assert.Equal(3 * Kib64, result.Totals.BytesOnDisk);
        Assert.True(SizeNotRead.OnDisk(result.Store) <= result.Totals.BytesOnDisk);
    }

    [Fact]
    public void ACleanTreeHasNoFileWhoseSizeWasNotRead()
    {
        using var tree = new TempTree();
        tree.File("a.bin", Kib64);
        tree.Folder("sub");
        tree.File(@"sub\b.bin", Kib64);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(0, result.ProbeFailures);
        Assert.Equal(0, ProbeFailedFiles(result.Store));
        Assert.Equal(0, SizeNotRead.OnDisk(result.Store));
    }

    /// <summary>The count the panel and the note show (ProbeFailures) is the count of files flagged ProbeFailed, on the WOF-denied fixture too.</summary>
    [Fact]
    public void TheCountOfProbeFailedFilesIsTheProbeFailuresTheNoteShowsOnTheWofDeniedFixture()
    {
        using var tree = new TempTree();
        string denied = tree.TextFile("wof-denied.txt", 1024 * 1024);
        TempTree.WofCompress(denied);
        tree.Deny(denied, SynchronizeAndReadAttributes);
        tree.File("plain.bin", Kib64);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(1, result.ProbeFailures);
        Assert.Equal(result.ProbeFailures, ProbeFailedFiles(result.Store));
        Assert.Equal(result.Store.ChargedOnDisk(Scans.Find(result, denied)), SizeNotRead.OnDisk(result.Store));
    }

    [Fact]
    public void ALaterNameOfAHardlinkedFileAddsNothingAsEverywhereElse()
    {
        using var tree = new TempTree();
        string first = tree.File("first.bin", Kib64);
        TempTree.Hardlink(tree.PathOf("second.bin"), first);
        tree.Deny(first, SynchronizeAndReadAttributes); // the ACL is the file's, so both names are refused

        ScanResult result = Scans.Run(tree.Root);

        Assert.True(result.FileIdsAvailable);
        Assert.Equal(Kib64, result.Totals.BytesOnDisk);
        Assert.Equal(Kib64, SizeNotRead.OnDisk(result.Store)); // the one charge, not once per name
    }

    private static int ProbeFailedFiles(ScanStore store) =>
        Enumerable.Range(0, store.Count).Count(i => store.Kind(i) == EntryKind.File && (store.Flags(i) & EntryFlags.ProbeFailed) != 0);
}
