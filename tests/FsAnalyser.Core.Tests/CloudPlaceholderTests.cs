using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The shipped Scanner and its SynchronousIoCancel guard on a real cloud-files (cldflt) sync root: the one
/// place the guard meets a listing a cloud provider holds, which the named-pipe stand-ins cannot show (SL-049).
/// </summary>
/// <remarks>
/// <para>Opt-in, and left out of every run that names no filter by the test project's VSTestTestCaseFilter
/// (<c>Cloud!=CF-SYNC-ROOT</c>). Run it with an explicit filter, which replaces the default:</para>
/// <code>pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test tests/FsAnalyser.Core.Tests/FsAnalyser.Core.Tests.csproj --filter Cloud=CF-SYNC-ROOT</code>
/// <para>The stall and tree Facts register a real sync root under %TEMP% (fsa-cf-&lt;pid&gt;-&lt;guid&gt;),
/// served unelevated by tests/CfProvider in a process of its own, and remove it: every run kills the provider,
/// unregisters the root from this process and deletes its folder, the path SL-049's spike 4 measured after a
/// killed provider. The stall Fact kills it with a second listing of stall held blocked on it, so a request of
/// this process is pending at the kill, and every teardown writes a <c>teardown shape:</c> line saying whether
/// one was: that line, in each passing run, is what shows the killed-provider case was fed. A run that crashed
/// leaves a root the next run sweeps, once the process that made it is gone. The two AProviderThatCannotStart
/// Facts register nothing.</para>
/// <para>Whether a sync root may be registered on a given PC is a person's decision (SL-049, open question 1).
/// Where it is declined, the shipped guard on a real cloud folder stays unproven (not reached), SL-049's spike
/// is the only contact, and the run is never counted as a pass. Where it is allowed, validate runs the filter
/// three times in a row: that is how the teardown after a killed provider is shown to leave nothing that stops
/// the next run.</para>
/// <para>Every Fact fails, and none is ever skipped, when the provider cannot start: its first failing call's
/// ERR line, or no READY within 30 s, with all it printed. A non-NTFS %TEMP% or a missing cldflt shows
/// Windows' own error.</para>
/// <para>The rig is private to this class so that only its Facts, which all carry the class's Trait, can
/// register a root; the cloud-files calls are confined to this file by a Fitness rule.</para>
/// </remarks>
[Trait("Cloud", "CF-SYNC-ROOT")]
public class CloudPlaceholderTests(ITestOutputHelper output)
{
    private const uint RecallOnDataAccess = 0x400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    /// <summary>How long the stall is held before Cancel, so the scan is seen blocked and not merely slow.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    /// <summary>The spike freed the stalled scan in 32 ms; cldflt's own timeout is 60 s.</summary>
    private static readonly TimeSpan CancelFrees = TimeSpan.FromSeconds(5);

    /// <summary>A scan that reads a placeholder's data blocks on this provider; this cancels it.</summary>
    private static readonly TimeSpan TreeDeadline = TimeSpan.FromSeconds(30);

    // What the provider declares (tests/CfProvider/Program.cs), which the scan must report.
    private static readonly (string Name, long Size)[] OkFiles = [("f1.txt", 1_000), ("f2.txt", 2_000), ("f3.txt", 3_000)];

    private static readonly (string Path, long Size)[] TreeFiles =
        [(@"a\f1.bin", 5 * TempTree.MiB), (@"a\f2.bin", 12_345), (@"a\sub\f3.bin", 3 * TempTree.MiB), (@"b\f4.bin", TempTree.MiB)];

    /// <summary>A second name for b\f4.bin, made by the provider with CreateHardLinkW.</summary>
    private const string TreeLink = @"b\f4-link.bin";

    private static readonly string[] TreeFolders = ["a", @"a\sub", "b"];

