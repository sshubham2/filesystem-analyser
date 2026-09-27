using FsAnalyser.Core.Native;
using FsAnalyser.Core.Store;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// Reads one file's true EndOfFile and AllocationSize. Opens with FILE_READ_ATTRIBUTES only and never reads
/// data; without OPEN_REPARSE_POINT, so the WOF filter answers for compressed files.
/// </summary>
/// <remarks>
/// Every file the scan charges is probed (SL-056), because no figure in the listing says when the listing is
/// stale: a file another process is growing lists 0 B for as long as it is held open (U-018), and a hardlink
/// name not written through keeps its old size (U-005). The handle's figures are exact in both cases, share
/// mode 0 included (U-010). A later name of a file already charged is never probed: every name of a file has the
/// same handle-reported sizes (U-010), and the first name the scan met is the one probed. Cloud-only files are
/// never opened.
/// </remarks>
internal static unsafe class SizeProbe
{
    /// <summary>
    /// Whether an appended entry is sized from the file itself: a file (a link is never one: EntryClassifier),
    /// not cloud-only, and the first name the scan met for its file ID. An entry with no usable file ID is never
    /// a later name (the store's HasFileId), so it is always probed. Read from the store's own record after the
    /// append, which has already applied the dedup.
    /// </summary>
    public static bool IsEligible(EntryKind kind, EntryFlags flags) =>
        kind == EntryKind.File && (flags & (EntryFlags.HardlinkDuplicate | EntryFlags.CloudOnly)) == 0;

    /// <summary>
    /// Whether a probe's error means the file was gone by the time it was opened: deleted, or its folder was,
    /// between being listed and being probed. Kept apart from a refusal (review m5).
    /// </summary>
    public static bool IsGone(int error) => error is Kernel32.ERROR_FILE_NOT_FOUND or Kernel32.ERROR_PATH_NOT_FOUND;

    /// <returns>0 with the file's true sizes, or the Win32 error that stopped the probe. After
    /// ERROR_OPERATION_ABORTED nothing was read, and the caller writes no size and no ProbeFailed.</returns>
    public static int Probe(SynchronousIoCancel io, string win32Path, out long endOfFile, out long allocationSize)
    {
        endOfFile = 0;
        allocationSize = 0;
        int error = io.OpenForAttributes(win32Path, out SafeFileHandle? handle);
        if (error != 0)
            return error;

        try
        {
            Kernel32.FILE_STANDARD_INFO info;
            error = io.Query(handle!, Kernel32.FileStandardInfo, &info, (uint)sizeof(Kernel32.FILE_STANDARD_INFO));
            if (error != 0)
                return error;

            endOfFile = info.EndOfFile;
            allocationSize = info.AllocationSize;
            return 0;
        }
        finally
        {
            io.Close(handle!);
        }
    }
}

/// <summary>
/// What a probe worker runs for one file: <see cref="SizeProbe.Probe"/>, or a test's stand-in (ScanOptions).
/// </summary>
internal delegate int ProbeFile(SynchronousIoCancel io, string win32Path, out long endOfFile, out long allocationSize);

/// <summary>
/// Which appended files are probed: <see cref="SizeProbe.IsEligible"/>, or a test's rule (ScanOptions), given the
/// store's record of the entry with its listed sizes.
/// </summary>
internal delegate bool ProbeRule(EntryKind kind, EntryFlags flags, uint attributes, long endOfFile, long allocationSize);
