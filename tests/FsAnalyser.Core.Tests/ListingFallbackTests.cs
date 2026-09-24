using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// FAT, exFAT and some SMB volumes have no FILE_ID_EXTD_DIR_INFO and cannot be made unelevated, so the
/// fallback class is forced over a real NTFS tree: tag from EaSize, name at +68, no FileId.
/// </summary>
public class ListingFallbackTests
{
    [Fact]
    public void TheFullDirectoryClassCountsTheSameWithoutFileIds()
    {
        using var tree = new TempTree();
        string sub = tree.Folder("sub");
        string x = tree.File(@"sub\x.txt", 1000);
        TempTree.Hardlink(tree.PathOf(@"sub\y.txt"), x);
        tree.Folder(@"sub\inner");
        tree.File(@"sub\inner\z.txt", 50);
        tree.Junction("j", sub);
        tree.DirectorySymlink("d", sub);
        tree.FileSymlink("f", x);

        ScanResult normal = Scans.Run(tree.Root);
        ScanResult fallback = Scans.Run(tree.Root, new ScanOptions { ForceFullDirectoryClass = true });

        Assert.Equal(ScanStatus.Completed, fallback.Status);
        Assert.True(normal.FileIdsAvailable);
        Assert.False(fallback.FileIdsAvailable);

        Dictionary<string, int> expected = Scans.ByPath(normal.Store);
        Dictionary<string, int> actual = Scans.ByPath(fallback.Store);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, int n) in expected)
        {
            int f = actual[path];
            Assert.Equal(normal.Store.Kind(n), fallback.Store.Kind(f));
            Assert.Equal(normal.Store.Size(n), fallback.Store.Size(f));
            Assert.Equal(UInt128.Zero, fallback.Store.FileId(f));
            Assert.False(fallback.Store.Flags(f).HasFlag(EntryFlags.HardlinkDuplicate));
        }

        Assert.Equal(new ScanTotals(Files: 3, Folders: 2, Links: 3, Bytes: 1050, BytesOnDisk: normal.Totals.BytesOnDisk), normal.Totals);
        Assert.Equal(normal.Totals with { Bytes = 2050, BytesOnDisk = fallback.Totals.BytesOnDisk }, fallback.Totals);
        Assert.Empty(Scans.UnderLinks(fallback.Store));
    }
}
