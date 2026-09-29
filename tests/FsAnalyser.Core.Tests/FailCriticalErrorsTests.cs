/*
 * SL-077 bug reproduction.
 *
 * Bug: only Kernel32.TryGetVolumeUsed sets SEM_FAILCRITICALERRORS on its thread. The scanner's own opens, which
 * every scan makes through SynchronousIoCancel.Open (the root's listing open on the scan thread, and SizeProbe's
 * FILE_READ_ATTRIBUTES open on each probe worker), run with the thread's error mode at 0. A whole-drive scan of
 * a removable drive with no media can then raise the system's "insert a disk" box from those threads, and the
 * scan stops until a person answers it.
 *
 * Expected: every guarded open the scanner makes runs with the thread's SEM_FAILCRITICALERRORS bit set, so an
 * empty drive's open fails with an error instead of asking for a disk.
 *
 * Actual: the bit is clear during the root open and during the probe's open.
 *
 * An empty removable drive cannot be conjured on a test machine, so the mode is read from the guard's own
 * AfterCall hook: it runs on the calling thread once the Win32 open has returned and before the bracket is left,
 * so the mode read there is the one the open ran under.
 */
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Tests;

/// <summary>Runs alone: the guard's Created seam is static, and the test reads and sets the scan thread's own error mode.</summary>
[CollectionDefinition(nameof(CriticalErrorMode), DisableParallelization = true)]
public class CriticalErrorMode;

