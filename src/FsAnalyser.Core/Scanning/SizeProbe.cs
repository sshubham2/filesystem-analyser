using System.Runtime.InteropServices;
using FsAnalyser.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Reads one file's true EndOfFile and AllocationSize when its listing cannot be trusted. Opens with
/// FILE_READ_ATTRIBUTES only and never reads data; without OPEN_REPARSE_POINT, so the WOF filter
/// answers for compressed files. Runs in three cases only: a WOF suspect, a large file, and the first
/// repeat of a FileId, and never twice for one file. Cloud-only files are never passed to it.
/// </summary>
internal static unsafe class SizeProbe
{
    /// <summary>Below this a file can live inside its MFT record and truly take 0 clusters.</summary>
    public const long ResidentLimit = 4096;

    /// <summary>
    /// From this size the listing is not trusted. A file held open and growing (a VM or WSL disk, a
    /// database, a log) can show a size in its directory entry that is older than the file's own, and
    /// such files carry most of a drive's bytes. There are few of them, so the extra opens cost little.
    /// </summary>
    public const long LargeFileThreshold = 64L * 1024 * 1024;

    /// <summary>Whether a newly seen file's listing sizes must be checked against the file itself.</summary>
    public static bool IsDue(long allocationSize, long endOfFile, uint attributes, bool cloudOnly) =>
        !cloudOnly
        && (IsWofSuspect(allocationSize, endOfFile, attributes, cloudOnly)
            || endOfFile >= LargeFileThreshold
            || allocationSize >= LargeFileThreshold);

    /// <summary>
    /// The listing says nothing is allocated for a file too big to live in its MFT record, and nothing
    /// else explains it: the way a WOF-compressed file looks (the WOF filter hides its reparse point).
    /// Sparse files really can have nothing allocated, and cloud-only files must not be opened.
    /// </summary>
    public static bool IsWofSuspect(long allocationSize, long endOfFile, uint attributes, bool cloudOnly) =>
        allocationSize == 0
        && endOfFile >= ResidentLimit
        && (attributes & Kernel32.FILE_ATTRIBUTE_SPARSE_FILE) == 0
        && !cloudOnly;

    /// <returns>0 with the file's true sizes, or the Win32 error that stopped the probe.</returns>
    public static int Probe(string win32Path, out long endOfFile, out long allocationSize)
    {
        endOfFile = 0;
        allocationSize = 0;
        using SafeFileHandle handle = Kernel32.CreateFile(win32Path, Kernel32.FILE_READ_ATTRIBUTES,
            Kernel32.FILE_SHARE_ALL, 0, Kernel32.OPEN_EXISTING, 0, 0);
        if (handle.IsInvalid)
            return Error();

        Kernel32.FILE_STANDARD_INFO info;
        if (!Kernel32.GetFileInformationByHandleEx(handle, Kernel32.FileStandardInfo, &info, (uint)sizeof(Kernel32.FILE_STANDARD_INFO)))
            return Error();

        endOfFile = info.EndOfFile;
        allocationSize = info.AllocationSize;
        return 0;
    }

    private static int Error()
    {
        int error = Marshal.GetLastPInvokeError();
        return error == 0 ? Kernel32.ERROR_ACCESS_DENIED : error;
    }
}
