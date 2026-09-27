using System.Diagnostics;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// tests/window/WindowCases.ps1 still parses under Windows PowerShell 5.1 and under pwsh 7 (SL-059). The window
/// cases run under pwsh, but tests/window/HookCases.ps1 loads WindowCases.ps1 through the parser of whichever
/// PowerShell runs it, and 5.1 reads a file with no byte-order mark in the ANSI code page: a dropped BOM or a
/// non-ASCII dash or quote in an edited comment can break 5.1 where 7 still reads the file. Each PowerShell parses
/// the file itself, reading it as it reads a script it runs, and names its own edition, so the test cannot pass on
/// the wrong host.
/// </summary>
public class WindowScriptParseTests
{
    [Theory]
    [InlineData("powershell.exe", "Desktop")]
    [InlineData("pwsh.exe", "Core")]
    public async Task WindowCasesParses(string host, string edition)
    {
        string script = Path.Combine(FindRepo(), "tests", "window", "WindowCases.ps1");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$errors = $null; " +
                     $"[void][System.Management.Automation.Language.Parser]::ParseFile('{script.Replace("'", "''")}', [ref]$null, [ref]$errors); " +
                     "'edition ' + $PSVersionTable.PSEdition + ' ' + $PSVersionTable.PSVersion; " +
                     "foreach ($e in $errors) { '{0}:{1}: {2}' -f $e.Extent.StartLineNumber, $e.Extent.StartColumnNumber, $e.Message }; " +
                     "if ($errors.Count -gt 0) { exit 1 }" })
            start.ArgumentList.Add(arg);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {host}.");
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
            Assert.Fail($"{host} did not parse tests/window/WindowCases.ps1 in 60 s.\n{await stdout}{await stderr}");
        }
        string output = (await stdout).Trim();
        string errors = (await stderr).Trim();
        Assert.True(output.StartsWith($"edition {edition} ", StringComparison.Ordinal),
            $"{host} is not the {edition} edition, or did not say: {output} {errors}");
        Assert.True(process.ExitCode == 0,
            $"{host} could not parse tests/window/WindowCases.ps1 (exit {process.ExitCode}):\n{output}\n{errors}");
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
