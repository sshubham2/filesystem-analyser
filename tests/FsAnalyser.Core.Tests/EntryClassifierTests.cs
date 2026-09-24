using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The link-or-data rule, tabled from the reparse spike's tally of C: on this PC (every tag it met)
/// plus the attributes that mark a cloud placeholder under placeholder mode 1.
/// </summary>
public class EntryClassifierTests
{
    private const uint Dir = 0x10, Archive = 0x20, Reparse = 0x400;
    private const uint Offline = 0x1000, RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;

    [Theory]
    // Links: the only two tags that are never entered or charged.
    [InlineData(Dir | Reparse, 0xA0000003u, true, EntryKind.Link, false)]      // junction / mount point
    [InlineData(Dir | Reparse, 0xA000000Cu, true, EntryKind.Link, false)]      // directory symlink
    [InlineData(Archive | Reparse, 0xA000000Cu, false, EntryKind.Link, false)] // file symlink
    // Reparse files that are data.
    [InlineData(Archive | Reparse, 0x8000001Bu, false, EntryKind.File, false)] // APPEXECLINK
    [InlineData(Archive | Reparse, 0x80000023u, false, EntryKind.File, false)] // AF_UNIX
    [InlineData(Archive | Reparse, 0xA000001Du, false, EntryKind.File, false)] // LX_SYMLINK
    [InlineData(Archive | Reparse, 0x80000013u, false, EntryKind.File, false)] // DEDUP
    [InlineData(Archive | Reparse, 0x80000017u, false, EntryKind.File, false)] // WOF, if ever visible
    [InlineData(Archive | Reparse, 0x9000001Au, false, EntryKind.File, false)] // CLOUD, hydrated
    // Reparse folders that are entered.
    [InlineData(Dir | Reparse, 0x9000001Au, true, EntryKind.Folder, false)]    // CLOUD (OneDrive root)
    [InlineData(Dir | Reparse, 0x9000701Au, true, EntryKind.Folder, false)]    // CLOUD_7
    [InlineData(Dir | Reparse, 0x80000018u, true, EntryKind.Folder, false)]    // WCI
    [InlineData(Dir | Reparse, 0x9000001Cu, true, EntryKind.Folder, false)]    // PROJFS
    // No reparse bit: the tag field means nothing (on the fallback class it is EaSize).
    [InlineData(Archive, 0u, false, EntryKind.File, false)]                    // plain, or WOF as listed
    [InlineData(Archive, 0xA000000Cu, false, EntryKind.File, false)]
    [InlineData(Dir, 0xA0000003u, true, EntryKind.Folder, false)]
    // Cloud placeholders: data not local, never opened.
    [InlineData(Archive | RecallOnDataAccess, 0u, false, EntryKind.File, true)]
    [InlineData(Archive | RecallOnOpen, 0u, false, EntryKind.File, true)]
    [InlineData(Archive | Offline, 0u, false, EntryKind.File, true)]
    [InlineData(Archive | Reparse | RecallOnDataAccess, 0x9000001Au, false, EntryKind.File, true)]
    [InlineData(Dir | Reparse | RecallOnOpen, 0x9000001Au, true, EntryKind.Folder, false)]
    public void ClassifiesByTagNotByTheReparseBit(uint attributes, uint tag, bool isDirectory, EntryKind kind, bool cloudOnly)
    {
        Assert.Equal((kind, cloudOnly), EntryClassifier.Classify(attributes, tag, isDirectory));
    }
}
