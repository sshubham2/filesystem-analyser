using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Win32;
using Smb = SMBLibrary;

namespace FsAnalyser.Core.Tests;

// The stalled-share rig (SL-024, extracted in SL-038). Used by StalledShareTests and compiled by link into
// tests/window/StallRig, so it uses nothing from xUnit. It is the only file allowed to change the machine's
// SMB mappings (.taad/fitness.yaml): every mapping the rig makes, and every one it removes, is here.

/// <summary>An SMBLibrary server on 127.0.0.1, on a spare port, reached through a non-persistent mapping.</summary>
/// <remarks>
/// Runs are serialised per sign-in session by <see cref="RigLock"/>, held from before the mapping is made
/// until after it is removed. The SMB client allows one port per server name, so two live rigs on
/// 127.0.0.1 would collide: a second run waits for the first, or fails with a TimeoutException that says
/// so. Under the lock no live rig owns a mapping to 127.0.0.1, so any rig mapping still listed is one a
/// killed run left behind; it is removed before the new mapping is made, and nothing else is
/// (<see cref="IsRigMapping"/>). Mappings made by the pre-SL-038 fixture, which took no lock, are not
/// protected from this sweep.
/// <para>Dispose on the thread that constructed it: the lock is a Mutex, which belongs to that thread.
/// A process killed while it holds the lock leaves its mapping behind until the next run or sign-out.</para>
/// </remarks>
internal sealed class LoopbackShare : IDisposable
{
    /// <summary>How long a run waits for another run to finish with the rig: one stalled-share run is about 70 s.</summary>
    public static readonly TimeSpan WaitForOtherRun = TimeSpan.FromSeconds(150);

    private const string User = "fsa-test";
    private const string Password = "Fsa-test-2026!";
    private static readonly Regex RigRemotePath = new(@"^\\\\127\.0\.0\.1\\fsa[0-9a-fA-F]{8}$", RegexOptions.CultureInvariant);

    private readonly RigLock _lock;
    private readonly SMBServer _server;
    private readonly ConcurrentQueue<string> _log = new();
    private readonly bool _mapped;

    public LoopbackShare(string folder) : this(folder, WaitForOtherRun)
    {
    }

