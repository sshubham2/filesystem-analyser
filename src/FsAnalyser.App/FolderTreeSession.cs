using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.App;

/// <summary>
/// The one completed scan the folder tree reads (SL-044). Every row holds this, never the store, and the window
/// closes it when the next scan starts: a row that WPF keeps after the tree is cleared (the selected item, a
/// recycled container, an automation peer) then keeps a few bytes, not a store of hundreds of MiB (review M2).
/// </summary>
/// <remarks>
/// Only a store whose scan has returned is ever given here, so no thread writes what the rows read. Once closed,
/// every row reads as empty text and 0. It is given the scan's record of failed folders as well, and answers from it
/// how a folder failed (<see cref="FailedKind"/>): the tree reads the kind the scanner stamped and does not work it out
/// again from what the store holds (SL-106). No WPF here, so Core.Tests compiles it as source.
/// </remarks>
internal sealed class FolderTreeSession(ScanStore? store, IReadOnlyList<UnreadableFolder> unreadable)
{
    /// <summary>Holds no store: what the shared not-opened-yet row reads.</summary>
    internal static readonly FolderTreeSession None = new(null, []);

    private ScanStore? _store = store;

    // The first kind per entry, as UnreadableRows.Build keeps the first row; a map of about as many entries as folders failed.
    private Dictionary<int, UnreadableKind>? _failed = Index(unreadable);

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
