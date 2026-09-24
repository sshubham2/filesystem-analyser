using FsAnalyser.Core.Native;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Decides what a listed entry is. This is the only place that reads the reparse attribute: a reparse
/// point is a link only when its tag is a junction/mount point (0xA0000003), a symbolic link
/// (0xA000000C) or a DFS link (0x8000000A, which leads to another server's volume). Every other reparse
/// folder (cloud, WCI, ProjFS) is entered, and every other reparse file (AppExecLink, AF_UNIX, WSL
/// symlink, dedup) is a file. WOF files carry no reparse bit at all.
/// </summary>
public static class EntryClassifier
{
    private const uint CloudAttributes =
        Kernel32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Kernel32.FILE_ATTRIBUTE_RECALL_ON_OPEN | Kernel32.FILE_ATTRIBUTE_OFFLINE;

    /// <param name="attributes">Win32 file attributes from the listing.</param>
    /// <param name="reparseTag">The listing's reparse tag. Only meaningful when the reparse attribute is set;
    /// on the fallback listing class it is the EaSize field, which holds the tag only for reparse points.</param>
    /// <param name="isDirectory">Whether the listing marks the entry as a directory.</param>
    /// <returns>The kind, and whether a file's data is not local (a cloud placeholder under placeholder
    /// mode 1 shows only these attributes, not the reparse bit). A cloud-only file is never opened.</returns>
    public static (EntryKind Kind, bool CloudOnly) Classify(uint attributes, uint reparseTag, bool isDirectory)
    {
        bool isLink = (attributes & Kernel32.FILE_ATTRIBUTE_REPARSE_POINT) != 0
            && reparseTag is Kernel32.IO_REPARSE_TAG_MOUNT_POINT or Kernel32.IO_REPARSE_TAG_SYMLINK or Kernel32.IO_REPARSE_TAG_DFS;
        if (isLink)
            return (EntryKind.Link, false);
        if (isDirectory)
            return (EntryKind.Folder, false);
        return (EntryKind.File, (attributes & CloudAttributes) != 0);
    }
}
