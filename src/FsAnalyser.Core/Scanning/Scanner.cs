using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// </remarks>
public static class Scanner
{
    /// <summary>Scans <paramref name="root"/>. Cancellation is checked once per folder.</summary>
    /// <exception cref="ArgumentException">The root is empty or not an absolute path.</exception>
    public static ScanResult Scan(string root, ScanProgress progress, CancellationToken ct) =>
        Scan(root, progress, ct, ScanOptions.Default);

    internal static ScanResult Scan(string root, ScanProgress progress, CancellationToken ct, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(options);
        (string display, string win32) = LongPath.NormalizeRoot(root);
        using var run = new Run(display, win32, progress, ct, options);
        return run.Execute();
    }

    private sealed class Run(string display, string win32Root, ScanProgress progress, CancellationToken ct, ScanOptions options)
        : IDisposable
    {
        private readonly ScanStore _store = new();
        private readonly DirectoryReader _reader = new();
        private readonly Stack<(int Entry, string Path)> _pending = new();
        private readonly List<UnreadableFolder> _unreadable = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _fullClass = options.ForceFullDirectoryClass;
        private long _files, _folders, _links, _bytes, _bytesOnDisk;
        private int _probes, _probeFailures, _foldersListed;

        public ScanResult Execute()
        {
            if (ct.IsCancellationRequested)
                return Finish(ScanStatus.Cancelled);

            int error = ListRoot(out int rootEntry);
            if (rootEntry < 0)
                return Finish(ScanStatus.RootUnreadable, error);
            if (FolderDone(rootEntry, win32Root, error))
                return Finish(ScanStatus.Cancelled);

            while (_pending.Count > 0)
            {
                (int folder, string path) = _pending.Pop();
                error = _reader.Read(path, _fullClass);
                if (FolderDone(folder, path, error))
                    return Finish(ScanStatus.Cancelled);
            }

            return Finish(ScanStatus.Completed);
        }

        // Opens the root, checks it is a folder, and picks the listing class from its first call.
        // Returns the root's entry, or -1 with the error when the root cannot be read at all.
        private int ListRoot(out int rootEntry)
        {
            rootEntry = -1;
            int error = DirectoryReader.Open(win32Root, out SafeFileHandle handle);
            if (error != 0)
                return error;

            using (handle)
            {
                error = QueryAttributes(handle, out uint attributes);
                if (error != 0)
                    return error;
                // Checked before the class is picked: error 87 must only ever mean "class unsupported".
                if ((attributes & Kernel32.FILE_ATTRIBUTE_DIRECTORY) == 0)
                    return Kernel32.ERROR_DIRECTORY;

                error = _reader.List(handle, _fullClass);
                if (!_fullClass && _reader.Count == 0 && error is Kernel32.ERROR_INVALID_FUNCTION
                        or Kernel32.ERROR_NOT_SUPPORTED or Kernel32.ERROR_INVALID_PARAMETER)
                {
                    _fullClass = true;
                    error = _reader.List(handle, _fullClass);
                }
                if (error != 0 && _reader.Count == 0)
                    return error;

                rootEntry = _store.AppendRoot(display, attributes);
                return error;
            }
        }

        // Appends the reader's listing as the folder's children, queues its subfolders, publishes
        // progress, and reports whether the scan must stop.
        private bool FolderDone(int folder, string folderPath, int error)
        {
            int first = _store.Count;
            int count = _reader.Count;
            for (int k = 0; k < count; k++)
                AddEntry(folder, folderPath, in _reader[k]);
            _store.SetChildren(folder, first, count);

            if (error != 0)
            {
                _store.AddFlags(folder, EntryFlags.Unreadable);
                _unreadable.Add(new UnreadableFolder(folder, error));
            }

            // Pushed last-first so folders are visited in listing order.
            for (int k = count - 1; k >= 0; k--)
            {
                if (_store.Kind(first + k) == EntryKind.Folder)
                    _pending.Push((first + k, LongPath.Child(folderPath, _store.Name(first + k))));
            }

            _foldersListed++;
            progress.Publish(Totals, _unreadable.Count, folderPath);
            options.AfterFolder?.Invoke(_foldersListed);
            return ct.IsCancellationRequested;
        }

        private void AddEntry(int parent, string parentPath, in RawEntry raw)
        {
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
            if (!_store.TryAddFileId(entry, out int charged))
            {
                // A later name of a file already charged: it counts as a file and adds no bytes. The
                // first repeat corrects the charged record, whose listing may be stale.
                _store.AddFlags(entry, EntryFlags.HardlinkDuplicate);
                const EntryFlags settled = EntryFlags.SizeProbed | EntryFlags.ProbeFailed | EntryFlags.CloudOnly;
                if (!cloudOnly && (_store.Flags(charged) & settled) == 0)
                    ProbeAndCorrect(charged, LongPath.Child(parentPath, name));
                return;
            }

            _bytes += raw.EndOfFile;
            _bytesOnDisk += raw.AllocationSize;
            if (SizeProbe.IsDue(raw.AllocationSize, raw.EndOfFile, raw.Attributes, cloudOnly))
                ProbeAndCorrect(entry, LongPath.Child(parentPath, name));
        }

        // Replaces a charged record's listing sizes with the file's own, and the totals by the difference.
        private void ProbeAndCorrect(int charged, string path)
        {
            _probes++;
            if (SizeProbe.Probe(path, out long size, out long onDisk) != 0)
            {
                _store.AddFlags(charged, EntryFlags.ProbeFailed);
                _probeFailures++;
                return;
            }

            _bytes += size - _store.Size(charged);
            _bytesOnDisk += onDisk - _store.OnDisk(charged);
            _store.CorrectSizes(charged, size, onDisk);
            _store.AddFlags(charged, EntryFlags.SizeProbed);
        }

        private ScanTotals Totals => new(_files, _folders, _links, _bytes, _bytesOnDisk);

        private ScanResult Finish(ScanStatus status, int rootError = 0) => new()
        {
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

        private static unsafe int QueryAttributes(SafeFileHandle handle, out uint attributes)
        {
            Kernel32.FILE_ATTRIBUTE_TAG_INFO info;
            if (!Kernel32.GetFileInformationByHandleEx(handle, Kernel32.FileAttributeTagInfo, &info, (uint)sizeof(Kernel32.FILE_ATTRIBUTE_TAG_INFO)))
            {
                attributes = 0;
                return Marshal.GetLastPInvokeError();
            }
            attributes = info.FileAttributes;
            return 0;
        }

        public void Dispose() => _reader.Dispose();
    }
}
