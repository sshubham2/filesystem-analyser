using System.Runtime.InteropServices;
using FsAnalyser.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// The only way the scanner makes a Win32 filesystem call, so that Cancel can interrupt the call the scan
/// thread is blocked in: an open waiting on a share that does not answer (21 s measured), or a listing
/// waiting on a cloud provider (measured never to return by itself).
/// </summary>
/// <remarks>
/// One per thread that makes the scan's calls, created on that thread and used only there: the scan thread's
/// own, and one for each of its probe workers (SL-056, <see cref="ProbePass"/>). CancelSynchronousIo interrupts
/// one named thread's call.
/// <para>Every call is bracketed. Enter publishes that the thread is in a call, then refuses once the
/// token is cancelled, so no call starts after Cancel. Exit reports whether a cancel was issued during the
/// call, and if one was, the call is aborted: its result is discarded and reported as
/// <see cref="Aborted"/>, whatever Windows returned. A cancelled cloud listing returns SUCCESS with
/// nothing in it (measured), so only this recorded intent tells it from an empty folder.</para>
/// <para>The token's callback runs inside Cancel(), on the UI thread in the app, and CancelSynchronousIo
/// can itself block, so the callback only starts the scan-cancel thread. That thread marks the call
/// cancelled, then starts a CancelSynchronousIo every 25 ms until the scan thread leaves it: a call that has
/// not reached the kernel yet is not found, and a cloud fetch can be re-issued. Each one runs on a thread
/// of its own: Windows was observed to hold a CancelSynchronousIo in the kernel while the call it
/// interrupted waits again, and a resend queued behind it never ran (SL-055, see
/// <see cref="Kernel32.CancelSynchronousIo"/>). Exit waits for the ones still running,
/// so no cancel lands after the call, and Dispose does the same before closing the thread handle.</para>
/// <para>A token that cannot be cancelled, or already is, still brackets every call, so every test scan
/// runs the app's path, but opens no thread handle and registers nothing.</para>
/// </remarks>
internal sealed unsafe class SynchronousIoCancel : IDisposable
{
    /// <summary>What an aborted call returns: ERROR_OPERATION_ABORTED.</summary>
    public const int Aborted = Kernel32.ERROR_OPERATION_ABORTED;

    private const int Idle = 0;
    private const int InCall = 1;
    private const int CancelIssued = 2;
    // Can a local cancel take more than 2 s under load (SL-055)? One that lands, no: 500 of 500 freed the
    // scan thread in at most 622 ms (p99 423 ms) with every CPU busy, on a Ryzen 9 8945H under Windows 11
    // 26200 (risk spike delivered-cancel-latency-under-load). One that is lost, yes, without bound: every run
    // past 2 s in 300 was a resend stuck behind a CancelSynchronousIo the kernel held, the scan thread still
    // blocked at 30 s (risk spike flake-is-lost-cancel-not-slow-cancel). That is why each attempt has its own
    // thread; neither this interval nor the tests' 2 s bar was the cause.
    private const int RetryMilliseconds = 25;

    // A redirector that ignores cancel holds every attempt; this bounds the threads it can keep. Measured on
    // one CPU with the call waiting again up to 8 times, an attempt held in the kernel returned as soon as the
    // next one interrupted the new wait: 2 were live at once, and 3 in 2 of 87 runs under load (SL-055).
    private const int MaxAttemptsInKernel = 4;

    private static int s_pendingCloses;

    // What a listing handle is given before anything is listed through it: LastAccessTime -1, "do not update it
    // through this handle", and 0, "leave it as it is", in every other field (SL-056). The one payload the scanner
    // ever sets, built once and checked when the type loads: a wrong size or field order would write wrong times
    // into every folder a scan lists (review m7).
    private static readonly Kernel32.FILE_BASIC_INFO KeepLastAccessInfo = CheckedKeepLastAccessInfo();

    /// <summary>Test seam: set on a thread before it runs a scan, and handed the guard that scan creates.</summary>
    [ThreadStatic]
    internal static Action<SynchronousIoCancel>? Created;

