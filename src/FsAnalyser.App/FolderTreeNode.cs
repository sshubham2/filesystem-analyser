using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using FsAnalyser.Core.Store;

namespace FsAnalyser.App;

/// <summary>
/// One row of the folder tree (SL-044): an entry of a completed scan, with the on-disk bytes it ranks by and its
/// share of its parent exactly as <see cref="ScanStore.ChildrenLargestFirst"/> returned them. The name and the
/// sizes are read from the store when a row is drawn, not copied into the row.
/// </summary>
/// <remarks>
/// A folder with children starts with one shared row standing in for them, so its expander shows; the first
/// time it is opened (<see cref="EnsureChildren"/>) its children are read in Core's order, built once as rows
/// and put in <see cref="Children"/> whole, one reset, and kept until the next scan. The window never sorts
/// them again. A file, a link, and a folder with nothing listed in it (empty, or not readable) have no
/// children and no expander. Expand state lives here and is bound TwoWay to the row's container, so it stays
/// with the row when a virtualized container is reused for another.
/// </remarks>
internal sealed class FolderTreeNode : INotifyPropertyChanged
{
    private static readonly FolderTreeNode[] NoChildren = []; // first: the row below takes it

    // Stands in for the children of every folder not opened yet. It reads nothing: its session holds no store,
    // so its texts are empty and its sizes 0 (review m4).
    private static readonly FolderTreeNode NotOpenedYetRow = new(FolderTreeSession.None, 0, 0, 0, isRoot: false, openable: false);
    private static readonly FolderTreeNode[] NotOpenedYet = [NotOpenedYetRow];

    private readonly FolderTreeSession _session;
    private readonly int _entry;
    private readonly bool _isRoot;
    private IReadOnlyList<FolderTreeNode> _children;
    private bool _isExpanded;

    private FolderTreeNode(FolderTreeSession session, int entry, long onDisk, double share, bool isRoot, bool openable)
    {
        _session = session;
        _entry = entry;
        OnDisk = onDisk;
        Share = share;
        _isRoot = isRoot;
        _children = openable ? NotOpenedYet : NoChildren;
    }

    /// <summary>The scan's root: its path as the label, its totals, and no share.</summary>
    internal static FolderTreeNode Root(FolderTreeSession session)
    {
        ScanStore store = session.Store ?? throw new InvalidOperationException("The folder tree's session is closed.");
        return new FolderTreeNode(session, 0, store.RolledOnDisk(0), 0, isRoot: true, openable: store.ChildCount(0) > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRoot => _isRoot;

    /// <summary>The name as listed; for the root, the path that was scanned.</summary>
    public string Name => _session.Store is { } store ? (_isRoot ? store.GetPath(_entry) : store.Name(_entry).ToString()) : "";

    private string? _fullPath;

    /// <summary>
    /// Lazily computed full display path for any tree row: <see cref="ScanStore.GetPath(int)"/>, cached on the node.
    /// The root keeps its existing full-path <see cref="Name"/> and <see cref="FullPath"/> returns it unchanged.
    /// </summary>
    public string FullPath
    {
        get
        {
            if (_isRoot)
                return Name;
            if (_fullPath != null)
                return _fullPath;
            if (_session.Store is { } store)
                _fullPath = store.GetPath(_entry);
            return _fullPath ?? string.Empty;
        }
    }

    public EntryKind? Kind => _session.Store?.Kind(_entry);

    /// <summary>Whether Core left this folder out on purpose (SL-095): the Windows container layer store, never opened.</summary>
    public bool NotScanned => _session.Store is { } store && (store.Flags(_entry) & EntryFlags.NotScanned) != 0;

    /// <summary>
    /// Why a row has no children to open, or fewer than it holds, when the name alone does not say. A folder whose
    /// listing failed partway keeps what was listed, so it was opened and has rows; it was not read in full.
    /// </summary>
    public string Note => _session.Store is not { } store ? ""
        : store.Kind(_entry) == EntryKind.Link ? "link, not followed"
        : (store.Flags(_entry) & EntryFlags.NotScanned) != 0 ? "size not counted: Windows container layers, skipped on purpose"
        : (store.Flags(_entry) & EntryFlags.Unreadable) == 0 ? ""
        : store.ChildCount(_entry) > 0 ? "could not be read in full"
        : "could not be opened";

    /// <summary>What the row ranks by: its charged on-disk bytes, everything below it for a folder.</summary>
    public long OnDisk { get; }

    /// <summary>OnDisk over the parent's, as Core computed it: 0 when the parent takes nothing. 0 for the root.</summary>
    public double Share { get; }

    /// <summary>Its charged logical bytes, everything below it for a folder. Shown, never ranked by.</summary>
    public long Size => _session.Store?.RolledSize(_entry) ?? 0;

    public int ChildCount => _session.Store?.ChildCount(_entry) ?? 0;

    public string OnDiskText => _session.Store is null ? "" : FigureText.FormatSize(OnDisk);

    public string SizeText => _session.Store is null ? "" : FigureText.FormatSize(Size);

    public string ShareText => _isRoot || _session.Store is null ? "" : FolderTreeRowText.Share(Share);

    public double BarWidth => _isRoot ? 0 : FolderTreeRowText.BarWidth(Share);

    /// <summary>The root has no share, so no bar; its column keeps its width.</summary>
    public Visibility ShareShown => _isRoot || _session.Store is null ? Visibility.Hidden : Visibility.Visible;

    /// <summary>The rows under it: none, the one standing in until it is opened, or its children largest first.</summary>
    public IReadOnlyList<FolderTreeNode> Children
    {
        get => _children;
        private set
        {
            _children = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Children)));
        }
    }

    /// <summary>Only records the state: opening a folder reads nothing here (<see cref="EnsureChildren"/>).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>
    /// The first time a folder is opened, reads its children in Core's order and puts them in whole. Later calls,
    /// and calls on a row with nothing to open or on a closed session, do nothing.
    /// </summary>
    internal void EnsureChildren()
    {
        if (!ReferenceEquals(_children, NotOpenedYet) || _session.Store is not { } store)
            return;
        long start = Stopwatch.GetTimestamp();
        ChildRow[] rows = store.ChildrenLargestFirst(_entry);
        long sorted = Stopwatch.GetTimestamp();
        var nodes = new FolderTreeNode[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            ChildRow row = rows[i];
            nodes[i] = new FolderTreeNode(_session, row.Entry, row.OnDisk, row.Share, isRoot: false,
                openable: store.ChildCount(row.Entry) > 0);
        }
        _session.LastSortMs = Stopwatch.GetElapsedTime(start, sorted).TotalMilliseconds;
        _session.LastBuildMs = Stopwatch.GetElapsedTime(sorted).TotalMilliseconds;
        Children = nodes;
    }
}
