using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The link-or-data rule (SL-043, U-004): a link is a reparse point whose tag has the name-surrogate bit,
/// 0x20000000. Tabled from the reparse spike's tally of C: on this PC (every tag it met), the four cloud
/// tags and tag 0 that 1.11M OneDrive entries carry, and the attributes that mark a cloud placeholder under
/// placeholder mode 1.
/// </summary>
public class EntryClassifierTests
{
    private const uint Dir = 0x10, Archive = 0x20, Reparse = 0x400;
    private const uint Offline = 0x1000, RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;

    [Theory]
    [InlineData(Dir | Reparse, 0xA0000003u, true)]      // junction / mount point
    [InlineData(Dir | Reparse, 0xA000000Cu, true)]      // directory symlink
    [InlineData(Archive | Reparse, 0xA000000Cu, false)] // file symlink
    [InlineData(Archive | Reparse, 0xA000001Du, false)] // LX_SYMLINK: a File under the old tag list
    [InlineData(Dir | Reparse, 0xA0000010u, true)]      // IIS_CACHE: a Folder, entered, under the old tag list
    [InlineData(Dir | Reparse, 0xA0000027u, true)]      // WCI_LINK
    [InlineData(Archive | Reparse | RecallOnDataAccess, 0xA000000Cu, false)] // a link is never a cloud file
    public void ANameSurrogateTagWithTheReparseAttributeIsALink(uint attributes, uint tag, bool isDirectory)
    {
        Assert.Equal((EntryKind.Link, false), EntryClassifier.Classify(attributes, tag, isDirectory));
    }

    [Theory]
    [InlineData(Dir | Reparse, 0x9000201Au, true, EntryKind.Folder)]    // CLOUD_2 (OneDrive)
    [InlineData(Archive | Reparse, 0x9000201Au, false, EntryKind.File)]
    [InlineData(Dir | Reparse, 0x9000401Au, true, EntryKind.Folder)]    // CLOUD_4
    [InlineData(Archive | Reparse, 0x9000401Au, false, EntryKind.File)]
    [InlineData(Dir | Reparse, 0x9000601Au, true, EntryKind.Folder)]    // CLOUD_6
    [InlineData(Archive | Reparse, 0x9000601Au, false, EntryKind.File)]
    [InlineData(Dir | Reparse, 0x9000E01Au, true, EntryKind.Folder)]    // CLOUD_E
    [InlineData(Archive | Reparse, 0x9000E01Au, false, EntryKind.File)]
    [InlineData(Dir | Reparse, 0u, true, EntryKind.Folder)]             // the reparse attribute with tag 0
    [InlineData(Archive | Reparse, 0u, false, EntryKind.File)]
    [InlineData(Dir | Reparse, 0x8000000Au, true, EntryKind.Folder)]    // DFS: a Link under the old tag list
    [InlineData(Archive | Reparse, 0x8000000Au, false, EntryKind.File)]
    public void ANonSurrogateTagWithTheReparseAttributeIsData(uint attributes, uint tag, bool isDirectory, EntryKind kind)
    {
        Assert.Equal((kind, false), EntryClassifier.Classify(attributes, tag, isDirectory));
    }

    // On the fallback listing class the tag field is EaSize whenever the reparse attribute is clear, and it
    // can hold anything. Reading it first would make such a folder a link and drop its whole subtree.
    [Theory]
    [InlineData(Dir, 0x20000000u, true, EntryKind.Folder)]
    [InlineData(Archive, 0x20000000u, false, EntryKind.File)]
    [InlineData(Dir, 0xA0000003u, true, EntryKind.Folder)]
    [InlineData(Archive, 0xA000000Cu, false, EntryKind.File)]
    [InlineData(Dir, 0xFFFFFFFFu, true, EntryKind.Folder)]
    [InlineData(Archive, 0u, false, EntryKind.File)] // plain, or WOF as listed
    public void WithoutTheReparseAttributeTheTagFieldIsNeverRead(uint attributes, uint tag, bool isDirectory, EntryKind kind)
    {
        Assert.Equal((kind, false), EntryClassifier.Classify(attributes, tag, isDirectory));
    }

    [Theory]
    // Reparse files that are data.
    [InlineData(Archive | Reparse, 0x8000001Bu, false, EntryKind.File, false)] // APPEXECLINK
    [InlineData(Archive | Reparse, 0x80000023u, false, EntryKind.File, false)] // AF_UNIX
    [InlineData(Archive | Reparse, 0x80000013u, false, EntryKind.File, false)] // DEDUP
    [InlineData(Archive | Reparse, 0x80000017u, false, EntryKind.File, false)] // WOF, if ever visible
    [InlineData(Archive | Reparse, 0x9000001Au, false, EntryKind.File, false)] // CLOUD, hydrated
    // Reparse folders that are entered.
    [InlineData(Dir | Reparse, 0x9000001Au, true, EntryKind.Folder, false)]    // CLOUD (OneDrive root)
    [InlineData(Dir | Reparse, 0x9000701Au, true, EntryKind.Folder, false)]    // CLOUD_7
    [InlineData(Dir | Reparse, 0x80000018u, true, EntryKind.Folder, false)]    // WCI
    [InlineData(Dir | Reparse, 0x9000001Cu, true, EntryKind.Folder, false)]    // PROJFS
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
