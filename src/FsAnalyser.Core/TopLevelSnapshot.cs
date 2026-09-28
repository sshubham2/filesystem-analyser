using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core;

/// <summary>What a top-level row is: an entry directly under the scan root.</summary>
public enum TopLevelKind
{
    File,
    Folder,
    Link,
}

/// <summary>How far a top-level row has been read.</summary>
public enum TopLevelState
{
    /// <summary>Every folder under it has been listed and measured (a file row always is). That is the whole
    /// promise, not that its figures are final: until the scan ends, the probe that reads each file's own size
    /// once every folder is listed (SL-056) can still correct a file under it in place, such as the first name
    /// of a hardlinked file that was written through another name. A Done row whose figures moved that way
    /// says so in <see cref="TopLevelRow.Revised"/> (SL-063). Once the scan has ended, completed or cancelled,
    /// nothing under it changes again.</summary>
    Done,

    /// <summary>Its own folder has been listed and folders under it are still to be listed.</summary>
    Reading,

    /// <summary>Its own folder has not been listed yet; after Cancel, it never was.</summary>
    NotRead,

    /// <summary>The scan was cancelled while folders under it were still to be listed.</summary>
    StoppedPartway,

    /// <summary>Its own folder could not be opened or listed. Anything under it that was listed is counted, and
    /// <see cref="TopLevelRow.ListedPartway"/> says whether anything was.</summary>
    Unreadable,

    /// <summary>A junction or symbolic link: recorded, never entered, charged 0 bytes.</summary>
    NotFollowed,
}

/// <summary>
/// One entry directly under the scan root, with everything the scan has counted under it so far. Built
/// once by the scanner's thread and never changed; a later snapshot carries a new object for a row that
/// changed.
/// </summary>
public sealed class TopLevelRow
{
    internal TopLevelRow(string name, string path, TopLevelKind kind, TopLevelState state, long bytes, long onDisk, long files,
        long folders, long unreadableBelow)
    {
        Name = name;
        Path = path;
        Kind = kind;
        State = state;
        Bytes = bytes;
        OnDisk = onDisk;
        Files = files;
        Folders = folders;
        UnreadableBelow = unreadableBelow;
    }

    /// <summary>The name as listed.</summary>
    public string Name { get; }

    /// <summary>The full display path of this entry under the root.</summary>
    public string Path { get; }

    public TopLevelKind Kind { get; }

    public TopLevelState State { get; }

    /// <summary>Charged logical bytes under it, the same rule as the totals: each byte counted once.</summary>
    public long Bytes { get; }

    /// <summary>Charged on-disk bytes under it.</summary>
    public long OnDisk { get; }

    /// <summary>Files under it, a file row counting itself. A later name of a hardlinked file counts as a file.</summary>
    public long Files { get; }

    /// <summary>Folders under it, a folder row counting itself.</summary>
    public long Folders { get; }

    /// <summary>Folders under it (not the row's own folder) that could not be opened or listed.</summary>
    public long UnreadableBelow { get; }

    /// <summary>
    /// A Done row whose bytes or on-disk bytes are no longer those it had when it became Done: a correction
    /// landed under it after it settled (SL-063). Only ever true on a Done row, and once true it stays true
    /// for the rest of the scan, even if a later correction brings the figures back.
    /// </summary>
    public bool Revised { get; init; }

    /// <summary>
    /// An Unreadable row whose own folder's listing failed partway (HF-002): what was listed before the error is kept
    /// and counted, so the folder was opened and not read in full. False on an Unreadable row that could not be opened
    /// or listed at all, and on every other state.
    /// </summary>
    public bool ListedPartway { get; init; }
}

/// <summary>
/// The scan root's direct children at one moment of a scan, published by the scanner's thread on
/// <see cref="ScanProgress.TopLevel"/> and, at the end, on <see cref="ScanResult.TopLevel"/>.
/// </summary>
/// <remarks>
/// Immutable, which is the whole of its thread-safety: it is handed over with a plain reference publish and
/// no lock. The rows are copied in when it is built and exposed read-only, so neither the scanner, which
/// keeps building later snapshots, nor a reader can change one another's view. The rows add up to
/// <see cref="Bytes"/>, <see cref="OnDisk"/>, <see cref="Files"/> and <see cref="Folders"/>, which are the
/// scanner's running totals at the moment it was built. The rows are in listing order, not sorted.
/// </remarks>
public sealed class TopLevelSnapshot
{
    internal TopLevelSnapshot(string root, long sequence, bool final, bool completed, TopLevelRow[] rows, in ScanTotals totals)
    {
        Root = root;
        Sequence = sequence;
        Final = final;
        Completed = completed;
        Rows = Array.AsReadOnly((TopLevelRow[])rows.Clone());
        Bytes = totals.Bytes;
        OnDisk = totals.BytesOnDisk;
        Files = totals.Files;
        Folders = totals.Folders;
    }

    /// <summary>The root as normalised for the scan, as <see cref="ScanResult.Root"/>.</summary>
    public string Root { get; }

    /// <summary>Rises by one with each snapshot of a scan, from 1.</summary>
    public long Sequence { get; }

    /// <summary>The last snapshot of the scan: nothing under it will change again.</summary>
    public bool Final { get; }

    /// <summary>The scan read everything it could (<see cref="ScanStatus.Completed"/>). Only ever true when Final.</summary>
    public bool Completed { get; }

    /// <summary>A read-only copy that cannot be cast back to an array; setting an item throws.</summary>
    public IReadOnlyList<TopLevelRow> Rows { get; }

    public long Bytes { get; }

    public long OnDisk { get; }

    public long Files { get; }

    public long Folders { get; }
}
