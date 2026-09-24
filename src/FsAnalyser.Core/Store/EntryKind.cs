namespace FsAnalyser.Core.Store;

/// <summary>What an entry in a scan is.</summary>
public enum EntryKind : byte
{
    /// <summary>A folder, entered and listed. Includes cloud, WCI and ProjFS folders that carry a reparse point.</summary>
    Folder = 0,

    /// <summary>A file. Includes WOF-compressed, AppExecLink, AF_UNIX, WSL-symlink and dedup files.</summary>
    File = 1,

    /// <summary>A junction or symbolic link (tag 0xA0000003 or 0xA000000C). Charged 0 bytes and never entered.</summary>
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

    /// <summary>Size and OnDisk were read from the file itself because the listing could not be trusted.</summary>
    SizeProbed = 8,

    /// <summary>A probe was due but the file could not be opened, so the listing values were kept.</summary>
    ProbeFailed = 16,
}