    private readonly CancellationToken _ct;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly SafeThreadHandle? _thread;
    private readonly CancellationTokenRegistration _registration;
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _noAttempts = new(initialState: true, spinCount: 0);
    private Thread? _canceller;
    private volatile bool _disposed;
    private int _state;
    private int _calls;
    private int _attempts;
    private int _delivered;
    private int _handedOff;
    private int _live; // cancel attempts reserved and not yet ended, under _gate
    private int _peakLive;
    private int _liveAtLastDrain;
    private bool _aborted;

    public SynchronousIoCancel(CancellationToken ct)
        : this(ct, announce: true)
    {
    }

    /// <summary>
    /// A guard that <see cref="Created"/> is not told of when <paramref name="announce"/> is false: the container layer
    /// store's resolution, which runs on a guard of its own before the scan's (SL-095), so that the scan's guard numbers
    /// its calls as it always has and a test that arms a call by its number arms the walk's.
    /// </summary>
    internal SynchronousIoCancel(CancellationToken ct, bool announce)
    {
        _ct = ct;
        if (ct.CanBeCanceled && !ct.IsCancellationRequested)
        {
            SafeThreadHandle thread = Kernel32.OpenThread(Kernel32.THREAD_TERMINATE, false, Kernel32.GetCurrentThreadId());
            if (thread.IsInvalid)
            {
                // Cancel falls back to the folder boundary; the scan does not fail.
                thread.Dispose();
            }
            else
            {
                _thread = thread;
                _registration = ct.UnsafeRegister(static guard => ((SynchronousIoCancel)guard!).StartCanceller(), this);
            }
        }
        if (announce)
            Created?.Invoke(this);
    }

    /// <summary>Tests only: called in Enter with the call's number (from 1), before the token is checked.</summary>
    internal Action<int>? BeforeCall { get; set; }

    /// <summary>Tests only: called when the Win32 call has returned, before Exit reads whether a cancel was issued.</summary>
    internal Action<int>? AfterCall { get; set; }

    /// <summary>
    /// Tests only: called on a cancel attempt's own thread with its number (from 1), once it may cancel and
    /// before it calls CancelSynchronousIo. Holds an attempt there, as a busy machine can.
    /// </summary>
    internal Action<int>? BeforeCancelCall { get; set; }

    /// <summary>
    /// Tests only: given a <see cref="Query"/> call's number and the error Windows returned, the error to
    /// report instead, as if Windows had returned it. Feeds in errors Windows gives rarely, such as a 995
    /// nobody asked for.
    /// </summary>
    internal Func<int, int, int>? OverrideQueryError { get; set; }

    // The listing failpoint (SL-082): null, as in every run of the app, until FailListingOf arms it. Written once,
    // read by every thread that lists.
    private static volatile ListingFailure? s_failListing;

    private sealed record ListingFailure(string FolderName, int Page, int Error, Action<string, int>? Fired);

