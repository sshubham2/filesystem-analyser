using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-008 review m2 and M1, closed as their class: the verdict of the local-drive window script
/// (tests/window/SL-008-RescanOfferOnALocalDrive.Tests.ps1) on every ending a run can have, with the launches, the report read, the
/// privilege read and the drive list stubbed (-Stub), so that nothing here prompts, opens a window or scans a drive. The class is
/// 'runs where offered=false is correct but the table says FAILED' and the table's other rows, enumerated in
/// tests/window/SL-008-RescanOfferEndingsCorpus.json. Each part is run here:
/// <list type="bullet">
/// <item>every row's exit code and step verdicts, under pwsh and under Windows PowerShell 5.1 (the script has to work under both: the
/// privilege read is the same under both, where [Environment]::IsPrivilegedProcess exists only on .NET 8 and later);</item>
/// <item>the same rows through a copy of the script with the two lines marked MUTATE taken out, which is the table as it was before
/// the fix: its exit codes are the corpus's 'before' column, and the rows where it differs from 'expect' are the members the fix closed;</item>
/// <item>the process's own exit code for one row of each verdict.</item>
/// </list>
/// </summary>
public class RescanOfferEndingsTests
{
    private static readonly string Repo = FailCriticalErrorsTests.FindRepo();
    private static readonly string Script = Path.Combine(Repo, "tests", "window", "SL-008-RescanOfferOnALocalDrive.Tests.ps1");

    private sealed record Row(string Id, int Expect, int Before, Dictionary<string, string> Steps, JsonNode Scenario);

