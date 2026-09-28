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
/// of a hardlinked file the scan meets is always the same one: the store charges that name and flags the
/// rest, and it is repeatable only because this is the one thread appending, in this order. Links are
/// recorded and never entered.
/// <para>Then every file the walk charged is sized from the file itself (SL-056, <see cref="SizeProbe"/>): the
/// listing's figures are stale for a file another process holds open and is growing, and for a hardlink name not
/// written through. Probes run on <see cref="ProbePass"/>'s worker threads a batch at a time, and this thread
/// applies each batch once its workers have finished, so it stays the store's one writer. The scan is Completed
/// only once every probe is applied.</para>
/// <para>On a local NTFS volume, outside a sync engine's folders, each folder is listed through a handle that
/// keeps its last-access time (<see cref="SynchronousIoCancel.OpenDirectory"/>), so the scan changes no folder's
/// metadata where the folder lets it.</para>
/// Nothing that goes wrong inside the tree fails the scan: an unreadable folder is flagged and listed in
/// <see cref="ScanResult.Unreadable"/>.
/// <para>Cancel interrupts the filesystem call the scan is blocked in, through the scan thread's
/// <see cref="SynchronousIoCancel"/>, and is otherwise checked once per folder. An interrupted call is
/// reported as ERROR_OPERATION_ABORTED (995). Under a requested cancel 995 is cancellation, never an
/// unreadable folder, and the folder it hit commits nothing: it stays as the scan left every folder it
/// never reached. With no cancel requested, 995 is an unreadable folder like any other error.</para>
/// <para>For a scan of a whole drive, the volume's used space is read as the scan starts, on a thread of
/// its own (<see cref="VolumeSpace"/>), and lands in <see cref="ScanStore.VolumeUsed"/>. The scan never waits
/// for it: it ends, completed or cancelled, whether or not the volume has answered.</para>
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

    /// <summary>What a folder's read error means for the scan.</summary>
    internal static ReadOutcome ClassifyReadError(int error, bool cancelRequested) =>
        error == 0 ? ReadOutcome.Read
        : error == Kernel32.ERROR_OPERATION_ABORTED && cancelRequested ? ReadOutcome.Cancelled
        : ReadOutcome.Unreadable;

    /// <summary>
    /// Whether a scan keeps its folders' last-access times, from what the root's volume says it is: only on a local
    /// NTFS volume, the one place the suppression was measured (review M6). A share, ReFS, FAT and exFAT are listed
    /// as before.
    /// </summary>
    internal static bool MayKeepLastAccess(string? fileSystem, bool remote) =>
        !remote && string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="win32Path"/> is <paramref name="win32Root"/> or a path below it. Both are \\?\ paths.</summary>
    internal static bool IsAtOrUnder(string win32Path, string win32Root) =>
        win32Path.StartsWith(win32Root, StringComparison.OrdinalIgnoreCase)
        && (win32Path.Length == win32Root.Length || win32Root.EndsWith('\\') || win32Path[win32Root.Length] == '\\');

    /// <summary>
    /// The OneDrive folders the environment names (OneDrive, OneDriveConsumer, OneDriveCommercial), in \\?\ form:
    /// the sync roots this PC has (U-004, U-019), under which no folder's last-access time is kept (review M6).
    /// A folder another sync engine manages is not found this way; see docs/design/SL-056-probe-every-file.md.
    /// </summary>
    internal static IReadOnlyList<string> SyncRootsFromEnvironment() =>
        NormalizeSyncRoots(new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable));

    private static IReadOnlyList<string> NormalizeSyncRoots(IEnumerable<string?> roots)
    {
        var normalized = new List<string>();
        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                continue;
            string win32 = LongPath.NormalizeRoot(root).Win32;
            if (!normalized.Contains(win32, StringComparer.OrdinalIgnoreCase))
                normalized.Add(win32);
        }
        return normalized;
    }

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
        private const int RemoteProtocolInfoSize = 128; // FILE_REMOTE_PROTOCOL_INFO is 116 bytes

        // What a sync engine sets on folders it manages. Such a folder is listed as before, its last-access time
        // not kept: a metadata call there could reach the engine (review M6).
        private const uint CloudFolderAttributes = Kernel32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Kernel32.FILE_ATTRIBUTE_RECALL_ON_OPEN
            | Kernel32.FILE_ATTRIBUTE_OFFLINE | Kernel32.FILE_ATTRIBUTE_PINNED | Kernel32.FILE_ATTRIBUTE_UNPINNED;

        private readonly ScanStore _store = new();
        private readonly DirectoryReader _reader = new(io);
        // The store's one writer. Every change to it after an append, and to the totals, comes through Correct and
        // Credit, which refuse any other thread (review M2).
        private readonly ThreadOwner _owner = new("A scan's store and totals");
        // Row: the folder's top-level row in the tracker, inherited from the root child it is under.
        private readonly Stack<(int Entry, string Path, int Row)> _pending = new();
        private readonly TopLevelTracker _topLevel = new(progress, display, options.TopLevelInterval);
        private readonly List<UnreadableFolder> _unreadable = [];
        private readonly IReadOnlyList<string> _syncRoots = options.SyncRoots is { } roots ? NormalizeSyncRoots(roots) : SyncRootsFromEnvironment();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _fullClass = options.ForceFullDirectoryClass;
        private bool _keepLastAccess; // the root's volume is local NTFS: decided once, at the root
        private string? _lastFolder;
        private TimeSpan _listingTime, _sizingTime, _longestProbe;
        private long _files, _folders, _links, _bytes, _bytesOnDisk, _filesWithoutId;
        private int _probes, _probeFailures, _probeCorrections, _vanished, _foldersListed, _lastAccessAsked, _lastAccessKept;

        public ScanResult Execute()
        {
            // First, before any way out of the scan, so that whatever its ending the figure is answered or
            // owned by its read (SL-043, m5). Nothing here waits for it.
            VolumeSpace.Start(display, _store.VolumeUsedSource, options.VolumeReader, options.VolumeReadBound, options.ReadVolumeOfAnyRoot);
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
                bool keep = KeepsLastAccess(path, (uint)_store.Attributes(folder));
                error = _reader.Read(path, _fullClass, keep);
                CountListingOpen(keep);
                if (FolderDone(folder, path, row, error))
                    return Finish(ScanStatus.Cancelled);
            }
            _listingTime = _clock.Elapsed;

            bool sized = SizeFiles();
            _sizingTime = _clock.Elapsed - _listingTime;
            return Finish(sized ? ScanStatus.Completed : ScanStatus.Cancelled);
        }

        // Opens the root, checks it is a folder, decides from its volume whether folders' last-access times are
        // kept, and picks the listing class from its first call. Returns the root's entry, or -1 with the error
        // when the root cannot be read at all.
        private int ListRoot(out int rootEntry)
        {
            rootEntry = -1;
            int error = _reader.Open(win32Root, keepLastAccess: false, out SafeFileHandle? handle);
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

                error = DecideLastAccess(handle!);
                if (error != 0)
                    return error;
                bool keep = KeepsLastAccess(win32Root, attributes);
                if (keep)
                {
                    // Opened again now that the volume is known, through a handle that keeps the root's last-access
                    // time. Nothing was listed through the first one, and an open alone moves no time.
                    error = _reader.Open(win32Root, keepLastAccess: true, out SafeFileHandle? keeping);
                    if (error != 0)
                        return error;
                    io.Close(handle!);
                    handle = keeping;
                }
                CountListingOpen(keep);

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

        // Decides once, from the root's handle, whether this scan keeps folders' last-access times: only on a local
        // NTFS volume (MayKeepLastAccess). A volume that will not say keeps nothing. 0, or Aborted when Cancel
        // stopped one of the two queries.
        private unsafe int DecideLastAccess(SafeFileHandle root)
        {
            int error = io.QueryFileSystemName(root, out string? fileSystem);
            if (io.LastCallAborted)
                return SynchronousIoCancel.Aborted;
            bool remote = false;
            if (error == 0)
            {
                // Answered only for a handle on a network file system, which may call itself NTFS.
                byte* info = stackalloc byte[RemoteProtocolInfoSize];
                remote = io.Query(root, Kernel32.FileRemoteProtocolInfo, info, RemoteProtocolInfoSize) == 0;
                if (io.LastCallAborted)
                    return SynchronousIoCancel.Aborted;
            }
            _keepLastAccess = error == 0 && MayKeepLastAccess(fileSystem, remote);
            return 0;
        }

        // Whether a folder's listing handle is asked to keep its last-access time: on the volume that allows it, and
        // not in a folder a sync engine manages (review M6). The folder itself may still refuse (KeptLastAccess).
        private bool KeepsLastAccess(string folderPath, uint attributes)
        {
            if (!_keepLastAccess || (attributes & CloudFolderAttributes) != 0)
                return false;
            foreach (string root in _syncRoots)
            {
                if (IsAtOrUnder(folderPath, root))
                    return false;
            }
            return true;
        }

        private void CountListingOpen(bool asked)
        {
            if (!asked)
                return;
            _lastAccessAsked++;
            if (io.KeptLastAccess)
                _lastAccessKept++;
        }

        // Appends the listing as the folder's children, queues its subfolders, publishes progress, and reports
        // whether the scan must stop. An aborted listing commits nothing and stops the scan. The one exit of every
        // folder's listing, so the top-level tracker hears of each committed folder once, here: the root's listing
        // seeds its rows, and every other folder is credited to its row.
        private bool FolderDone(int folder, string folderPath, int row, int error)
        {
            if (ClassifyReadError(error, ct.IsCancellationRequested) == ReadOutcome.Cancelled)
                return true;

            int first = _store.Count;
            int count = _reader.Count;
            for (int k = 0; k < count; k++)
                AddEntry(folder, k);
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
            _lastFolder = folderPath;
            progress.Publish(Totals, _unreadable.Count, folderPath);
            options.AfterFolder?.Invoke(_foldersListed);
            return ct.IsCancellationRequested;
        }

        private void AddEntry(int parent, int index)
        {
            ref readonly RawEntry raw = ref _reader[index];
            bool isDirectory = (raw.Attributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) != 0;
            (EntryKind kind, bool cloudOnly) = EntryClassifier.Classify(raw.Attributes, raw.ReparseTag, isDirectory);
            UInt128 fileId = options.ListedFileId is { } listed ? listed(raw.FileId) : raw.FileId;
            // The store applies the counting rule: a folder or a link posts nothing, and a later name of a
            // file already charged is flagged and posts one file and no bytes. A file is charged its listing sizes
            // here, which SizeFiles replaces with the file's own once every folder is listed.
            int entry = _store.Append(parent, _reader.NameOf(raw), kind, cloudOnly ? EntryFlags.CloudOnly : EntryFlags.None,
                raw.Attributes, raw.EndOfFile, raw.AllocationSize, fileId, out _, out ScanStore.Posted posted);
            Credit(entry, posted, inPlace: false);

            if (kind == EntryKind.Link)
                _links++;
            else if (kind == EntryKind.Folder)
                _folders++;
            else if (!ScanStore.HasFileId(fileId))
                _filesWithoutId++; // never deduplicated: any other name it has is charged in full too
        }

        // Sizes every file the walk charged from the file itself (SL-056), a batch at a time: this thread builds the
        // batch's paths from the store, the pass's workers probe them, and this thread applies every reading once
        // the batch's workers have all finished. A later name of a hardlinked file is never probed: it charges
        // nothing, and the first name's probe reads the same file (review M4). False when Cancel stopped a batch,
        // which commits nothing: its files keep their listing sizes, like every file of a batch never reached.
        private bool SizeFiles()
        {
            ProbeRule rule = options.ProbeRule ?? ((kind, flags, _, _, _) => SizeProbe.IsEligible(kind, flags));
            int due = 0;
            for (int e = 1; e < _store.Count; e++)
            {
                if (IsDue(rule, e))
                    due++;
            }
            progress.PublishSizing(0, due);
            if (due == 0)
                return true;

            var pass = new ProbePass(ct, options.Probe ?? SizeProbe.Probe,
                options.ProbeWorkers > 0 ? options.ProbeWorkers : ProbePass.DefaultWorkers, options.ProbeAbandonAfter);
            int size = Math.Min(due, options.ProbeBatch > 0 ? options.ProbeBatch : ProbePass.BatchSize);
            var entries = new int[size];
            var paths = new string[size];
            var readings = new ProbeReading[size];
            int batched = 0, sized = 0, parent = -1;
            string parentPath = win32Root;
            try
            {
                for (int e = 1; e < _store.Count; e++)
                {
                    if (!IsDue(rule, e))
                        continue;
                    int p = _store.Parent(e);
                    if (p != parent)
                    {
                        parent = p;
                        parentPath = p == 0 ? win32Root : LongPath.ToExtended(_store.GetPath(p));
                    }
                    entries[batched] = e;
                    paths[batched] = LongPath.Child(parentPath, _store.Name(e));
                    if (++batched < size)
                        continue;
                    if (!SizeBatch(pass, entries, paths, readings, batched))
                        return false;
                    sized += batched;
                    batched = 0;
                    Sized(sized, due);
                }
                if (batched > 0)
                {
                    if (!SizeBatch(pass, entries, paths, readings, batched))
                        return false;
                    sized += batched;
                    Sized(sized, due);
                }
                return true;
            }
            finally
            {
                _longestProbe = pass.LongestProbe;
            }
        }

        private bool IsDue(ProbeRule rule, int entry) =>
            rule(_store.Kind(entry), _store.Flags(entry), (uint)_store.Attributes(entry), _store.Size(entry), _store.OnDisk(entry));

        private bool SizeBatch(ProbePass pass, int[] entries, string[] paths, ProbeReading[] readings, int count)
        {
            if (pass.Run(paths, readings, count) == BatchOutcome.Cancelled)
                return false;
            for (int k = 0; k < count; k++)
                Correct(entries[k], readings[k]);
            return true;
        }

        private void Sized(int sized, int due)
        {
            progress.Publish(Totals, _unreadable.Count, _lastFolder ?? win32Root);
            progress.PublishSizing(sized, due);
            _topLevel.Corrected(Totals);
        }

        // Applies one probe's reading. The file's own sizes replace its listing's, which the store posts up its
        // folders and this credits to the totals; a refusal (ProbeFailed) or a file gone since it was listed keeps
        // the listing's sizes.
        private void Correct(int entry, ProbeReading reading)
        {
            _owner.Check();
            _probes++;
            if (reading.Error != 0)
            {
                if (SizeProbe.IsGone(reading.Error))
                {
                    _vanished++;
                    return;
                }
                _store.AddFlags(entry, EntryFlags.ProbeFailed);
                _probeFailures++;
                return;
            }

            EntryFlags flags = EntryFlags.SizeProbed;
            if (_store.Size(entry) != reading.EndOfFile || _store.OnDisk(entry) != reading.AllocationSize)
            {
                Credit(entry, _store.CorrectSizes(entry, reading.EndOfFile, reading.AllocationSize), inPlace: true);
                flags |= EntryFlags.ProbeCorrected;
                _probeCorrections++;
            }
            _store.AddFlags(entry, flags);
        }

        // The one way what the store posted reaches the running totals, so the root's rollups stay the
        // totals. An append is always under the folder being listed, whose row the tracker credits when the
        // listing commits. A correction can land on a record under any row, so that row is credited now.
        private void Credit(int entry, ScanStore.Posted posted, bool inPlace)
        {
            _owner.Check();
            _files += posted.Files;
            _bytes += posted.Size;
            _bytesOnDisk += posted.OnDisk;
            if (inPlace)
                _topLevel.Moved(entry, posted.Size, posted.OnDisk);
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
            VanishedBeforeProbe = _vanished,
            FileIdsAvailable = status != ScanStatus.RootUnreadable && _filesWithoutId == 0,
            Elapsed = _clock.Elapsed,
            Probes = _probes,
            ProbeCorrections = _probeCorrections,
            ListingTime = _listingTime == TimeSpan.Zero ? _clock.Elapsed : _listingTime,
            SizingTime = _sizingTime,
            LongestProbe = _longestProbe,
            LastAccessAsked = _lastAccessAsked,
            LastAccessKept = _lastAccessKept,
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
}
