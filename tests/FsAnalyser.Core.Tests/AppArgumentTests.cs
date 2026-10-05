using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-089 review M1, closed as its class: a root ending in a backslash handed to the app's argv undoubled. Quoted by
/// hand, W-001's <c>-Root C:\</c> reached the app as the one argument <c>C:" --report &lt;file&gt;</c>, so no report was
/// ever written. The class is enumerated in tests/window/AppArgumentCorpus.json, and each part of it is run here:
/// <list type="bullet">
/// <item>its roots, through tests/window/WindowCases.ps1's own Invoke-HookedRun and Format-AppArgument (read from the
/// script's parse and loaded by name under pwsh, as WindowCasesHelperTests does), split by Windows' own rules;</item>
/// <item>its plants, against the pattern that finds a path quoted by hand on a line that launches the app;</item>
/// <item>its producers: every script in the repository that names the app's exe or launches it is listed, and quotes by
/// the rule.</item>
/// </list>
/// </summary>
public class AppArgumentTests
{
    // A case-script line, outside a comment, that launches the app with a path quoted by hand: an escaped quote on a line
    // that calls Start-App or Invoke-App.
    private const string HandQuoted = @"(?m)^(?!\s*#).*\b(?:Start-App|Invoke-App)\b.*`""";

    private static readonly string[] NotSource = [".git", ".dotnet", ".vs", "artifacts", "bin", "obj", "node_modules"];

    private sealed record RootRow(string Id, string Producer, string Root, string HandQuoted);

