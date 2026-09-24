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
/// One per scan, created on the scan thread and used only there. A later slice that lists on several
/// threads gives each its own: CancelSynchronousIo interrupts one named thread's call.
/// <para>Every call is bracketed. Enter publishes that the thread is in a call, then refuses once the
/// token is cancelled, so no call starts after Cancel. Exit reports whether a cancel was issued during the
/// call, and if one was, the call is aborted: its result is discarded and reported as
/// <see cref="Aborted"/>, whatever Windows returned. A cancelled cloud listing returns SUCCESS with
/// nothing in it (measured), so only this recorded intent tells it from an empty folder.</para>
/// <para>The token's callback runs inside Cancel(), on the UI thread in the app, and CancelSynchronousIo
/// can itself block, so the callback only starts the scan-cancel thread. That thread marks the call
/// cancelled, then repeats CancelSynchronousIo every 25 ms until the scan thread leaves it: a call that has
/// not reached the kernel yet is not found, and a cloud fetch can be re-issued. Dispose joins it before
/// closing the thread handle, so no cancel lands after the scan.</para>
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
    private const int RetryMilliseconds = 25;

    private static int s_pendingCloses;

    /// <summary>Test seam: set on a thread before it runs a scan, and handed the guard that scan creates.</summary>
    [ThreadStatic]
    internal static Action<SynchronousIoCancel>? Created;

    private readonly CancellationToken _ct;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly SafeThreadHandle? _thread;
    private readonly CancellationTokenRegistration _registration;
    private readonly Lock _gate = new();
    private Thread? _canceller;
    private volatile bool _disposed;
    private int _state;
    private int _calls;
    private int _attempts;
    private int _delivered;
    private int _handedOff;
    private bool _aborted;

    public SynchronousIoCancel(CancellationToken ct)
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
        Created?.Invoke(this);
    }

    /// <summary>Tests only: called in Enter with the call's number (from 1), before the token is checked.</summary>
    internal Action<int>? BeforeCall { get; set; }

    /// <summary>Tests only: called when the Win32 call has returned, before Exit reads whether a cancel was issued.</summary>
    internal Action<int>? AfterCall { get; set; }

    /// <summary>How many calls were bracketed, refused ones included.</summary>
    internal int Calls => _calls;

    /// <summary>How many times the canceller called CancelSynchronousIo.</summary>
    internal int CancelAttempts => Volatile.Read(ref _attempts);

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
    /// OPEN_REPARSE_POINT, so a link a person chose as the root is opened through to its target.
    /// </summary>
    /// <returns>0 with the handle, or the Win32 error with none. An aborted open hands out no handle,
    /// even one Windows returned.</returns>
    public int OpenDirectory(string win32Path, out SafeFileHandle? handle) =>
        Open(win32Path, Kernel32.FILE_LIST_DIRECTORY, Kernel32.FILE_FLAG_BACKUP_SEMANTICS, out handle);

    /// <summary>Opens a file with FILE_READ_ATTRIBUTES only, never data, and without OPEN_REPARSE_POINT.</summary>
    /// <returns>As <see cref="OpenDirectory"/>.</returns>
    public int OpenForAttributes(string win32Path, out SafeFileHandle? handle) =>
        Open(win32Path, Kernel32.FILE_READ_ATTRIBUTES, 0, out handle);

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
            Interlocked.Exchange(ref _state, Idle);
            _aborted = true;
            return false;
        }
        return true;
    }

    /// <summary>Leaves the call. True when a cancel was issued during it, so its result must be discarded.</summary>
    internal bool Exit()
    {
        bool issued = Interlocked.Exchange(ref _state, Idle) == CancelIssued;
        if (issued)
            _aborted = true;
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
        _thread?.Dispose();
    }

    private int Open(string win32Path, uint access, uint flags, out SafeFileHandle? handle)
    {
        handle = null;
        if (!Enter())
            return Aborted;
        SafeFileHandle? opened = null;
        int error = 0;
        bool issued;
        try
        {
            opened = Kernel32.CreateFile(win32Path, access, Kernel32.FILE_SHARE_ALL, 0, Kernel32.OPEN_EXISTING, flags, 0);
            if (opened.IsInvalid)
                error = LastError();
            AfterCall?.Invoke(_calls);
        }
        finally
        {
            issued = Exit();
        }

        if (error != 0 || issued)
        {
            Close(opened);
            return Settle(error, issued);
        }
        handle = opened;
        return 0;
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
        try
        {
            while (!_disposed)
            {
                // The call in progress is marked cancelled before it is interrupted, so the scan thread
                // discards its result whatever it returns. Idle means no call is in progress, and none can
                // start now that the token is cancelled.
                if (Interlocked.CompareExchange(ref _state, CancelIssued, InCall) == Idle)
                    return;
                Interlocked.Increment(ref _attempts);
                // FALSE (ERROR_NOT_FOUND) when the call has not reached the kernel yet or has just left it.
                if (Kernel32.CancelSynchronousIo(_thread!))
                    Interlocked.Increment(ref _delivered);
                Thread.Sleep(RetryMilliseconds);
            }
        }
        catch (ObjectDisposedException)
        {
            // The thread handle was closed under the loop: the scan is over.
        }
    }
}
