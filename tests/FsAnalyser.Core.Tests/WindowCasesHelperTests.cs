using System.Diagnostics;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// What tests/window/WindowCases.ps1 takes for granted before any case runs (SL-073). Each test reads the script's own
/// parse through pwsh, the host the window cases run under, and loads only the helper it is about, by name, as
/// tests/window/HookCases.ps1 does: running the script itself would run cases.
/// <list type="bullet">
/// <item>-Case All leaves out the cases whose checks are timings on a shared machine (HF-010): M-001 (SL-039) and
/// T-005 (SL-044), whose bars a busy machine can miss with no defect in the code. They run only when named.</item>
/// <item>A case's tree is never made in an NTFS-compressed folder (HF-027): New-CaseFolder refuses one by name, and
/// New-File writes bytes that do not compress, so a file's size on disk is its size.</item>
/// </list>
/// </summary>
public class WindowCasesHelperTests
{
    private const long KiB64 = 64 * 1024;

    [Fact]
    public async Task AllLeavesOutTheTimingCases()
    {
        string output = await Pwsh(
            ParseScript() +
            "$assigned = @($script.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.VariablePath.UserPath -eq 'cases' }); " +
            "if ($assigned.Count -ne 1 -or $assigned[0].Right -isnot [System.Management.Automation.Language.IfStatementAst]) { 'no single $cases = if (...) at the top level'; exit 1 }; " +
            "$all = @($assigned[0].Right.Clauses | Where-Object { $_.Item1.Extent.Text -eq '$Case -eq ''All''' }); " +
            "if ($all.Count -ne 1) { 'no $Case -eq ''All'' branch'; exit 1 }; " +
            "$all[0].Item2.FindAll({ param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { 'case ' + $_.Value }");

        string[] all = [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("case ", StringComparison.Ordinal)).Select(l => l[5..])];
        Assert.True(all.Contains("T-004") && all.Contains("T-006"), $"-Case All was not read, or lost its neighbours of T-005:\n{output}");
        Assert.False(all.Contains("M-001"), $"-Case All runs M-001, a measurement on a shared machine:\n{output}");
        Assert.False(all.Contains("T-005"), $"-Case All runs T-005, whose UI-thread latency bars a busy machine can miss:\n{output}");
    }

    [Fact]
    public async Task NewCaseFolderRefusesACompressedTempByName()
    {
        using var tree = new TempTree();
        string temp = tree.Folder("temp");
        TempTree.Compress(temp); // folders made in it from now on are compressed, as under a compressed drive

        string output = await Pwsh(
            ParseScript() + Load("New-CaseFolder") +
            "try { $made = New-CaseFolder; 'made ' + $made } catch { 'refused ' + $_.Exception.Message }",
            temp);

        Assert.True(output.StartsWith("refused ", StringComparison.Ordinal), $"New-CaseFolder made a case folder in a compressed %TEMP%:\n{output}");
        Assert.Contains("NTFS-compressed", output);
        string cases = Path.Combine(temp, "fsa-window-cases");
        Assert.True(!Directory.Exists(cases) || !Directory.EnumerateFileSystemEntries(cases).Any(),
            $"the refused case folder was left behind in {cases}");
    }

    [Fact]
    public async Task NewFileTakesItsSizeOnDiskInACompressedFolder()
    {
        using var tree = new TempTree();
        string folder = tree.Folder("compressed");
        TempTree.Compress(folder);
        string one = Path.Combine(folder, "one.bin");
        string five = Path.Combine(folder, "five.bin");

        await Pwsh(ParseScript() + Load("New-File") +
            $"New-File '{one.Replace("'", "''")}' {KiB64}; New-File '{five.Replace("'", "''")}' {5 * KiB64}; 'written'");

        foreach ((string path, long length) in new[] { (one, KiB64), (five, 5 * KiB64) })
        {
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Compressed), $"{path} is not compressed, so this proves nothing");
            // NTFS compresses a file's cached data as it is written out, and its size on disk is final only then.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.Flush(flushToDisk: true);
            Assert.Equal(length, new FileInfo(path).Length);
            Assert.Equal(length, TestNative.SizeOnDisk(path));
        }
    }

    // $script: WindowCases.ps1's parse, stopping at the first error.
    private static string ParseScript()
    {
        string script = Path.Combine(FindRepo(), "tests", "window", "WindowCases.ps1");
        return "$errors = $null; " +
            $"$script = [System.Management.Automation.Language.Parser]::ParseFile('{script.Replace("'", "''")}', [ref]$null, [ref]$errors); " +
            "if ($errors.Count -gt 0) { 'WindowCases.ps1 does not parse: ' + $errors[0].Message; exit 1 }; ";
    }

    // One of its top-level functions, defined here as HookCases.ps1 defines it.
    private static string Load(string function) =>
        "$found = @($script.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq '" + function + "' }); " +
        "if ($found.Count -ne 1) { 'WindowCases.ps1 no longer defines " + function + " at its top level'; exit 1 }; " +
        ". ([scriptblock]::Create($found[0].Extent.Text)); ";

    // Runs a command under pwsh and returns what it wrote; with a temp folder, as its %TEMP% and %TMP%.
    private static async Task<string> Pwsh(string command, string? temp = null)
    {
        var start = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            start.ArgumentList.Add(arg);
        if (temp is not null)
        {
            start.Environment["TEMP"] = temp;
            start.Environment["TMP"] = temp;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pwsh.exe.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"pwsh did not finish in 60 s.\n{await stdout}{await stderr}");
        }
        string output = (await stdout).Trim();
        string errors = (await stderr).Trim();
        Assert.True(process.ExitCode == 0 && errors.Length == 0, $"pwsh exited {process.ExitCode}:\n{output}\n{errors}");
        return output;
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FsAnalyser.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"No FsAnalyser.slnx above {AppContext.BaseDirectory}.");
    }
}
