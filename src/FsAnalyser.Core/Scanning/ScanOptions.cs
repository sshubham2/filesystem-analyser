namespace FsAnalyser.Core.Scanning;

/// <summary>The scanner's only test seam. The app never sets it.</summary>
internal sealed class ScanOptions
{
    public static readonly ScanOptions Default = new();

    /// <summary>List the whole scan with FILE_FULL_DIR_INFO, as on a volume without the extended class.</summary>
    public bool ForceFullDirectoryClass { get; init; }

    /// <summary>Called after each folder is listed, with the number of folders listed so far (the root is 1).</summary>
    public Action<int>? AfterFolder { get; init; }
}