    /// <summary>
    /// 'stall' is a folder placeholder whose FETCH_PLACEHOLDERS the provider never answers; 'ok', which lists
    /// before it, holds three placeholders and needs no callback. Cancel, 3 s into the stall, must free the scan
    /// through the shipped guard, well inside cldflt's 60 s, with ok's files kept (SL-049 spike 1: 32 ms).
    /// </summary>
    [Fact]
    public void StalledFetchIsFreedByCancelThroughTheShippedGuard()
    {
        using var rig = new SyncRootRig(output);
        rig.Launch("stall");
        using var cts = new CancellationTokenSource();
        var cancelClock = new Stopwatch();
        TimeSpan cancelTook = default, cancelledAt = default;
        string stall = $@"\{rig.RootName}\stall";

        (ScanResult result, SynchronousIoCancel guard) = rig.RunScan(cts, CancelFrees, watch =>
        {
            Assert.True(rig.WaitForCallback("FETCH_PLACEHOLDERS", stall, TimeSpan.FromSeconds(10)),
                $"VOID: no FETCH_PLACEHOLDERS for stall from this process ({Environment.ProcessId}) within 10 s, and the scan " +
                $"{(watch.IsRunning ? "is still running" : "has ended")}: no stalled listing was exercised.\n{rig.Captured()}");
            Thread.Sleep(Hold);
            Assert.True(watch.IsRunning,
                $"VOID: the scan ended by itself {watch.Elapsed.TotalMilliseconds:F0} ms in, while the provider was not answering stall's listing.\n{rig.Captured()}");
            cancelledAt = watch.Elapsed;
            cancelClock.Start();
            cts.Cancel();
            cancelTook = cancelClock.Elapsed;
        });
        TimeSpan sinceCancel = cancelClock.Elapsed;
        output.WriteLine($"cancelled {cancelledAt.TotalMilliseconds:F0} ms into the scan; Cancel() took {cancelTook.TotalMilliseconds:F1} ms; " +
            $"the scan returned within {sinceCancel.TotalMilliseconds:F0} ms of it; CancelSynchronousIo attempts {guard.CancelAttempts}, " +
            $"delivered {guard.CancelsDelivered}; closes handed off {guard.ClosesHandedOff}; {result.Status}, unreadable {result.Unreadable.Count}");

        // Cancel freed it, not the provider going away nor cldflt's own timeout.
        Assert.True(rig.ProviderIsRunning, $"the provider was no longer running when the scan returned: its end, not Cancel, may have freed the scan.\n{rig.Captured()}");
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(0, result.RootError);
        Assert.True(guard.CancelsDelivered >= 1, $"no CancelSynchronousIo found the stalled listing ({guard.CancelAttempts} attempts)");

        int ok = Scans.Find(result, rig.PathOf("ok"));
        Assert.Equal(OkFiles.Length, result.Store.ChildCount(ok));
        foreach ((string name, long size) in OkFiles)
        {
            int file = Scans.Find(result, rig.PathOf($@"ok\{name}"));
            Assert.Equal(EntryKind.File, result.Store.Kind(file));
            Assert.Equal(size, result.Store.ChargedSize(file));
            Assert.Equal(0, result.Store.ChargedOnDisk(file));
        }
        Scans.AssertChargedBytesAreTheTotals(result);

        // A characterization, pinned as the shipped scanner does it today (spike 1, constraint 2): a folder
        // whose listing Cancel stopped is an empty Folder in a Cancelled result, and not in Unreadable. A later
        // slice that reports it as not read changes this on purpose.
        int stalled = Scans.Find(result, rig.PathOf("stall"));
        Assert.Equal(EntryKind.Folder, result.Store.Kind(stalled));
        Assert.Equal(0, result.Store.ChildCount(stalled));
        Assert.DoesNotContain(result.Unreadable, u => u.Entry == stalled);

        // C-006, the teardown after a killed provider with stall's request pending (SL-049 review m1). Cancel
        // withdrew the scan's own request (the provider reports CANCEL_FETCH_PLACEHOLDERS), so the test lists stall
        // again and leaves that listing blocked on the provider. Dispose then kills the provider with it pending,
        // the path spike 4's kill arm measured, and fails this Fact if the kill does not free it within 10 s. Whether
        // the second listing raised a fresh request is written to the output, not asserted: no SL-049 spike listed a
        // folder again after a cancelled listing of it.
        bool pending = rig.HoldListing("stall", TimeSpan.FromSeconds(10));
        output.WriteLine(pending
            ? "a second listing of stall is blocked on a fresh FETCH_PLACEHOLDERS from this process; the provider is killed with it pending"
            : "a second listing of stall raised no fresh FETCH_PLACEHOLDERS from this process within 10 s, or did not stay blocked; nothing of this process is known to be pending at the kill");
        rig.Passed();
    }

