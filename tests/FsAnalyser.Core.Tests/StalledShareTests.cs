using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FsAnalyser.Core.Scanning;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Win32;
using Xunit.Abstractions;
using Smb = SMBLibrary;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A share that was reachable and then stopped answering mid-scan, as after Wi-Fi loss, a VPN drop or
/// resume from sleep. The session is established and the listing goes out, but the server never answers
/// it, nor anything after it.
/// </summary>
/// <remarks>
/// Opt-in, and left out of the default run by the test project's VSTestTestCaseFilter. It runs an
/// SMBLibrary server on 127.0.0.1 on a spare port, with a test user, and reaches it through a
/// non-persistent New-SmbMapping -TcpPort (Windows 11 24H2 and later). It needs no elevation and changes
/// nothing that outlives it: the mapping is removed and the server stopped when the test ends.
/// <para>Measured on this PC (SL-024 build 3): this is the one shape Cancel cannot cut short. The SMB
/// client holds the unanswered listing, and the CancelSynchronousIo aimed at it, until it gives up on the
/// server: the scan returned 61.9 and 66.0 s after Cancel in two runs, the canceller's single CancelSynchronousIo blocked
/// all that time. Cancel() itself still returned in about 1 ms, because it only starts the canceller.
/// So the bound asserted here is the client's own limit, not criterion (1)'s 2 s, which the design does
/// not promise for a stalled session.</para>
/// </remarks>
public class StalledShareTests(ITestOutputHelper output)
{
    /// <summary>The SMB client's session timeout is 60 s by default; 62 to 66 s was measured, with margin here.</summary>
    private static readonly TimeSpan ClientGivesUp = TimeSpan.FromSeconds(90);

    [Fact]
    [Trait("Network", "SMB-LOOPBACK")]
    public void CancellingAScanOfAShareThatStoppedAnsweringNeverHangsInTheClose()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 11);
        tree.Folder("hung");
        for (int i = 0; i < 5; i++)
            tree.File($@"hung\file{i}.txt", 100);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);
        using var share = new LoopbackShare(tree.Root);
        share.Store.StopAnsweringAtTheListingOf("hung");

        using var cts = new CancellationTokenSource();
        var progress = new ScanProgress();
        SynchronousIoCancel? guard = null;
        ScanResult? result = null;
        Exception? scanError = null;
        var clock = Stopwatch.StartNew();
        TimeSpan returned = default;
        var scan = new Thread(() =>
        {
            SynchronousIoCancel.Created = g => guard = g;
            try
            {
                result = Scanner.Scan(share.RemotePath, progress, cts.Token);
            }
            catch (Exception e)
            {
                scanError = e;
            }
            finally
            {
                SynchronousIoCancel.Created = null;
                returned = clock.Elapsed;
            }
        }) { IsBackground = true };
        scan.Start();

        Assert.True(share.Store.Stalled.Wait(TimeSpan.FromSeconds(30)),
            $"the scan never sent the listing of hung; it ended {returned.TotalMilliseconds:F0} ms in with {result?.Status}, root error {result?.RootError}, {scanError?.Message}\nThe server's log:\n  {share.ServerLog}");
        Thread.Sleep(500);
        Assert.True(scan.IsAlive, $"VOID: the scan ended by itself {returned.TotalMilliseconds:F0} ms in while the server was not answering; no stalled call was exercised");

        var cancelClock = Stopwatch.StartNew();
        cts.Cancel();
        TimeSpan cancelTook = cancelClock.Elapsed;
        bool within2s = scan.Join(TimeSpan.FromSeconds(2));
        bool freed = within2s || scan.Join(ClientGivesUp - TimeSpan.FromSeconds(2));
        TimeSpan sinceCancel = cancelClock.Elapsed;
        int closesPending = SynchronousIoCancel.PendingCloses;
        bool closeDoneWithin1s = SpinWait.SpinUntil(() => SynchronousIoCancel.PendingCloses == 0, TimeSpan.FromSeconds(1));
        output.WriteLine($"server stalled {share.Store.StalledAt.TotalMilliseconds:F0} ms after it started; Cancel() took {cancelTook.TotalMilliseconds:F1} ms; " +
            $"scan returned: {freed}, {sinceCancel.TotalMilliseconds:F0} ms after Cancel (within 2 s: {within2s}); " +
            $"CancelSynchronousIo attempts {guard?.CancelAttempts}, delivered {guard?.CancelsDelivered}; " +
            $"closes handed off {guard?.ClosesHandedOff}, pending when the scan returned {closesPending}, done within 1 s after {closeDoneWithin1s}; " +
            $"result {result?.Status}, root error {result?.RootError}, unreadable [{string.Join(",", result?.Unreadable.Select(u => u.Win32Error) ?? [])}], error {scanError?.Message}");

        Assert.True(cancelTook < TimeSpan.FromMilliseconds(250), $"Cancel() took {cancelTook.TotalMilliseconds:F0} ms on the calling thread");
        Assert.True(freed, $"the scan was still blocked {ClientGivesUp.TotalSeconds:F0} s after Cancel on a share that stopped answering");
        Assert.Null(scanError);
        Assert.True(guard!.CancelAttempts >= 1, "the canceller never tried to interrupt the stalled listing");

        // The stalled folder is not passed off as read: the scan says it was stopped, and the folder holds
        // nothing, as a folder never reached. Whatever the client finally returned for the listing, a cancel
        // had been issued during it, so it is discarded (criterion 2: under a requested cancel, cancellation,
        // not an unreadable folder).
        Assert.Equal(ScanStatus.Cancelled, result!.Status);
        Assert.Equal(0, result.RootError);
        Assert.Empty(result.Unreadable);
        string hung = share.RemotePath + @"\hung";
        Assert.Equal(0, result.Store.ChildCount(Scans.Find(result, hung)));
        Assert.Equal(1, result.Store.ChildCount(Scans.Find(result, share.RemotePath + @"\a")));
        Assert.Equal(share.RemotePath + @"\a", progress.CurrentFolder); // hung was never reported listed
        Assert.DoesNotContain(Scans.ByPath(result.Store).Keys, path => path.StartsWith(hung + @"\", StringComparison.Ordinal));
        TraversalTests.AssertChildrenAreContiguous(result.Store);

        // The stalled folder's handle was closed on a pool thread, never on the scan thread: once the listing
        // came back, nothing else kept the scan.
        Assert.True(guard.ClosesHandedOff >= 1, "the aborted listing's handle was not handed to the background close");
    }
}

