using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Scanning;

public enum ScanStatus
{
    /// <summary>Everything under the root that could be read was read.</summary>
    Completed,

    /// <summary>Stopped on request. The store and totals hold what was read before the stop.</summary>
    Cancelled,

    /// <summary>The root itself could not be opened or listed; see <see cref="ScanResult.RootError"/>.</summary>
    RootUnreadable,
}

/// <summary>
/// Totals over every entry under the root (the root itself is not counted). Each byte is charged once:
/// links and later names of a hardlinked file add 0 bytes, and a folder adds nothing of its own stated
/// size. Bytes, BytesOnDisk and Files are the root's <see cref="ScanStore.RolledSize"/>,
/// <see cref="ScanStore.RolledOnDisk"/> and <see cref="ScanStore.FilesBelow"/>.
/// </summary>
public readonly record struct ScanTotals(long Files, long Folders, long Links, long Bytes, long BytesOnDisk);

/// <summary>
/// How much of a folder the scan listed before it failed, as the folder tree and the top-level rows word it (HF-002).
/// It says what was listed, not where Windows refused: the scan does not tell a refused open from a folder that
/// opened and then failed before its first entry came back, and both are NothingListed (SL-087, review M2).
/// </summary>
public enum UnreadableKind
{
    /// <summary>No entry of the folder was listed, so nothing in it is counted: worded "could not be opened".</summary>
    NothingListed,

    /// <summary>At least one entry was listed before the error, and what was listed is kept and counted.</summary>
    ListedPartway,
}

/// <summary>
/// A folder under the root that could not be opened or listed in full: its entry, the Win32 error and how much of it
/// was listed. Only Core makes one (SL-087, review M3): the constructor is internal and nothing can be set after it, so
/// the App cannot add a record of its own or copy one with another kind.
/// </summary>
public sealed record UnreadableFolder
{
    internal UnreadableFolder(int entry, int win32Error, UnreadableKind kind)
    {
        Entry = entry;
        Win32Error = win32Error;
        Kind = kind;
    }

    public int Entry { get; }

    public int Win32Error { get; }

    public UnreadableKind Kind { get; }
}

public sealed class ScanResult
{
    public required ScanStatus Status { get; init; }

    /// <summary>The root as normalised for the scan (for example <c>C:\</c>).</summary>
    public required string Root { get; init; }

    /// <summary>The Win32 error when <see cref="Status"/> is RootUnreadable, otherwise 0.</summary>
    public int RootError { get; init; }

    public required ScanStore Store { get; init; }

    public ScanTotals Totals { get; init; }

    public IReadOnlyList<UnreadableFolder> Unreadable { get; init; } = [];

    /// <summary>
    /// The scan root's direct children as the scan left them: the final snapshot (Final set), the same object
    /// as the last <see cref="ScanProgress.TopLevel"/>. After Cancel its rows add up to <see cref="Totals"/>
    /// like the rest. Null only when the root's listing never committed (unreadable, or cancelled before).
    /// </summary>
    public required TopLevelSnapshot? TopLevel { get; init; }

    /// <summary>Files that refused to have their true size read (ProbeFailed); their listing sizes were kept.</summary>
    public int ProbeFailures { get; init; }

    /// <summary>
    /// Files gone by the time their size was read: deleted, or their folder was, after being listed. Their listing
    /// sizes were kept. Not counted in <see cref="ProbeFailures"/>, which are refusals (SL-056, review m5).
    /// </summary>
    public int VanishedBeforeProbe { get; init; }

    /// <summary>
    /// False when a file was listed without a FileId (<see cref="ScanStore.HasFileId"/>), whatever the listing class,
    /// so the store could not count it once if it has other names; and when the root could not be read.
    /// </summary>
    public bool FileIdsAvailable { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>How many files were opened to read their true size, whatever came of it. Visible to tests only.</summary>
    internal int Probes { get; init; }

    /// <summary>How many of those probes changed the figure a file is charged (ProbeCorrected). Visible to tests only.</summary>
    internal int ProbeCorrections { get; init; }

    /// <summary>The listing walk's share of <see cref="Elapsed"/>: the scan thread's time before any file was sized.</summary>
    internal TimeSpan ListingTime { get; init; }

    /// <summary>The time spent sizing files after the walk, applying included; zero when the scan never got there.</summary>
    internal TimeSpan SizingTime { get; init; }

    /// <summary>The longest single probe.</summary>
    internal TimeSpan LongestProbe { get; init; }

    /// <summary>Folders whose listing was asked to keep their last-access time: local NTFS, not in a sync root.</summary>
    internal int LastAccessAsked { get; init; }

    /// <summary>
    /// Of <see cref="LastAccessAsked"/>, the folders whose listing handle did keep it. The rest refused the
    /// write-attributes open, were listed plainly, and their last-access time may have moved.
    /// </summary>
    internal int LastAccessKept { get; init; }
}