    /// <summary>
    /// A tree of dehydrated placeholders under folders with on-demand population disabled. The scan must enter
    /// the cloud folders as folders, report each file's declared size and 0 on disk, and hydrate nothing: no
    /// FETCH_DATA from this process, and every file still RECALL_ON_DATA_ACCESS after it (SL-049 spikes 2 and 3).
    /// This provider never answers, so a scan that read data would block until the 30 s deadline cancels it.
    /// </summary>
    [Fact]
    public void DehydratedPlaceholdersAreTraversedWithTrueSizesAndNeverHydrated()
    {
        using var rig = new SyncRootRig(output);
        rig.Launch("tree");
        using var cts = new CancellationTokenSource(TreeDeadline);

        (ScanResult result, _) = rig.RunScan(cts, CancelFrees);
        output.WriteLine($"{result.Status} in {result.Elapsed.TotalMilliseconds:F0} ms: {result.Totals}, unreadable {result.Unreadable.Count}");

        Assert.True(result.Status == ScanStatus.Completed,
            $"the scan ended {result.Status}, not Completed: it blocked on this provider, which answers nothing, until the {TreeDeadline.TotalSeconds:F0} s deadline.\n{rig.Captured()}");
        Assert.Empty(result.Unreadable);
        ScanStore store = result.Store;

        // A cloud folder is entered, never recorded as a link.
        foreach (string path in TreeFolders)
            Assert.Equal(EntryKind.Folder, store.Kind(Scans.Find(result, rig.PathOf(path))));
        Assert.Equal(3, store.ChildCount(Scans.Find(result, rig.PathOf("a"))));
        Assert.Equal(1, store.ChildCount(Scans.Find(result, rig.PathOf(@"a\sub"))));

        foreach ((string path, long size) in TreeFiles)
        {
            int file = Scans.Find(result, rig.PathOf(path));
            output.WriteLine($"{path}: size {store.Size(file)}, on disk {store.OnDisk(file)}, flags {store.Flags(file)}, attributes 0x{(uint)store.Attributes(file):X}");
            Assert.Equal(EntryKind.File, store.Kind(file));
            Assert.Equal(size, store.Size(file));
            Assert.Equal(0, store.OnDisk(file));
        }
        Assert.Equal(TreeFolders.Length, result.Totals.Folders);
        Assert.Equal(0, result.Totals.Links);
        Assert.Equal(0, result.Totals.BytesOnDisk);
        Scans.AssertChargedBytesAreTheTotals(result); // every folder's totals are the sums below it

        List<CloudRig.ProviderCallback> fetches = rig.Callbacks("FETCH_DATA");
        Assert.True(fetches.Count == 0, $"the scan asked for file data {fetches.Count} time(s):\n  {string.Join("\n  ", fetches)}");

        // Read again after the scan, by name and never by opening the data. Bounded like the scan, because a
        // read that did hydrate would block on this provider.
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        List<string> hydrated = ScanDeadline.Run(
            () => TreeFiles.Where(f => ((uint)File.GetAttributes(rig.PathOf(f.Path)) & RecallOnDataAccess) == 0).Select(f => f.Path).ToList(),
            readCts, CancelFrees, rig.KillProvider);
        Assert.True(hydrated.Count == 0, $"no longer RECALL_ON_DATA_ACCESS after the scan: {string.Join(", ", hydrated)}");

        // Last, so that a failure here hides none of the above. No spike measured how a hardlink among
        // placeholders is charged (SL-049, m6): a failure here is a counting-rule question for the person, and is
        // not patched in this test.
        const string Unmeasured = "UNMEASURED (SL-049 m6), a counting-rule question for the person and not a test to patch: ";
        int f4 = Scans.Find(result, rig.PathOf(@"b\f4.bin"));
        int link = Scans.Find(result, rig.PathOf(TreeLink));
        int b = Scans.Find(result, rig.PathOf("b"));
        Assert.True(store.ChildCount(b) == 2 && store.Kind(link) == EntryKind.File && store.Size(link) == TempTree.MiB && store.OnDisk(link) == 0,
            $"{Unmeasured}b lists {store.ChildCount(b)} names; {TreeLink} is a {store.Kind(link)} of {store.Size(link)} bytes, {store.OnDisk(link)} on disk");
        Assert.True(result.Totals.Files == TreeFiles.Length + 1, $"{Unmeasured}{result.Totals.Files} files counted; every name of a hardlinked file counts, so {TreeFiles.Length + 1}");
        Assert.True(store.FileId(f4) == store.FileId(link) && ScanStore.HasFileId(store.FileId(f4)),
            $"{Unmeasured}the two names of b\\f4.bin were listed with file IDs {store.FileId(f4):X} and {store.FileId(link):X}");
        bool f4Duplicate = (store.Flags(f4) & EntryFlags.HardlinkDuplicate) != 0;
        bool linkDuplicate = (store.Flags(link) & EntryFlags.HardlinkDuplicate) != 0;
        Assert.True(f4Duplicate != linkDuplicate, $"{Unmeasured}HardlinkDuplicate on f4.bin {f4Duplicate}, on f4-link.bin {linkDuplicate}: one name, and only one, is charged");
        Assert.True(store.ChargedSize(f4) + store.ChargedSize(link) == TempTree.MiB && store.RolledSize(b) == TempTree.MiB && store.FilesBelow(b) == 2,
            $"{Unmeasured}b charges {store.RolledSize(b)} bytes over {store.FilesBelow(b)} files (f4.bin {store.ChargedSize(f4)}, f4-link.bin {store.ChargedSize(link)}); one MiB over 2 files was expected");
        long declared = TreeFiles.Sum(f => f.Size);
        Assert.True(result.Totals.Bytes == declared, $"{Unmeasured}the scan totals {result.Totals.Bytes} bytes; the four files declare {declared}, each counted once");
        rig.Passed();
    }