/// <summary>An SMBLibrary server on 127.0.0.1, on a spare port, reached through a non-persistent mapping.</summary>
internal sealed class LoopbackShare : IDisposable
{
    private const string User = "fsa-test";
    private const string Password = "Fsa-test-2026!";
    private readonly SMBServer _server;
    private readonly ConcurrentQueue<string> _log = new();
    private readonly bool _mapped;

    public LoopbackShare(string folder)
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
        try
        {
            // The client allows one port per server name, so a mapping this fixture left behind (a test host
            // that died before Dispose) would refuse the new one. Only this fixture's own names are removed.
            PowerShell(@"Get-SmbMapping | Where-Object RemotePath -match '^\\\\127\.0\.0\.1\\fsa[0-9a-f]{8}$' | " +
                "ForEach-Object { Remove-SmbMapping -RemotePath $_.RemotePath -Force -UpdateProfile -Confirm:$false }; " +
                $"New-SmbMapping -RemotePath '{RemotePath}' -TcpPort {Port} -UserName '{User}' -Password '{Password}' -Persistent $false | Out-Null");
            _mapped = true;
        }
        catch (Exception e)
        {
            _server.Stop();
            throw new InvalidOperationException($"{e.Message}\nThe server's log:\n  {ServerLog}", e);
        }
    }

    public StallingFileStore Store { get; }

    /// <summary>What the server logged at Information and above, for a failure message.</summary>
    public string ServerLog => string.Join("\n  ", _log);

    /// <summary>The share as a person would write it.</summary>
    public string RemotePath { get; }

    public int Port { get; }

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
        }
    }

    private static void PowerShell(string command)
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
        Smb.NTStatus status = inner.CreateFile(out object opened, out fileStatus, path, desiredAccess, fileAttributes, shareAccess,
            createDisposition, createOptions, securityContext);
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
