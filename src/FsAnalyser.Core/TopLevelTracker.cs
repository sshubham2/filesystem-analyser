using System.Diagnostics;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core;

/// <summary>
/// One row per entry directly under the scan root, with everything counted under it so far, published as
/// immutable <see cref="TopLevelSnapshot"/>s so that nothing outside the scanner ever reads the live store.
/// </summary>
/// <remarks>
/// Owned by the scanner's thread: every call comes from its run loop, so the accumulators are plain fields
/// and nothing here takes a lock (SL-039). Every change to the scanner's running totals lands in exactly one
/// row, so the rows add up to the totals in every snapshot:
/// <list type="bullet">
/// <item>The root listing seeds the rows from each child's own charged size in the store.</item>
/// <item>Every later listing that commits (<see cref="FolderExited"/>) credits the change in the totals
/// since the last credit to its row, which the scanner carries on its stack. A listing that Cancel aborted
/// commits nothing and changes no total, so it is never credited.</item>
/// <item>A probe that corrects a record in place (<see cref="Moved"/>) credits that record's row at once. Since
/// SL-056 probes run once every folder is listed, so a correction lands on a row that is already Done, and
/// <see cref="Corrected"/> publishes each batch of them.</item>
/// </list>
/// A folder row is Done when its pending count reaches 0: one for its own folder, plus one for every folder
/// pushed under it, less one for every folder under it that exits a listing, read or unreadable. Links are
/// never pushed, so they never hold a row open.
/// <para>A row keeps the figures it had when its pending count reached 0 (a file row: when it was seeded). A
/// Done row built with other figures is Revised from then on (SL-063), whichever change moved them, so no
/// later writer of a row's figures has to remember to mark it.</para>
/// </remarks>
internal sealed class TopLevelTracker(ScanProgress progress, string root, TimeSpan interval)
{
    private readonly long _interval = (long)(interval.TotalSeconds * Stopwatch.Frequency);
    private readonly List<int> _dirty = [];
    private ScanStore? _store; // null until the root listing has seeded the rows
    private int _root;
    private int _first; // row i is entry _first + i
    private Row[] _rows = [];
    private TopLevelRow[] _published = [];
    private ScanTotals _credited;
    private long _sequence;
    private long _lastPublish;

    /// <summary>After the root listing has committed: one row per child, then the first snapshot.</summary>
    public void Seed(ScanStore store, int rootEntry, int first, int count, in ScanTotals totals)
    {
        _store = store;
        _root = rootEntry;
        _first = first;
        _rows = new Row[count];
        _published = new TopLevelRow[count];
        for (int i = 0; i < count; i++)
        {
            int entry = first + i;
            EntryKind kind = store.Kind(entry);
            _rows[i] = new Row
            {
                Name = store.Name(entry).ToString(),
                Kind = kind,
                Bytes = store.ChargedSize(entry),
                OnDisk = store.ChargedOnDisk(entry),
                Files = kind == EntryKind.File ? 1 : 0,
                Folders = kind == EntryKind.Folder ? 1 : 0,
                Pending = kind == EntryKind.Folder ? 1 : 0, // pushed by the root listing
            };
            if (_rows[i].Pending == 0)
                Settle(ref _rows[i]);
            _published[i] = Build(i, final: false);
        }
        _credited = totals;
        Publish(totals, final: false, completed: false);
    }

    /// <summary>
    /// A folder under the root has committed its listing: <paramref name="row"/> is its top-level row. The
    /// one call for every folder that exits a listing, readable or not, so pending moves once per folder. A folder left
    /// out on purpose (<paramref name="notScanned"/>, SL-095) exits here too, with nothing listed and nothing pushed.
    /// <paramref name="failed"/> is how the scanner stamped the folder when it could not be read in full, or null: the
    /// row carries that stamp as it is and never works the kind out again (SL-106).
    /// </summary>
    public void FolderExited(int folder, int row, UnreadableKind? failed, int subfoldersPushed, in ScanTotals totals, bool notScanned = false)
    {
        ref Row r = ref _rows[row];
        r.Bytes += totals.Bytes - _credited.Bytes;
        r.OnDisk += totals.BytesOnDisk - _credited.BytesOnDisk;
        r.Files += totals.Files - _credited.Files;
        r.Folders += totals.Folders - _credited.Folders;
        _credited = totals;

        r.Pending += subfoldersPushed - 1;
        Debug.Assert(r.Pending >= 0, $"row {r.Name}: pending {r.Pending} after folder {folder}");
        if (r.Pending == 0)
            Settle(ref r);
        if (folder == _first + row)
        {
            r.Started = true;
            r.OwnFailed = failed;
            r.OwnNotScanned = notScanned;
        }
        else if (failed is not null)
        {
            r.UnreadableBelow++;
        }
        MarkDirty(row);

        if (Stopwatch.GetTimestamp() - _lastPublish >= _interval)
            Publish(totals, final: false, completed: false);
    }

