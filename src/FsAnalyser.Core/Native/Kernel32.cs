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
    // Opens the reparse point itself, past its filter: wof.sys, cldflt and dedup do not answer. Only the probe's 1920
    // retry passes it (SynchronousIoCancel.OpenReparsePointForAttributes, SL-084).
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // File attributes. Only EntryClassifier may look at the reparse bit.
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x200;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_PINNED = 0x80000;
    public const uint FILE_ATTRIBUTE_UNPINNED = 0x100000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    /// <summary>
    /// The name-surrogate bit of a reparse tag (winnt.h's IsReparseTagNameSurrogate): the reparse point
    /// stands for another named entity, as a junction or a symbolic link does. EntryClassifier's link rule.
    /// A junction's and a symbolic link's tags carry it, so they are links; DFS's does not, and it is entered.
    /// The classifier reads only the bit, never a list of tags (SL-043).
    /// </summary>
    public const uint REPARSE_TAG_NAME_SURROGATE = 0x20000000;

    // Thread error mode: no "insert a disk" box for a drive with no media, the call fails instead (CriticalErrorBoxOff).
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
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_DIRECTORY = 267;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_CANT_ACCESS_FILE = 1920; // a reparse tag no filter handles: an AF_UNIX socket, an app alias

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

    /// <summary>
    /// CreateFileW, only with the critical-error box off (SL-077): the caller hands over the
    /// <see cref="CriticalErrorBoxOff"/> it holds, so no path is opened without one. A path on a drive with no
    /// media then fails with ERROR_NOT_READY instead of asking for a disk.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="boxOff"/> did not come from
    /// <see cref="CriticalErrorBoxOff.Begin"/>, so it turned nothing off.</exception>
    public static SafeFileHandle CreateFile(in CriticalErrorBoxOff boxOff,
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile)
    {
        if (!boxOff.IsArmed)
        {
            throw new InvalidOperationException(
                "A path is opened only inside a CriticalErrorBoxOff from Kernel32.CriticalErrorBoxOff.Begin(). A default one turns " +
                "nothing off, and a drive with no media would ask for a disk.");
        }
        return CreateFileW(fileName, desiredAccess, shareMode, securityAttributes, creationDisposition, flagsAndAttributes, templateFile);
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    /// <summary>Only SynchronousIoCancel's directory open calls this, to keep a listed folder's last-access time (SL-056).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    /// <summary>
    /// Only SynchronousIoCancel calls this, for the container layer store's resolution by handle (SL-095): the path the
    /// handle's file is reached by once subst drives, junctions, 8.3 names and volume GUIDs are resolved, in \\?\ form.
    /// </summary>
    /// <returns>The length written, without the terminating null; more than <paramref name="pathLength"/> when the
    /// buffer is too small (the length needed); 0 on failure.</returns>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint pathLength, uint flags);

    /// <summary>GetFinalPathNameByHandleW's flags for a normalized name with its drive letter: FILE_NAME_NORMALIZED | VOLUME_NAME_DOS.</summary>
    public const uint FinalPathNormalizedDos = 0;

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
    /// <returns>False, with the Win32 error, when the volume did not answer; false with error 0 when it answered
    /// more free bytes than total bytes (<see cref="TryUsedBytes"/>).</returns>
    public static bool TryGetVolumeUsed(string root, out long used, out int error)
    {
        using CriticalErrorBoxOff boxOff = CriticalErrorBoxOff.Begin();
        BeforeVolumeRead?.Invoke();
        if (!GetDiskFreeSpaceEx(root, out _, out ulong total, out ulong free))
        {
            error = Marshal.GetLastPInvokeError();
            used = 0;
            return false;
        }
        error = 0;
        return TryUsedBytes(total, free, out used);
    }

    /// <summary>
    /// Tests only: set on a thread before it calls <see cref="TryGetVolumeUsed"/>, and called there inside the
    /// critical-error scope, just before GetDiskFreeSpaceExW, so a test can read the mode the call runs under.
    /// </summary>
    [ThreadStatic]
    internal static Action? BeforeVolumeRead;

    /// <summary>
    /// The critical-error box off on this thread until Dispose (SL-077): SetThreadErrorMode(SEM_FAILCRITICALERRORS),
    /// and the thread's previous mode back on Dispose. The one place in Core that sets an error mode. Thread-scoped
    /// only, never the process-wide SetErrorMode, so the work inside must stay on the thread that began it. It sets
    /// exactly SEM_FAILCRITICALERRORS, as the BCL's DisableMediaInsertionPrompt does, and nested scopes unwind in order.
    /// </summary>
    /// <remarks>
    /// Made only by <see cref="Begin"/>: <c>new()</c> does not compile, and a <c>default</c> one turns nothing off, is not
    /// <see cref="IsArmed"/>, and is refused by <see cref="CreateFile"/>. The BCL shipped that mistake twice with its own
    /// scope (corefx #35045, dotnet/runtime #126544). A failed set is best effort, as there: the call still runs, and
    /// nothing is restored.
    /// </remarks>
    public readonly struct CriticalErrorBoxOff : IDisposable
    {
        private readonly uint _previous;
        private readonly bool _set;

        [Obsolete("Use Kernel32.CriticalErrorBoxOff.Begin(): a CriticalErrorBoxOff made any other way turns nothing off.", error: true)]
        public CriticalErrorBoxOff()
        {
        }

        private CriticalErrorBoxOff(bool set, uint previous)
        {
            IsArmed = true;
            _set = set;
            _previous = previous;
        }

        /// <summary>Whether this came from <see cref="Begin"/>. Not whether Windows took the mode.</summary>
        public bool IsArmed { get; }

        public static CriticalErrorBoxOff Begin() =>
            SetThreadErrorMode(SEM_FAILCRITICALERRORS, out uint previous)
                ? new CriticalErrorBoxOff(set: true, previous)
                : new CriticalErrorBoxOff(set: false, 0);

        public void Dispose()
        {
            if (_set)
                SetThreadErrorMode(_previous, out _);
        }
    }

    /// <summary>
    /// Total less free, or false when free is more than total or the difference is past a long. Under a per-user quota
    /// GetDiskFreeSpaceExW's total is the caller's quota while its free bytes are the whole volume's (HF-024): a quota
    /// below the volume's free space would wrap the difference to a large negative figure, and so would a volume that
    /// claims more than 8 EiB in use.
    /// </summary>
    internal static bool TryUsedBytes(ulong total, ulong free, out long used)
    {
        if (free > total || total - free > long.MaxValue)
        {
            used = 0;
            return false;
        }
        used = (long)(total - free);
        return true;
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
