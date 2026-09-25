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
/// links and later names of a hardlinked file add 0 bytes.
/// </summary>
public readonly record struct ScanTotals(long Files, long Folders, long Links, long Bytes, long BytesOnDisk);

/// <summary>A folder under the root that could not be opened or listed, and the Win32 error.</summary>
public readonly record struct UnreadableFolder(int Entry, int Win32Error);

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

    /// <summary>Files whose true size was due to be read from the file but could not be; their listing sizes were kept.</summary>
    public int ProbeFailures { get; init; }

    /// <summary>False when the volume gave no FileIds, so hardlinks could not be counted once.</summary>
    public bool FileIdsAvailable { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>How many files were opened to read their true size. Visible to tests only.</summary>
    internal int Probes { get; init; }
}
