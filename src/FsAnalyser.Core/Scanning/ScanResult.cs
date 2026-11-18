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

    /// <summary>
    /// Not scanned at all, on purpose: nothing was listed or probed. <see cref="ScanResult.Refusal"/> says why (SL-095).
    /// A result, never an exception.
    /// </summary>
    Refused,
}

/// <summary>Why a scan was <see cref="ScanStatus.Refused"/>.</summary>
public enum ScanRefusal
{
    /// <summary>The scan was not refused.</summary>
    None,

    /// <summary>
    /// The root is the Windows container layer store or a folder inside it, which wcifs.sys leaks kernel memory and a
    /// System handle for, entry by entry, until a restart (SL-095, Q-179, Q-182). Refused while the Settings precaution
    /// against that leak is on, as it is at every launch; with it off the root is scanned as picked (Q-186).
    /// </summary>
    ContainerLayerStore,
}

/// <summary>Why a folder under the root was left out on purpose.</summary>
public enum NotScannedReason
{
    /// <summary>The Windows container layer store (SL-095), left out while its precaution is on.</summary>
    ContainerLayerStore,
}

/// <summary>
/// A folder under the root the scan reached and left out on purpose, never opened: its entry and why (SL-095). It is
/// kept apart from <see cref="ScanResult.Unreadable"/> by construction, so nothing that reads or retries unreadable
/// folders can reach it (review M5). Its size is not counted anywhere; it is in the volume's coverage gap. Only Core
/// makes one.
/// </summary>
public sealed record NotScannedFolder
{
    internal NotScannedFolder(int entry, NotScannedReason reason, string workaroundId)
    {
        Entry = entry;
        Reason = reason;
        WorkaroundId = workaroundId;
    }

    public int Entry { get; }

    public NotScannedReason Reason { get; }

    /// <summary>The precaution that left it out (<see cref="Workarounds.KnownWorkarounds"/>), which the window words it from.</summary>
    public string WorkaroundId { get; }
}

/// <summary>
/// Totals over every entry under the root (the root itself is not counted). Each byte is charged once:
/// links and later names of a hardlinked file add 0 bytes, and a folder adds nothing of its own stated
/// size. Bytes, BytesOnDisk and Files are the root's <see cref="ScanStore.RolledSize"/>,
/// <see cref="ScanStore.RolledOnDisk"/> and <see cref="ScanStore.FilesBelow"/>.
/// </summary>
public readonly record struct ScanTotals(long Files, long Folders, long Links, long Bytes, long BytesOnDisk);

/// <summary>
/// How far the scan got with a folder before it failed, as the unreadable-folders list, the folder tree and the top-level
/// rows word it (HF-002). The scan keeps the phase that failed (SL-106, which closes SL-087's review M2 and m1): whether the
/// folder's open was refused, or it opened and failed before its first entry came back, or it failed after some were
/// listed. Scanner.FolderDone stamps it once, and every surface reads that stamp. A refused open is a fact of the open:
/// the code does not decide it (5, 32, 53, 363 and 1920 all fail there). The 27 code-363 folders measured on this PC, whose
/// cloud reparse tag cldflt cannot read, fail when the folder is opened (SL-106 survey), so they stay NothingListed.
/// </summary>
public enum UnreadableKind
{
    /// <summary>The open was refused, so nothing was listed and nothing in the folder is counted: worded "could not be opened".</summary>
    NothingListed,

    /// <summary>At least one entry was listed before the error, and what was listed is kept and counted.</summary>
    ListedPartway,

    /// <summary>
    /// The folder opened, and its listing failed before the first entry was kept, so nothing in it is counted. Appended
    /// after the other two so that they keep their values (SL-106).
    /// </summary>
    OpenedNothingListed,
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

    /// <summary>Why the scan was refused; None unless <see cref="Status"/> is Refused.</summary>
    public ScanRefusal Refusal { get; init; }

    /// <summary>
    /// The precaution whose outside defect a refusal avoids (<see cref="Workarounds.KnownWorkarounds"/>), which the window
    /// words the refusal from; null unless <see cref="Status"/> is Refused.
    /// </summary>
    public string? RefusalWorkaroundId { get; init; }

    /// <summary>
    /// The folders the scan reached and left out on purpose: the Windows container layer store, at most once (SL-095).
    /// Never in <see cref="Unreadable"/>, and their sizes are in no total.
    /// </summary>
    public IReadOnlyList<NotScannedFolder> SkippedStores { get; init; } = [];

    /// <summary>
    /// How many listings of a folder at or under a skipped store the scan's backstop refused (SL-095, review m4, M6).
    /// 0 whenever the walk's own check held, which is every scan so far: anything else is a way into the store that
    /// check missed.
    /// </summary>
    public int OpensUnderExcludedStore { get; init; }