/// <summary>The critical-error box stays off for the scanner's own opens (SL-077).</summary>
[Collection(nameof(CriticalErrorMode))]
public class FailCriticalErrorsTests
{
    [Fact]
    public void TheScannersRootOpenAndSizeProbeRunWithTheCriticalErrorBoxOff()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 10);

        uint? rootMode = null, probeMode = null;
        var options = new ScanOptions
        {
            // Runs on a probe worker's own thread, whose guard the wrapper hooks before the real probe opens the file.
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                io.AfterCall = _ => probeMode ??= TestNative.ThreadErrorMode();
                return SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
            },
        };

        // Nothing set on this thread beforehand, so a bit seen during the root open was put there by the scanner.
        uint original = TestNative.SetThreadErrorMode(0);
        try
        {
            // The scan runs on this thread, and its root open is the guard's first call.
            ScanResult result = Scans.WithGuard(
                guard => guard.AfterCall = call =>
                {
                    if (call == 1)
                        rootMode = TestNative.ThreadErrorMode();
                },
                () => Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None, options));

            Assert.Equal(ScanStatus.Completed, result.Status);
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }

        var failures = new List<string>();
        Check(failures, "the scanner's root open", rootMode);
        Check(failures, "SizeProbe's open", probeMode);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static void Check(List<string> failures, string open, uint? mode)
    {
        if (mode is not { } seen)
            failures.Add($"{open} was never observed");
        else if ((seen & Kernel32.SEM_FAILCRITICALERRORS) == 0)
            failures.Add($"{open} ran with thread error mode 0x{seen:X}, without SEM_FAILCRITICALERRORS (0x1): an empty removable drive would raise the \"insert a disk\" box");
    }

    // SEM_NOGPFAULTERRORBOX: a bit a caller may have set that is not the scope's, so a restore that clears the mode, or
    // leaves the scope's bit behind, shows.
    private const uint NoGpFaultErrorBox = 0x2;
    private const uint FileListDirectory = 0x1;
    private const uint FileWriteAttributes = 0x100;

    /// <summary>
    /// Every folder the scan opens, not the root alone: a drive can be pulled after the root is open, and each folder
    /// below it is opened by its path. On local NTFS the root is opened twice, plainly and then keeping its last-access
    /// time (Scanner.ListRoot), and each folder below it once, so those four are the scan thread's calls that run with
    /// the box off. The listing and the queries go through a handle and keep the thread's own mode.
    /// </summary>
    [Fact]
    public void EveryFolderTheScanOpensIsOpenedWithTheBoxOff()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\sub");
        tree.File(@"a\sub\c.bin", 10);

        var calls = new List<(int Call, uint Mode)>();
        ScanResult result;
        uint original = TestNative.SetThreadErrorMode(0);
        try
        {
            result = Scans.WithGuard(
                guard => guard.AfterCall = call => calls.Add((call, TestNative.ThreadErrorMode())),
                () => Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None));
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(2, result.Totals.Folders); // a and a\sub, both listed
        string seen = string.Join(", ", calls.Select(c => $"{c.Call}:0x{c.Mode:X}"));
        int[] boxOff = calls.Where(c => c.Mode == Kernel32.SEM_FAILCRITICALERRORS).Select(c => c.Call).ToArray();
        Assert.True(boxOff.Length == 4 && boxOff[0] == 1,
            $"expected the root's two opens and one for each of a and a\\sub with the box off, the first being call 1; the scan thread's calls and modes were {seen}");
        Assert.All(calls, c => Assert.True(c.Mode is 0 or Kernel32.SEM_FAILCRITICALERRORS, $"call {c.Call} ran with mode 0x{c.Mode:X}: {seen}"));
    }

    /// <summary>
    /// A folder open takes one of two routes (SynchronousIoCancel.Open): asking for FILE_WRITE_ATTRIBUTES to keep the
    /// folder's last-access time, or, where the folder refuses that, the plain open. KeptLastAccess says which ran, and
    /// the box is off for both, with the caller's mode back after each.
    /// </summary>
    [Fact]
    public void BothRoutesOfAFolderOpenRunWithTheBoxOff()
    {
        using var tree = new TempTree();
        string keeping = tree.Folder("keeping");
        string refusing = tree.Folder("refusing");
        tree.Deny(refusing, FileWriteAttributes);

        uint? inside = null;
        (bool Kept, uint? Inside, uint After) viaKeeping, viaPlain;
        uint original = TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
        try
        {
            using var io = new SynchronousIoCancel(CancellationToken.None);
            io.AfterCall = _ => inside = TestNative.ThreadErrorMode();

            Assert.Equal(0, io.OpenDirectory(LongPath.ToExtended(keeping), keepLastAccess: true, out var kept));
            viaKeeping = (io.KeptLastAccess, inside, TestNative.ThreadErrorMode());
            io.Close(kept!);

            inside = null;
            Assert.Equal(0, io.OpenDirectory(LongPath.ToExtended(refusing), keepLastAccess: true, out var plain));
            viaPlain = (io.KeptLastAccess, inside, TestNative.ThreadErrorMode());
            io.Close(plain!);
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }

        Assert.Equal((true, (uint?)Kernel32.SEM_FAILCRITICALERRORS, NoGpFaultErrorBox), viaKeeping);
        Assert.Equal((false, (uint?)Kernel32.SEM_FAILCRITICALERRORS, NoGpFaultErrorBox), viaPlain);
    }

    /// <summary>
    /// SizeProbe called on this thread: its open runs with exactly SEM_FAILCRITICALERRORS, whatever the thread had, and the
    /// thread has its own mode back after, whether that was nothing, another bit, or the same bit set by a caller that
    /// already guards what it does.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(NoGpFaultErrorBox)]
    [InlineData(Kernel32.SEM_FAILCRITICALERRORS)]
    public void AProbeOnThisThreadOpensWithTheBoxOffAndGivesTheModeBack(uint preset)
    {
        using var tree = new TempTree();
        string file = tree.File("a.bin", 10);

        var modes = new List<uint>();
        long endOfFile;
        uint after;
        uint original = TestNative.SetThreadErrorMode(preset);
        try
        {
            using var io = new SynchronousIoCancel(CancellationToken.None);
            io.AfterCall = _ => modes.Add(TestNative.ThreadErrorMode());
            Assert.Equal(0, SizeProbe.Probe(io, LongPath.ToExtended(file), out endOfFile, out _));
            after = TestNative.ThreadErrorMode();
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }

        Assert.Equal(10, endOfFile);
        Assert.Equal(Kernel32.SEM_FAILCRITICALERRORS, modes[0]); // the open, the probe's first call
        Assert.Equal(preset, after);
    }

    /// <summary>
    /// Probes on several threads of their own at once, as the scan's probe workers run, two probes each: every open runs
    /// with the box off, and each thread has its own mode back after each probe, so a thread that goes on to other work
    /// carries no bit the scope left (review m3: dedicated threads, so nothing rests on which pool thread runs next).
    /// </summary>
    [Fact]
    public void ProbesOnSeveralThreadsAtOnceEachGiveTheirThreadItsModeBack()
    {
        const int Threads = 4, ProbesEach = 2;
        using var tree = new TempTree();
        string[] files = Enumerable.Range(0, Threads).Select(i => LongPath.ToExtended(tree.File($"f{i}.bin", 10 + i))).ToArray();

        var failures = new ConcurrentQueue<string>();
        using var together = new Barrier(Threads);
        Thread[] threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
        {
            try
            {
                // Half the threads carry another bit of their own, which must come back too.
                if (i % 2 == 1)
                    TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
                uint baseline = TestNative.ThreadErrorMode();
                using var io = new SynchronousIoCancel(CancellationToken.None);
                var modes = new List<uint>();
                io.AfterCall = _ => modes.Add(TestNative.ThreadErrorMode());
                if (!together.SignalAndWait(TimeSpan.FromSeconds(30)))
                    failures.Enqueue($"thread {i}: the others never arrived");
                for (int probe = 1; probe <= ProbesEach; probe++)
                {
                    modes.Clear();
                    int error = SizeProbe.Probe(io, files[i], out long endOfFile, out _);
                    uint after = TestNative.ThreadErrorMode();
                    if (error != 0 || endOfFile != 10 + i)
                        failures.Enqueue($"thread {i} probe {probe}: error {error}, {endOfFile} bytes");
                    if (modes.Count == 0 || modes[0] != Kernel32.SEM_FAILCRITICALERRORS)
                        failures.Enqueue($"thread {i} probe {probe}: the open ran with mode 0x{(modes.Count == 0 ? 0 : modes[0]):X}");
                    if (after != baseline)
                        failures.Enqueue($"thread {i} probe {probe}: mode 0x{after:X} after the probe, 0x{baseline:X} before");
                }
            }
            catch (Exception e)
            {
                failures.Enqueue($"thread {i}: {e}");
            }
        }) { IsBackground = true, Name = $"probe-{i}" }).ToArray();

        foreach (Thread thread in threads)
            thread.Start();
        foreach (Thread thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"{thread.Name} did not finish in 60 s");
        Assert.True(failures.IsEmpty, string.Join("\n", failures));
    }

    /// <summary>
    /// An open that fails takes Open's failure path: a folder that refuses to be listed (error 5, on both routes), and a
    /// path that is not there. Each ran with the box off and left the thread's mode as it found it, and a scan of the tree
    /// holding the refusing folder records it as unreadable and does the same.
    /// </summary>
    [Fact]
    public void AFailedOpenRanWithTheBoxOffAndGivesTheModeBack()
    {
        using var tree = new TempTree();
        string denied = tree.Folder("denied");
        tree.File(@"denied\not-seen.txt", 10);
        tree.File("seen.txt", 20);
        tree.Deny(denied, FileListDirectory);

        uint? inside = null;
        var failed = new List<(string What, int Error, bool Handed, uint? Inside, uint After)>();
        ScanResult result;
        uint afterScan;
        uint original = TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
        try
        {
            using (var io = new SynchronousIoCancel(CancellationToken.None))
            {
                io.AfterCall = _ => inside = TestNative.ThreadErrorMode();
                int error = io.OpenDirectory(LongPath.ToExtended(denied), keepLastAccess: true, out var handle);
                failed.Add(("the denied folder", error, handle is not null, inside, TestNative.ThreadErrorMode()));
                inside = null;
                error = io.OpenDirectory(LongPath.ToExtended(tree.PathOf("missing")), keepLastAccess: false, out handle);
                failed.Add(("a missing folder", error, handle is not null, inside, TestNative.ThreadErrorMode()));
                inside = null;
                error = io.OpenForAttributes(LongPath.ToExtended(tree.PathOf(@"missing\a.bin")), out handle);
                failed.Add(("a file in a missing folder", error, handle is not null, inside, TestNative.ThreadErrorMode()));
            }
            result = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None);
            afterScan = TestNative.ThreadErrorMode();
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }

        Assert.Equal(Kernel32.ERROR_ACCESS_DENIED, failed[0].Error);
        Assert.True(SizeProbe.IsGone(failed[1].Error), $"a missing folder's open failed with {failed[1].Error}");
        Assert.True(SizeProbe.IsGone(failed[2].Error), $"a missing file's open failed with {failed[2].Error}");
        Assert.All(failed, f =>
        {
            Assert.False(f.Handed, $"{f.What}: a handle was handed out");
            Assert.True(f.Inside == Kernel32.SEM_FAILCRITICALERRORS, $"{f.What}: the open ran with mode 0x{f.Inside:X}");
            Assert.True(f.After == NoGpFaultErrorBox, $"{f.What}: mode 0x{f.After:X} after the failed open");
        });
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal([(denied, Kernel32.ERROR_ACCESS_DENIED)], Scans.UnreadableByPath(result));
        Assert.Equal(NoGpFaultErrorBox, afterScan);
    }

    /// <summary>
    /// The ways out of Open that are not a result: a scan cancelled while its root open is in flight, so the guard
    /// discards the open; an open refused because Cancel came first; and an exception thrown inside the bracket. Each ran
    /// with the box off, and the thread has its own mode back after each.
    /// </summary>
    [Fact]
    public void ACancelledOrThrowingOpenGivesTheModeBack()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\b.bin", 10);

        uint? duringScan = null, beforeRefusal = null, beforeThrow = null;
        uint afterScan, afterRefusal, afterThrow;
        ScanResult cancelled;
        uint original = TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
        try
        {
            using (var cts = new CancellationTokenSource())
            {
                cancelled = Scans.WithGuard(
                    guard => guard.AfterCall = call =>
                    {
                        if (call != 1)
                            return;
                        duringScan = TestNative.ThreadErrorMode();
                        Scans.CancelDuringTheCall(cts, guard);
                    },
                    () => Scanner.Scan(tree.Root, new ScanProgress(), cts.Token));
            }
            afterScan = TestNative.ThreadErrorMode();

            using (var cts = new CancellationTokenSource())
            using (var io = new SynchronousIoCancel(cts.Token))
            {
                io.BeforeCall = _ =>
                {
                    beforeRefusal = TestNative.ThreadErrorMode();
                    cts.Cancel();
                };
                Assert.Equal(SynchronousIoCancel.Aborted, io.OpenDirectory(tree.ExtendedRoot, keepLastAccess: true, out var refused));
                Assert.Null(refused);
            }
            afterRefusal = TestNative.ThreadErrorMode();

            using (var io = new SynchronousIoCancel(CancellationToken.None))
            {
                io.AfterCall = _ =>
                {
                    beforeThrow = TestNative.ThreadErrorMode();
                    throw new PlantedFailure();
                };
                // A path that is not there, so the open leaves no handle behind the exception.
                Assert.Throws<PlantedFailure>(() => io.OpenForAttributes(LongPath.ToExtended(tree.PathOf("missing.bin")), out _));
            }
            afterThrow = TestNative.ThreadErrorMode();
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }
        Scans.WaitForAbortedCloses();

        Assert.Equal(ScanStatus.Cancelled, cancelled.Status);
        Assert.Equal(
            [Kernel32.SEM_FAILCRITICALERRORS, Kernel32.SEM_FAILCRITICALERRORS, Kernel32.SEM_FAILCRITICALERRORS],
            new[] { duringScan, beforeRefusal, beforeThrow }.Select(m => m ?? uint.MaxValue).ToArray());
        Assert.Equal([NoGpFaultErrorBox, NoGpFaultErrorBox, NoGpFaultErrorBox], new[] { afterScan, afterRefusal, afterThrow });
    }

    /// <summary>
    /// The volume read (SL-043's guard, now on the shared scope): GetDiskFreeSpaceExW runs with the box off, read through
    /// the test hook inside the scope, so the refactor cannot drop the guard with every result still right (review M1).
    /// A drive with a volume answers and one with none does not, as before, and the thread's mode comes back after both.
    /// </summary>
    [Fact]
    public void TheVolumeReadRunsWithTheBoxOffAndGivesTheModeBack()
    {
        string present = Path.GetPathRoot(Path.GetTempPath())!;
        string missing = FreeDriveLetter() + @":\";

        var inside = new List<uint>();
        bool answered, missingAnswered;
        long used;
        int error, missingError;
        uint afterPresent, afterMissing;
        uint original = TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
        Kernel32.BeforeVolumeRead = () => inside.Add(TestNative.ThreadErrorMode());
        try
        {
            answered = Kernel32.TryGetVolumeUsed(present, out used, out error);
            afterPresent = TestNative.ThreadErrorMode();
            missingAnswered = Kernel32.TryGetVolumeUsed(missing, out _, out missingError);
            afterMissing = TestNative.ThreadErrorMode();
        }
        finally
        {
            Kernel32.BeforeVolumeRead = null;
            TestNative.SetThreadErrorMode(original);
        }

        Assert.True(answered && error == 0 && used > 0, $"{present}: answered {answered}, error {error}, {used:N0} bytes used");
        Assert.False(missingAnswered, $"GetDiskFreeSpaceExW answered for {missing}");
        Assert.NotEqual(0, missingError);
        Assert.Equal([Kernel32.SEM_FAILCRITICALERRORS, Kernel32.SEM_FAILCRITICALERRORS], inside);
        Assert.Equal((NoGpFaultErrorBox, NoGpFaultErrorBox), (afterPresent, afterMissing));
    }

    /// <summary>
    /// Only CriticalErrorBoxOff.Begin makes a scope that does anything (review M1). A default one is not armed, turns
    /// nothing off and puts nothing back, and Kernel32.CreateFile refuses it, so a path open cannot be written with one.
    /// </summary>
    [Fact]
    public void AScopeNotMadeByBeginTurnsNothingOffAndOpensNothing()
    {
        using var tree = new TempTree();
        string file = LongPath.ToExtended(tree.File("a.bin", 1));

        uint original = TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
        try
        {
            Kernel32.CriticalErrorBoxOff unarmed = default;
            Assert.False(unarmed.IsArmed);
            Assert.Throws<InvalidOperationException>(() => Kernel32.CreateFile(unarmed, file, Kernel32.FILE_READ_ATTRIBUTES,
                Kernel32.FILE_SHARE_ALL, 0, Kernel32.OPEN_EXISTING, 0, 0));
            unarmed.Dispose();
            Assert.Equal(NoGpFaultErrorBox, TestNative.ThreadErrorMode());

            using Kernel32.CriticalErrorBoxOff armed = Kernel32.CriticalErrorBoxOff.Begin();
            Assert.True(armed.IsArmed);
            using SafeFileHandle opened = Kernel32.CreateFile(armed, file, Kernel32.FILE_READ_ATTRIBUTES,
                Kernel32.FILE_SHARE_ALL, 0, Kernel32.OPEN_EXISTING, 0, 0);
            Assert.False(opened.IsInvalid);
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }
    }

    /// <summary>
    /// Scopes nest: each gives back the mode it found, the inner one the outer one's bit and the outer one the caller's,
    /// as a guarded call made inside another guarded call does.
    /// </summary>
    [Fact]
    public void NestedScopesUnwindInOrder()
    {
        var modes = new List<uint>();
        uint original = TestNative.SetThreadErrorMode(NoGpFaultErrorBox);
        try
        {
            using (Kernel32.CriticalErrorBoxOff.Begin())
            {
                modes.Add(TestNative.ThreadErrorMode());
                using (Kernel32.CriticalErrorBoxOff.Begin())
                    modes.Add(TestNative.ThreadErrorMode());
                modes.Add(TestNative.ThreadErrorMode());
            }
            modes.Add(TestNative.ThreadErrorMode());
        }
        finally
        {
            TestNative.SetThreadErrorMode(original);
        }

        Assert.Equal([Kernel32.SEM_FAILCRITICALERRORS, Kernel32.SEM_FAILCRITICALERRORS, Kernel32.SEM_FAILCRITICALERRORS, NoGpFaultErrorBox], modes);
    }

    /// <summary>
    /// Kernel32.cs is excepted from ArchitectureRulesTests.PathOpensGoOnlyThroughTheGuardedOpen, and the compiler does not
    /// hold it: any member of Kernel32 can call its private CreateFileW or GetDiskFreeSpaceEx with no scope (SL-077's
    /// second review, m1). So its calls of the rule's named path-taking entry points are counted here, over its code lines,
    /// comments aside: the CreateFile wrapper, which refuses a scope that is not armed; CreateFileW declared and called
    /// once, in that wrapper; GetDiskFreeSpaceEx declared, named by its entry point and called once, in TryGetVolumeUsed
    /// inside the scope. A call or import of any other named entry point there changes the count, and fails here until
    /// somebody has put it inside a CriticalErrorBoxOff and written it into this list; a name the rule does not list is
    /// not counted. The patterns are the rule's own, read from ArchitectureRulesTests' source (the copy rules-parity
    /// compares with the yaml), so a widening of the rule reaches this count with no third copy to fall behind (the
    /// third review, m2).
    /// </summary>
    [Fact]
    public void Kernel32MakesOnlyThePathCallsThatRunInsideTheScope()
    {
        string repo = FindRepo();
        List<Regex> patterns = FactPatterns(Path.Combine(repo, @"tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs"),
            nameof(ArchitectureRulesTests.PathOpensGoOnlyThroughTheGuardedOpen));
        Assert.True(patterns.Count == 2 && patterns.All(p => p.GetGroupNumbers().Contains(1)),
            "PathOpensGoOnlyThroughTheGuardedOpen no longer has the two patterns this count reads, a call and an entry-point " +
            "name, each with the name as group 1: " + string.Join(" | ", patterns));

        var found = new List<(string Name, string Line)>();
        foreach (string line in File.ReadAllLines(Path.Combine(repo, @"src\FsAnalyser.Core\Native\Kernel32.cs")))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            foreach (Regex pattern in patterns)
                found.AddRange(pattern.Matches(line).Select(m => (m.Groups[1].Value, line.Trim())));
        }

        string[] read = ["CreateFile", "CreateFileW", "CreateFileW", "GetDiskFreeSpaceEx", "GetDiskFreeSpaceEx", "GetDiskFreeSpaceExW"];
        Assert.True(found.Select(f => f.Name).Order(StringComparer.Ordinal).SequenceEqual(read),
            "Kernel32.cs makes path calls other than the ones read for SL-077 (" + string.Join(", ", read) + "). Put each new one " +
            "inside a CriticalErrorBoxOff, then add it to this list:\n" + string.Join("\n", found.Select(f => $"{f.Name}: {f.Line}")));
    }

    /// <summary>
    /// The patterns of one ArchitectureRulesTests Fact, read from its source the way eng/rules-parity.ps1 reads them: each
    /// pattern is a line holding one verbatim literal that ends in ',' or ');', and the one ending in ');' is the last.
    /// Compiled with RegexOptions.Multiline, as that Fact compiles them.
    /// </summary>
    internal static List<Regex> FactPatterns(string file, string fact)
    {
        var literal = new Regex(@"^\s*@""((?:[^""]|"""")*)""\s*(,|\);)\s*$");
        string[] lines = File.ReadAllLines(file);
        int at = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^\s*public\s+void\s+{fact}\s*\(\s*\)\s*$"));
        Assert.True(at >= 0, $"No Fact {fact} in {file}.");

        var patterns = new List<Regex>();
        for (int i = at + 1; i < lines.Length && lines[i].TrimEnd() != "    }"; i++)
        {
            Match pattern = literal.Match(lines[i]);
            if (!pattern.Success)
                continue;
            patterns.Add(new Regex(pattern.Groups[1].Value.Replace("\"\"", "\""), RegexOptions.Multiline));
            if (pattern.Groups[2].Value == ");")
                break;
        }
        return patterns;
    }

    internal static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FsAnalyser.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"No FsAnalyser.slnx above {AppContext.BaseDirectory}.");
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

    private sealed class PlantedFailure : Exception;
}
