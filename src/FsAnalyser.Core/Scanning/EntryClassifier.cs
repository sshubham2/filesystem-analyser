using FsAnalyser.Core.Native;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Decides what a listed entry is. This is the only place that reads the reparse attribute: a reparse
/// point is a link exactly when its tag has the name-surrogate bit, which says it stands for another named
/// entity (junctions and mount points, symbolic links, WSL symlinks, and any other tag with the bit). A link
/// is recorded and never entered or charged. Every other reparse folder (cloud, WCI, ProjFS, DFS) is
/// entered, and every other reparse file (AppExecLink, AF_UNIX, dedup, cloud) is a file (SL-043, U-004).
/// WOF files carry no reparse bit at all.
/// </summary>
/// <remarks>
/// A DFS tag has no surrogate bit, so a DFS folder is entered. Through a share, the SMB client follows its
/// referral (the critique's account, not measured here), and the scan goes on in the target server's share:
/// the one place the scan follows a link, and there FileIds from two volumes meet in one dedup table. The
/// slice's criterion asks for exactly this rule, and it was accepted with that consequence (SL-043, m1).
/// Nothing fails there, so no unreadable-folder record covers it.
/// </remarks>
public static class EntryClassifier
{
    private const uint CloudAttributes =
        Kernel32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Kernel32.FILE_ATTRIBUTE_RECALL_ON_OPEN | Kernel32.FILE_ATTRIBUTE_OFFLINE;

    /// <param name="attributes">Win32 file attributes from the listing.</param>
    /// <param name="reparseTag">The listing's reparse tag. Only meaningful when the reparse attribute is set;
    /// on the fallback listing class it is the EaSize field, which holds the tag only for reparse points, so
    /// it is never read without the attribute.</param>
    /// <param name="isDirectory">Whether the listing marks the entry as a directory.</param>
    /// <returns>The kind, and whether a file's data is not local (a cloud placeholder under placeholder
    /// mode 1 shows only these attributes, not the reparse bit). A cloud-only file is never opened.</returns>
    public static (EntryKind Kind, bool CloudOnly) Classify(uint attributes, uint reparseTag, bool isDirectory)
    {
        bool isLink = (attributes & Kernel32.FILE_ATTRIBUTE_REPARSE_POINT) != 0
            && (reparseTag & Kernel32.REPARSE_TAG_NAME_SURROGATE) != 0;
        if (isLink)
            return (EntryKind.Link, false);
        if (isDirectory)
            return (EntryKind.Folder, false);
        return (EntryKind.File, (attributes & CloudAttributes) != 0);
    }
}
