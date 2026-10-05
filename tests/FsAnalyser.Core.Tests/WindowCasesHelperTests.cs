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

    // As a person meets it (SL-073 review, R2-m2): the fsa-window-cases folder New-CaseFolder makes on the way is
    // compressed too, and stays so once the refused case folder is removed. The refusal must name that folder, and its
    // remedy, run through cmd as written, must let the next case folder be made, although %TEMP% is still compressed.
    [Fact]
    public async Task NewCaseFolderRefusesACompressedTempByName()
    {
        using var tree = new TempTree();
        string temp = tree.Folder("temp");
        TempTree.Compress(temp); // folders made in it from now on are compressed, as under a compressed drive
        string cases = Path.Combine(temp, "fsa-window-cases");

        string output = await Pwsh(
            ParseScript() + Load("New-CaseFolder") +
            "try { $made = New-CaseFolder; 'made first ' + $made } catch { $message = $_.Exception.Message; 'refused ' + $message }; " +
            "if ($message -match 'compact /u /s:\"[^\"]+\"') { " +
            "$start = [System.Diagnostics.ProcessStartInfo]::new('cmd.exe', '/c ' + $Matches[0]); $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; " +
            "$cmd = [System.Diagnostics.Process]::Start($start); [void]$cmd.StandardOutput.ReadToEnd(); $cmd.WaitForExit(); " +
            "'remedy exit ' + $cmd.ExitCode + ': ' + $Matches[0] }; " +
            "try { $made = New-CaseFolder; 'made ' + [System.IO.File]::GetAttributes($made) + ' ' + $made } catch { 'refused again ' + $_.Exception.Message }",
            temp);

        string[] lines = [.. output.Split('\n').Select(l => l.Trim())];
        Assert.True(lines.Length == 3 && lines[0].StartsWith("refused ", StringComparison.Ordinal),
            $"New-CaseFolder did not refuse a case folder in a compressed %TEMP%, or gave no remedy to run:\n{output}");
        Assert.Contains($"{cases} is NTFS-compressed", lines[0]);
        string refusedFolder = lines[0]["refused ".Length..].Split(' ')[0];
        Assert.False(Directory.Exists(refusedFolder), $"the refused case folder {refusedFolder} was left behind");
        Assert.True(lines[1].StartsWith("remedy exit 0: ", StringComparison.Ordinal), $"the refusal's remedy failed:\n{output}");
        Assert.True(lines[2].StartsWith("made Directory ", StringComparison.Ordinal),
            $"after the refusal's remedy, the next case folder was refused or came out compressed:\n{output}");
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

    // $script: WindowCases.ps1's parse, stopping at the first error. W001OracleTests loads its functions the same way.
    internal static string ParseScript()
    {
        string script = Path.Combine(FindRepo(), "tests", "window", "WindowCases.ps1");
        return "$errors = $null; " +
            $"$script = [System.Management.Automation.Language.Parser]::ParseFile('{script.Replace("'", "''")}', [ref]$null, [ref]$errors); " +
            "if ($errors.Count -gt 0) { 'WindowCases.ps1 does not parse: ' + $errors[0].Message; exit 1 }; ";
    }

    // One of its top-level functions, defined here as HookCases.ps1 defines it.
    internal static string Load(string function) =>
        "$found = @($script.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq '" + function + "' }); " +
        "if ($found.Count -ne 1) { 'WindowCases.ps1 no longer defines " + function + " at its top level'; exit 1 }; " +
        ". ([scriptblock]::Create($found[0].Extent.Text)); ";

    // Runs a command under pwsh and returns what it wrote; with a temp folder, as its %TEMP% and %TMP%.
    internal static async Task<string> Pwsh(string command, string? temp = null)
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
