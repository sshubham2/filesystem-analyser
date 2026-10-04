namespace FsAnalyser.Core.Store;

/// <summary>What an entry in a scan is.</summary>
public enum EntryKind : byte
{
    /// <summary>
    /// A folder, entered and listed. Includes cloud, WCI, ProjFS and DFS folders that carry a reparse point. The one
    /// folder never entered is the Windows container layer store, which is left out by its path, not its attributes:
    /// a live layer's folders carry no ReparsePoint or Offline attribute (measured, SL-095). It stays a Folder with
    /// nothing listed and is flagged <see cref="EntryFlags.NotScanned"/>.
    /// </summary>
    Folder = 0,

    /// <summary>A file. Includes WOF-compressed, AppExecLink, AF_UNIX and dedup files.</summary>
    File = 1,

    /// <summary>
    /// A reparse point whose tag has the name-surrogate bit: a junction, a symbolic link, a WSL symlink.
    /// Charged 0 bytes and never entered.
    /// </summary>
    Link = 2,
}

/// <summary>What the scan learned about an entry beyond its kind.</summary>
[Flags]
public enum EntryFlags : byte
{
    None = 0,

    /// <summary>A folder that could not be opened or listed; see <c>ScanResult.Unreadable</c> for the Win32 error.</summary>
    Unreadable = 1,

    /// <summary>A later name of a file already charged. Counted as a file, charged 0 bytes.</summary>
    HardlinkDuplicate = 2,

    /// <summary>RECALL_ON_DATA_ACCESS, RECALL_ON_OPEN or OFFLINE: the data is not local. Never opened.</summary>
    CloudOnly = 4,

    /// <summary>
    /// Size and OnDisk were read from the file itself. Since SL-056 every file the scan charges is probed, so this
    /// marks every such file whose probe succeeded, whether or not the listing agreed.
    /// </summary>
    SizeProbed = 8,

    /// <summary>A probe was made but the file refused the open or the query, so the listing values were kept.</summary>
    ProbeFailed = 16,

    /// <summary>
    /// Set with SizeProbed when the file's own Size or OnDisk differed from its listing, so the probe, not the
    /// listing, supplied the figure the file is charged (SL-056, review m2).
    /// </summary>
    ProbeCorrected = 32,

    /// <summary>
    /// A folder the scan reached and left out on purpose, never opened, with nothing listed under it and its size not
    /// counted: the Windows container layer store (SL-095). Decided in Core; see <c>ScanResult.SkippedStores</c>.
    /// Never set with <see cref="Unreadable"/>.
    /// </summary>
    NotScanned = 64,
}