    /// <summary>
    /// A provider whose first call fails (here CfRegisterSyncRoot, on a folder that does not exist, as on a
    /// non-NTFS %TEMP% or without cldflt it would fail with its own code) fails the rig with that call's
    /// ERR line and exit 2, never a skip and never a timeout. It registers nothing.
    /// </summary>
    [Fact]
    public void AProviderThatCannotStartFailsWithItsOwnError()
    {
        using var rig = new SyncRootRig(output);
        var clock = Stopwatch.StartNew();

        var failure = Assert.Throws<XunitException>(() => rig.Launch("stall", providerRoot: rig.PathOf("missing")));

        output.WriteLine(failure.Message);
        Assert.Contains("ERR CfRegisterSyncRoot hr=0x8", failure.Message);
        Assert.Equal(2, rig.ProviderExitCode);
        Assert.False(rig.Registered);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the failure took {clock.Elapsed.TotalSeconds:F1} s to surface");
        rig.Passed();
    }

    /// <summary>
    /// A provider that prints no READY and does not exit (the 'silent' scenario) is killed at the READY
    /// deadline and fails the rig with what it printed. 2 s here in place of the rig's 30 s; the path is the
    /// same. It registers nothing.
    /// </summary>
    [Fact]
    public void AProviderThatCannotStartSilentlyIsKilledAndFails()
    {
        using var rig = new SyncRootRig(output);
        var clock = Stopwatch.StartNew();

        var failure = Assert.Throws<XunitException>(() => rig.Launch("silent", readyDeadline: TimeSpan.FromSeconds(2)));

        output.WriteLine(failure.Message);
        Assert.Contains("printed no READY within 2 s", failure.Message);
        Assert.Contains("silent: registering nothing", failure.Message); // its stderr is in the failure
        Assert.False(rig.ProviderIsRunning);
        Assert.False(rig.Registered);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the failure took {clock.Elapsed.TotalSeconds:F1} s to surface");
        rig.Passed();
    }