    /// <summary>
    /// A probe corrected <paramref name="record"/>, an earlier name, in place: the change is that name's
    /// row's, whichever listing it happened in. Before the rows are seeded it is already in the store.
    /// </summary>
    public void Moved(int record, long bytes, long onDisk)
    {
        if (_store is null)
            return;
        int top = record;
        for (int parent = _store.Parent(top); parent != _root; parent = _store.Parent(top))
            top = parent;
        int row = top - _first;
        Debug.Assert((uint)row < (uint)_rows.Length, $"record {record} is under no row");

        ref Row r = ref _rows[row];
        r.Bytes += bytes;
        r.OnDisk += onDisk;
        _credited = _credited with { Bytes = _credited.Bytes + bytes, BytesOnDisk = _credited.BytesOnDisk + onDisk };
        MarkDirty(row);
    }

    /// <summary>
    /// A batch of probes has been applied after the walk (SL-056): publishes the rows it moved, at most once per
    /// interval, as a listing does.
    /// </summary>
    public void Corrected(in ScanTotals totals)
    {
        if (_store is null || _dirty.Count == 0)
            return;
        if (Stopwatch.GetTimestamp() - _lastPublish >= _interval)
            Publish(totals, final: false, completed: false);
    }

    /// <summary>The last snapshot, always published; null when the root listing never committed.</summary>
    public TopLevelSnapshot? Finish(bool completed, in ScanTotals totals)
    {
        if (_store is null)
            return null;
        Debug.Assert(_credited == totals, "a change to the totals was never credited to a row");
        for (int i = 0; i < _rows.Length; i++)
        {
            Debug.Assert(!completed || _rows[i].Pending == 0, $"row {_rows[i].Name} is still pending at completion");
            if (_rows[i].Started && _rows[i].Pending > 0)
                MarkDirty(i); // Reading becomes StoppedPartway
        }
        return Publish(totals, final: true, completed);
    }

    private void MarkDirty(int row)
    {
        if (_rows[row].Dirty)
            return;
        _rows[row].Dirty = true;
        _dirty.Add(row);
    }

    // Unchanged rows keep their objects; the snapshot copies the array, so a row replaced here never
    // changes a snapshot already handed out.
    private TopLevelSnapshot Publish(in ScanTotals totals, bool final, bool completed)
    {
        foreach (int i in _dirty)
        {
            _published[i] = Build(i, final);
            _rows[i].Dirty = false;
        }
        _dirty.Clear();
        var snapshot = new TopLevelSnapshot(root, ++_sequence, final, completed, _published, totals);
        progress.PublishTopLevel(snapshot);
        _lastPublish = Stopwatch.GetTimestamp();
        return snapshot;
    }

    private TopLevelRow Build(int i, bool final)
    {
        ref Row r = ref _rows[i];
        TopLevelState state = r.Kind switch
        {
            EntryKind.File => TopLevelState.Done,
            EntryKind.Link => TopLevelState.NotFollowed,
            _ when !r.Started => TopLevelState.NotRead,
            _ when r.Pending > 0 => final ? TopLevelState.StoppedPartway : TopLevelState.Reading,
            _ when r.OwnNotScanned => TopLevelState.NotScanned,
            _ => r.OwnFailed is not null ? TopLevelState.Unreadable : TopLevelState.Done,
        };
        if (state == TopLevelState.Done && (r.Bytes, r.OnDisk) != (r.SettledBytes, r.SettledOnDisk))
            r.Revised = true;
        string path = LongPath.Child(root, r.Name);
        return new TopLevelRow(r.Name, path, r.Kind, state, r.Bytes, r.OnDisk, r.Files, r.Folders, r.UnreadableBelow)
        {
            Revised = r.Revised,
            FailedKind = state == TopLevelState.Unreadable ? r.OwnFailed : null,
        };
    }

    // Its pending count has reached 0 and never rises again: what it holds now is what Done first shows.
    private static void Settle(ref Row r) => (r.SettledBytes, r.SettledOnDisk) = (r.Bytes, r.OnDisk);

    private struct Row
    {
        public string Name;
        public EntryKind Kind;
        public long Bytes, OnDisk, Files, Folders, UnreadableBelow;
        public long SettledBytes, SettledOnDisk; // Bytes and OnDisk when its pending count reached 0
        public int Pending; // its own folder and every folder pushed under it, less those that exited a listing
        public bool Started; // its own folder has exited its listing
        public UnreadableKind? OwnFailed; // how the scanner stamped its own folder when it could not be read in full (SL-106)
        public bool OwnNotScanned; // its own folder is the store, left out on purpose (SL-095)
        public bool Revised; // built Done with figures other than the settled ones; never cleared
        public bool Dirty;
    }
}