    private static JsonObject Corpus() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Repo, "tests", "window", "SL-008-RescanOfferEndingsCorpus.json")))!;

    // A merge patch: objects merge key by key, null removes a key, anything else replaces.
    private static JsonNode? Merge(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject || target is not JsonObject targetObject)
            return patch?.DeepClone();
        var result = (JsonObject)targetObject.DeepClone();
        foreach ((string key, JsonNode? value) in patchObject)
        {
            if (value is null)
                result.Remove(key);
            else
                result[key] = Merge(result[key], value);
        }
        return result;
    }

    private static Row[] Rows()
    {
        JsonObject corpus = Corpus();
        JsonNode defaults = corpus["defaults"]!;
        return [.. ((JsonArray)corpus["rows"]!).Select(r =>
        {
            var row = (JsonObject)r!;
            var scenario = (JsonObject)Merge(defaults, row["set"])!;
            scenario["id"] = row["id"]!.GetValue<string>();
            Dictionary<string, string> steps = ((JsonObject)row["steps"]!).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
            return new Row(row["id"]!.GetValue<string>(), row["expect"]!.GetValue<int>(), row["before"]!.GetValue<int>(), steps, scenario);
        })];
    }

    private static string WindowsPowerShell() =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static (int ExitCode, string Output) Run(string shell, string script, string stub)
    {
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Stub", stub })
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), $"{shell} did not finish in 120 s");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static Dictionary<string, (int Exit, Dictionary<string, string> Steps)> ParseRows(string output)
    {
        var parsed = new Dictionary<string, (int, Dictionary<string, string>)>();
        foreach (string line in output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ROW ", StringComparison.Ordinal)))
        {
            string[] parts = line.Split(' ');
            int exit = int.Parse(parts[2]["exit=".Length..]);
            parsed[parts[1]] = (exit, parts.Skip(3).Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]));
        }
        return parsed;
    }

    private static string WriteRows(string folder, string name, IEnumerable<Row> rows)
    {
        string path = Path.Combine(folder, name);
        var file = new JsonObject { ["rows"] = new JsonArray([.. rows.Select(r => r.Scenario.DeepClone())]) };
        File.WriteAllText(path, file.ToJsonString());
        return path;
    }

    private static void CheckRows(string label, string output, Row[] rows, Func<Row, int> expect, bool steps)
    {
        Dictionary<string, (int Exit, Dictionary<string, string> Steps)> parsed = ParseRows(output);
        var wrong = new List<string>();
        foreach (Row row in rows)
        {
            if (!parsed.TryGetValue(row.Id, out var got))
            {
                wrong.Add($"{row.Id}: no ROW line");
                continue;
            }
            if (got.Exit != expect(row))
                wrong.Add($"{row.Id}: exit {got.Exit}, expected {expect(row)} ({string.Join(" ", got.Steps.Select(s => $"{s.Key}={s.Value}"))})");
            if (steps)
            {
                foreach ((string step, string verdict) in row.Steps)
                {
                    if (!got.Steps.TryGetValue(step, out string? seen) || seen != verdict)
                        wrong.Add($"{row.Id}: step {step} was {seen ?? "absent"}, expected {verdict}");
                }
            }
        }
        Assert.True(wrong.Count == 0, $"{label}:\n{string.Join("\n", wrong)}\n--- output ---\n{output}");
    }

    [Fact]
    public void EveryRowGivesItsExitCodeAndStepsUnderPwsh()
    {
        Row[] rows = Rows();
        Assert.True(rows.Length >= 30, $"{rows.Length} rows");
        using var tree = new TempTree();
        (int exit, string output) = Run("pwsh.exe", Script, WriteRows(tree.Root, "rows.json", rows));
        Assert.True(exit == 0, $"the script exited {exit} in stub mode over rows:\n{output}");
        CheckRows("pwsh", output, rows, r => r.Expect, steps: true);
    }

    [Fact]
    public void EveryRowGivesItsExitCodeAndStepsUnderWindowsPowerShell()
    {
        Row[] rows = Rows();
        using var tree = new TempTree();
        (int exit, string output) = Run(WindowsPowerShell(), Script, WriteRows(tree.Root, "rows.json", rows));
        Assert.True(exit == 0, $"the script exited {exit} under Windows PowerShell 5.1 in stub mode over rows:\n{output}");
        CheckRows("Windows PowerShell 5.1", output, rows, r => r.Expect, steps: true);
    }

    // The table before the fix: a copy of the script without the privilege read and without the global-namespace test on the drive
    // it picks. Its exit codes are the corpus's 'before' column; the rows where they differ from 'expect' are the class's members,
    // which the script before the fix called FAILED.
    [Fact]
    public void TheTableBeforeTheFixFailsTheRowsTheFixClosed()
    {
        string text = File.ReadAllText(Script);
        const string Privilege = "$privileged = Get-Privileged # MUTATE:privileged";
        const string Global = " -and $_.opensGlobal }) # MUTATE:global";
        Assert.Equal(1, Count(text, Privilege));
        Assert.Equal(1, Count(text, Global));
        string before = text.Replace(Privilege, "$privileged = $false").Replace(Global, " }) # was: -and $_.opensGlobal");

        Row[] rows = Rows();
        using var tree = new TempTree();
        string mutant = Path.Combine(tree.Root, "before.ps1");
        File.WriteAllText(mutant, before);
        (int exit, string output) = Run("pwsh.exe", mutant, WriteRows(tree.Root, "rows.json", rows));
        Assert.True(exit == 0, $"the table before the fix exited {exit} in stub mode:\n{output}");
        CheckRows("the table before the fix", output, rows, r => r.Before, steps: false);

        Row[] closed = [.. rows.Where(r => r.Before != r.Expect)];
        Assert.True(closed.Length >= 4, $"only {closed.Length} rows differ between the table before the fix and the one after it");
        Assert.All(closed, r => Assert.Equal(1, r.Before)); // each was called a defect
        Assert.All(closed, r => Assert.NotEqual(1, r.Expect)); // and is not one
        Assert.Contains(closed, r => r.Id == "M01"); // the shell that is administrator
        Assert.Contains(closed, r => r.Id == "M02"); // a letter no elevated copy can scan
        Assert.Contains(closed, r => r.Id == "M03"); // the least-used drive being such a letter
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    // One scenario alone: the process's own exit code is the verdict's (0 held, 1 FAILED, 2 INCONCLUSIVE), not the loop's 0.
    [Theory]
    [InlineData("R01", 0)]
    [InlineData("D01", 1)]
    [InlineData("S06", 1)]
    [InlineData("M01", 2)]
    [InlineData("D07", 2)]
    public void OneScenarioAloneExitsWithItsVerdict(string id, int expected)
    {
        Row row = Rows().Single(r => r.Id == id);
        Assert.Equal(expected, row.Expect);
        using var tree = new TempTree();
        string path = Path.Combine(tree.Root, id + ".json");
        File.WriteAllText(path, row.Scenario.ToJsonString());
        (int exit, string output) = Run("pwsh.exe", Script, path);
        Assert.True(exit == expected, $"{id}: the script exited {exit}, expected {expected}:\n{output}");
        Assert.Contains(expected switch { 0 => "ALL CHECKS HELD", 1 => "FAILED", _ => "INCONCLUSIVE" }, output);
    }

    // What the corpus claims about itself: every verdict is a row, every step is named, and every row is a scenario the script can read.
    [Fact]
    public void TheCorpusCoversEveryVerdictAndEveryStep()
    {
        Row[] rows = Rows();
        Assert.Equal(rows.Length, rows.Select(r => r.Id).Distinct().Count());
        Assert.Contains(rows, r => r.Expect == 0);
        Assert.Contains(rows, r => r.Expect == 1);
        Assert.Contains(rows, r => r.Expect == 2);
        string[] steps = ["privileged", "no-path", "path-window", "folder", "folder-slash", "root-unreadable", "drive-pick", "drive"];
        foreach (string step in steps)
        {
            Assert.Contains(rows, r => r.Steps.TryGetValue(step, out string? v) && v == "held");
            if (step is not "privileged" and not "drive-pick")
                Assert.Contains(rows, r => r.Steps.TryGetValue(step, out string? v) && v == "FAILED" || r.Steps.TryGetValue(step, out v) && v == "INCONCLUSIVE");
        }
        using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tests", "window", "SL-008-RescanOfferEndingsCorpus.json")));
    }
}
