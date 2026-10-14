namespace FsAnalyser.Core.Store;

/// <summary>
/// Rules over an entry's kind and flags that the store and the scan's probe pass must read the same way, kept in one place so
/// that what the pass sizes and what the store reports as not yet sized cannot drift apart. It names neither side.
/// </summary>
internal static class EntryRules
{
    /// <summary>
    /// Whether an entry is sized from the file itself: a file (a link is never one) that is not a later name of a file already
    /// charged and not cloud-only. A later name charges nothing, and a cloud-only file is never opened.
    /// </summary>
    public static bool ChecksOwnSize(EntryKind kind, EntryFlags flags) =>
        kind == EntryKind.File && (flags & (EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly)) == 0;
}
