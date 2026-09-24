using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Running counters for a scan in progress. One writer, the scanner, once per folder; any number of
/// readers poll them. There are no events.
/// </summary>
public sealed class ScanProgress
{
    private long _files;
    private long _folders;
    private long _links;
    private long _bytes;
    private long _bytesOnDisk;
    private long _unreadable;
    private string? _currentFolder;

    public long Files => Volatile.Read(ref _files);
    public long Folders => Volatile.Read(ref _folders);
    public long Links => Volatile.Read(ref _links);
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

    internal void Publish(in ScanTotals totals, long unreadable, string currentFolder)
    {
        Volatile.Write(ref _files, totals.Files);
        Volatile.Write(ref _folders, totals.Folders);
        Volatile.Write(ref _links, totals.Links);
        Volatile.Write(ref _bytes, totals.Bytes);
        Volatile.Write(ref _bytesOnDisk, totals.BytesOnDisk);
        Volatile.Write(ref _unreadable, unreadable);
        Volatile.Write(ref _currentFolder, currentFolder);
    }
}