    public LoopbackShare(string folder, TimeSpan waitForOtherRun)
    {
        _lock = RigLock.Take(RigLock.SessionName, waitForOtherRun);
        try
        {
            Store = new StallingFileStore(new NTDirectoryFileSystem(folder));
            string name = "fsa" + Guid.NewGuid().ToString("N")[..8];
            var shares = new SMBShareCollection();
            shares.Add(new FileSystemShare(name, Store));
            var users = new IndependentNTLMAuthenticationProvider(user => string.Equals(user, User, StringComparison.OrdinalIgnoreCase) ? Password : null!);
            _server = new SMBServer(shares, new GSSProvider(users));
            _server.LogEntryAdded += (_, entry) =>
            {
                if (entry.Severity <= Utilities.Severity.Information)
                    _log.Enqueue($"{entry.Time:HH:mm:ss.fff} {entry.Severity}: {entry.Message}");
            };
            Port = FreePort();
            // SMBServer's public Start listens on 445 only, which Windows' own server holds. The overload that
            // takes a port is private.
            MethodInfo start = typeof(SMBServer).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance,
                [typeof(IPAddress), typeof(Smb.SMBTransportType), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(TimeSpan?)])
                ?? throw new InvalidOperationException("SMBLibrary no longer has SMBServer.Start(address, transport, port, ...).");
            start.Invoke(_server, [IPAddress.Loopback, Smb.SMBTransportType.DirectTCPTransport, Port, false, true, true, null]);
            RemotePath = $@"\\127.0.0.1\{name}";
        }
        catch
        {
            _lock.Dispose();
            throw;
        }
        try
        {
            // A killed run's mapping to 127.0.0.1 on a dead port would refuse this one. Only the rig's own
            // address and share prefix are ever removed: never another server's, never a drive a person mapped.
            LeftBehind = PowerShell("Get-SmbMapping | ForEach-Object { $_.RemotePath }")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsRigMapping)
                .ToList();
            string removals = string.Concat(LeftBehind.Select(path =>
                $"Remove-SmbMapping -RemotePath '{path}' -Force -UpdateProfile -Confirm:$false; "));
            PowerShell(removals +
                $"New-SmbMapping -RemotePath '{RemotePath}' -TcpPort {Port} -UserName '{User}' -Password '{Password}' -Persistent $false | Out-Null");
            _mapped = true;
        }
        catch (Exception e)
        {
            _server.Stop();
            _lock.Dispose();
            throw new InvalidOperationException($"{e.Message}\nThe server's log:\n  {ServerLog}", e);
        }
    }

    public StallingFileStore Store { get; } = null!;

    /// <summary>What the server logged at Information and above, for a failure message.</summary>
    public string ServerLog => string.Join("\n  ", _log);

    /// <summary>The share as a person would write it.</summary>
    public string RemotePath { get; } = "";

    public int Port { get; }

    /// <summary>Rig mappings a killed run left behind, removed before this run's own was made.</summary>
    public IReadOnlyList<string> LeftBehind { get; } = [];

    /// <summary>Whether a mapping is one the rig makes: 127.0.0.1 and a share named fsa and 8 hex digits, exactly.</summary>
    public static bool IsRigMapping(string remotePath) => RigRemotePath.IsMatch(remotePath);

    /// <summary>
    /// Removes every rig mapping a killed run left behind, under the lock, as a new run does before it maps (SL-089,
    /// review M5): a case that kills its rig, so that the share's teardown fails the listing it held, runs this next, so
    /// no mapping to 127.0.0.1 outlives the case. Returns what it removed.
    /// </summary>
    public static IReadOnlyList<string> SweepLeftBehind(TimeSpan waitForOtherRun)
    {
        using RigLock held = RigLock.Take(RigLock.SessionName, waitForOtherRun);
        List<string> left = PowerShell("Get-SmbMapping | ForEach-Object { $_.RemotePath }")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsRigMapping)
            .ToList();
        if (left.Count > 0)
            PowerShell(string.Concat(left.Select(path => $"Remove-SmbMapping -RemotePath '{path}' -Force -UpdateProfile -Confirm:$false; ")));
        return left;
    }

    public void Dispose()
    {
        Store.Release();
        try
        {
            if (_mapped)
                PowerShell($"Remove-SmbMapping -RemotePath '{RemotePath}' -Force -UpdateProfile -Confirm:$false");
        }
        finally
        {
            _server.Stop();
            Store.CloseAll();
            _lock.Dispose();
        }
    }

    private static string PowerShell(string command)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start powershell.exe.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"'{command.Replace(Password, "***")}' did not finish within 60 s.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{command.Replace(Password, "***")}' exited {process.ExitCode}: {(stdout.Result + stderr.Result).Trim()}");
        return stdout.Result;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// One rig run at a time per sign-in session: a named Mutex, because Windows never lets a dead holder keep
/// one. A run killed while it holds the lock either takes the mutex with it (it had the last handle) or
/// leaves it abandoned to the run already waiting, which then owns it. Either way the next run proceeds,
/// and since it cannot tell whether a killed run left a mapping, it always sweeps (LoopbackShare).
/// </summary>
internal sealed class RigLock : IDisposable
{
    /// <summary>The lock every rig run takes. Tests of the lock itself use a name of their own.</summary>
    public const string SessionName = @"Local\FsAnalyser-SmbRig";

    private readonly Mutex _mutex;

    private RigLock(Mutex mutex) => _mutex = mutex;