    /// <summary>
    /// One sync root under %TEMP% and the provider serving it. Private to the Trait-gated class, so nothing in
    /// the default suite can build one.
    /// </summary>
    /// <remarks>
    /// Teardown is crash-only: Dispose always kills the provider, then unregisters the root from this process,
    /// then deletes its folder. Each step is caught, bounded to 10 s and written to the test output; teardown
    /// throws only when the Fact called <see cref="Passed"/>, so it never replaces the Fact's own failure (m8).
    /// What it could not remove is left to the next run's sweep.
    /// <para>Before the kill it writes which shape it is tearing down (review m1): a line starting
    /// <c>teardown shape:</c> that says whether a request of this process was pending, the counts of this
    /// process's FETCH_PLACEHOLDERS and CANCEL_FETCH_PLACEHOLDERS, and all the provider printed. A listing held by
    /// <see cref="HoldListing"/> must be freed by the kill within 10 s, and is waited for before the unregister.</para>
    /// </remarks>
    private sealed class SyncRootRig : IDisposable
    {
        private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TeardownStep = TimeSpan.FromSeconds(10);

        private readonly ITestOutputHelper _output;
        private readonly object _gate = new();
        private readonly List<string> _stdout = [];
        private readonly List<string> _stderr = [];
        private Process? _provider;
        private HeldListing? _held;
        private bool _stdoutClosed;
        private bool _passed;
        private bool _disposed;

        public SyncRootRig(ITestOutputHelper output)
        {
            _output = output;
            // The temp folder as inherited, not TempTree's choice: under a TEMP in the Windows folder (the engine's) this rig
            // still lands there, and its sweep there is denied (SL-096 spike 4). Exempt by name from the .taad/fitness.yaml
            // rule 'no Core test builds a fixture in %SystemRoot%\Temp except the deliberate one' until the filed slice
            // that routes SyncRootRig and CloudRig through TempTree's root choice removes the exemption.
            string temp = Path.GetTempPath();
            List<string> swept = CloudRig.Sweep(temp, CloudRig.IsRunning, Unregister, output.WriteLine);
            if (swept.Count > 0)
                output.WriteLine($"swept stale sync roots: {string.Join(", ", swept)}");
            Root = Path.Combine(temp, CloudRig.RootName(Environment.ProcessId, Guid.NewGuid()));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string RootName => Path.GetFileName(Root);

        /// <summary>Whether the provider reported the root registered, so teardown must unregister it.</summary>
        public bool Registered
        {
            get
            {
                lock (_gate)
                    return _stdout.Contains("REGISTERED");
            }
        }

        public bool ProviderIsRunning => _provider is { HasExited: false };

        public int? ProviderExitCode => _provider is { HasExited: true } ? _provider.ExitCode : null;

        public string PathOf(string relative) => Path.Combine(Root, relative);

        /// <summary>Starts the provider on this root, and returns once it printed READY.</summary>
        /// <exception cref="XunitException">It could not be started, printed ERR, exited, or printed no READY in time.</exception>
        public void Launch(string scenario, string? providerRoot = null, TimeSpan? readyDeadline = null)
        {
            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            string muxer = CloudRig.MuxerFor(runtime);
            string provider = Path.Combine(AppContext.BaseDirectory, "CfProvider.dll");
            string config = Path.Combine(AppContext.BaseDirectory, "FsAnalyser.Core.Tests.runtimeconfig.json");
            if (!File.Exists(muxer))
                throw new XunitException($"No dotnet.exe at {muxer}, three levels above the running runtime's directory {runtime}. The provider is started through the muxer of the runtime this test runs on.");
            if (!File.Exists(provider))
                throw new XunitException($"No CfProvider.dll at {provider}. The ProjectReference to tests/CfProvider/CfProvider.csproj in FsAnalyser.Core.Tests.csproj puts it there when the test project is built.");
            if (!File.Exists(config))
                throw new XunitException($"No {Path.GetFileName(config)} at {config}. The provider runs on the test project's own runtimeconfig, which its build writes there.");

            var start = new ProcessStartInfo(muxer)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string arg in new[] { "exec", "--runtimeconfig", config, provider, scenario, providerRoot ?? Root })
                start.ArgumentList.Add(arg);
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => Received(_stdout, e.Data, closes: true);
            process.ErrorDataReceived += (_, e) => Received(_stderr, e.Data, closes: false);
            process.Start();
            _provider = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            TimeSpan deadline = readyDeadline ?? ReadyDeadline;
            string? outcome = WaitForLine(line => line == "READY" || line.StartsWith("ERR ", StringComparison.Ordinal), deadline);
            if (outcome == "READY")
            {
                _output.WriteLine($"provider pid {process.Id} ready on {Root}");
                return;
            }
            if (outcome is null && !process.HasExited && !StdoutClosed)
            {
                KillProvider();
                throw new XunitException($"The provider printed no READY within {deadline.TotalSeconds:0.#} s; it was killed.\n{Captured()}");
            }
            if (process.WaitForExit(5000))
                process.WaitForExit(); // and its output read to the end
            throw new XunitException($"The provider could not start: {outcome ?? "it ended without READY"}, exit {ProviderExitCode?.ToString() ?? "(still running)"}.\n{Captured()}");
        }

