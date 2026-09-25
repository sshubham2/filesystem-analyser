using System.Diagnostics;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Store;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Reads everything under a drive or folder, as the invoking user, into a <see cref="ScanStore"/>.
/// </summary>
/// <remarks>
/// Depth-first with an explicit stack, on the caller's thread. Each folder is listed once through
/// <see cref="DirectoryReader"/> and its listing is appended as one contiguous block, so the first name
/// of a hardlinked file the scan meets is always the same one. Links are recorded and never entered.
/// Nothing that goes wrong inside the tree fails the scan: an unreadable folder is flagged and listed in
/// <see cref="ScanResult.Unreadable"/>.
/// <para>Cancel interrupts the filesystem call the scan is blocked in, through the scan thread's
/// <see cref="SynchronousIoCancel"/>, and is otherwise checked once per folder. An interrupted call is
/// reported as ERROR_OPERATION_ABORTED (995). Under a requested cancel 995 is cancellation, never an
/// unreadable folder, and the folder it hit commits nothing: it stays as the scan left every folder it
/// never reached. With no cancel requested, 995 is an unreadable folder like any other error.</para>
/// </remarks>
public static class Scanner
{
    /// <summary>Scans <paramref name="root"/>. Cancel interrupts a blocked call; see the remarks.</summary>
    /// <exception cref="ArgumentException">The root is empty or not an absolute path.</exception>
    public static ScanResult Scan(string root, ScanProgress progress, CancellationToken ct) =>
        Scan(root, progress, ct, ScanOptions.Default);

    internal static ScanResult Scan(string root, ScanProgress progress, CancellationToken ct, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(options);
        (string display, string win32) = LongPath.NormalizeRoot(root);
        using var io = new SynchronousIoCancel(ct);
        using var run = new Run(display, win32, progress, ct, options, io);
        return run.Execute();
    }

    /// <summary>What a folder's read error, or a probe's, means for the scan.</summary>
    internal static ReadOutcome ClassifyReadError(int error, bool cancelRequested) =>
        error == 0 ? ReadOutcome.Read
        : error == Kernel32.ERROR_OPERATION_ABORTED && cancelRequested ? ReadOutcome.Cancelled
        : ReadOutcome.Unreadable;

    internal enum ReadOutcome
    {
        Read,

        /// <summary>Flagged and listed in <see cref="ScanResult.Unreadable"/>, so no folder is dropped silently.</summary>
        Unreadable,

        /// <summary>Aborted by Cancel: nothing of it is committed, and the scan stops.</summary>
        Cancelled,
    }