    /// <summary>
    /// The scan root's direct children as the scan left them: the final snapshot (Final set), the same object
    /// as the last <see cref="ScanProgress.TopLevel"/>. After Cancel its rows add up to <see cref="Totals"/>
    /// like the rest. Null only when the root's listing never committed (unreadable, or cancelled before).
    /// </summary>
    public required TopLevelSnapshot? TopLevel { get; init; }

    /// <summary>
    /// Whether this result is a stopped scan whose figures can be worded "at least" (SL-113): the scan was cancelled, its root
    /// listing committed and its final snapshot says it did not complete, and the store holds entries. The one guard of every
    /// read of <see cref="ScanStore.Unfinished"/> and <see cref="ScanStore.AtLeast"/> for the window, the stopped list's and the tree's
    /// alike: it is false for a completed scan (which shows "at least" nowhere), for a root that could not be read, for a refused
    /// scan, and for a store with nothing in it, which those reads throw on. Valid on a result Scan has returned.
    /// </summary>
    public bool MarksAtLeast => Status == ScanStatus.Cancelled && TopLevel is { Final: true, Completed: false } && Store.Count > 0;

    /// <summary>
    /// The final top-level snapshot as the stopped ending shows it: with the store's lower bound on every row it reports
    /// unfinished (<see cref="TopLevelRow.AtLeast"/>), a new snapshot whenever any row is marked and <see cref="TopLevel"/> itself otherwise.
    /// <see cref="TopLevel"/> unread when <see cref="MarksAtLeast"/> is false. Reads the store, so only on a returned result; about one
    /// pass over the root's children, 10 ms at most cold on a 4.88M-entry store (spike 126).
    /// </summary>
    public TopLevelSnapshot? StoppedTopLevel() => MarksAtLeast ? TopLevelTracker.WithAtLeast(TopLevel!, Store) : TopLevel;

    /// <summary>Files that refused to have their true size read (ProbeFailed); their listing sizes were kept.</summary>
    public int ProbeFailures { get; init; }

    /// <summary>
    /// Files gone by the time their size was read: deleted, or their folder was, after being listed. Their listing
    /// sizes were kept. Not counted in <see cref="ProbeFailures"/>, which are refusals (SL-056, review m5).
    /// </summary>
    public int VanishedBeforeProbe { get; init; }

    /// <summary>
    /// Folders a parent listed that were gone when the walk came to open them (SL-005): the open found no folder, or, with
    /// its parent gone too, no path, and the scan root still opened, so it was the folder that was deleted and not the
    /// path above it that stopped resolving. Each stays in the store as a listed folder with no children and is counted in
    /// <see cref="Totals"/>; none is flagged or recorded in <see cref="Unreadable"/>. Not counted: the folders below one, which
    /// the scan never saw; a folder whose delete was still pending at the open or that a file replaced, which stay
    /// unreadable; a folder whose open failed because the scan root no longer opens either (a drive letter removed, a share
    /// dropped), which stays unreadable as before. Kept apart from <see cref="VanishedBeforeProbe"/> (files) and
    /// <see cref="ProbeFailures"/>.
    /// </summary>
    public int FoldersVanished { get; init; }

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
    /// Of <see cref="LastAccessAsked"/>, the folders whose keep-open succeeded (the write-attributes open, and the
    /// last-access time set to -1 through it), minus each such folder at or under the Windows folder (%SystemRoot%) that
    /// has a child folder whose keep-open also succeeded. The rest refused the write-attributes open, were listed
    /// plainly, and their last-access time may have moved.
    /// </summary>
    /// <remarks>
    /// The subtraction is a rule about what was opened, not a reading: nothing reads a time back. It stands for what was
    /// measured on Windows 11 in %SystemRoot%\Temp (SL-096): there a child's write-class open moves its parent's
    /// last-access time to now, inside the child's open, and the parent's own -1 handle, closed by then, cannot prevent it
    /// (attributed to UAC file virtualization, luafv.sys). The scan does not prevent it; this count says it. Known gaps:
    /// a parent is counted out even when nothing moved (its time within NTFS's hour, or last-access updates off); one is
    /// not counted out when a child's write-attributes open succeeded but its -1 set failed, though that open moved it;
    /// and the Windows folder is matched by how the root is spelled, so a root reaching it through a subst drive, a junction
    /// or a volume path is not corrected. The root's own parent, outside the scan, moves too and is never counted.
    /// Outside the Windows folder nothing is corrected, deliberately and as measured (SL-097,
    /// docs/design/SL-097-last-access-outside-the-windows-folder.md): on Windows 11 26200, in a non-elevated scan, a kept child
    /// folder moved no parent in %ProgramData% or %ProgramFiles%, and in %ProgramFiles% a standard user's keep-open is refused
    /// almost everywhere, so few folders are counted kept there. Windows 10 (Q-205) and elevated scans are unmeasured.
    /// Internal: the window never shows it (Q-103, Q-159).
    /// </remarks>
    internal int LastAccessKept { get; init; }
}