        /// <summary>
        /// Scans the root with the shipped <see cref="Scanner.Scan(string, ScanProgress, CancellationToken)"/> on a
        /// thread of its own, handing back the guard it made. Bounded for every Fact (M2): still running
        /// <paramref name="grace"/> after <paramref name="cts"/> is cancelled, the provider is killed, which frees
        /// the blocked call, and the scan fails.
        /// </summary>
        public (ScanResult Result, SynchronousIoCancel Guard) RunScan(CancellationTokenSource cts, TimeSpan grace,
            Action<ScanDeadline.Watch>? whileRunning = null)
        {
            SynchronousIoCancel? guard = null;
            ScanResult result = ScanDeadline.Run(() =>
            {
                SynchronousIoCancel.Created = g => guard = g;
                try
                {
                    return Scanner.Scan(Root, new ScanProgress(), cts.Token);
                }
                finally
                {
                    SynchronousIoCancel.Created = null;
                }
            }, cts, grace, KillProvider, whileRunning);
            return (result, guard!);
        }

        /// <summary>Waits for a callback of <paramref name="type"/> raised by this process on a path ending <paramref name="pathEnd"/>.</summary>
        public bool WaitForCallback(string type, string pathEnd, TimeSpan timeout) =>
            WaitForLine(line => IsOwnCallback(line, type, pathEnd), timeout) is not null;

        /// <summary>
        /// Lists <paramref name="relative"/> on a thread of its own, through the same DirectoryReader the scan
        /// uses, and waits up to <paramref name="timeout"/> for this process's fresh FETCH_PLACEHOLDERS on it. The
        /// provider never answers, so the listing stays blocked until Dispose kills the provider with that request
        /// pending, and Dispose fails the Fact if the kill does not free it (C-006). One per rig.
        /// </summary>
        /// <returns>Whether a fresh request of this process was seen and the listing is still blocked on it.</returns>
        public bool HoldListing(string relative, TimeSpan timeout)
        {
            if (_held is not null)
                throw new InvalidOperationException("A rig holds one listing at most.");
            string folder = PathOf(relative);
            string pathEnd = $@"\{RootName}\{relative}";
            int from;
            lock (_gate)
                from = _stdout.Count;
            var held = new HeldListing(relative);
            held.Thread = new Thread(() =>
            {
                try
                {
                    held.Outcome = $"it listed {Scans.Listing(folder).Count} names";
                }
                catch (Exception e)
                {
                    held.Outcome = e.Message;
                }
            }) { IsBackground = true, Name = "rig-held-listing" };
            _held = held;
            held.Thread.Start();
            held.Pending = WaitForLine(line => IsOwnCallback(line, "FETCH_PLACEHOLDERS", pathEnd), timeout, from, stop: () => !held.IsRunning) is not null
                && held.IsRunning;
            return held.Pending;
        }

