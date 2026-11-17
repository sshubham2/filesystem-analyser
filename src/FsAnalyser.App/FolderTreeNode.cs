using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using FsAnalyser.Core;
using FsAnalyser.Core.Store;

namespace FsAnalyser.App;

/// <summary>
/// One row of the folder tree (SL-044): an entry of a scan that has returned, with the on-disk bytes it ranks by and its
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
/// A stopped scan's tree (SL-110) holds folders the scan never listed. Such a folder has no children, no flags and no
/// record, the same columns as a folder that was listed and found empty, so only the store's mark
/// (<see cref="ScanStore.NotListed"/>) tells them apart. The mark is read once, as the row is built, into
/// <c>_notListed</c>: the store has returned and nothing writes it. A row with it reads "not read yet" and draws no
/// figure at all, through the one gate <see cref="HasFigures"/>, because a "0 bytes" beside a folder nobody read
/// is a figure the scan never measured.
/// A stopped scan's tree (SL-113) also marks the folders whose figures could still grow: a row the store reports unfinished
/// draws its Size and On disk as the store's lower bound (<see cref="ScanStore.AtLeast"/>), worded "at least" by
/// <see cref="TopLevelRowText"/>, and every folder above one is unfinished too, so the marks reach the root without a walk
/// here. A row the store reports final draws what it always drew. Order, shares and bars stay on the listed figures.
/// </remarks>
internal sealed class FolderTreeNode : INotifyPropertyChanged
{
    private static readonly FolderTreeNode[] NoChildren = []; // first: the row below takes it

    // Stands in for the children of every folder not opened yet. It reads nothing: its session holds no store,
    // so its texts are empty and its sizes 0 (review m4).
    private static readonly FolderTreeNode NotOpenedYetRow = new(FolderTreeSession.None, 0, 0, 0, isRoot: false, openable: false, notListed: false);
    private static readonly FolderTreeNode[] NotOpenedYet = [NotOpenedYetRow];

    private readonly FolderTreeSession _session;
    private readonly int _entry;
    private readonly bool _isRoot;
    private readonly bool _notListed; // the store's mark for this entry, read as the row is built (SL-110)
    private IReadOnlyList<FolderTreeNode> _children;
    private bool _isExpanded;

    private FolderTreeNode(FolderTreeSession session, int entry, long onDisk, double share, bool isRoot, bool openable, bool notListed)
    {
        _session = session;
        _entry = entry;
        OnDisk = onDisk;
        Share = share;
        _isRoot = isRoot;
        _notListed = notListed;
        _children = openable ? NotOpenedYet : NoChildren;
    }

    /// <summary>The scan's root: its path as the label, its totals, and no share.</summary>
    internal static FolderTreeNode Root(FolderTreeSession session)
    {
        ScanStore store = session.Store ?? throw new InvalidOperationException("The folder tree's session is closed.");
        return new FolderTreeNode(session, 0, store.RolledOnDisk(0), 0, isRoot: true, openable: store.ChildCount(0) > 0,
            notListed: store.NotListed(0));
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
    /// Why a row has no children to open, or fewer than it holds, when the name alone does not say. A folder that could
    /// not be read in full is worded from the kind the scanner stamped (the session's record), through the one
    /// function the unreadable-folders list and the top-level row use too, so the three cannot disagree about it
    /// (SL-106). A folder listed partway keeps what was listed, so it was opened and has rows. A folder the scan never
    /// listed, queued behind a Stop or cut inside its own listing by it, says so (SL-110) in the stopped list's word,
    /// and is placed before the unreadable words: Core stamps a failure only on a folder whose listing it committed,
    /// which clears the mark, so the two never meet, and if they ever did the stop is the truer thing to say.
    /// </summary>
    public string Note => _session.Store is not { } store ? ""
        : store.Kind(_entry) == EntryKind.Link ? "link, not followed"
        : (store.Flags(_entry) & EntryFlags.NotScanned) != 0 ? UnreadableRows.NotScannedText(UnreadableRows.FlaggedReason)
        : _notListed ? TopLevelRowText.NotReadYet
        : _session.FailedKind(_entry) is { } failed ? UnreadableRows.KindText(failed)
        : "";

    /// <summary>What the row ranks by: its charged on-disk bytes, everything below it for a folder.</summary>
    public long OnDisk { get; }

    /// <summary>OnDisk over the parent's, as Core computed it: 0 when the parent takes nothing. 0 for the root.</summary>
    public double Share { get; }

    /// <summary>Its charged logical bytes, everything below it for a folder. Shown, never ranked by.</summary>
    public long Size => _session.Store?.RolledSize(_entry) ?? 0;

    public int ChildCount => _session.Store?.ChildCount(_entry) ?? 0;

    // The one gate every figure the row draws reads (SL-110): a closed session has no store to read them from, and a
    // folder the scan never listed has no figure to give, only the 0 it holds. A figure property added later reads
    // this too, so the next column blanks for an unread folder by default.
    private bool HasFigures => _session.Store is not null && !_notListed;

    // What a stopped scan's store holds for certain under this row (SL-113): the store's lower bound when it reports the folder
    // unfinished, null when it reports it final, and null for every row of a tree whose session does not read bounds (a
    // completed scan's). Read once, the first time a figure is drawn, so the rows of a 100,000-child folder that are not on
    // screen read nothing; the figure it gives is AtLeast, never the rolled size the row ranks by, which a pending check can
    // leave above the final one. It is read only behind HasFigures, so a folder the scan never listed asks nothing.
    private AtLeastFigures? _bound;
    private bool _boundRead;

    private AtLeastFigures? Bound
    {
        get
        {
            if (!_boundRead)
            {
                _boundRead = true;
                if (_session.ReadsBounds && _session.Store is { } store && store.Unfinished(_entry))
                    _bound = store.AtLeast(_entry);
            }
            return _bound;
        }
    }

    // A figure that is only a lower bound is worded as one, by the one place that words it (TopLevelRowText); a final one is
    // written as it always was. The ranking value (OnDisk), the share and the bar are not touched: they stay on the listed figures.
    public string OnDiskText => !HasFigures ? "" : Bound is { } bound ? TopLevelRowText.AtLeastOnDiskText(bound) : FigureText.FormatSize(OnDisk);

    public string SizeText => !HasFigures ? "" : Bound is { } bound ? TopLevelRowText.AtLeastSizeText(bound) : FigureText.FormatSize(Size);

    public string ShareText => _isRoot || !HasFigures ? "" : FolderTreeRowText.Share(Share);

    /// <summary>0 for an unread folder: nothing was rolled into it, so its share is 0 and the bar is not drawn.</summary>
    public double BarWidth => _isRoot ? 0 : FolderTreeRowText.BarWidth(Share);

    /// <summary>The root has no share, so no bar; its column keeps its width. Nor has a folder the scan never listed.</summary>
    public Visibility ShareShown => _isRoot || !HasFigures ? Visibility.Hidden : Visibility.Visible;

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
                openable: store.ChildCount(row.Entry) > 0, notListed: store.NotListed(row.Entry));
        }
        _session.LastSortMs = Stopwatch.GetElapsedTime(start, sorted).TotalMilliseconds;
        _session.LastBuildMs = Stopwatch.GetElapsedTime(sorted).TotalMilliseconds;
        Children = nodes;
    }
}