    [Fact]
    public async Task EveryRootOfTheCorpusReachesTheAppsArgvAsWritten()
    {
        using JsonDocument corpus = ReadCorpus();
        using var tree = new TempTree();
        string report = Path.Combine(tree.Root, "report.json");
        RootRow[] rows = [.. corpus.RootElement.GetProperty("roots").EnumerateArray().Select(r => new RootRow(
            r.GetProperty("id").GetString()!, r.GetProperty("producer").GetString()!,
            r.GetProperty("root").GetString()!.Replace("<temp>", tree.Root, StringComparison.Ordinal), r.GetProperty("handQuoted").GetString()!))];
        Assert.NotEmpty(rows);

        // The command line Invoke-HookedRun gives Start-App for each root, with the launch itself stood in for.
        string input = Path.Combine(tree.Root, "roots.json");
        File.WriteAllText(input, JsonSerializer.Serialize(rows.Select(r => new { id = r.Id, root = r.Root, report })));
        string output = await WindowCasesHelperTests.Pwsh(
            WindowCasesHelperTests.ParseScript() + WindowCasesHelperTests.Load("Format-AppArgument") + WindowCasesHelperTests.Load("Invoke-HookedRun") +
            "function Invoke-WithHook($mode, $hookDll, $action, $armed) { [pscustomobject]@{ Result = (& $action); Log = @() } }; " +
            "function Wait-App($process, $timeoutSec) { $process }; " +
            "function Start-App($arguments) { [string]$arguments }; " +
            "$lines = [ordered]@{}; " +
            $"foreach ($row in (Get-Content -LiteralPath '{input.Replace("'", "''")}' -Raw | ConvertFrom-Json)) {{ $lines[$row.id] = [string](Invoke-HookedRun 'rows' $row.root $row.report).Exit }}; " +
            "'json ' + ($lines | ConvertTo-Json -Compress)");
        string? json = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("json ", StringComparison.Ordinal));
        Assert.True(json is not null, "Invoke-HookedRun gave no command lines:\n" + output);
        Dictionary<string, string> lines = JsonSerializer.Deserialize<Dictionary<string, string>>(json[5..])!;

        var wrong = new List<string>();
        foreach (RootRow row in rows)
        {
            if (!lines.TryGetValue(row.Id, out string? line))
            {
                wrong.Add($"{row.Id}: no command line");
                continue;
            }
            string[] argv = TestNative.SplitArguments(line);
            if (!Reaches(argv, row.Root, report) || CommandLine.RootArgument(argv[0]) != row.Root)
                wrong.Add($"{row.Id} ({row.Producer}): {line} reached the app as [{string.Join(" | ", argv)}]");

            // What quoting by hand gave the same root, as the scripts did before the fix.
            string[] byHand = TestNative.SplitArguments($"\"{row.Root}\" --report \"{report}\"");
            string handQuoted = Reaches(byHand, row.Root, report) ? "right" : "wrong";
            if (handQuoted != row.HandQuoted)
                wrong.Add($"{row.Id}: the corpus says hand-quoting is {row.HandQuoted}, it is {handQuoted} ([{string.Join(" | ", byHand)}])");
        }
        Assert.True(wrong.Count == 0, "Roots that do not reach the app as written:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void ThePatternFindsAPathQuotedByHandAndNothingElse()
    {
        using JsonDocument corpus = ReadCorpus();
        string[] wrong = [.. corpus.RootElement.GetProperty("plants").EnumerateArray()
            .Where(p => Regex.IsMatch(p.GetProperty("line").GetString()!, HandQuoted) != (p.GetProperty("expect").GetString() == "fire"))
            .Select(p => $"{p.GetProperty("id").GetString()} should {p.GetProperty("expect").GetString()}: {p.GetProperty("line").GetString()}")];
        Assert.True(wrong.Length == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void EveryScriptThatLaunchesTheAppIsListedAndQuotesByTheRule()
    {
        string repo = FailCriticalErrorsTests.FindRepo();
        using JsonDocument corpus = ReadCorpus();
        Dictionary<string, (string Check, string How)> producers = corpus.RootElement.GetProperty("producers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("file").GetString()!, p => (p.GetProperty("check").GetString()!, p.GetProperty("how").GetString()!));

        string[] unlisted = [.. Scripts(repo)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"FsAnalyser\.App\.exe|\bStart-App\b|\bInvoke-App\b"))
            .Select(f => Path.GetRelativePath(repo, f).Replace('\\', '/'))
            .Where(f => !producers.ContainsKey(f))];
        Assert.True(unlisted.Length == 0, "Scripts that name or launch the app and are not in the corpus's producers: " + string.Join(", ", unlisted));

        var wrong = new List<string>();
        foreach ((string file, (string check, string how)) in producers)
        {
            string path = Path.Combine(repo, file);
            if (!File.Exists(path))
            {
                wrong.Add($"{file}: listed, and not there");
                continue;
            }
            string text = File.ReadAllText(path);
            switch (check)
            {
                case "uses-format":
                    foreach (Match m in Regex.Matches(text, HandQuoted))
                        wrong.Add($"{file}: a path quoted by hand: {m.Value.Trim()}");
                    if (!text.Contains("Format-AppArgument", StringComparison.Ordinal))
                        wrong.Add($"{file}: never calls Format-AppArgument ({how})");
                    break;
                case "doubles":
                    if (!text.Contains(@"-replace '(\\+)$', '$1$1'", StringComparison.Ordinal))
                        wrong.Add($"{file}: no longer doubles a trailing backslash ({how})");
                    break;
                case "not-a-launcher":
                    if (Regex.IsMatch(text, @"\bStart-App\b|\bInvoke-App\b"))
                        wrong.Add($"{file}: listed as no launcher, and calls Start-App or Invoke-App");
                    break;
                default:
                    wrong.Add($"{file}: no check named '{check}'");
                    break;
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    private static bool Reaches(string[] argv, string root, string report) =>
        argv.Length == 3 && argv[0] == root && argv[1] == "--report" && argv[2] == report;

    private static JsonDocument ReadCorpus() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "window", "AppArgumentCorpus.json")));

    // Every PowerShell script in the repository's own source, never in a build output or the repo-local SDK.
    private static IEnumerable<string> Scripts(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*.ps1"))
            yield return file;
        foreach (string child in Directory.EnumerateDirectories(folder))
        {
            if (NotSource.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (string file in Scripts(child))
                yield return file;
        }
    }
}
