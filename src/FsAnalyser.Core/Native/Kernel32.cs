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
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    // Reparse tags that make an entry a link
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    public const uint IO_REPARSE_TAG_DFS = 0x8000000A;

    // FILE_INFO_BY_HANDLE_CLASS
    public const int FileStandardInfo = 1;
    public const int FileAttributeTagInfo = 9;
    public const int FileFullDirectoryInfo = 14;
    public const int FileFullDirectoryRestartInfo = 15;
    public const int FileIdExtdDirectoryInfo = 19;
    public const int FileIdExtdDirectoryRestartInfo = 20;

    // Win32 errors the scanner branches on
    public const int ERROR_FILE_NOT_FOUND = 2;
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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeThreadHandle OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    /// <summary>Only SynchronousIoCancel's own canceller thread calls this: it can block.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelSynchronousIo(SafeThreadHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
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
