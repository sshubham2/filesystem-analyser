using System.Runtime.InteropServices;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A folder the scan lists keeps its last-access time where the folder and its volume let it (SL-056, criterion
/// 6's folder half): the listing handle asks for FILE_WRITE_ATTRIBUTES and is given LastAccessTime -1 before
/// anything is listed through it. Where the folder refuses, it is listed as before, and where the volume is not
/// local NTFS, or a sync engine manages the folder, nothing is asked (review M6). HeldOpenTreeTests checks the
/// whole of criterion 6 through another process; these check the mechanism.
/// </summary>
public class LastAccessTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>
    /// The mechanism alone, against its own control: the same listing call on two back-dated folders, one asked
    /// to keep the time and one not. Only the second one's last-access time moves.
    /// </summary>
    [Fact]
    public void AListingThatKeepsTheLastAccessTimeLeavesItWhereAPlainOneMovesIt()
    {
        using var tree = new TempTree();
        string kept = tree.Folder("kept");
        tree.File(@"kept\a.txt", 10);
        string plain = tree.Folder("plain");
        tree.File(@"plain\b.txt", 10);
        DateTime old = DateTime.UtcNow.AddHours(-48);
        Directory.SetLastAccessTimeUtc(kept, old);
        Directory.SetLastAccessTimeUtc(plain, old);

        using var io = new SynchronousIoCancel(CancellationToken.None);
        using var reader = new DirectoryReader(io);
        Assert.Equal(0, reader.Read(LongPath.ToExtended(kept), fullClass: false, keepLastAccess: true));
        Assert.True(io.KeptLastAccess);
        Assert.Equal(1, reader.Count);
        Assert.Equal(0, reader.Read(LongPath.ToExtended(plain), fullClass: false, keepLastAccess: false));
        Assert.False(io.KeptLastAccess);
        Assert.Equal(1, reader.Count);

        Assert.Equal(old, Directory.GetLastAccessTimeUtc(kept));
        DateTime moved = Directory.GetLastAccessTimeUtc(plain);
        Assert.True(moved > old + Hour, $"the plain listing left plain's last access at {moved:u}: this machine may record no access, and the kept one proves nothing");
    }

    /// <summary>
    /// A folder that refuses FILE_WRITE_ATTRIBUTES, as most of C:\Windows does to an ordinary user, is listed through
    /// the plain open: counted in full, and not unreadable (review M7). Its last-access time is not kept there, the
    /// limit of criterion 6, and the scan's counters say so. Its subfolder does not inherit the deny and keeps its time.
    /// </summary>
    [Fact]
    public void AFolderThatRefusesWriteAttributesIsListedPlainlyAndCountedInFull()
    {
        using var tree = new TempTree();
        tree.Folder("open");
        tree.File(@"open\a.txt", 10);
        tree.Folder(@"refusing\sub");
        string refusing = tree.PathOf("refusing");
        tree.File(@"refusing\b.txt", 20);
        tree.File(@"refusing\sub\c.txt", 30);
        tree.Deny(refusing, 0x100); // FILE_WRITE_ATTRIBUTES, no inheritance: only this folder refuses
        // Back-dated after the deny, whose inheritance propagation (SetNamedSecurityInfo) lists the folders below it.
        // refusing itself cannot be back-dated now, so whether its own time moved is not what shows the fallback:
        // the counters below are. The root goes last, after its children (SL-096): where a child's write-attributes open moves
        // its parent's last-access time, as under the Windows folder, back-dating them would reset it.
        DateTime old = DateTime.UtcNow.AddHours(-48);
        string[] kept = [tree.PathOf("open"), tree.PathOf(@"refusing\sub"), tree.Root];
        foreach (string keeping in kept)
            Directory.SetLastAccessTimeUtc(keeping, old);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Empty(result.Unreadable);
        ScanStore store = result.Store;
        int folder = Scans.Find(result, refusing);
        Assert.Equal(EntryFlags.None, store.Flags(folder));
        Assert.Equal(2, store.ChildCount(folder));
        Assert.Equal(new Rolled(50, store.RolledOnDisk(folder), 2), Rolled.Of(store, folder));
        Assert.Equal(new ScanTotals(Files: 3, Folders: 3, Links: 0, Bytes: 60, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
        // Asked for the root, open, refusing and refusing\sub, and kept by three of them, whose times did not move:
        // so refusing alone refused, and was listed plainly.
        Assert.Equal((4, 3), (result.LastAccessAsked, result.LastAccessKept));
        foreach (string keeping in kept)
            Assert.Equal(old, Directory.GetLastAccessTimeUtc(keeping));
    }

    /// <summary>
    /// A folder a sync engine manages is never asked to keep its last-access time (review M6): the set was measured
    /// only on plain local NTFS, and there it could reach the engine. The OneDrive folders the environment names are
    /// the scan's sync roots; this one is named through ScanOptions.
    /// </summary>
    [Fact]
    public void NothingUnderASyncRootIsAskedToKeepItsLastAccessTime()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 1);
        tree.Folder(@"synced\inner");
        tree.File(@"synced\inner\two.txt", 2);

        ScanResult result = Scans.Run(tree.Root, new ScanOptions { SyncRoots = [tree.PathOf("synced")] });

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(3, result.Totals.Bytes); // listed and counted as before
        Assert.Equal((2, 2), (result.LastAccessAsked, result.LastAccessKept)); // the root and a; not synced or synced\inner
    }

    /// <summary>
    /// A folder a sync engine marks is never asked to keep its last-access time (review M6, SL-102): the gate is the
    /// folder's own listed attributes, so the marked folder is left out while it is still entered and what is under
    /// it is still counted and still asked. One row per bit a user can set; the two recall bits cannot be set from
    /// user mode. The rows are literals, not read from the mask under test, and Pinned and Unpinned are numbers
    /// because System.IO.FileAttributes names neither.
    /// </summary>
    [Theory]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x80000)] // FILE_ATTRIBUTE_PINNED
    [InlineData((FileAttributes)0x100000)] // FILE_ATTRIBUTE_UNPINNED
    public void AFolderWithACloudAttributeIsNotAskedToKeepItsLastAccessTimeButIsStillEntered(FileAttributes attribute)
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 1);
        tree.Folder(@"off\inner");
        tree.File(@"off\one.txt", 1);
        tree.File(@"off\inner\two.txt", 2);
        TempTree.AddAttributes(tree.PathOf("off"), attribute);
        // The bit must show in the listing the scan reads, or the row would pass without the gate being reached.
        Assert.Equal(attribute, (FileAttributes)Scans.Listing(tree.Root)["off"].Attributes & attribute);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(4, result.Totals.Bytes); // off is entered and everything under it counted
        Assert.Equal(3, result.LastAccessAsked); // the root, a and off\inner; not off
    }

    /// <summary>
    /// A share whose server calls itself NTFS is never asked to keep a last-access time (review M6, SL-102): only
    /// the FileRemoteProtocolInfo query tells it from a local volume, and without it every folder of the share would
    /// get a write-attributes open and a SetFileInformationByHandle. This PC's administrative share, as
    /// ContainerLayerStoreTests scans it: a plain tree, since a marked folder is left out by the attribute gate alone
    /// and would hide part of the remote gate.
    /// </summary>
    [Fact]
    public void NothingOnThisPcsAdminShareIsAskedToKeepItsLastAccessTime()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 1);
        tree.Folder(@"b\inner");
        tree.File(@"b\one.txt", 1);
        tree.File(@"b\inner\two.txt", 2);

        foreach (string host in new[] { "localhost", "127.0.0.1" })
        {
            string share = $@"\\{host}\{tree.Root[0]}$\{tree.Root[3..]}";
            if (!Directory.Exists(share))
                continue; // administrative shares are off on this PC: nothing to scan through

            ScanResult result = Scans.Run(share);

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(4, result.Totals.Bytes);
            Assert.Equal((0, 0), (result.LastAccessAsked, result.LastAccessKept)); // under the gate removed: 4 and 4
        }
    }

    [Theory]
    [InlineData("NTFS", false, true)]
    [InlineData("ntfs", false, true)]
    [InlineData("NTFS", true, false)] // a share whose server calls itself NTFS
    [InlineData("ReFS", false, false)]
    [InlineData("FAT32", false, false)]
    [InlineData("exFAT", false, false)]
    [InlineData(null, false, false)] // the volume did not say
    public void OnlyALocalNtfsVolumeKeepsLastAccessTimes(string? fileSystem, bool remote, bool keeps)
    {
        Assert.Equal(keeps, Scanner.MayKeepLastAccess(fileSystem, remote));
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\x\OneDrive", @"\\?\C:\Users\x\OneDrive", true)]
    [InlineData(@"\\?\C:\Users\x\OneDrive\Documents", @"\\?\C:\Users\x\OneDrive", true)]
    [InlineData(@"\\?\C:\Users\x\onedrive\Documents", @"\\?\C:\Users\x\OneDrive", true)]
    [InlineData(@"\\?\C:\Users\x\OneDrive - Work", @"\\?\C:\Users\x\OneDrive", false)]
    [InlineData(@"\\?\C:\Users\x", @"\\?\C:\Users\x\OneDrive", false)]
    [InlineData(@"\\?\D:\anything", @"\\?\D:\", true)]
    public void AFolderIsUnderASyncRootOnlyAtASeparator(string folder, string root, bool under)
    {
        Assert.Equal(under, Scanner.IsAtOrUnder(folder, root));
    }

    /// <summary>
    /// The one payload the scanner writes to a folder (review m7): FILE_BASIC_INFO is 40 bytes on x64 with the
    /// last-access time at offset 8. SynchronousIoCancel checks the same when it loads, and refuses to run otherwise.
    /// </summary>
    [Fact]
    public void TheBasicInfoPayloadHasTheLayoutWindowsReads()
    {
        Assert.Equal(40, Kernel32.FileBasicInfoSize);
        Assert.Equal(40, Marshal.SizeOf<Kernel32.FILE_BASIC_INFO>());
        Assert.Equal(8, (int)Marshal.OffsetOf<Kernel32.FILE_BASIC_INFO>(nameof(Kernel32.FILE_BASIC_INFO.LastAccessTime)));
        Assert.Equal(32, (int)Marshal.OffsetOf<Kernel32.FILE_BASIC_INFO>(nameof(Kernel32.FILE_BASIC_INFO.FileAttributes)));
    }
}
