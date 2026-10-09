using System.Diagnostics;
using System.Runtime.InteropServices;
using FsAnalyser.Core.Scanning;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The opt-in cloud rig's safety branches, in the default suite (SL-049, m5): what its sweep may delete under
/// %TEMP%, which provider callbacks count as the scan's, where the provider's dotnet.exe is, and what happens
/// to a scan that Cancel does not free. None of it registers a sync root.
/// </summary>
public class CloudRigTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("fsa-cf-4243-" + Hex, true)] // a dead run's root
    [InlineData("fsa-cf-4242-" + Hex, false)] // a live run's root, in another worktree or session
    [InlineData("fsa-cf-" + Hex, false)] // no process id
    [InlineData("fsa-cf--" + Hex, false)]
    [InlineData("fsa-cf-04243-" + Hex, false)]
    [InlineData("fsa-cf-4243-0123456789abcdef0123456789abcde", false)] // 31 hex digits
    [InlineData("fsa-cf-4243-" + Hex + "0", false)] // 33
    [InlineData("fsa-cf-4243-0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("fsa-cf-4243-01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("fsa-cf-4243-" + Hex + ".old", false)]
    [InlineData("xfsa-cf-4243-" + Hex, false)]
    [InlineData("fsa-cf-4243-" + Hex + " ", false)]
    [InlineData("fsa-cf-99999999999-" + Hex, false)] // more than an int
    [InlineData("fsa-tests", false)]
    [InlineData("fsa-cf-", false)]
    public void OnlyARootOfARunThatIsGoneIsStale(string name, bool stale)
    {
        Assert.Equal(stale, CloudRig.IsStaleRoot(name, pid => pid == 4242));
    }

    /// <summary>
    /// A root left by a crashed run, next to names that look like it, a live run's root, and a junction that
    /// carries a dead run's root name: the sweep unregisters and deletes that one root, with what is in it, and
    /// touches nothing else. Above all it neither unregisters nor deletes through the junction, whose target lies
    /// outside the folder being swept (SL-049 review m2).
    /// </summary>
    [Fact]
    public void TheSweepRemovesOnlyTheRootOfARunThatIsGone()
    {
        using var tree = new TempTree();
        int gone = ExitedProcessId();
        string stale = CloudRig.RootName(gone, Guid.NewGuid());
        tree.Folder(stale);
        tree.Folder($@"{stale}\stall");
        tree.File($@"{stale}\ok.txt", 10);
        string live = CloudRig.RootName(Environment.ProcessId, Guid.NewGuid());
        tree.Folder(live);
        tree.File($@"{live}\in-use.txt", 10);
        string[] lookalikes =
        [
            $"fsa-cf-{Guid.NewGuid():N}",
            $"fsa-cf-{gone}-{Guid.NewGuid():N}".ToUpperInvariant(),
            $"fsa-cf-{gone}-{Guid.NewGuid():D}",
            $"fsa-cf-{gone}-{Guid.NewGuid():N}-old",
            $"xfsa-cf-{gone}-{Guid.NewGuid():N}",
            "fsa-cf-",
        ];
        foreach (string name in lookalikes)
        {
            tree.Folder(name);
            tree.File($@"{name}\keep.txt", 10);
        }
        string fileWithARootsName = tree.File(CloudRig.RootName(gone, Guid.NewGuid()), 10);
        string target = tree.Outside("target");
        string targetFile = TempTree.WriteFile(Path.Combine(target, "not-ours.txt"), 10);
        string junction = tree.Junction(CloudRig.RootName(gone, Guid.NewGuid()), target);
        var unregistered = new List<string>();
        var log = new List<string>();

        var clock = Stopwatch.StartNew();
        List<string> swept = CloudRig.Sweep(tree.Root, CloudRig.IsRunning, unregistered.Add, log.Add);
        TimeSpan took = clock.Elapsed;

        Assert.Equal([stale], swept);
        Assert.Equal([tree.PathOf(stale)], unregistered);
        Assert.Empty(log);
        Assert.False(Directory.Exists(tree.PathOf(stale)));
        Assert.True(File.Exists(tree.PathOf($@"{live}\in-use.txt")));
        foreach (string name in lookalikes)
            Assert.True(File.Exists(tree.PathOf($@"{name}\keep.txt")), $"the sweep touched {name}");
        Assert.True(File.Exists(fileWithARootsName));
        Assert.DoesNotContain(Path.GetFileName(junction), swept);
        Assert.DoesNotContain(junction, unregistered);
        Assert.True(Directory.Exists(junction), "the sweep removed the junction named like a dead run's root");
        Assert.True(File.Exists(targetFile), "the sweep deleted a file through a junction named like a dead run's root");
        Assert.True(took < TimeSpan.FromSeconds(10), $"the sweep took {took.TotalSeconds:F1} s");
    }

    /// <summary>A failed unregister is logged and the folder is still removed; the sweep never throws.</summary>
    [Fact]
    public void TheSweepGoesOnWhenAnUnregisterFails()
    {
        using var tree = new TempTree();
        string stale = CloudRig.RootName(ExitedProcessId(), Guid.NewGuid());
        tree.Folder(stale);
        var log = new List<string>();

        List<string> swept = CloudRig.Sweep(tree.Root, CloudRig.IsRunning, _ => throw new InvalidOperationException("not a sync root"), log.Add);

        Assert.Equal([stale], swept);
        Assert.Contains("not a sync root", Assert.Single(log));
        Assert.False(Directory.Exists(tree.PathOf(stale)));
    }

    /// <summary>
    /// The folder the rig's roots go in is made before it is swept, sits where TempTree puts its trees, and is outside the
    /// Windows folder: under any temp folder, the engine's (which this user cannot list) included, where a sweep of the
    /// temp folder itself was denied before anything was made (SL-098). Everything counts as running for the real folder,
    /// so a root of another run is never touched by a test; the sweep itself is fed a folder of a tree's own.
    /// </summary>
    [Fact]
    public void TheRigsParentIsMadeBeforeItIsSweptAndSitsWhereTheTreesDo()
    {
        (string parent, List<string> none) = CloudRig.PrepareParent(_ => true, _ => throw new InvalidOperationException("nothing is stale"), _ => Assert.Fail("nothing to log"));
        Assert.Equal(TempTree.DefaultTestsRoot(), parent);
        Assert.True(Directory.Exists(parent));
        Assert.Empty(none);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(Path.TrimEndingDirectorySeparator(parent).StartsWith(Path.TrimEndingDirectorySeparator(windows) + @"\", StringComparison.OrdinalIgnoreCase),
            $"the rig's parent {parent} is in the Windows folder {windows}");

        using var tree = new TempTree();
        string made = tree.PathOf("rig-parent");
        Assert.Throws<DirectoryNotFoundException>(() => CloudRig.Sweep(made, CloudRig.IsRunning, _ => { }, _ => { })); // the order this method fixes
        string stale = CloudRig.RootName(ExitedProcessId(), Guid.NewGuid());
        string live = CloudRig.RootName(Environment.ProcessId, Guid.NewGuid());
        tree.Folder($@"rig-parent\{stale}");
        tree.File($@"rig-parent\{stale}\ok.txt", 10);
        tree.Folder($@"rig-parent\{live}");
        tree.File($@"rig-parent\{live}\in-use.txt", 10);
        var unregistered = new List<string>();

        (string prepared, List<string> swept) = CloudRig.PrepareParent(made, CloudRig.IsRunning, unregistered.Add, _ => Assert.Fail("nothing to log"));

        Assert.Equal(made, prepared);
        Assert.Equal([stale], swept);
        Assert.Equal([Path.Combine(made, stale)], unregistered);
        Assert.False(Directory.Exists(Path.Combine(made, stale)));
        Assert.True(File.Exists(Path.Combine(made, live, "in-use.txt")), "the sweep removed a root of a run that is still going");

        string later = tree.PathOf(@"not\yet\made");
        Assert.Empty(CloudRig.PrepareParent(later, CloudRig.IsRunning, _ => { }, _ => { }).Swept);
        Assert.True(Directory.Exists(later));
    }

    [Fact]
    public void IsRunningTellsThisProcessFromOneThatHasExited()
    {
        Assert.True(CloudRig.IsRunning(Environment.ProcessId));
        Assert.False(CloudRig.IsRunning(ExitedProcessId()));
    }

    /// <summary>
    /// The indexer, Defender or Explorer may touch the sync root while the scan runs. Only callbacks raised by
    /// this process count as the scan's; a process id that merely starts with this one's does not.
    /// </summary>
    [Fact]
    public void OnlyThisProcesssCallbacksCountAsTheScans()
    {
        int me = Environment.ProcessId;
        const string Root = @"C:\Users\a b\AppData\Local\Temp\fsa-cf-7-0123456789abcdef0123456789abcdef";
        string[] lines =
        [
            "REGISTERED",
            "READY",
            $@"CB FETCH_PLACEHOLDERS pid={me + 1} path={Root}\stall",
            $@"CB FETCH_PLACEHOLDERS pid={me * 10 + 1} path={Root}\stall",
            $@"CB FETCH_DATA pid={me + 2} path={Root}\a\f1.bin offset=0 length=4096",
            $@"CB FETCH_DATA pid={me * 10} path={Root}\b\f4.bin offset=0 length=1048576",
            $@"CB FETCH_DATA pid=0 path={Root}\a\f2.bin offset=0 length=12345",
            $@"CB FETCH_PLACEHOLDERS pid={me} path={Root}\stall",
            $@"CB CANCEL_FETCH_PLACEHOLDERS pid={me} path={Root}\stall",
            $@"CB FETCH_DATA pid={me} path={Root}\a\sub\f3.bin offset=65536 length=3080192",
            $"CB FETCH_DATA pid=x{me} path={Root}\\a\\f1.bin",
            "ERR something hr=0x80070005",
        ];

        List<CloudRig.ProviderCallback> fetches = CloudRig.CallbacksFrom(lines, me, "FETCH_DATA");
        List<CloudRig.ProviderCallback> listings = CloudRig.CallbacksFrom(lines, me, "FETCH_PLACEHOLDERS");

        Assert.Equal([new CloudRig.ProviderCallback("FETCH_DATA", me, $@"{Root}\a\sub\f3.bin", 65536, 3080192)], fetches);
        Assert.Equal([new CloudRig.ProviderCallback("FETCH_PLACEHOLDERS", me, $@"{Root}\stall", null, null)], listings);
        // The others were read, and left out for their process alone.
        Assert.Single(CloudRig.CallbacksFrom(lines, me + 1, "FETCH_PLACEHOLDERS"));
        Assert.Single(CloudRig.CallbacksFrom(lines, me * 10 + 1, "FETCH_PLACEHOLDERS"));
        Assert.Single(CloudRig.CallbacksFrom(lines, me + 2, "FETCH_DATA"));
        Assert.Single(CloudRig.CallbacksFrom(lines, me * 10, "FETCH_DATA"));
    }

    [Theory]
    [InlineData(@"C:\repo\.dotnet\shared\Microsoft.NETCore.App\10.0.12\", @"C:\repo\.dotnet\dotnet.exe")]
    [InlineData(@"C:\repo\.dotnet\shared\Microsoft.NETCore.App\10.0.12", @"C:\repo\.dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\", @"C:\Program Files\dotnet\dotnet.exe")]
    public void TheMuxerIsThreeLevelsAboveTheRuntimeDirectory(string runtimeDirectory, string muxer)
    {
        Assert.Equal(muxer, CloudRig.MuxerFor(runtimeDirectory));
    }

    /// <summary>The muxer of the runtime this test runs on exists, whichever dotnet root that is on this PC.</summary>
    [Fact]
    public void TheRunningRuntimesMuxerExists()
    {
        string muxer = CloudRig.MuxerFor(RuntimeEnvironment.GetRuntimeDirectory());
        Assert.True(File.Exists(muxer), $"no {muxer}");
    }

    /// <summary>
    /// A regressed guard, stood in for on the shipped Scanner: its first call waits on an event that
    /// CancelSynchronousIo cannot interrupt, so the scan is still running after Cancel. The deadline frees it by
    /// force, as the rig does by killing its provider, and fails; it does not hang.
    /// </summary>
    [Fact]
    public void AScanCancelDoesNotFreeIsFreedByForceAndFails()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);
        using var cts = new CancellationTokenSource();
        using var stuck = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        ScanResult? result = null;
        bool forced = false;
        var clock = Stopwatch.StartNew();

        var failure = Assert.Throws<XunitException>(() => ScanDeadline.Run(() =>
        {
            SynchronousIoCancel.Created = g => g.BeforeCall = call =>
            {
                if (call != 1)
                    return;
                stuck.Set();
                released.Wait();
            };
            try
            {
                return result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token);
            }
            finally
            {
                SynchronousIoCancel.Created = null;
            }
        }, cts, TimeSpan.FromMilliseconds(500), () =>
        {
            forced = true;
            released.Set();
        }, watch =>
        {
            Assert.True(stuck.Wait(TimeSpan.FromSeconds(5)), "the scan never reached its first call");
            cts.Cancel();
        }));

        Assert.True(forced);
        Assert.Contains("still running 0.5 s after Cancel", failure.Message);
        Assert.Contains("returned", failure.Message);
        Assert.Equal(ScanStatus.Cancelled, result?.Status); // freed, it ended as a cancelled scan
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>The control: a scan Cancel frees is returned as it is, and nothing is forced.</summary>
    [Fact]
    public void AScanCancelFreesIsReturnedWithNothingForced()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);
        using var cts = new CancellationTokenSource();
        using var server = SynchronousIoCancelTests.NewPipe(out _);
        using var stuck = new ManualResetEventSlim();
        bool forced = false;

        ScanResult result = ScanDeadline.Run(() =>
        {
            SynchronousIoCancel.Created = g => g.BeforeCall = call =>
            {
                if (call != 1)
                    return;
                stuck.Set();
                try
                {
                    server.WaitForConnection(); // a wait in the kernel, which the guard interrupts
                }
                catch (Exception)
                {
                    // interrupted by the guard's CancelSynchronousIo
                }
            };
            try
            {
                return Scanner.Scan(tree.Root, new ScanProgress(), cts.Token);
            }
            finally
            {
                SynchronousIoCancel.Created = null;
            }
        }, cts, TimeSpan.FromSeconds(2), () => forced = true, watch =>
        {
            Assert.True(stuck.Wait(TimeSpan.FromSeconds(5)), "the scan never reached its first call");
            Thread.Sleep(200);
            Assert.True(watch.IsRunning);
            cts.Cancel();
        });

        Assert.False(forced);
        Assert.Equal(ScanStatus.Cancelled, result.Status);
    }

    /// <summary>An assertion that fails while the scan runs is the failure reported, once the scan is bounded.</summary>
    [Fact]
    public void AFailureWhileTheScanRunsIsReportedAfterTheScanIsBounded()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);
        using var cts = new CancellationTokenSource();
        bool forced = false;

        var failure = Assert.ThrowsAny<XunitException>(() => ScanDeadline.Run(
            () => Scanner.Scan(tree.Root, new ScanProgress(), cts.Token), cts, TimeSpan.FromSeconds(2), () => forced = true,
            _ => Assert.Fail("VOID: the stand-in for a missing callback")));

        Assert.Contains("VOID: the stand-in", failure.Message);
        Assert.False(forced);
        Assert.True(cts.IsCancellationRequested);
    }

    private static int ExitedProcessId()
    {
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.Id;
    }
}
