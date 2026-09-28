using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Running counters for a scan in progress. One writer, the scanner's thread, once per folder and then once per
/// batch of files sized; any number of readers poll them. There are no events.
/// </summary>
public sealed class ScanProgress
{
    private long _files;
    private long _folders;
    private long _bytes;
    private long _bytesOnDisk;
    private long _unreadable;
    private string? _currentFolder;
    private TopLevelSnapshot? _topLevel;
    private long _filesToSize;
    private long _filesSized;

    public long Files => Volatile.Read(ref _files);
    public long Folders => Volatile.Read(ref _folders);
    public long Bytes => Volatile.Read(ref _bytes);
    public long BytesOnDisk => Volatile.Read(ref _bytesOnDisk);
    public long Unreadable => Volatile.Read(ref _unreadable);

    /// <summary>The folder most recently listed, as a person would write it.</summary>
    public string? CurrentFolder
    {
        get
        {
            string? path = Volatile.Read(ref _currentFolder);
            return path is null ? null : LongPath.ToDisplay(path);
        }
    }

    /// <summary>
    /// The scan root's direct children as last published (SL-039): null until the root has been listed,
    /// and for the whole scan when it never is. Each read is an immutable snapshot, published by the scanner
    /// at most every 250 ms and once more at the end (the same object as <see cref="ScanResult.TopLevel"/>).
    /// Each scan has its own ScanProgress, so a snapshot never outlives its scan here.
    /// </summary>
    public TopLevelSnapshot? TopLevel => Volatile.Read(ref _topLevel);

    /// <summary>
    /// How many files the scan is reading its own size for, once every folder is listed (SL-056); 0 while it is
    /// still listing. From then on the counters above take each batch's corrections as it is applied.
    /// </summary>
    public long FilesToSize => Volatile.Read(ref _filesToSize);

    /// <summary>How many of <see cref="FilesToSize"/> have been sized and applied.</summary>
    public long FilesSized => Volatile.Read(ref _filesSized);

    internal void PublishTopLevel(TopLevelSnapshot snapshot) => Volatile.Write(ref _topLevel, snapshot);

    internal void PublishSizing(long sized, long toSize)
    {
        Volatile.Write(ref _filesSized, sized);
        Volatile.Write(ref _filesToSize, toSize);
    }

    internal void Publish(in ScanTotals totals, long unreadable, string currentFolder)
    {
        Volatile.Write(ref _files, totals.Files);
        Volatile.Write(ref _folders, totals.Folders);
        Volatile.Write(ref _bytes, totals.Bytes);
        Volatile.Write(ref _bytesOnDisk, totals.BytesOnDisk);
        Volatile.Write(ref _unreadable, unreadable);
        Volatile.Write(ref _currentFolder, currentFolder);
    }
}