        /// <summary>The callbacks of <paramref name="type"/> raised by this process, and by no other.</summary>
        public List<CloudRig.ProviderCallback> Callbacks(string type)
        {
            lock (_gate)
                return CloudRig.CallbacksFrom(_stdout.ToList(), Environment.ProcessId, type);
        }

        /// <summary>Everything the provider printed, for a failure message.</summary>
        public string Captured()
        {
            lock (_gate)
            {
                return $"provider stdout ({_stdout.Count} lines):\n  {string.Join("\n  ", _stdout)}\n" +
                    $"provider stderr ({_stderr.Count} lines):\n  {string.Join("\n  ", _stderr)}";
            }
        }

        /// <summary>Kills the provider and waits for it. Every blocked call on its root then fails at once.</summary>
        public void KillProvider()
        {
            Process? process = _provider;
            if (process is null || process.HasExited)
                return;
            process.Kill(entireProcessTree: true);
            if (process.WaitForExit((int)TeardownStep.TotalMilliseconds))
                process.WaitForExit();
        }

        /// <summary>Called by a Fact whose assertions all held: from then on a teardown failure fails it.</summary>
        public void Passed() => _passed = true;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            var failures = new List<string>();
            var clock = Stopwatch.StartNew();
            _output.WriteLine(ShapeAtKill());
            _output.WriteLine("before the kill, " + Captured());
            Thread? listing = _held?.Thread is { IsAlive: true } running ? running : null;
            Step(failures, "kill the provider", () =>
            {
                KillProvider();
                if (_provider is { HasExited: false })
                    throw new InvalidOperationException("it was still running after Kill");
            });
            TimeSpan killed = clock.Elapsed;
            // The held listing keeps a handle on its folder, so it must be gone before the folder can be deleted.
            if (listing is not null)
            {
                if (listing.Join(TeardownStep))
                    _output.WriteLine($"the held listing of {_held!.Relative} returned {(clock.Elapsed - killed).TotalMilliseconds:F0} ms after the provider was gone: {_held.Outcome}");
                else
                    failures.Add($"could not free the held listing of {_held!.Relative}: still blocked {TeardownStep.TotalSeconds:F0} s after the provider was killed");
            }
            if (Registered)
                Step(failures, $"unregister {Root}", () => Unregister(Root));
            Step(failures, $"delete {Root}", () =>
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            });
            _provider?.Dispose();

