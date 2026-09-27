using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Native;

/// <summary>
/// The Win32 surface the scanner uses. Production calls only: fixture interop lives in the test project.
/// </summary>
internal static unsafe partial class Kernel32
{
    // Access rights
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100; // asked for only to keep a listed folder's last-access time (SL-056)
    public const uint THREAD_TERMINATE = 0x0001; // the right CancelSynchronousIo needs

    // Share modes and creation
    public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // read | write | delete
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // File attributes. Only EntryClassifier may look at the reparse bit.
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x200;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_PINNED = 0x80000;
    public const uint FILE_ATTRIBUTE_UNPINNED = 0x100000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    // Reparse tags, for reference: the first two carry the name-surrogate bit and so are links, DFS does
    // not and is entered. The classifier reads only the bit, never a list of tags (SL-043).
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    public const uint IO_REPARSE_TAG_DFS = 0x8000000A;

    /// <summary>
    /// The name-surrogate bit of a reparse tag (winnt.h's IsReparseTagNameSurrogate): the reparse point
    /// stands for another named entity, as a junction or a symbolic link does. EntryClassifier's link rule.
    /// </summary>
    public const uint REPARSE_TAG_NAME_SURROGATE = 0x20000000;

    // Thread error mode: no "insert a disk" box for a drive with no media, the call fails instead.
    public const uint SEM_FAILCRITICALERRORS = 0x0001;

    // FILE_INFO_BY_HANDLE_CLASS
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileAttributeTagInfo = 9;
    public const int FileRemoteProtocolInfo = 13; // answered only for a handle on a network file system
    public const int FileFullDirectoryInfo = 14;
    public const int FileFullDirectoryRestartInfo = 15;
    public const int FileIdExtdDirectoryInfo = 19;
    public const int FileIdExtdDirectoryRestartInfo = 20;

    // Win32 errors the scanner branches on
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_DIRECTORY = 267;
    public const int ERROR_OPERATION_ABORTED = 995;

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary>
    /// Times are FILETIMEs. Set through a handle, 0 leaves a field as it is, and a LastAccessTime of -1 tells the
    /// file system not to update the last-access time for anything done through that handle. 40 bytes on x64:
    /// four 8-byte times, the attributes, and 4 bytes of padding (<see cref="FileBasicInfoSize"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    /// <summary>The size of FILE_BASIC_INFO in winbase.h on x64, which every use of the struct is checked against.</summary>
    public const int FileBasicInfoSize = 40;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    /// <summary>Only SynchronousIoCancel's directory open calls this, to keep a listed folder's last-access time (SL-056).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformationByHandle(SafeFileHandle file, char* volumeName, uint volumeNameSize,
        uint* serialNumber, uint* maximumComponentLength, uint* fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeThreadHandle OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    /// <summary>Only SynchronousIoCancel's own cancel attempts call this: it can block.</summary>
    /// <remarks>Observed on Windows 11 26200, against the documented Remarks ("does not wait for all
    /// canceled operations to complete"): a call that interrupted a wait stays in the kernel while the
    /// target thread waits again, until another cancel interrupts that wait. So callers must not queue a
    /// retry behind it (SL-055's risk spikes flake-is-lost-cancel-not-slow-cancel and
    /// retry-cancel-loop-clears-flake-without-collateral, and its SynchronousIoCancelResendRaceTests).</remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelSynchronousIo(SafeThreadHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// <summary>
    /// The bytes in use on the volume that holds <paramref name="root"/>: GetDiskFreeSpaceExW's total less its
    /// free bytes, the figure Get-Volume reports as Size minus SizeRemaining (measured equal, SL-043's spike).
    /// The critical-error box is off on this thread for the call, as the BCL's drive-space getters have it,
    /// so a drive with no media answers false instead of asking for a disk. The call is not bracketed by the
    /// cancel guard: it runs on a thread of its own that nothing waits for (VolumeSpace).
    /// </summary>
    /// <param name="root">A drive's root, <c>X:\</c>, in the plain form the spike measured.</param>
    /// <returns>False, with the Win32 error, when the volume did not answer.</returns>
    public static bool TryGetVolumeUsed(string root, out long used, out int error)
    {
        bool modeSet = SetThreadErrorMode(SEM_FAILCRITICALERRORS, out uint oldMode);
        try
        {
            if (!GetDiskFreeSpaceEx(root, out _, out ulong total, out ulong free))
            {
                error = Marshal.GetLastPInvokeError();
                used = 0;
                return false;
            }
            error = 0;
            used = (long)(total - free);
            return true;
        }
        finally
        {
            if (modeSet)
                SetThreadErrorMode(oldMode, out _);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadErrorMode(uint newMode, out uint oldMode);
}

/// <summary>
/// A real handle to a thread, from OpenThread with that thread's id. Never the pseudo-handle that means
/// "the calling thread": passed from another thread, that names the wrong thread.
/// </summary>
internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeThreadHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}
