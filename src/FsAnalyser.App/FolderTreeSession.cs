using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.App;

/// <summary>
/// The one scan that has returned, completed or stopped, which the folder tree reads (SL-044, SL-110). Every row holds
/// this, never the store, and the window closes it when the next scan starts: a row that WPF keeps after the tree is
/// cleared (the selected item, a recycled container, an automation peer) then keeps a few bytes, not a store of hundreds
/// of MiB (review M2).
/// </summary>
/// <remarks>
/// Only a store whose scan has returned is ever given here, so no thread writes what the rows read. Once closed,
/// every row reads as empty text and 0. It is given the scan's record of failed folders as well, and answers from it
/// how a folder failed (<see cref="FailedKind"/>): the tree reads the kind the scanner stamped and does not work it out
/// again from what the store holds (SL-106). No WPF here, so Core.Tests compiles it as source.
/// </remarks>
internal sealed class FolderTreeSession(ScanStore? store, IReadOnlyList<UnreadableFolder> unreadable, bool readsBounds = false)
{
    /// <summary>Holds no store: what the shared not-opened-yet row reads.</summary>
    internal static readonly FolderTreeSession None = new(null, []);

    /// <summary>
    /// Whether the rows may ask the store which of their figures are only a lower bound (SL-113): set for a stopped scan's
    /// tree, from <see cref="ScanResult.MarksAtLeast"/>, the one guard the stopped list's marks read too, and false for a
    /// completed scan's tree, <see cref="None"/> and every session built without it, whose rows then never ask. A row asks
    /// at most once, when its figures are first drawn, so opening a folder of 100,000 children reads the store for the
    /// rows on screen, not for each of them.
    /// </summary>
    internal bool ReadsBounds => readsBounds;

    private ScanStore? _store = store;

    // The first kind per entry, as UnreadableRows.Build keeps the first row; a map of about as many entries as folders failed.
    private Dictionary<int, UnreadableKind>? _failed = Index(unreadable);

    /// <summary>
    /// Whether a stopped scan's ending offers the folder tree (SL-110): the scan was stopped and its root's listing
    /// committed. <see cref="ScanResult.TopLevel"/> is null exactly when it never did, so a Stop in the root's own
    /// listing (the root is in the store, reading not listed) or before the root was appended (an empty store) offers
    /// none: the first would head the tree "is empty" over a folder nobody read, the second has no root to open. A scan
    /// that completed builds its tree without being asked.
    /// </summary>
    internal static bool OffersStoppedTree(ScanResult result) =>
        result.Status == ScanStatus.Cancelled && result.TopLevel is not null;

    /// <summary>The store the rows read; null once the session is closed.</summary>
    internal ScanStore? Store => _store;

    /// <summary>
    /// How the scan stamped <paramref name="entry"/> when it could not be read in full, or null for an entry the record
    /// does not hold and for every entry once the session is closed.
    /// </summary>
    internal UnreadableKind? FailedKind(int entry) =>
        _failed is not null && _failed.TryGetValue(entry, out UnreadableKind kind) ? kind : null;

    private static Dictionary<int, UnreadableKind> Index(IReadOnlyList<UnreadableFolder> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var index = new Dictionary<int, UnreadableKind>(record.Count);
        foreach (UnreadableFolder folder in record)
            index.TryAdd(folder.Entry, folder.Kind);
        return index;
    }

    /// <summary>How long the last folder opened took to sort its children, in ms; null before any.</summary>
    internal double? LastSortMs { get; set; }

    /// <summary>How long the last folder opened took to build its rows, in ms; null before any.</summary>
    internal double? LastBuildMs { get; set; }

    internal void Close()
    {
        _store = null;
        _failed = null;
    }
}
