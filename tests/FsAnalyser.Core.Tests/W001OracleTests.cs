using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// W-001's checker (tests/window/WindowCases.ps1, SL-089 criterion 3), run as the script defines it: its functions are
/// read from the script's own parse and loaded by name under pwsh, as WindowCasesHelperTests loads its helpers, so these
/// tests reach the code W-001 runs and never a copy of it.
/// <list type="bullet">
/// <item>The oracle, Get-OracleRow, against its corpus, tests/window/W001OracleCorpus.json: every shape a panel row can
/// have by the time the check looks, its folder planted for real, part of it before the moment taken as the scan's start
/// and part after (review m3: a cause that ended between the scan and the check is no leak; criterion 3's cases C-010 and
/// C-011).</item>
/// <item>The checker's own controls, Get-CheckerControls, on this machine (review M9).</item>
/// <item>The verdict, Get-W001Verdict: no leak with a control that did not behave is inconclusive, never held.</item>
/// </list>
/// </summary>
public class W001OracleTests
{
    private const uint FileListDirectory = 0x1;

    private sealed record CorpusRow(string Id, string Producer, string Kind, int Code, string Plant, string Expect);

    [Fact]
    public async Task TheOracleGivesEveryRowOfTheCorpusItsClass()
    {
        string corpusFile = Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "window", "W001OracleCorpus.json");
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(corpusFile));
        CorpusRow[] rows = [.. corpus.RootElement.GetProperty("rows").EnumerateArray().Select(r => new CorpusRow(
            r.GetProperty("id").GetString()!, r.GetProperty("producer").GetString()!, r.GetProperty("kind").GetString()!,
            r.GetProperty("code").GetInt32(), r.GetProperty("plant").GetString()!, r.GetProperty("expect").GetString()!))];
        Assert.NotEmpty(rows);

        using var tree = new TempTree();
        string target = tree.Folder("junction-target");
        var paths = new Dictionary<string, string>();
        var resets = new List<string>();
        var recreate = new List<string>();
        var delete = new List<string>();
        foreach (CorpusRow row in rows)
        {
            string path = tree.PathOf(row.Id);
            switch (row.Plant)
            {
                case "denied":
                    tree.Folder(row.Id);
                    tree.Deny(path, FileListDirectory);
                    break;
                case "listable":
                    tree.Folder(row.Id);
                    break;
                case "acl-reset":
                    tree.Folder(row.Id);
                    SetListDeny(path, add: true);
                    resets.Add(path);
                    break;
                case "recreated":
                    tree.Folder(row.Id);
                    recreate.Add(path);
                    break;
                case "gone":
                    tree.Folder(row.Id);
                    delete.Add(path);
                    break;
                case "junction":
                    tree.Junction(row.Id, target);
                    break;
                default:
                    Assert.Fail($"{row.Id}: no plant named '{row.Plant}'");
                    break;
            }
            paths[row.Id] = path;
        }
        Assert.Equal(5, TestNative.FolderOpenError(paths[rows.First(r => r.Plant == "denied").Id], FileListDirectory));

        // Every folder above was made before this moment, which the oracle is told is when the scan started; everything
        // below changes after it.
        Thread.Sleep(50);
        DateTime scanStart = DateTime.UtcNow;
        Thread.Sleep(50);
        foreach (string path in resets)
            SetListDeny(path, add: false);
        foreach (string path in recreate)
        {
            Directory.Delete(path);
            Directory.CreateDirectory(path);
        }
        foreach (string path in delete)
            Directory.Delete(path);

        string input = Path.Combine(tree.Root, "rows.json");
        File.WriteAllText(input, JsonSerializer.Serialize(rows.Select(r => new { id = r.Id, path = paths[r.Id], kind = r.Kind, code = r.Code })));
        string output = await WindowCasesHelperTests.Pwsh(
            WindowCasesHelperTests.ParseScript() + Load("Initialize-SeamWin32") + Load("Get-OracleRow") +
            "Initialize-SeamWin32; " +
            $"$start = [DateTime]::Parse('{scanStart:O}', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime(); " +
            $"foreach ($row in (Get-Content -LiteralPath '{input.Replace("'", "''")}' -Raw | ConvertFrom-Json)) {{ 'class ' + $row.id + ' ' + (Get-OracleRow $row $start) }}");

        Dictionary<string, string> classes = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("class ", StringComparison.Ordinal))
            .Select(l => l.Split(' ')).ToDictionary(p => p[1], p => p[2]);
        string[] wrong = [.. rows.Where(r => !classes.TryGetValue(r.Id, out string? got) || got != r.Expect)
            .Select(r => $"{r.Id} ({r.Producer}): expected {r.Expect}, got {(classes.TryGetValue(r.Id, out string? got) ? got : "nothing")}")];
        Assert.True(wrong.Length == 0, "The oracle misread rows of its corpus:\n" + string.Join("\n", wrong) + "\n" + output);
    }

    [Fact]
    public async Task TheCheckersControlsBehaveOnThisMachine()
    {
        using var tree = new TempTree();
        string output = await WindowCasesHelperTests.Pwsh(
            WindowCasesHelperTests.ParseScript() + Load("Initialize-SeamWin32") + Load("Set-DenyRule") + Load("Get-CheckerControls") +
            "Initialize-SeamWin32; " +
            $"$controls = Get-CheckerControls '{tree.Root.Replace("'", "''")}'; " +
            "foreach ($folder in $controls.Folders) { Set-DenyRule $folder 'ListDirectory' $false; Set-DenyRule $folder 'WriteAttributes' $false }; " +
            "'controls ' + $controls.Openable + ' ' + $controls.Denied + ' ' + $controls.RefusedThenListed");

        Assert.Contains("controls True True True", output);
    }

    /// <summary>
    /// Review M9: a checker that cannot open anything finds no leak in any run, so W-001 holds only beside controls that
    /// behaved. Each control that did not makes a run with no leak inconclusive; a leak or a failed check fails it.
    /// </summary>
    [Theory]
    [InlineData("$true", "$true", "$true", 0, 0, "held")]
    [InlineData("$false", "$true", "$true", 0, 0, "inconclusive")]
    [InlineData("$true", "$false", "$true", 0, 0, "inconclusive")]
    [InlineData("$true", "$true", "$false", 0, 0, "inconclusive")]
    [InlineData("$true", "$true", "$true", 1, 0, "failed")]
    [InlineData("$false", "$false", "$false", 0, 2, "failed")]
    public async Task AW001RunHoldsOnlyBesideControlsThatBehaved(string openable, string denied, string refusedThenListed, int leaks, int failures, string verdict)
    {
        string output = await WindowCasesHelperTests.Pwsh(
            WindowCasesHelperTests.ParseScript() + Load("Get-W001Verdict") +
            $"$controls = [pscustomobject]@{{ Openable = {openable}; Denied = {denied}; RefusedThenListed = {refusedThenListed} }}; " +
            $"'verdict ' + (Get-W001Verdict $controls {leaks} {failures})");

        Assert.Contains($"verdict {verdict}", output);
    }

    private static string Load(string function) => WindowCasesHelperTests.Load(function);

    // A deny of listing for this user, added or taken away, through the rule itself so nothing restores it later.
    private static void SetListDeny(string folder, bool add)
    {
        var info = new DirectoryInfo(folder);
        DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        if (add)
            security.AddAccessRule(rule);
        else
            security.RemoveAccessRule(rule);
        info.SetAccessControl(security);
    }
}
