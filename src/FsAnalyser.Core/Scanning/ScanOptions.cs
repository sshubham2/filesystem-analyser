namespace FsAnalyser.Core.Scanning;

/// <summary>The scanner's test seam for a whole scan; the cancel guard has its own, per call. The app never sets it.</summary>
internal sealed class ScanOptions
{
    public static readonly ScanOptions Default = new();

    /// <summary>List the whole scan with FILE_FULL_DIR_INFO, as on a volume without the extended class.</summary>
    public bool ForceFullDirectoryClass { get; init; }

    /// <summary>Called after each folder is listed, with the number of folders listed so far (the root is 1).</summary>
    public Action<int>? AfterFolder { get; init; }

    /// <summary>
    /// The least time between two top-level snapshots with something changed in them; the first and the
    /// last are always published. Tests set it to zero to see every change.
    /// </summary>
    public TimeSpan TopLevelInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
