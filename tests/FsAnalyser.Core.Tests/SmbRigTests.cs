using System.Diagnostics;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The stall rig's isolation (SL-038, M1), without a share: the lock that keeps two rig runs off
/// 127.0.0.1 at once, and the filter that decides which mappings a new run may sweep. Each lock test
/// uses a name of its own, never <see cref="RigLock.SessionName"/>, so it cannot hold up a real rig run.
/// The same shapes with real mappings are window cases N-003 and N-004 (-IncludeNetwork).
/// </summary>
public class SmbRigTests
{
    [Fact]
    public void ASecondRunWaitsForTheFirst()
    {
        string name = UniqueName();
        using var firstHolds = new ManualResetEventSlim();
        using var firstMayEnd = new ManualResetEventSlim();
        Exception? failed = null;
        var first = new Thread(() =>
        {
            try
            {
                using RigLock held = RigLock.Take(name, TimeSpan.FromSeconds(5));
                firstHolds.Set();
                firstMayEnd.Wait(TimeSpan.FromSeconds(30));
            }
            catch (Exception e)
            {
                failed = e;
            }
        });
        first.Start();
        Assert.True(firstHolds.Wait(TimeSpan.FromSeconds(5)), $"the first run never took the lock: {failed?.Message}");

        Thread waiting = TakeOnItsOwnThread(name, TimeSpan.FromSeconds(30), e => failed = e);
        Assert.False(waiting.Join(TimeSpan.FromMilliseconds(500)), "the second run took the lock while the first held it");

        firstMayEnd.Set();
        Assert.True(waiting.Join(TimeSpan.FromSeconds(5)), "the second run did not get the lock once the first let go");
        Assert.True(first.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failed);
    }

    /// <summary>
    /// A live holder in another process makes the next run fail with a message that says why. That process
    /// is then killed (TerminateProcess, no cleanup) while a run waits: the waiter gets the lock at once, and
    /// so does a run that starts after it.
    /// </summary>
    [Fact]
    public void ALiveHolderFailsTheNextRunLoudlyAndAKilledOneDoesNotBlockIt()
    {
        string name = UniqueName();
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"$m = [System.Threading.Mutex]::new($false, '{name}'); [void]$m.WaitOne(); [Console]::Out.WriteLine('held'); [Console]::Out.Flush(); Start-Sleep -Seconds 120" })
            start.ArgumentList.Add(arg);
        using Process holder = Process.Start(start) ?? throw new InvalidOperationException("Could not start powershell.exe.");
        Exception? failed = null;
        Thread waiting;
        try
        {
            // The name is this test's own, so nothing else holds it: the holder prints, or fails and exits.
            string? line = holder.StandardOutput.ReadLine();
            Assert.True(line == "held", $"the holder process never took the lock (it printed {line ?? "nothing"})");

            var refused = Assert.Throws<TimeoutException>(() => RigLock.Take(name, TimeSpan.FromMilliseconds(300)));
            Assert.Contains("Another stall-rig run", refused.Message);

            waiting = TakeOnItsOwnThread(name, TimeSpan.FromSeconds(60), e => failed = e);
            Assert.False(waiting.Join(TimeSpan.FromMilliseconds(500)), "a run took the lock while the holder process was alive");
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }

        Assert.True(waiting.Join(TimeSpan.FromSeconds(5)), "the waiting run was still blocked 5 s after the holder was killed");
        Assert.Null(failed);
        var clock = Stopwatch.StartNew();
        using (RigLock.Take(name, TimeSpan.FromSeconds(5)))
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"a run after the kill waited {clock.ElapsedMilliseconds} ms");
    }

    // Takes and releases the lock on a thread of its own, as a separate run would: a Mutex is released by
    // the thread that owns it.
    private static Thread TakeOnItsOwnThread(string name, TimeSpan wait, Action<Exception> failed)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using RigLock held = RigLock.Take(name, wait);
            }
            catch (Exception e)
            {
                failed(e);
            }
        });
        thread.Start();
        return thread;
    }

    /// <summary>
    /// What a new run sweeps under the lock, through the same predicate the sweep calls: the rig's own
    /// address and share prefix, and nothing a person mapped (case C-022). The prefix 'fsa' is
    /// case-sensitive; the 8 hex digits after it may be either case.
    /// </summary>
    [Theory]
    [InlineData(@"\\127.0.0.1\fsa0123abcd", true)]
    [InlineData(@"\\127.0.0.1\fsaABCDEF01", true)]
    [InlineData(@"\\127.0.0.1\FSA0123abcd", false)]
    [InlineData(@"\\100.109.48.75\collab", false)]
    [InlineData(@"\\127.0.0.1\collab", false)]
    [InlineData(@"\\127.0.0.1\fsa0123abc", false)]
    [InlineData(@"\\127.0.0.1\fsa0123abcde", false)]
    [InlineData(@"\\127.0.0.1\fsa0123abcg", false)]
    [InlineData(@"\\127.0.0.1\fsa0123abcd\sub", false)]
    [InlineData(@"\\127.0.0.2\fsa0123abcd", false)]
    [InlineData(@"\\127.0.0.10\fsa0123abcd", false)]
    [InlineData(@"\\localhost\fsa0123abcd", false)]
    [InlineData(@"\\--1.ipv6-literal.net\fsa0123abcd", false)]
    [InlineData(@"\\server\share\127.0.0.1\fsa0123abcd", false)]
    [InlineData("Z:", false)]
    [InlineData("", false)]
    public void OnlyTheRigsOwnMappingsAreSwept(string remotePath, bool swept)
    {
        Assert.Equal(swept, LoopbackShare.IsRigMapping(remotePath));
    }

    private static string UniqueName() => $@"Local\FsAnalyser-SmbRig-test-{Guid.NewGuid():N}";
}
