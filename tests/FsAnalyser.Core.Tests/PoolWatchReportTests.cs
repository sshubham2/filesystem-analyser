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
    [InlineData("boot-too-old", "VOID", "none")]                        // X4: R0 more than 30 min after the boot
    [InlineData("idle-burst", "A", "PASS")]                             // C-027: a burst gap is left out of drift
    [InlineData("wcsc-during-listing", "WCIFS-UNATTRIBUTED", "none")]   // N2, X5: wcifs moved while the app listed
    [InlineData("aborted-before-f1", "WCIFS-UNATTRIBUTED", "none")]     // N1: -Protocol stopped itself after L and P
    [InlineData("listing-over-pressure-ineffective", "UNDECIDED", "none")] // N3: cache and leak cannot be told apart
    [InlineData("listing-over-survives-pressure", "C", "OS-recorded")]  // the OS keeps it, the app does not
    [InlineData("listing-over-reclaimed", "A", "PASS")]                 // M3: it was cache
    [InlineData("probe-over-plain-within", "B", "FAIL")]                // O-003 outcome B
    [InlineData("cancelled-run-over", "FAIL-cancel", "FAIL")]           // the cancel path, when the full runs are within
    public void TheReportRoutesARunAsTheToleranceRowSays(string scenario, string outcome, string verdict)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fsa-poolwatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var run = new Run();
            run.Build(scenario);
            File.WriteAllLines(Path.Combine(dir, "marks.jsonl"), run.Marks.Select(m => JsonSerializer.Serialize(m)));
            File.WriteAllLines(Path.Combine(dir, "arms.jsonl"), run.Arms.Select(a => JsonSerializer.Serialize(a)));

            (int exit, string output) = Report(dir);

            Assert.True(exit == 0, $"PoolWatch.ps1 -Report exited {exit}:\n{output}");
            Assert.Contains($"OUTCOME {outcome}\n", output.Replace("\r", ""));
            Assert.Contains($"VERDICT {verdict}", output);
            if (outcome != "A")
                Assert.DoesNotContain("VERDICT PASS", output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (int Exit, string Output) Report(string runDir)
    {
        string script = Path.Combine(RepoRoot(), "tests", "window", "PoolWatch.ps1");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Report", "-NoWrite", "-RunDir", runDir })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "PoolWatch.ps1 -Report did not exit within 2 min");
        return (process.ExitCode, output + error.Result);
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
    /// A protocol run as -Protocol writes it: R0, I0, L, S, P, five full exe runs, five cancelled or closed, the pressure
    /// step, I1, then the closing control. Pool flat at 5 GB paged and 3 GB nonpaged, WCsc flat, unless a scenario moves it.
    /// </summary>
    private sealed class Run
    {
        private double _t;
        private long _paged = 5000 * MiB, _nonpaged = 3000 * MiB, _wcsc = 211_000, _handles = 211_000, _fmfn = 1_800_000;
        private long _uptime = 600;
        private readonly List<string> _dropped = [];
        private readonly HashSet<string> _nullTags = [];

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

            Mark("R0");
            Idle("I0", burstAt: scenario == "idle-burst" ? 7 : -1);

            long listingPool = scenario.StartsWith("listing-over", StringComparison.Ordinal) ? 500 * MiB : 2 * MiB;
            Arm("L", "listing", summary: new() { ["listed"] = 1_000_000L }, pagedRise: listingPool,
                wcscRise: scenario == "wcsc-during-listing" ? 400 : 0);
            Arm("S", "probe", summary: new() { ["opened"] = 50_000L, ["failed"] = 0L },
                pagedRise: scenario == "probe-over-plain-within" ? 40 * MiB : 1 * MiB);
            Arm("P", "plain", summary: new() { ["opened"] = 50_000L, ["failed"] = 0L }, pagedRise: 1 * MiB);
            if (scenario == "aborted-before-f1")
            {
                // -Protocol's own stop after L and P (review N1): the abort mark, and nothing after it.
                Mark("abort", reason: "arm L raised WCsc by 400, over the idle envelope of 0.0");
                return;
            }
            for (int i = 1; i <= 5; i++)
                Arm($"F{i}", "exe-full", report: Report(), pagedRise: 1 * MiB);
            for (int i = 1; i <= 5; i++)
            {
                Arm($"X{i}", i <= 3 ? "exe-cancel" : "exe-close", report: i <= 3 ? Report() : null,
                    pagedRise: scenario == "cancelled-run-over" && i == 2 ? 200 * MiB : 1 * MiB, ranFull: false);
            }

            // The pressure step: cache tags fall when it works; the pool falls back when what L kept was cache.
            Mark("PR/before");
            bool effective = scenario != "listing-over-pressure-ineffective";
            if (effective)
                _fmfn -= 300_000;
            if (scenario == "listing-over-reclaimed")
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
            long pagedRise = 0, long wcscRise = 0, bool? ranFull = null, Dictionary<string, object>? extra = null)
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
                ["kind"] = "arm", ["arm"] = name, ["type"] = type, ["exitCode"] = 0, ["guardStop"] = null, ["summary"] = summary,
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
                ["system120089"] = _nullTags.Contains(label) ? null : _handles,
                ["tags"] = tags,
            });
            if (reason is not null)
                Marks[^1]["reason"] = reason;
        }

        private static Dictionary<string, object> Tag(string tag, long pa, long pf, long na, long nf) =>
            new() { ["tag"] = tag, ["pa"] = pa, ["pf"] = pf, ["pagedBytes"] = 0, ["na"] = na, ["nf"] = nf, ["nonpagedBytes"] = 0 };
    }
}
