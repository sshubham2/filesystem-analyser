using System.Diagnostics;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// tests/window/WindowCases.ps1 is read the same by Windows PowerShell 5.1 and by pwsh 7 (SL-059). The window cases
/// run under pwsh, but tests/window/HookCases.ps1 loads WindowCases.ps1 through the parser of whichever PowerShell
/// runs it, and 5.1 reads a file with no byte-order mark in the ANSI code page, where 7 reads UTF-8. The parse check
/// catches only non-ASCII that 5.1 decodes into a quote character: a curly quote or dash inside a double-quoted
/// string gave 5.1 two parse errors (SL-059 review), but an e with an acute accent in the same place gave none, and
/// a curly quote or dash in a comment gave none either. WindowCasesIsAscii guards every byte: the file stays ASCII,
/// with no BOM, as SL-059's design asked of every edit to it ("write ASCII only"), and then both editions read the
/// same text.
/// Each PowerShell parses the file itself, reading it as it reads a script it runs, and names its own edition, so
/// the parse check cannot pass on the wrong host.
/// </summary>
public class WindowScriptParseTests
{
    [Fact]
    public void WindowCasesIsAscii()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(FindRepo(), "tests", "window", "WindowCases.ps1"));
        Assert.True(bytes.Length > 0, "tests/window/WindowCases.ps1 is empty.");
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "tests/window/WindowCases.ps1 starts with a UTF-8 byte-order mark; it is ASCII with none.");
        int at = Array.FindIndex(bytes, b => b >= 0x80);
        if (at >= 0)
        {
            int line = 1 + bytes.Take(at).Count(b => b == (byte)'\n');
            Assert.Fail($"tests/window/WindowCases.ps1 has byte 0x{bytes[at]:X2} at offset {at} (line {line}); " +
                "5.1 reads it in the ANSI code page and pwsh as UTF-8, so the file stays ASCII.");
        }
    }

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