    /// <summary>
    /// Tests only (SL-082): arms the listing failpoint for the rest of the process. From then on every listing of a
    /// folder whose own name is <paramref name="folderName"/>, compared without case, reports
    /// <paramref name="win32Error"/> in place of its <paramref name="page"/>-th page, as if Windows had failed that
    /// call (<see cref="ListingError"/>). The pages before it are kept, so the folder is listed partway and marked
    /// unreadable, the way a listing Windows fails partway is. A listing that ends before that page is left alone,
    /// and so is the scan root, which is listed through a handle and no name. <paramref name="fired"/> is told the
    /// name of the folder it failed, as listed, and the page each time it fires. The product reads no flag or
    /// environment variable that arms it, and never calls this: arming it takes code injected into the process, as
    /// the window cases' startup hook calls it by reflection before the app's Main, the one way to the tree's "could
    /// not be read in full" row, since an ordinary user cannot make a real listing fail partway. It ships dormant, not
    /// compiled out, like the other test seams here, so it is no boundary against code already running in the process.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or holds a path separator: it is one folder's name.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page is under 2: a first page that fails is a folder never
    /// opened, not one read partway. Or the error is 0 or ERROR_NO_MORE_FILES, which end a listing as complete.</exception>
    internal static void FailListingOf(string folderName, int page, int win32Error, Action<string, int>? fired = null)
    {
        if (string.IsNullOrWhiteSpace(folderName) || folderName.IndexOfAny(['\\', '/']) >= 0)
            throw new ArgumentException($"'{folderName}' is not one folder's name", nameof(folderName));
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 2);
        if (win32Error is <= 0 or Kernel32.ERROR_NO_MORE_FILES)
            throw new ArgumentOutOfRangeException(nameof(win32Error), win32Error, "an error that fails a listing, not 0 or ERROR_NO_MORE_FILES");
        s_failListing = new ListingFailure(folderName, page, win32Error, fired);
    }

    /// <summary>
    /// What the <paramref name="page"/>-th call listing <paramref name="win32Path"/> returned, <paramref name="error"/>,
    /// or the failpoint's error in its place when <see cref="FailListingOf"/> armed it for that folder and page and
    /// the call returned a page (0). Always <paramref name="error"/> in the app.
    /// </summary>
    internal static int ListingError(string win32Path, int page, int error)
    {
        if (s_failListing is not { } fail || error != 0 || page != fail.Page
            || !win32Path.AsSpan(win32Path.LastIndexOf('\\') + 1).Equals(fail.FolderName, StringComparison.OrdinalIgnoreCase))
            return error;
        fail.Fired?.Invoke(win32Path[(win32Path.LastIndexOf('\\') + 1)..], page);
        return fail.Error;
    }

    /// <summary>
    /// Whether the last call was aborted by this guard: refused after Cancel, or a cancel was issued
    /// during it. False for an error Windows returned by itself, 995 included.
    /// </summary>
    internal bool LastCallAborted { get; private set; }

    /// <summary>How many calls were bracketed, refused ones included.</summary>
    internal int Calls => _calls;

    /// <summary>How many times the canceller's attempts called CancelSynchronousIo.</summary>
    internal int CancelAttempts => Volatile.Read(ref _attempts);

    /// <summary>The most cancel attempts that were live at once, held in the kernel or about to call it.</summary>
    internal int PeakAttemptsInKernel
    {
        get
        {
            lock (_gate)
                return _peakLive;
        }
    }

    /// <summary>
    /// How many cancel attempts were still live when Exit, a refused Enter or Dispose last waited for them
    /// to end. Nothing waits when no cancel was issued during the call.
    /// </summary>
    internal int AttemptsLiveAtLastDrain
    {
        get
        {
            lock (_gate)
                return _liveAtLastDrain;
        }
    }

    /// <summary>How many of those found a call to cancel (returned TRUE).</summary>
    internal int CancelsDelivered => Volatile.Read(ref _delivered);

    /// <summary>Whether the canceller has marked the call in progress as cancelled.</summary>
    internal bool CancelIsIssued => Volatile.Read(ref _state) == CancelIssued;

    internal bool HasThreadHandle => _thread is not null;

    /// <summary>How many handles this guard handed to a pool thread to close.</summary>
    internal int ClosesHandedOff => _handedOff;

    /// <summary>Handles of aborted calls still waiting for their close on a pool thread, across all scans.</summary>
    internal static int PendingCloses => Volatile.Read(ref s_pendingCloses);

    /// <summary>
    /// Opens a directory for listing: FILE_LIST_DIRECTORY, every share mode, backup semantics, and no
    /// OPEN_REPARSE_POINT, so a link a person chose as the root is opened through to its target. The one way the
    /// scanner gets a listing handle. A folder's 1920 is never retried: it stays unreadable (U-004).
    /// </summary>
    /// <remarks>
    /// With <paramref name="keepLastAccess"/> the listing leaves the folder's last-access time alone (SL-056): the
    /// open also asks for FILE_WRITE_ATTRIBUTES, and before the handle is handed out it is given LastAccessTime -1
    /// and 0 in every other field, which changes nothing and tells the file system not to update that time for
    /// anything done through the handle. Measured on local NTFS to leave a listed folder's four times, attributes
    /// and USN as they were (spike folder-atime-suspend-on-list-handle, 3 of 3). A folder that refuses the
    /// write-attributes open or the set, as most of C:\Windows does to an ordinary user, is opened plainly
    /// instead and listed as before, its last-access time moving: the open fails only when the plain open fails.
    /// All of it is one bracketed call. <see cref="KeptLastAccess"/> says which way it went.
    /// </remarks>
    /// <returns>0 with the handle, or the Win32 error with none. An aborted open hands out no handle,
    /// even one Windows returned.</returns>
    public int OpenDirectory(string win32Path, bool keepLastAccess, out SafeFileHandle? handle) =>
        Open(win32Path, Kernel32.FILE_LIST_DIRECTORY, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, keepLastAccess, out handle);

    /// <summary>
    /// Opens a file with FILE_READ_ATTRIBUTES only, never data, and without OPEN_REPARSE_POINT, so a WOF, dedup or
    /// cloud file is answered by its filter. The probe's first open.
    /// </summary>
    /// <returns>As <see cref="OpenDirectory"/>.</returns>
    public int OpenForAttributes(string win32Path, out SafeFileHandle? handle) =>
        Open(win32Path, Kernel32.FILE_READ_ATTRIBUTES, 0, keepLastAccess: false, out handle);

    /// <summary>
    /// Opens a folder, or a file, with FILE_READ_ATTRIBUTES only and backup semantics, without OPEN_REPARSE_POINT, so a
    /// junction or symbolic link is opened through to its target: nothing is listed and no data is read. Only for the
    /// container layer store's resolution by handle (SL-095, <see cref="ContainerLayerStore"/>), which asks the handle
    /// for its final path.
    /// </summary>
    /// <returns>As <see cref="OpenDirectory"/>.</returns>
    public int OpenForFinalPath(string win32Path, out SafeFileHandle? handle) =>
        Open(win32Path, Kernel32.FILE_READ_ATTRIBUTES, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, keepLastAccess: false, out handle);

    /// <summary>
    /// GetFinalPathNameByHandleW, normalized, with its drive letter: the path <paramref name="handle"/>'s file is
    /// reached by, in \\?\ form (\\?\UNC\ for a share), once subst drives, junctions, 8.3 names and volume GUIDs are
    /// resolved.
    /// </summary>
    /// <returns>0 with the path, or the Win32 error with none.</returns>
    public int QueryFinalPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        const int Length = 32 * 1024 + 8; // past the longest path NTFS holds, with its \\?\ prefix
        char[] buffer = new char[Length];
        if (!Enter())
            return Aborted;
        int error = 0;
        bool issued;
        try
        {
            fixed (char* chars = buffer)
            {
                uint written = Kernel32.GetFinalPathNameByHandle(handle, chars, Length, Kernel32.FinalPathNormalizedDos);
                if (written == 0)
                    error = LastError();
                else if (written >= Length)
                    error = Kernel32.ERROR_INSUFFICIENT_BUFFER;
                else
                    path = new string(chars, 0, (int)written);
            }
            AfterCall?.Invoke(_calls);
        }
        finally
        {
            issued = Exit();
        }
        if (issued)
            path = null;
        return Settle(error, issued);
    }

    /// <summary>
    /// The same open as <see cref="OpenForAttributes"/>, with OPEN_REPARSE_POINT: the reparse point itself is opened,
    /// past any filter. Only for the probe's retry after its first open failed with ERROR_CANT_ACCESS_FILE, a tag no
    /// filter handles, such as an AF_UNIX socket's or an app-execution alias's (SL-084). Never a first open: the flag
    /// skips wof.sys, cldflt and dedup, whose files the first open sizes through their filters.
    /// </summary>
    /// <returns>As <see cref="OpenDirectory"/>, or the error <see cref="OverrideReparseRetryError"/> reports instead.</returns>
    public int OpenReparsePointForAttributes(string win32Path, out SafeFileHandle? handle)
    {
        int error = Open(win32Path, Kernel32.FILE_READ_ATTRIBUTES, Kernel32.FILE_FLAG_OPEN_REPARSE_POINT, keepLastAccess: false, out handle);
        if (OverrideReparseRetryError is not { } overrideError)
            return error;
        int reported = overrideError(error);
        if (reported == error)
            return error;
        if (reported == 0)
            throw new InvalidOperationException($"OverrideReparseRetryError cannot turn error {error} into an open: there is no handle to hand out.");
        if (handle is not null)
            Close(handle);
        handle = null;
        return reported;
    }

    /// <summary>
    /// Tests only: given the error <see cref="OpenReparsePointForAttributes"/> got (0 when it opened), a failure to
    /// report instead, as if Windows had refused the retry: a file deleted between the probe's two opens, or refused
    /// on the second. A handle it opened is closed, not handed out. Never an open that failed made to succeed.
    /// </summary>
    internal Func<int, int>? OverrideReparseRetryError { get; set; }

    /// <summary>
    /// Whether the last open that handed out a handle gave one through which the folder's last-access time is
    /// not updated. False after any open that asked for nothing of the kind, or that failed.
    /// </summary>
    internal bool KeptLastAccess { get; private set; }

    /// <summary>The name of the file system that holds <paramref name="handle"/>: GetVolumeInformationByHandleW.</summary>
    /// <returns>0 with the name (NTFS, ReFS, FAT32, exFAT, and a share's own answer), or the Win32 error with none.</returns>
    public int QueryFileSystemName(SafeFileHandle handle, out string? name)
    {
        name = null;
        const int Length = 261; // MAX_PATH + 1, as the documentation asks
        char* buffer = stackalloc char[Length];
        if (!Enter())
            return Aborted;
        int error = 0;
        bool issued;
        try
        {
            if (Kernel32.GetVolumeInformationByHandle(handle, null, 0, null, null, null, buffer, Length))
                name = new string(buffer);
            else
                error = LastError();
            AfterCall?.Invoke(_calls);
        }
        finally
        {
            issued = Exit();
        }
        if (issued)
            name = null;
        return Settle(error, issued);
    }

    /// <summary>GetFileInformationByHandleEx.</summary>
    /// <returns>0, or the Win32 error. After <see cref="Aborted"/> the buffer holds nothing to read.</returns>
    public int Query(SafeFileHandle handle, int infoClass, void* buffer, uint bufferSize)
    {
        if (!Enter())
            return Aborted;
        int error = 0;
        bool issued;
        try
        {
            if (!Kernel32.GetFileInformationByHandleEx(handle, infoClass, buffer, bufferSize))
                error = LastError();
            if (OverrideQueryError is { } overrideError)
                error = overrideError(_calls, error);
            AfterCall?.Invoke(_calls);
        }
        finally
        {
            issued = Exit();
        }
        return Settle(error, issued);
    }

    /// <summary>
    /// Closes a handle a guarded open gave out. Once a call of this scan was aborted, the close runs on a
    /// pool thread instead: on a share that stopped answering it can wait on the server, and Cancel would
    /// only have moved the hang into cleanup.
    /// </summary>
    public void Close(SafeFileHandle handle)
    {
        if (!_aborted || handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }
        _handedOff++;
        Interlocked.Increment(ref s_pendingCloses);
        ThreadPool.UnsafeQueueUserWorkItem(static aborted =>
        {
            try
            {
                aborted.Dispose();
            }
            finally
            {
                Interlocked.Decrement(ref s_pendingCloses);
            }
        }, handle, preferLocal: false);
    }

    /// <summary>A call during which a cancel was issued is aborted, whatever Windows returned.</summary>
    internal static int Settle(int error, bool cancelIssued) => cancelIssued ? Aborted : error;

    /// <summary>Publishes that the scan thread is in a call. False, and no call may be made, once Cancel was requested.</summary>
    /// <exception cref="InvalidOperationException">The caller is not the thread that created the guard.</exception>
    internal bool Enter()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException(
                "A SynchronousIoCancel is used only on the thread that created it: CancelSynchronousIo interrupts that thread's calls and no other's. Give each scanning thread its own.");
        }
        int call = ++_calls;
        Interlocked.Exchange(ref _state, InCall);
        BeforeCall?.Invoke(call);
        if (_ct.IsCancellationRequested || _disposed)
        {
            Leave();
            _aborted = true;
            LastCallAborted = true;
            return false;
        }
        return true;
    }

    /// <summary>Leaves the call. True when a cancel was issued during it, so its result must be discarded.</summary>
    /// <remarks>Once it returns, no CancelSynchronousIo of this guard is in flight or can start, so later
    /// synchronous I/O on this thread is not aborted by it.</remarks>
    internal bool Exit()
    {
        bool issued = Leave();
        if (issued)
            _aborted = true;
        LastCallAborted = issued;
        return issued;
    }

    public void Dispose()
    {
        Thread? canceller;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            canceller = _canceller;
        }
        _registration.Dispose();
        canceller?.Join();
        // Reached without Exit, as when an exception unwinds through a using, the state may still say a
        // cancel was issued; _disposed, set under the gate, is what stops an attempt from starting.
        Drain();
        _noAttempts.Dispose();
        _thread?.Dispose();
    }

    // Idle first, then the drain: an attempt checks the state under the gate, so one that saw a call in
    // progress was counted before Drain reads the count. With no cancel issued no attempt was started, and
    // this takes no lock and waits for nothing.
    private bool Leave()
    {
        if (Interlocked.Exchange(ref _state, Idle) != CancelIssued)
            return false;
        Drain();
        return true;
    }

    // Every path the scanner opens, the root's, each folder's and each probed file's, on whichever thread opens it, is
    // opened here with the critical-error box off for the whole call (SL-077): a drive with no media fails the open
    // with ERROR_NOT_READY instead of asking for a disk. The calling thread has its own mode back on every way out.
    private int Open(string win32Path, uint access, uint flags, bool keepLastAccess, out SafeFileHandle? handle)
    {
        using Kernel32.CriticalErrorBoxOff boxOff = Kernel32.CriticalErrorBoxOff.Begin();
        handle = null;
        KeptLastAccess = false;
        if (!Enter())
            return Aborted;
        SafeFileHandle? opened = null;
        bool kept = false;
        int error = 0;
        bool issued;
        try
        {
            if (keepLastAccess)
                kept = TryOpenKeepingLastAccess(boxOff, win32Path, access, flags, out opened);
            if (!kept)
            {
                opened = Kernel32.CreateFile(boxOff, win32Path, access, Kernel32.FILE_SHARE_ALL, 0, Kernel32.OPEN_EXISTING, flags, 0);
                if (opened.IsInvalid)
                    error = LastError();
            }
            AfterCall?.Invoke(_calls);
        }
        finally
        {
            issued = Exit();
        }

        if (error != 0 || issued)
        {
            Close(opened!);
            return Settle(error, issued);
        }
        handle = opened;
        KeptLastAccess = kept;
        return 0;
    }

    // Inside Open's bracket and its critical-error scope. True with a handle whose last-access time the file system will
    // not update; false, with nothing left open, when the write-attributes open or the set is refused, and the caller
    // opens plainly.
    private static bool TryOpenKeepingLastAccess(in Kernel32.CriticalErrorBoxOff boxOff, string win32Path, uint access, uint flags,
        out SafeFileHandle? opened)
    {
        opened = Kernel32.CreateFile(boxOff, win32Path, access | Kernel32.FILE_WRITE_ATTRIBUTES, Kernel32.FILE_SHARE_ALL, 0,
            Kernel32.OPEN_EXISTING, flags, 0);
        if (!opened.IsInvalid)
        {
            Kernel32.FILE_BASIC_INFO keep = KeepLastAccessInfo;
            if (Kernel32.SetFileInformationByHandle(opened, Kernel32.FileBasicInfo, &keep, (uint)sizeof(Kernel32.FILE_BASIC_INFO)))
                return true;
        }
        opened.Dispose();
        opened = null;
        return false;
    }

    private static Kernel32.FILE_BASIC_INFO CheckedKeepLastAccessInfo()
    {
        if (sizeof(Kernel32.FILE_BASIC_INFO) != Kernel32.FileBasicInfoSize
            || Marshal.SizeOf<Kernel32.FILE_BASIC_INFO>() != Kernel32.FileBasicInfoSize
            || Marshal.OffsetOf<Kernel32.FILE_BASIC_INFO>(nameof(Kernel32.FILE_BASIC_INFO.LastAccessTime)) != 8
            || Marshal.OffsetOf<Kernel32.FILE_BASIC_INFO>(nameof(Kernel32.FILE_BASIC_INFO.FileAttributes)) != 32)
        {
            throw new PlatformNotSupportedException(
                $"FILE_BASIC_INFO is {sizeof(Kernel32.FILE_BASIC_INFO)} bytes here, not the {Kernel32.FileBasicInfoSize} with LastAccessTime at 8 " +
                "that SetFileInformationByHandle reads: setting it would write the wrong times into every folder listed.");
        }
        return new Kernel32.FILE_BASIC_INFO { LastAccessTime = -1 };
    }

    private static int LastError()
    {
        int error = Marshal.GetLastPInvokeError();
        return error == 0 ? Kernel32.ERROR_ACCESS_DENIED : error;
    }

    // The token's callback. It runs inside Cancel(), on whichever thread called it, so it does nothing that
    // can block.
    private void StartCanceller()
    {
        lock (_gate)
        {
            if (_disposed || _canceller is not null)
                return;
            _canceller = new Thread(CancelLoop) { IsBackground = true, Name = "scan-cancel" };
            _canceller.Start();
        }
    }

    private void CancelLoop()
    {
        while (!_disposed)
        {
            // The call in progress is marked cancelled before it is interrupted, so the scan thread
            // discards its result whatever it returns. Idle means no call is in progress, and none can
            // start now that the token is cancelled.
            if (Interlocked.CompareExchange(ref _state, CancelIssued, InCall) == Idle)
                return;
            StartAttempt();
            Thread.Sleep(RetryMilliseconds);
        }
    }

    // Reserves a slot under the gate before the thread starts, so the cap and the drain both see an attempt
    // from the moment it exists. A thread that fails to start gives its slot back; the next tick tries again.
    private void StartAttempt()
    {
        lock (_gate)
        {
            if (_disposed || Volatile.Read(ref _state) != CancelIssued || _live >= MaxAttemptsInKernel)
                return;
            if (_live++ == 0)
                _noAttempts.Reset();
            _peakLive = Math.Max(_peakLive, _live);
        }
        try
        {
            new Thread(CancelOnce) { IsBackground = true, Name = "scan-cancel-attempt" }.Start();
        }
        catch (Exception)
        {
            EndAttempt();
        }
    }

    // One CancelSynchronousIo, on a thread of its own: a call Windows holds in the kernel keeps only this
    // thread, and the next tick's attempt still reaches the call the scan thread blocked in again.
    private void CancelOnce()
    {
        try
        {
            int attempt;
            lock (_gate)
            {
                // Exit and Dispose change these before they drain, so an attempt that passes here is one they
                // wait for, and one that comes later cancels nothing.
                if (_disposed || Volatile.Read(ref _state) != CancelIssued)
                    return;
                attempt = Interlocked.Increment(ref _attempts);
            }
            BeforeCancelCall?.Invoke(attempt);
            // FALSE (ERROR_NOT_FOUND) when the call has not reached the kernel yet or has just left it.
            if (Kernel32.CancelSynchronousIo(_thread!))
                Interlocked.Increment(ref _delivered);
        }
        catch (ObjectDisposedException)
        {
            // The thread handle was closed: the scan is over. Dispose drains first, so this is not expected,
            // but an exception left to escape this thread would end the process.
        }
        finally
        {
            EndAttempt();
        }
    }

    private void EndAttempt()
    {
        lock (_gate)
        {
            if (--_live == 0)
                _noAttempts.Set();
        }
    }

    // Waits until no attempt is live. Called once _state is Idle or _disposed is set, so none can start.
    // Unbounded: an attempt held in the kernel was measured to return once the call it cancelled has left
    // (SL-055), and nothing here watches for one that never does.
    private void Drain()
    {
        lock (_gate)
        {
            _liveAtLastDrain = _live;
            if (_live == 0)
                return;
        }
        _noAttempts.Wait();
    }
}