            _output.WriteLine($"teardown took {clock.Elapsed.TotalMilliseconds:F0} ms{(failures.Count == 0 ? "" : ":")}");
            foreach (string failure in failures)
                _output.WriteLine("  " + failure);
            if (failures.Count > 0 && _passed)
                throw new InvalidOperationException($"The sync root was not fully torn down: {string.Join("; ", failures)}");
        }

        private static void Step(List<string> failures, string what, Action action)
        {
            Exception? error = null;
            var step = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = e;
                }
            }) { IsBackground = true, Name = "rig-teardown" };
            step.Start();
            if (!step.Join(TeardownStep))
                failures.Add($"could not {what}: still blocked after {TeardownStep.TotalSeconds:F0} s");
            else if (error is not null)
                failures.Add($"could not {what}: {error.Message}");
        }

        private static void Unregister(string root)
        {
            int hr = CloudFiles.CfUnregisterSyncRoot(root);
            if (hr < 0)
                throw new InvalidOperationException($"CfUnregisterSyncRoot({root}) returned 0x{hr:X8}");
        }

        private bool StdoutClosed
        {
            get
            {
                lock (_gate)
                    return _stdoutClosed;
            }
        }

        private void Received(List<string> lines, string? line, bool closes)
        {
            lock (_gate)
            {
                if (line is not null)
                    lines.Add(line);
                else if (closes)
                    _stdoutClosed = true;
                Monitor.PulseAll(_gate);
            }
        }

        private static bool IsOwnCallback(string line, string type, string pathEnd) =>
            CloudRig.TryParseCallback(line, out CloudRig.ProviderCallback callback)
            && callback.Pid == Environment.ProcessId && callback.Type == type
            && callback.Path.EndsWith(pathEnd, StringComparison.OrdinalIgnoreCase);

        // The first stdout line from line `from` on that matches, or null once the timeout passes, stdout has ended
        // or `stop` says so, without one.
        private string? WaitForLine(Func<string, bool> match, TimeSpan timeout, int from = 0, Func<bool>? stop = null)
        {
            var clock = Stopwatch.StartNew();
            lock (_gate)
            {
                for (int seen = from; ; )
                {
                    for (; seen < _stdout.Count; seen++)
                    {
                        if (match(_stdout[seen]))
                            return _stdout[seen];
                    }
                    TimeSpan left = timeout - clock.Elapsed;
                    if (_stdoutClosed || left <= TimeSpan.Zero || stop?.Invoke() == true)
                        return null;
                    Monitor.Wait(_gate, stop is null || left < StopPoll ? left : StopPoll);
                }
            }
        }

        private static readonly TimeSpan StopPoll = TimeSpan.FromMilliseconds(100);

        // What the rig says about teardown, taken just before the kill: whether a request of this process is
        // pending, and the counts that show it either way.
        private string ShapeAtKill()
        {
            int fetches = Callbacks("FETCH_PLACEHOLDERS").Count;
            int cancels = Callbacks("CANCEL_FETCH_PLACEHOLDERS").Count;
            bool pending = _held is { Pending: true, IsRunning: true };
            string provider = _provider switch
            {
                null => "no provider was started",
                { HasExited: true } exited => $"the provider had already exited ({exited.ExitCode})",
                _ => "the provider is killed now",
            };
            string held = _held switch
            {
                null => "the test held no listing",
                { IsRunning: true, Pending: true } h => $"the test's listing of {h.Relative} has been blocked {h.Clock.Elapsed.TotalSeconds:F1} s on a FETCH_PLACEHOLDERS of its own",
                { IsRunning: true } h => $"the test's listing of {h.Relative} is still running after {h.Clock.Elapsed.TotalSeconds:F1} s, with no FETCH_PLACEHOLDERS seen for it",
                { } h => $"the test's listing of {h.Relative} ended before the kill: {h.Outcome}",
            };
            string shape = pending
                ? $"PENDING, a FETCH_PLACEHOLDERS of this process on {_held!.Relative} is pending at the kill"
                : "none pending, no request of this process is known to be pending at the kill";
            return $"teardown shape: {shape}; {provider}; this process raised {fetches} FETCH_PLACEHOLDERS and {cancels} " +
                $"CANCEL_FETCH_PLACEHOLDERS; {held}";
        }

        /// <summary>A listing the test left blocked on the provider, for teardown to kill under.</summary>
        private sealed class HeldListing(string relative)
        {
            public string Relative { get; } = relative;

            public Thread? Thread { get; set; }

            public Stopwatch Clock { get; } = Stopwatch.StartNew();

            /// <summary>What the listing returned; written by its thread, read after it ended.</summary>
            public string? Outcome { get; set; }

            public bool Pending { get; set; }

            public bool IsRunning => Thread is { IsAlive: true };
        }
    }

    /// <summary>The one cloud-files call the tests make themselves; the provider makes the rest.</summary>
    private static class CloudFiles
    {
        [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int CfUnregisterSyncRoot(string syncRootPath);
    }
}
