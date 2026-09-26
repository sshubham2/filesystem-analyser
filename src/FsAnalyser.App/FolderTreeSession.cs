using FsAnalyser.Core.Store;

namespace FsAnalyser.App;

/// <summary>
/// The one completed scan the folder tree reads (SL-044). Every row holds this, never the store, and the window
/// closes it when the next scan starts: a row that WPF keeps after the tree is cleared (the selected item, a
/// recycled container, an automation peer) then keeps a few bytes, not a store of hundreds of MiB (review M2).
/// </summary>
/// <remarks>
/// Only a store whose scan has returned is ever given here, so no thread writes what the rows read. Once closed,
/// every row reads as empty text and 0.
/// </remarks>
internal sealed class FolderTreeSession(ScanStore? store)
{
    /// <summary>Holds no store: what the shared not-opened-yet row reads.</summary>
    internal static readonly FolderTreeSession None = new(null);

    private ScanStore? _store = store;

    /// <summary>The store the rows read; null once the session is closed.</summary>
    internal ScanStore? Store => _store;

    /// <summary>How long the last folder opened took to sort its children, in ms; null before any.</summary>
    internal double? LastSortMs { get; set; }

    /// <summary>How long the last folder opened took to build its rows, in ms; null before any.</summary>
    internal double? LastBuildMs { get; set; }

    internal void Close() => _store = null;
}