    /// <summary>Takes the lock, waiting up to <paramref name="wait"/> for whoever holds it.</summary>
    /// <exception cref="TimeoutException">Another run held it the whole time; this run must not start.</exception>
    public static RigLock Take(string name, TimeSpan wait)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(wait))
                throw new TimeoutException($"Another stall-rig run held {name} for {wait.TotalSeconds:N0} s, so this run did not start. " +
                    "Two rig runs cannot share 127.0.0.1: the SMB client allows one port per server name.");
        }
        catch (AbandonedMutexException)
        {
            // Its holder died without releasing it; this thread owns it now.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new RigLock(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>
/// A real folder served as it is, until the listing of one folder: that request is never answered, and
/// neither is any request after it (a close, a cancel, another listing), until the test releases them.
/// </summary>
internal sealed class StallingFileStore(Smb.INTFileStore inner) : Smb.INTFileStore
{
    private readonly ManualResetEventSlim _release = new();
    private readonly ConcurrentDictionary<Opened, byte> _open = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile string? _stallAt;
    private volatile bool _stopped;

    /// <summary>Set once the server has stopped answering.</summary>
    public ManualResetEventSlim Stalled { get; } = new();

    public TimeSpan StalledAt { get; private set; }

    /// <summary>The folder, relative to the share, whose listing is the first request not answered.</summary>
    public void StopAnsweringAtTheListingOf(string folder) => _stallAt = folder;

    /// <summary>
    /// A folder, relative to the share, whose every open is answered STATUS_ACCESS_DENIED, as a server refusing it would
    /// (SL-089): the scan records it as could not be opened, code 5.
    /// </summary>
    public string? DenyAtOpen { get; set; }

    /// <summary>
    /// Called with one line per open the server answers, 'create &lt;name&gt; &lt;status&gt;', and 'hold &lt;name&gt;' as it stops
    /// answering (SL-089), so a case can tell which folders were opened before its Stop and which after. The root is
    /// '&lt;root&gt;'. The rig stamps the time.
    /// </summary>
    public Action<string>? Trace { get; set; }

    public void Release() => _release.Set();

    /// <summary>Closes what the client left open, so the folder can be removed.</summary>
    public void CloseAll()
    {
        foreach (Opened opened in _open.Keys)
        {
            if (_open.TryRemove(opened, out _))
                inner.CloseFile(opened.Handle);
        }
    }

    public Smb.NTStatus CreateFile(out object handle, out Smb.FileStatus fileStatus, string path, Smb.AccessMask desiredAccess,
        Smb.FileAttributes fileAttributes, Smb.ShareAccess shareAccess, Smb.CreateDisposition createDisposition,
        Smb.CreateOptions createOptions, Smb.SecurityContext securityContext)
    {
        Gate();
        string name = path.Trim('\\');
        if (DenyAtOpen is { } denied && string.Equals(name, denied, StringComparison.OrdinalIgnoreCase))
        {
            handle = null!;
            fileStatus = Smb.FileStatus.FILE_DOES_NOT_EXIST;
            Trace?.Invoke($"create {name} {Smb.NTStatus.STATUS_ACCESS_DENIED}");
            return Smb.NTStatus.STATUS_ACCESS_DENIED;
        }
        Smb.NTStatus status = inner.CreateFile(out object opened, out fileStatus, path, desiredAccess, fileAttributes, shareAccess,
            createDisposition, createOptions, securityContext);
        Trace?.Invoke($"create {(name.Length == 0 ? "<root>" : name)} {status}");
        if (status != Smb.NTStatus.STATUS_SUCCESS)
        {
            handle = opened;
            return status;
        }
        var tracked = new Opened(opened, path);
        _open.TryAdd(tracked, 0);
        handle = tracked;
        return status;
    }

    public Smb.NTStatus CloseFile(object handle)
    {
        Gate();
        if (handle is not Opened opened)
            return inner.CloseFile(handle);
        return _open.TryRemove(opened, out _) ? inner.CloseFile(opened.Handle) : Smb.NTStatus.STATUS_SUCCESS;
    }

    public Smb.NTStatus QueryDirectory(out List<Smb.QueryDirectoryFileInformation> result, object handle, string fileName,
        Smb.FileInformationClass informationClass)
    {
        Gate();
        string? stallAt = _stallAt;
        if (stallAt is not null && handle is Opened opened
                && string.Equals(opened.Path.Trim('\\'), stallAt, StringComparison.OrdinalIgnoreCase))
        {
            _stopped = true;
            StalledAt = _clock.Elapsed;
            Trace?.Invoke($"hold {stallAt}");
            Stalled.Set();
            _release.Wait();
        }
        try
        {
            return inner.QueryDirectory(out result, Unwrap(handle), fileName, informationClass);
        }
        catch (NotImplementedException)
        {
            // SMBLibrary throws on classes it cannot encode, such as FileIdExtdDirectoryInformation (60),
            // which the Windows client passes through. A server without the class refuses it, and the
            // scanner then lists the whole scan with FILE_FULL_DIR_INFO.
            result = [];
            return Smb.NTStatus.STATUS_INVALID_INFO_CLASS;
        }
    }

    public Smb.NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
    {
        Gate();
        return inner.ReadFile(out data, Unwrap(handle), offset, maxCount);
    }

    public Smb.NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        Gate();
        return inner.WriteFile(out numberOfBytesWritten, Unwrap(handle), offset, data);
    }

    public Smb.NTStatus FlushFileBuffers(object handle)
    {
        Gate();
        return inner.FlushFileBuffers(Unwrap(handle));
    }

    public Smb.NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock)
    {
        Gate();
        return inner.LockFile(Unwrap(handle), byteOffset, length, exclusiveLock);
    }

    public Smb.NTStatus UnlockFile(object handle, long byteOffset, long length)
    {
        Gate();
        return inner.UnlockFile(Unwrap(handle), byteOffset, length);
    }

    public Smb.NTStatus GetFileInformation(out Smb.FileInformation result, object handle, Smb.FileInformationClass informationClass)
    {
        Gate();
        try
        {
            return inner.GetFileInformation(out result, Unwrap(handle), informationClass);
        }
        catch (NotImplementedException)
        {
            result = null!;
            return Smb.NTStatus.STATUS_INVALID_INFO_CLASS; // as for QueryDirectory
        }
    }

    public Smb.NTStatus SetFileInformation(object handle, Smb.FileInformation information)
    {
        Gate();
        return inner.SetFileInformation(Unwrap(handle), information);
    }

    public Smb.NTStatus GetFileSystemInformation(out Smb.FileSystemInformation result, Smb.FileSystemInformationClass informationClass)
    {
        Gate();
        return inner.GetFileSystemInformation(out result, informationClass);
    }

    public Smb.NTStatus SetFileSystemInformation(Smb.FileSystemInformation information)
    {
        Gate();
        return inner.SetFileSystemInformation(information);
    }

    public Smb.NTStatus GetSecurityInformation(out Smb.SecurityDescriptor result, object handle, Smb.SecurityInformation securityInformation)
    {
        Gate();
        return inner.GetSecurityInformation(out result, Unwrap(handle), securityInformation);
    }

    public Smb.NTStatus SetSecurityInformation(object handle, Smb.SecurityInformation securityInformation, Smb.SecurityDescriptor securityDescriptor)
    {
        Gate();
        return inner.SetSecurityInformation(Unwrap(handle), securityInformation, securityDescriptor);
    }

    public Smb.NTStatus NotifyChange(out object ioRequest, object handle, Smb.NotifyChangeFilter completionFilter, bool watchTree,
        int outputBufferSize, Smb.OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
    {
        Gate();
        return inner.NotifyChange(out ioRequest, Unwrap(handle), completionFilter, watchTree, outputBufferSize,
            onNotifyChangeCompleted, context);
    }

    public Smb.NTStatus Cancel(object ioRequest)
    {
        Gate();
        return inner.Cancel(ioRequest);
    }

    public Smb.NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
    {
        Gate();
        return inner.DeviceIOControl(Unwrap(handle), ctlCode, input, out output, maxOutputLength);
    }

    // SMBLibrary passes a null handle for requests on the tree itself, such as FSCTL_VALIDATE_NEGOTIATE_INFO.
    private static object Unwrap(object handle) => handle is Opened opened ? opened.Handle : handle;

    // Once the server has stopped answering, every request waits until the test releases it.
    private void Gate()
    {
        if (_stopped)
            _release.Wait();
    }

    private sealed class Opened(object handle, string path)
    {
        public object Handle { get; } = handle;

        public string Path { get; } = path;
    }
}