    private sealed class Run(string display, string win32Root, ScanProgress progress, CancellationToken ct, ScanOptions options,
        SynchronousIoCancel io) : IDisposable
    {
        private const int RootRow = -1; // the root's own listing, which belongs to no row
        private readonly ScanStore _store = new();
        private readonly DirectoryReader _reader = new(io);
        // Row: the folder's top-level row in the tracker, inherited from the root child it is under.
        private readonly Stack<(int Entry, string Path, int Row)> _pending = new();
        private readonly TopLevelTracker _topLevel = new(progress, display, options.TopLevelInterval);
        private readonly List<UnreadableFolder> _unreadable = [];
        private readonly List<DueProbe> _dueProbes = [];
        private readonly HashSet<UInt128> _settled = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _fullClass = options.ForceFullDirectoryClass;
        private long _files, _folders, _links, _bytes, _bytesOnDisk;
        private int _probes, _probeFailures, _foldersListed, _nextDueProbe;

        public ScanResult Execute()
        {
            if (ct.IsCancellationRequested)
                return Finish(ScanStatus.Cancelled);

            int error = ListRoot(out int rootEntry);
            if (rootEntry < 0)
            {
                return ClassifyReadError(error, ct.IsCancellationRequested) == ReadOutcome.Cancelled
                    ? Finish(ScanStatus.Cancelled)
                    : Finish(ScanStatus.RootUnreadable, error);
            }
            if (FolderDone(rootEntry, win32Root, RootRow, error))
                return Finish(ScanStatus.Cancelled);

            while (_pending.Count > 0)
            {
                (int folder, string path, int row) = _pending.Pop();
                error = _reader.Read(path, _fullClass);
                if (FolderDone(folder, path, row, error))
                    return Finish(ScanStatus.Cancelled);
            }

            return Finish(ScanStatus.Completed);
        }

        // Opens the root, checks it is a folder, and picks the listing class from its first call.
        // Returns the root's entry, or -1 with the error when the root cannot be read at all.
        private int ListRoot(out int rootEntry)
        {
            rootEntry = -1;
            int error = _reader.Open(win32Root, out SafeFileHandle? handle);
            if (error != 0)
                return error;

            try
            {
                error = QueryAttributes(handle!, out uint attributes);
                if (error != 0)
                    return error;
                // Checked before the class is picked: error 87 must only ever mean "class unsupported".
                if ((attributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) == 0)
                    return Kernel32.ERROR_DIRECTORY;

                error = _reader.List(handle!, _fullClass);
                if (!_fullClass && _reader.Count == 0 && error is Kernel32.ERROR_INVALID_FUNCTION
                        or Kernel32.ERROR_NOT_SUPPORTED or Kernel32.ERROR_INVALID_PARAMETER)
                {
                    _fullClass = true;
                    error = _reader.List(handle!, _fullClass);
                }
                if (error != 0 && _reader.Count == 0)
                    return error;

                rootEntry = _store.AppendRoot(display, attributes);
                return error;
            }
            finally
            {
                io.Close(handle!);
            }
        }

        // Probes what the listing cannot be trusted for, appends the listing as the folder's children,
        // queues its subfolders, publishes progress, and reports whether the scan must stop. An aborted
        // listing or probe commits nothing and stops the scan. The one exit of every folder's listing, so
        // the top-level tracker hears of each committed folder once, here: the root's listing seeds its rows,
        // and every other folder is credited to its row.
        private bool FolderDone(int folder, string folderPath, int row, int error)
        {
            if (ClassifyReadError(error, ct.IsCancellationRequested) == ReadOutcome.Cancelled || !ProbeListing(folderPath))
                return true;

            int first = _store.Count;
            int count = _reader.Count;
            _nextDueProbe = 0;
            for (int k = 0; k < count; k++)
                AddEntry(folder, folderPath, k);
            _store.SetChildren(folder, first, count);

            if (error != 0)
            {
                _store.AddFlags(folder, EntryFlags.Unreadable);
                _unreadable.Add(new UnreadableFolder(folder, error));
            }

            // Pushed last-first so folders are visited in listing order. A root child is its own row.
            int pushed = 0;
            for (int k = count - 1; k >= 0; k--)
            {
                if (_store.Kind(first + k) == EntryKind.Folder)
                {
                    _pending.Push((first + k, LongPath.Child(folderPath, _store.Name(first + k)), row == RootRow ? k : row));
                    pushed++;
                }
            }

            if (row == RootRow)
                _topLevel.Seed(_store, folder, first, count, Totals);
            else
                _topLevel.FolderExited(folder, row, error != 0, pushed, Totals);

            _foldersListed++;
            progress.Publish(Totals, _unreadable.Count, folderPath);
            options.AfterFolder?.Invoke(_foldersListed);
            return ct.IsCancellationRequested;
        }

        // Reads the true size of every file in the listing that is due a probe (SizeProbe.IsDue), before
        // anything of the folder is appended, so that an aborted probe aborts the folder whole. A name of a
        // file already settled by a probe is skipped, as the append would skip it. False when a probe was
        // aborted by Cancel.
        private bool ProbeListing(string folderPath)
        {
            _dueProbes.Clear();
            for (int k = 0; k < _reader.Count; k++)
            {
                ref readonly RawEntry raw = ref _reader[k];
                bool isDirectory = (raw.Attributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) != 0;
                (EntryKind kind, bool cloudOnly) = EntryClassifier.Classify(raw.Attributes, raw.ReparseTag, isDirectory);
                if (kind != EntryKind.File
                        || !SizeProbe.IsDue(raw.AllocationSize, raw.EndOfFile, raw.Attributes, cloudOnly)
                        || (raw.FileId != UInt128.Zero && _settled.Contains(raw.FileId)))
                    continue;

                int error = SizeProbe.Probe(io, LongPath.Child(folderPath, _reader.NameOf(raw)), out long size, out long onDisk);
                if (ClassifyReadError(error, ct.IsCancellationRequested) == ReadOutcome.Cancelled)
                    return false;
                _dueProbes.Add(new DueProbe(k, error, size, onDisk));
                if (raw.FileId != UInt128.Zero)
                    _settled.Add(raw.FileId);
            }
            return true;
        }

        private void AddEntry(int parent, string parentPath, int index)
        {
            ref readonly RawEntry raw = ref _reader[index];
            bool isDirectory = (raw.Attributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) != 0;
            (EntryKind kind, bool cloudOnly) = EntryClassifier.Classify(raw.Attributes, raw.ReparseTag, isDirectory);
            ReadOnlySpan<char> name = _reader.NameOf(raw);
            int entry = _store.Append(parent, name, kind, cloudOnly ? EntryFlags.CloudOnly : EntryFlags.None,
                raw.Attributes, raw.EndOfFile, raw.AllocationSize, raw.FileId);

            switch (kind)
            {
                case EntryKind.Link:
                    _links++;
                    return;
                case EntryKind.Folder:
                    _folders++;
                    _bytes += raw.EndOfFile;
                    _bytesOnDisk += raw.AllocationSize;
                    return;
            }

            _files++;
            DueProbe? due = null;
            if (_nextDueProbe < _dueProbes.Count && _dueProbes[_nextDueProbe].Index == index)
                due = _dueProbes[_nextDueProbe++];

            if (!_store.TryAddFileId(entry, out int charged))
            {
                // A later name of a file already charged: it counts as a file and adds no bytes. The
                // first repeat corrects the charged record, whose listing may be stale.
                _store.AddFlags(entry, EntryFlags.HardlinkDuplicate);
                const EntryFlags settled = EntryFlags.SizeProbed | EntryFlags.ProbeFailed | EntryFlags.CloudOnly;
                if (!cloudOnly && (_store.Flags(charged) & settled) == 0)
                {
                    if (due is { } read)
                        Correct(charged, read.Error, read.Size, read.OnDisk);
                    else
                        ProbeAndCorrect(charged, LongPath.Child(parentPath, name));
                }
                return;
            }

            _bytes += raw.EndOfFile;
            _bytesOnDisk += raw.AllocationSize;
            if (due is { } probed)
                Correct(entry, probed.Error, probed.Size, probed.OnDisk);
        }

        // The first repeat of a file whose charged record was never probed. An aborted probe changes
        // nothing: the charged record stays as it would be had this name never been reached.
        private void ProbeAndCorrect(int charged, string path)
        {
            int error = SizeProbe.Probe(io, path, out long size, out long onDisk);
            if (ClassifyReadError(error, ct.IsCancellationRequested) != ReadOutcome.Cancelled)
                Correct(charged, error, size, onDisk);
        }

        // Replaces a charged record's listing sizes with the file's own, and the totals by the difference,
        // or flags it ProbeFailed and keeps the listing sizes.
        private void Correct(int charged, int probeError, long size, long onDisk)
        {
            _probes++;
            UInt128 fileId = _store.FileId(charged);
            if (fileId != UInt128.Zero)
                _settled.Add(fileId);
            if (probeError != 0)
            {
                _store.AddFlags(charged, EntryFlags.ProbeFailed);
                _probeFailures++;
                return;
            }

            long bytes = size - _store.Size(charged), onDiskBytes = onDisk - _store.OnDisk(charged);
            _bytes += bytes;
            _bytesOnDisk += onDiskBytes;
            _store.CorrectSizes(charged, size, onDisk);
            _topLevel.Moved(charged, bytes, onDiskBytes); // the charged name's row, whatever listing this is
            _store.AddFlags(charged, EntryFlags.SizeProbed);
        }

        private ScanTotals Totals => new(_files, _folders, _links, _bytes, _bytesOnDisk);

        // Every return of a ScanResult comes through here, so the last top-level snapshot is always published.
        private ScanResult Finish(ScanStatus status, int rootError = 0) => new()
        {
            TopLevel = _topLevel.Finish(status == ScanStatus.Completed, Totals),
            Status = status,
            Root = display,
            RootError = rootError,
            Store = _store,
            Totals = Totals,
            Unreadable = _unreadable,
            ProbeFailures = _probeFailures,
            FileIdsAvailable = status != ScanStatus.RootUnreadable && !_fullClass,
            Elapsed = _clock.Elapsed,
            Probes = _probes,
        };

        private unsafe int QueryAttributes(SafeFileHandle handle, out uint attributes)
        {
            Kernel32.FILE_ATTRIBUTE_TAG_INFO info;
            int error = io.Query(handle, Kernel32.FileAttributeTagInfo, &info, (uint)sizeof(Kernel32.FILE_ATTRIBUTE_TAG_INFO));
            attributes = error == 0 ? info.FileAttributes : 0;
            return error;
        }

        public void Dispose() => _reader.Dispose();
    }

    /// <summary>A probe run before its folder was appended: the listing index it belongs to and what it read.</summary>
    private readonly record struct DueProbe(int Index, int Error, long Size, long OnDisk);
}
