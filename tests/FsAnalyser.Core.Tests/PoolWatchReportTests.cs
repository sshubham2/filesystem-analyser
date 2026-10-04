using System.Diagnostics;
using System.Text.Json;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-095: tests/window/PoolWatch.ps1 -Report, the judge of the pool protocol, on run logs written here: no scan, no
/// restart, no reading of the machine. Each case is the clean run that passes, with one thing changed, and says which
/// outcome the tolerance row routes it to. The point of most of them is what must never read PASS: a run whose closing
/// control shows the layer was not armable (spike 85's VOID), a mark or a reading that is missing (review m7), wcifs
/// activity in an arm beyond the idle envelope (reviews N2, X5), and a pressure step that took nothing back (review N3).
/// </summary>
public class PoolWatchReportTests
{
    private const long MiB = 1024 * 1024;
    private static readonly DateTime Start = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("pass", "A", "PASS")]
    [InlineData("control-leaks-zero", "VOID", "none")]                  // C-041: the layer was already touched
    [InlineData("control-leaks-one", "VOID", "none")]                   // Y3: one ambient WCsc is not an armable layer
    [InlineData("mark-missing", "UNDECIDED", "none")]                   // C-027: L/settled was never taken
    [InlineData("tags-null", "UNDECIDED", "none")]                      // C-027: a reading Windows would not give
    [InlineData("handles-null", "UNDECIDED", "none")]                   // C-027: the class 64 handle query failed
    [InlineData("boot-too-old", "VOID", "none")]                        // X4: R0 more than 30 min after the boot
    [InlineData("idle-burst", "A", "PASS")]                             // C-027: a burst gap is left out of drift
    [InlineData("wcsc-during-listing", "WCIFS-UNATTRIBUTED", "none")]   // N2, X5: wcifs moved while the app listed
    [InlineData("aborted-before-f1", "WCIFS-UNATTRIBUTED", "none")]     // N1: -Protocol stopped itself after L and P
    [InlineData("listing-over-pressure-ineffective", "UNDECIDED", "none")] // N3: cache and leak cannot be told apart
    [InlineData("listing-over-survives-pressure", "C", "OS-recorded")]  // the OS keeps it, the app does not
    [InlineData("listing-over-reclaimed", "A", "PASS")]                 // M3: it was cache
    [InlineData("probe-over-plain-within", "B", "FAIL")]                // O-003 outcome B
    [InlineData("cancelled-run-over", "FAIL-cancel", "FAIL")]           // the cancel path, when the full runs are within
    [InlineData("full-run-crashed", "UNDECIDED", "none")]               // code review M1: F3 exited 70 with no report
    [InlineData("exe-runs-missing", "UNDECIDED", "none")]               // code review M1: none of the ten exe runs is there
    [InlineData("exe-settled-missing", "UNDECIDED", "none")]            // code review M1: F4's settled mark was never taken
    [InlineData("exe-guard-stop", "FAIL", "FAIL")]                      // code review M1: the guard stopped F2, so it is over T
    [InlineData("cancel-guard-stop", "FAIL-cancel", "FAIL")]            // ... and X1, a cancelled run
    [InlineData("probe-guard-stop", "B", "FAIL")]                       // ... and S, the per-file probe
    [InlineData("cancel-ran-full", "UNDECIDED", "none")]                // X3's trigger never fired: four cancelled runs, not five
    [InlineData("boot-settles-before-i0", "A", "PASS")]                 // R2-N1: 1 GiB of boot settle, all before I0
    [InlineData("boot-settles-then-listing-reclaimed", "A", "PASS")]    // ... and post-pressure is judged from L/before, not R0
    [InlineData("boot-never-settled", "VOID", "none")]                  // R2-N1: no two quiet gaps within the cap
    [InlineData("settle-marks-missing", "UNDECIDED", "none")]           // R2-N1: R0 straight into I0
    public void TheReportRoutesARunAsTheToleranceRowSays(string scenario, string outcome, string verdict)
    {
        string output = Judge(scenario);

        Assert.Contains($"OUTCOME {outcome}\n", output);
        Assert.Contains($"VERDICT {verdict}", output);
        if (outcome != "A")
            Assert.DoesNotContain("VERDICT PASS", output);
    }

    /// <summary>
    /// Code review M1: DONE (c) is five full and five cancelled C: scans, so a session that does not hold all ten, or
    /// whose full run crashed, decides nothing; and an arm the guard stopped is over T, whichever arm it is. Each of
    /// these read A/PASS before, with every other reading clean.
    /// </summary>
    [Fact]
    public void ExeRunsThatAreMissingCrashedOrStoppedByTheGuardNeverPass()
    {
        foreach ((string scenario, string line) in new[]
        {
            ("full-run-crashed", "REASON UNDECIDED: full run F3 did not complete: exit 70, no report"),
            ("exe-runs-missing", "REASON UNDECIDED: the protocol's ten exe runs are not all there: 0 full of 5, 0 cancelled or closed of 5"),
            ("exe-settled-missing", "REASON UNDECIDED: mark F4/settled is missing"),
            ("cancel-ran-full", "REASON UNDECIDED: only 4 of the 5 cancelled or closed runs stopped before the end; X3 ran to the end"),
            ("exe-guard-stop", "REASON FAIL: full exe runs over T: F2 (stopped by the guard: available 4000 MB, pool 9.1 GB)"),
            ("cancel-guard-stop", "REASON FAIL-cancel: cancelled or closed runs over T while the full runs are within: X1 (stopped by the guard"),
            ("probe-guard-stop", "REASON B: the app's per-file probe (stopped by the guard"),
        })
        {
            string output = Judge(scenario);
            Assert.Contains(line, output);
            Assert.DoesNotContain("VERDICT PASS", output);
        }
    }

    /// <summary>
    /// Review R2-N1: the boot's own settle (spike 3 measured about 1 GiB here) lands in settle marks between R0 and I0,
    /// not in I0, where spread over most of its gaps it made I0's drift disagree with I1's and VOIDed the session. A
    /// boot that never settles is VOID with the reason, and a log with no settle phase is UNDECIDED.
    /// </summary>
    [Fact]
    public void TheBootSettlesBeforeI0OrTheRunDecidesNothing()
    {
        double drift = double.NaN;
        string settled = Judge("boot-settles-before-i0", write: true, after: dir =>
        {
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "result.json")));
            drift = result.RootElement.GetProperty("tolerance").GetProperty("driftPagedPerSec").GetDouble();
        });
        Assert.Contains("OUTCOME A\n", settled);
        Assert.True(Math.Abs(drift) < 1, $"paged drift {drift} B/s: the boot's settle reached I0");

        string never = Judge("boot-never-settled");
        Assert.Contains("OUTCOME VOID\n", never);
        Assert.Contains("REASON VOID: the boot had not settled before I0: 31 settle marks, the last gap moved paged 40.0 MiB", never);

        string none = Judge("settle-marks-missing");
        Assert.Contains("REASON UNDECIDED: there are no settle marks between R0 and I0", none);
    }

    /// <summary>
    /// Review R2-N2: -Prepare's check of the no-link walker can fail. Through Invoke-NoLinkWalk's own flags, a planted
    /// 'All Users' junction (and a directory symlink, where this user may make one) is skipped: none of its target's
    /// bytes counted, its Microsoft folder never listed at three levels; the same listing without /XJ does go through
    /// it, so a followed link would have shown; and the real C:\Users\All Users is skipped. -Protocol refuses to start
    /// on a prepare.json that does not record the walker as safe, before it takes a single mark.
    /// </summary>
    [Fact]
    public void TheNoLinkCheckCanFailAndTheProtocolRefusesWhenItDid()
    {
        (int exit, string output) = PoolWatch("-NoLinkCheck");
        Assert.True(exit == 0, $"PoolWatch.ps1 -NoLinkCheck exited {exit}:\n{output}");
        using (JsonDocument check = JsonDocument.Parse(output.Trim().Split('\n')[^1]))
        {
            JsonElement c = check.RootElement;
            Assert.Equal(10, c.GetProperty("plantedBytes").GetInt64());
            Assert.True(c.GetProperty("plantedLinksSkipped").GetBoolean(), output);
            Assert.True(c.GetProperty("detectorSeesAFollowedLink").GetBoolean(), "without /XJ the walk did not go through the junction, so the check could not see a followed link: " + output);
            if (Directory.Exists(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("PUBLIC"))!, "All Users")))
                Assert.True(c.GetProperty("usersAllUsersSkipped").GetBoolean(), output);
            Assert.True(c.GetProperty("skips").GetBoolean(), output);
        }

        string dir = Path.Combine(Path.GetTempPath(), "fsa-poolwatch-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "root");
        Directory.CreateDirectory(Path.Combine(dir, "prepare"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(dir, "prepare", "prepare.json"), """{ "kind": "prepare", "noLinkWalkerSkipsAllUsers": false }""");

            (int refused, string said) = PoolWatch("-Protocol", "-Smoke", "-Root", root, "-RunDir", dir);

            Assert.NotEqual(0, refused);
            Assert.Contains("noLinkWalkerSkipsAllUsers is not true", said);
            Assert.False(File.Exists(Path.Combine(dir, "marks.jsonl")), "-Protocol took marks before refusing");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// C-011: a run on a boot that has already walked the layer. Its first touches are spent, so fixed arms that leak
    /// nothing pass vacuously, which is what voided spike 85. Both ways it shows: R0 taken long after the boot (here
    /// 2 h, over the 30 min the design allows and the 15 min an earlier design did), and a closing control over a
    /// layer subtree that leaks 0 WCsc. Every arm is clean in both, and neither may read PASS.
    /// </summary>
    [Fact]
    public void ARunOnABootThatAlreadyWalkedTheLayerIsVoidNeverPass()
    {
        string old = Judge("boot-too-old");
        Assert.Contains("OUTCOME VOID\n", old);
        Assert.Contains("REASON VOID: the boot was 7200 s old at R0", old);
        Assert.DoesNotContain("VERDICT PASS", old);

        string spent = Judge("control-leaks-zero");
        Assert.Contains("OUTCOME VOID\n", spent);
        Assert.Contains("REASON VOID: the closing control leaked 0 WCsc", spent);
        Assert.DoesNotContain("VERDICT PASS", spent);
    }

    /// <summary>
    /// C-027 (review m7): a mark that is missing, a reading with null tags, a null class 64 handle count, or no
    /// `fltmc filters` paste is UNDECIDED for what needs it, never PASS; and an idle gap that is a burst (over 3x the
    /// median gap's rate) is left out of drift, so the clean run beside it still reads A with zero drift.
    /// </summary>
    [Fact]
    public void MissingOrNullReadingsAreUndecidedAndABurstGapIsLeftOutOfDrift()
    {
        foreach ((string scenario, string reason) in new[]
        {
            ("mark-missing", "REASON UNDECIDED: mark L/settled is missing"),
            ("tags-null", "REASON UNDECIDED: arm L has no WCsc reading"),
            ("handles-null", "REASON UNDECIDED: arm L has no 0x120089 handle reading"),
        })
        {
            string output = Judge(scenario);
            Assert.Contains("OUTCOME UNDECIDED\n", output);
            Assert.Contains(reason, output);
            Assert.DoesNotContain("VERDICT PASS", output);
        }

        // No fltmc paste: the filter list is UNDECIDED, whatever the pool verdict; a paste records it.
        string unpasted = Judge("pass", beside: dir => File.WriteAllText(Path.Combine(dir, "environment.json"),
            """{ "kind": "environment", "fltmcFilters": "not supplied: needs an administrator; paste `fltmc filters` into fltmc.txt" }"""));
        Assert.Contains("FILTERS UNDECIDED: no `fltmc filters` output was supplied", unpasted);
        Assert.DoesNotContain("FILTERS recorded", unpasted);
        string pasted = Judge("pass", beside: dir => File.WriteAllLines(Path.Combine(dir, "fltmc.txt"),
            new[] { "Filter Name                     Num Instances    Altitude    Frame", "wcifs                                   1       189900         0" }));
        Assert.Contains("FILTERS recorded:", pasted);

        // What fltmc prints without elevation is not a filter list (review R2-N3), pasted or carried in a record.
        string refused = Judge("pass", beside: dir => File.WriteAllLines(Path.Combine(dir, "fltmc.txt"),
            new[] { "Filter listing failed with error: 0x80070005", "Access is denied." }));
        Assert.Contains("FILTERS UNDECIDED: fltmc.txt holds no filter list (Filter listing failed with error: 0x80070005)", refused);
        Assert.DoesNotContain("FILTERS recorded", refused);
        string refusedInRecord = Judge("pass", beside: dir => File.WriteAllText(Path.Combine(dir, "environment.json"),
            """{ "kind": "environment", "fltmcFilters": [ "Filter listing failed with error: 0x80070005", "Access is denied." ] }"""));
        Assert.Contains("FILTERS UNDECIDED: fltmc.txt holds no filter list (Filter listing failed with error: 0x80070005)", refusedInRecord);
        Assert.DoesNotContain("FILTERS recorded", refusedInRecord);

        double drift = double.NaN;
        string burst = Judge("idle-burst", write: true, after: dir =>
        {
            using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "result.json")));
            drift = result.RootElement.GetProperty("tolerance").GetProperty("driftPagedPerSec").GetDouble();
        });
        Assert.Contains("OUTCOME A\n", burst);
        Assert.Contains("VERDICT PASS", burst);
        Assert.True(Math.Abs(drift) < 1, $"paged drift {drift} B/s: the 300 MiB burst gap was counted into it");
    }

    /// <summary>Writes the scenario's run logs to a scratch folder, runs -Report on it, and returns what it printed.</summary>
    private static string Judge(string scenario, Action<string>? beside = null, Action<string>? after = null, bool write = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fsa-poolwatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var run = new Run();
            run.Build(scenario);
            File.WriteAllLines(Path.Combine(dir, "marks.jsonl"), run.Marks.Select(m => JsonSerializer.Serialize(m)));
            File.WriteAllLines(Path.Combine(dir, "arms.jsonl"), run.Arms.Select(a => JsonSerializer.Serialize(a)));
            beside?.Invoke(dir);

            (int exit, string output) = Report(dir, write);

            Assert.True(exit == 0, $"PoolWatch.ps1 -Report exited {exit}:\n{output}");
            after?.Invoke(dir);
            return output.Replace("\r", "");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (int Exit, string Output) Report(string runDir, bool write) =>
        write ? PoolWatch("-Report", "-RunDir", runDir) : PoolWatch("-Report", "-RunDir", runDir, "-NoWrite");

    private static (int Exit, string Output) PoolWatch(params string[] arguments)
    {
        string script = Path.Combine(RepoRoot(), "tests", "window", "PoolWatch.ps1");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), $"PoolWatch.ps1 {arguments[0]} did not exit within 2 min");
        return (process.ExitCode, (output + error.Result).Replace("\r", ""));
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "FsAnalyser.slnx")))
                return dir;
        }
        throw new InvalidOperationException("FsAnalyser.slnx is not above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// A protocol run as -Protocol writes it: R0, the settle marks, I0, L, S, P, five full exe runs, five cancelled or
    /// closed, the pressure step, I1, then the closing control. Pool flat at 5 GB paged and 3 GB nonpaged, WCsc flat, unless a scenario moves it.
    /// </summary>
    private sealed class Run
    {
        private double _t;
        private long _paged = 5000 * MiB, _nonpaged = 3000 * MiB, _wcsc = 211_000, _handles = 211_000, _fmfn = 1_800_000;
        private long _uptime = 600;
        private readonly List<string> _dropped = [];
        private readonly HashSet<string> _nullTags = [];
        private readonly HashSet<string> _nullHandles = [];

        public List<Dictionary<string, object?>> Marks { get; } = [];

        public List<Dictionary<string, object?>> Arms { get; } = [];

        public void Build(string scenario)
        {
            if (scenario == "boot-too-old")
                _uptime = 7200;
            if (scenario == "mark-missing")
                _dropped.Add("L/settled");
            if (scenario == "tags-null")
                _nullTags.Add("L/settled");
            if (scenario == "handles-null")
                _nullHandles.Add("L/settled"); // the tags read, the handle table did not
            if (scenario == "exe-settled-missing")
                _dropped.Add("F4/settled");
            const string guard = "available 4000 MB, pool 9.1 GB";

            Mark("R0");
            if (!Settle(scenario))
                return; // -Protocol stops when the boot has not settled within the cap (review R2-N1)
            Idle("I0", burstAt: scenario == "idle-burst" ? 7 : -1);

            bool listingOver = scenario.StartsWith("listing-over", StringComparison.Ordinal) || scenario == "boot-settles-then-listing-reclaimed";
            long listingPool = listingOver ? 500 * MiB : 2 * MiB;
            Arm("L", "listing", summary: new() { ["listed"] = 1_000_000L }, pagedRise: listingPool,
                wcscRise: scenario == "wcsc-during-listing" ? 400 : 0);
            Arm("S", "probe", summary: new() { ["opened"] = 50_000L, ["failed"] = 0L },
                pagedRise: scenario == "probe-over-plain-within" ? 40 * MiB : 1 * MiB,
                guardStop: scenario == "probe-guard-stop" ? guard : null);
            Arm("P", "plain", summary: new() { ["opened"] = 50_000L, ["failed"] = 0L }, pagedRise: 1 * MiB);
            if (scenario == "aborted-before-f1")
            {
                // -Protocol's own stop after L and P (review N1): the abort mark, and nothing after it.
                Mark("abort", reason: "arm L raised WCsc by 400, over the idle envelope of 0.0");
                return;
            }
            if (scenario != "exe-runs-missing")
            {
                for (int i = 1; i <= 5; i++)
                {
                    bool crashed = scenario == "full-run-crashed" && i == 3, stopped = scenario == "exe-guard-stop" && i == 2;
                    Arm($"F{i}", "exe-full", report: crashed || stopped ? null : Report(), pagedRise: 1 * MiB,
                        exitCode: crashed ? 70 : stopped ? -1 : 0, guardStop: stopped ? guard : null);
                }
                for (int i = 1; i <= 5; i++)
                {
                    bool stopped = scenario == "cancel-guard-stop" && i == 1;
                    Arm($"X{i}", i <= 3 ? "exe-cancel" : "exe-close", report: i <= 3 && !stopped ? Report() : null,
                        pagedRise: scenario == "cancelled-run-over" && i == 2 ? 200 * MiB : 1 * MiB,
                        ranFull: scenario == "cancel-ran-full" && i == 3, exitCode: stopped ? -1 : i <= 3 ? 0 : 70, guardStop: stopped ? guard : null);
                }
            }

            // The pressure step: cache tags fall when it works; the pool falls back when what L kept was cache.
            Mark("PR/before");
            bool effective = scenario != "listing-over-pressure-ineffective";
            if (effective)
                _fmfn -= 300_000;
            if (scenario is "listing-over-reclaimed" or "boot-settles-then-listing-reclaimed")
                _paged -= listingPool;
            _t += 120;
            Mark("PR/after");
            Arms.Add(new() { ["kind"] = "arm", ["arm"] = "PR", ["type"] = "pressure", ["exitCode"] = 0, ["summary"] = new Dictionary<string, object> { ["minAvailableMB"] = 4600, ["lowMemorySeen"] = true } });

            Idle("I1", burstAt: -1);
            int entries = 50;
            long controlRise = scenario switch { "control-leaks-zero" => 0, "control-leaks-one" => 1, _ => 48 };
            Arm("C", "control", pagedRise: 0, wcscRise: controlRise, extra: new() { ["entries"] = entries });
        }

        private static Dictionary<string, object?> Report() => new() { ["status"] = "Completed", ["skippedStores"] = 1, ["opensUnderExcludedStore"] = 0 };

        /// <summary>
        /// Between R0 and I0, a mark a minute until two gaps in a row move each pool by under 16 MiB. A quiet boot settles
        /// at once; one that grows 1 GiB does so over twelve gaps, then goes quiet; one that never settles reaches the
        /// 30 min cap. False when it did not settle, where -Protocol stops.
        /// </summary>
        private bool Settle(string scenario)
        {
            if (scenario == "settle-marks-missing")
                return true;
            int growing = scenario switch { "boot-never-settled" => 30, _ when scenario.StartsWith("boot-settles", StringComparison.Ordinal) => 12, _ => 0 };
            long step = scenario == "boot-never-settled" ? 40 * MiB : 85 * MiB;
            int quiet = scenario == "boot-never-settled" ? 0 : 2;
            Mark("settle/00");
            for (int i = 1; i <= growing + quiet; i++)
            {
                _t += 60;
                if (i <= growing)
                {
                    _paged += step;
                    _nonpaged += step / 8;
                }
                Mark($"settle/{i:D2}");
            }
            return quiet > 0;
        }

        private void Idle(string window, int burstAt)
        {
            for (int i = 0; i <= 20; i++)
            {
                if (i > 0)
                    _t += 60;
                if (i == burstAt)
                    _paged += 300 * MiB; // a burst: someone else's, in one gap, and it stays
                Mark($"{window}/{i:D2}");
            }
        }

        private void Arm(string name, string type, Dictionary<string, object>? summary = null, Dictionary<string, object?>? report = null,
            long pagedRise = 0, long wcscRise = 0, bool? ranFull = null, Dictionary<string, object>? extra = null, int exitCode = 0,
            string? guardStop = null)
        {
            Mark($"{name}/before");
            _t += 300;
            _paged += pagedRise;
            _wcsc += wcscRise;
            _handles += wcscRise;
            Mark($"{name}/exit");
            _t += 600;
            Mark($"{name}/settled");
            var line = new Dictionary<string, object?>
            {
                ["kind"] = "arm", ["arm"] = name, ["type"] = type, ["exitCode"] = exitCode, ["guardStop"] = guardStop, ["summary"] = summary,
                ["report"] = report, ["ranFull"] = ranFull,
            };
            foreach (var pair in extra ?? [])
                line[pair.Key] = pair.Value;
            Arms.Add(line);
        }

        private void Mark(string label, string? reason = null)
        {
            if (_dropped.Contains(label))
                return;
            object? tags = _nullTags.Contains(label) ? null : new object[]
            {
                Tag("WCsc", 0, 0, _wcsc, 0),
                Tag("FMfn", _fmfn, 0, 0, 0),
                Tag("File", 0, 0, 4_700_000, 0),
                Tag("NtfF", 170_000, 0, 0, 0),
            };
            Marks.Add(new()
            {
                ["kind"] = "mark",
                ["label"] = label,
                ["utc"] = Start.AddSeconds(_t).ToString("o"),
                ["uptimeSec"] = _uptime + (long)_t,
                ["pagedBytes"] = _paged,
                ["nonpagedBytes"] = _nonpaged,
                ["availableMB"] = 12000,
                ["systemHandles"] = _handles + 7000,
                ["system120089"] = _nullTags.Contains(label) || _nullHandles.Contains(label) ? null : _handles,
                ["tags"] = tags,
            });
            if (reason is not null)
                Marks[^1]["reason"] = reason;
        }

        private static Dictionary<string, object> Tag(string tag, long pa, long pf, long na, long nf) =>
            new() { ["tag"] = tag, ["pa"] = pa, ["pf"] = pf, ["pagedBytes"] = 0, ["na"] = na, ["nf"] = nf, ["nonpagedBytes"] = 0 };
    }
}
