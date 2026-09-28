using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

[CollectionDefinition(nameof(VolumeFigure), DisableParallelization = true)]
public class VolumeFigure;

/// <summary>
/// The used space a whole-drive scan records alongside its tree (SL-043, criterion 6): read once as the scan
/// starts, on a thread nothing waits for (O-031), published as ScanStore.VolumeUsed. Every wait here has a
/// bound, so a scan that does not end fails the test instead of hanging the suite.
/// </summary>
/// <remarks>
/// Runs alone, after the parallel classes (M3). The Get-Volume bracket allows 64 MiB of drift, and
/// HardlinkTests writes and deletes exactly 64 MiB on the same volume: run beside it, a figure taken between
/// that write and its delete sits just outside a bracket read around both.
/// </remarks>
[Collection(nameof(VolumeFigure))]
public class VolumeUsageTests(ITestOutputHelper output)
{
    private const long Slack = 64L * 1024 * 1024;
    private static readonly TimeSpan FigureWait = TimeSpan.FromSeconds(45); // longer than the read's own 30 s bound
    private static readonly TimeSpan ScanWait = TimeSpan.FromSeconds(60);
    private static readonly string SystemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;

    /// <summary>
    /// The system drive's figure against Get-Volume's Size minus SizeRemaining, read before the scan and after
    /// the figure landed. Two processes cannot read at one moment, so the figure must fall inside that bracket,
    /// widened by 64 MiB. Each attempt is a fresh scan, cancelled as soon as the figure has landed, which also
    /// shows a cancelled scan keeps it. Up to three attempts, so that one burst of writes on the drive by
    /// something else cannot fail it; every attempt is printed.
    /// </summary>
    [Fact]
    public async Task ASystemDriveScanRecordsWhatGetVolumeReports()
    {
        var attempts = new List<string>();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            long before = GetVolumeUsed(SystemRoot[0]);
            (ScanResult result, long? figure) = await ScanUntilTheFigureLands(SystemRoot);
            long after = GetVolumeUsed(SystemRoot[0]);

            Assert.Equal(ScanStatus.Cancelled, result.Status);
            Assert.True(figure is not null, $"the scan of {SystemRoot} recorded no figure (the volume answered nothing within the bound)");
            long low = Math.Min(before, after) - Slack, high = Math.Max(before, after) + Slack;
            bool inside = figure >= low && figure <= high;
            attempts.Add(string.Create(CultureInfo.InvariantCulture,
                $"attempt {attempt}: Get-Volume before {before:N0}, figure {figure:N0}, Get-Volume after {after:N0}, " +
                $"figure - before {figure - before:N0}, after - figure {after - figure:N0}: {(inside ? "inside" : "OUTSIDE")}"));
            output.WriteLine(attempts[^1]);
            if (inside)
                return;
        }
        Assert.Fail($"The figure fell outside [min(before, after) - 64 MiB, max(before, after) + 64 MiB] in every attempt:\n{string.Join("\n", attempts)}");
    }

    [Fact]
    public async Task AFolderScanRecordsNoFigure()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\b.bin", 10);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.True(result.Store.VolumeUsed.IsCompletedSuccessfully, "a folder scan answers the figure at once");
        Assert.Null(await result.Store.VolumeUsed);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"c:\", true)]
    [InlineData(@"\\?\C:\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\server\share\", false)]
    [InlineData(@"\\?\UNC\server\share\", false)]
    [InlineData(@"C:\Mounted\Data", false)]  // a volume mounted on a folder: its figure is not the tree's
    [InlineData(@"C:\Mounted\Data\", false)]
    [InlineData(@"\\?\Volume{0b1d2e3f-0000-0000-0000-100000000000}\", false)]
    public void OnlyAWholeDriveIsADriveRoot(string root, bool driveRoot)
    {
        Assert.Equal(driveRoot, VolumeSpace.IsDriveRoot(root));
    }

    /// <summary>
    /// HF-024: under a per-user quota GetDiskFreeSpaceExW's total is the caller's quota while its free bytes are the
    /// whole volume's, so free can exceed total. That is no figure, never the negative one the subtraction wraps to.
    /// Setting a quota needs an administrator, so the two figures are fed in as the call returns them.
    /// </summary>
    [Fact]
    public void AFreeFigureAboveTheQuotaTotalIsNoFigure()
    {
        const ulong quotaTotal = 100UL << 30, volumeFree = 626UL << 30;

        Assert.False(Kernel32.TryUsedBytes(quotaTotal, volumeFree, out long used), $"a used figure of {used:N0} bytes");
        Assert.True(Kernel32.TryUsedBytes(volumeFree, quotaTotal, out used));
        Assert.Equal(526L << 30, used);
    }

    /// <summary>A drive letter with nothing behind it: GetDiskFreeSpaceExW answers false, and the figure is null, not faulted.</summary>
    [Fact]
    public async Task ADriveLetterWithNoVolumeRecordsNoFigure()
    {
        string root = FreeDriveLetter() + @":\";
        Assert.False(Kernel32.TryGetVolumeUsed(root, out _, out int error), $"GetDiskFreeSpaceExW answered for {root}");
        output.WriteLine($"GetDiskFreeSpaceExW({root}) failed with Win32 error {error}");

        ScanResult result = await Task.Run(() => Scanner.Scan(root, new ScanProgress(), CancellationToken.None)).WaitAsync(ScanWait);

        Assert.Equal(ScanStatus.RootUnreadable, result.Status);
        long? figure = await result.Store.VolumeUsed.WaitAsync(FigureWait);
        Assert.Null(figure);
        Assert.True(result.Store.VolumeUsed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// O-031's reason to exist: a drive-root scan whose volume read never returns still ends when it is
    /// cancelled, with the figure pending, and the figure is given up as null once the read's bound passes.
    /// </summary>
    [Fact]
    public async Task ADriveRootScanEndsWhileTheReadIsStalled()
    {
        TimeSpan bound = TimeSpan.FromSeconds(5);
        using var release = new ManualResetEventSlim();
        var cts = new CancellationTokenSource();
        var options = new ScanOptions
        {
            VolumeReader = _ => { release.Wait(); return 1; },
            VolumeReadBound = bound,
            AfterFolder = _ => cts.Cancel(), // once the root's listing has committed
        };
        try
        {
            var clock = Stopwatch.StartNew();
            ScanResult result = await Task.Run(() => Scanner.Scan(SystemRoot, new ScanProgress(), cts.Token, options)).WaitAsync(ScanWait);
            TimeSpan ended = clock.Elapsed;

            Assert.Equal(ScanStatus.Cancelled, result.Status);
            Assert.True(ended < bound, $"the scan took {ended.TotalSeconds:N1} s, past the {bound.TotalSeconds} s bound, so pending cannot be told apart");
            Assert.False(result.Store.VolumeUsed.IsCompleted, "the scan ended while the read was stalled, so the figure must still be pending");

            Assert.Null(await result.Store.VolumeUsed.WaitAsync(bound + FigureWait));
            Assert.True(clock.Elapsed >= bound - TimeSpan.FromMilliseconds(100), $"null came at {clock.Elapsed.TotalSeconds:N1} s, before the bound");
        }
        finally
        {
            release.Set();
            cts.Cancel();
        }
    }

    /// <summary>
    /// The same on the path every scan that is not cancelled takes (M2): a folder scan made to read the volume
    /// completes while the read is stalled, and the figure lands when the read returns.
    /// </summary>
    [Fact]
    public async Task AScanCompletesWhileTheReadIsStalled()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\b.bin", 10);
        tree.Folder("c");
        tree.File(@"c\d.bin", 20);
        using var release = new ManualResetEventSlim();
        var options = new ScanOptions
        {
            VolumeReader = _ => { release.Wait(); return 42; },
            VolumeReadBound = TimeSpan.FromMinutes(10),
            ReadVolumeOfAnyRoot = true,
        };
        try
        {
            ScanResult result = await Task.Run(() => Scans.Run(tree.Root, options)).WaitAsync(ScanWait);

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(2, result.Totals.Files);
            Assert.False(result.Store.VolumeUsed.IsCompleted, "the scan completed while the read was stalled, so the figure must still be pending");

            release.Set();
            Assert.Equal(42, await result.Store.VolumeUsed.WaitAsync(FigureWait));
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>A scan cancelled before it starts still answers the figure (m5): a scan that returns never leaves it pending with no read.</summary>
    [Fact]
    public async Task AScanCancelledBeforeItStartsAnswersTheFigure()
    {
        using var tree = new TempTree();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.True(result.Store.VolumeUsed.IsCompletedSuccessfully, "the figure of a scan that never started must not stay pending");
        Assert.Null(await result.Store.VolumeUsed);
    }

    /// <summary>
    /// M1, as behaviour rather than names: a drive-root scan whose read is stalled, with a continuation on its
    /// figure shaped like the window's (it holds a window, a generation and texts, never the result), is
    /// dropped by everyone else. Its store must then be collectable while the read is still stuck.
    /// </summary>
    [Fact]
    public void AStalledReadKeepsNoDroppedScanInMemory()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            (WeakReference store, WeakReference scan, Task<long?> figure) = ScanTheSystemDriveAndDropIt(release);
            for (int i = 0; i < 3 && (store.IsAlive || scan.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(figure.IsCompleted, "the read must still be stalled when the store is looked for");
            Assert.False(scan.IsAlive, "a stalled volume read keeps a dropped scan's result alive");
            Assert.False(store.IsAlive, "a stalled volume read keeps a dropped scan's store alive");
        }
        finally
        {
            release.Set();
        }
    }

    // Not inlined, so nothing of the scan outlives this frame but what the read and the continuation hold.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Store, WeakReference Scan, Task<long?> Figure) ScanTheSystemDriveAndDropIt(ManualResetEventSlim release)
    {
        var cts = new CancellationTokenSource();
        var options = new ScanOptions
        {
            VolumeReader = _ => { release.Wait(); return 1; },
            VolumeReadBound = TimeSpan.FromMinutes(10),
            AfterFolder = _ => cts.Cancel(),
        };
        ScanResult result = Scanner.Scan(SystemRoot, new ScanProgress(), cts.Token, options);
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.False(result.Store.VolumeUsed.IsCompleted);

        Task<long?> figure = result.Store.VolumeUsed;
        var window = new object();
        int generation = 7;
        string texts = "1 file and 1 folder; 0 links not followed\n";
        figure.ContinueWith(landed => GC.KeepAlive((window, generation, texts, landed)), TaskScheduler.Default);
        return (new WeakReference(result.Store), new WeakReference(result), figure);
    }

    // A scan of root whose real volume read is watched: the scan is cancelled once the root's listing has
    // committed and the read has returned. The figure is then awaited with a bound.
    private static async Task<(ScanResult Result, long? Figure)> ScanUntilTheFigureLands(string root)
    {
        using var landed = new ManualResetEventSlim();
        var cts = new CancellationTokenSource();
        var options = new ScanOptions
        {
            VolumeReader = volume =>
            {
                try
                {
                    return VolumeSpace.Read(volume);
                }
                finally
                {
                    landed.Set();
                }
            },
            AfterFolder = _ =>
            {
                landed.Wait(FigureWait);
                cts.Cancel();
            },
        };
        try
        {
            ScanResult result = await Task.Run(() => Scanner.Scan(root, new ScanProgress(), cts.Token, options)).WaitAsync(ScanWait);
            long? figure = await result.Store.VolumeUsed.WaitAsync(FigureWait);
            return (result, figure);
        }
        finally
        {
            cts.Cancel();
        }
    }

    // Get-Volume's Size minus SizeRemaining, from Windows PowerShell as an ordinary user. No retry and no skip:
    // a failure fails the test with what PowerShell said.
    private static long GetVolumeUsed(char letter)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"$v = Get-Volume -DriveLetter {letter} -ErrorAction Stop; [long]($v.Size - $v.SizeRemaining)" })
            start.ArgumentList.Add(arg);

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Get-Volume did not answer in 60 s.");
        }
        string text = stdout.Result.Trim();
        Assert.True(process.ExitCode == 0 && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _),
            $"Get-Volume -DriveLetter {letter} exited {process.ExitCode}: {text} {stderr.Result.Trim()}");
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static char FreeDriveLetter()
    {
        string[] used = Environment.GetLogicalDrives();
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Any(d => char.ToUpperInvariant(d[0]) == letter))
                return letter;
        }
        throw new InvalidOperationException("Every drive letter from D to Z is in use, so none can stand for a drive with no volume.");
    }
}
