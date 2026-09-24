using System.Diagnostics;
using System.Reflection;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The architecture rules have two copies: <see cref="ArchitectureRulesTests"/> and the twinned entries of
/// .taad/fitness.yaml, which the engine's Fitness check runs. eng/rules-parity.ps1 compares their patterns;
/// this launches it and reads nothing itself, so no third reader of the rules lives in .NET (SL-031). The
/// Fact count passed to the script comes from reflection, so a Fact the script's reader cannot see fails
/// here instead of going uncompared. The class name leaves out "ArchitectureRulesTests" so that the rules
/// harness's filter on that name does not run this.
/// </summary>
public class RuleCopiesAgreeTests
{
    [Fact]
    public async Task TheTwoCopiesOfTheRulePatternsAgree()
    {
        string repo = FindRepo();
        int facts = typeof(ArchitectureRulesTests)
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Count(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(repo, "eng", "rules-parity.ps1"),
                     "-Root", repo, "-ExpectFacts", facts.ToString() })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
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
            Assert.Fail($"eng/rules-parity.ps1 did not finish in 60 s.\n{await stdout}{await stderr}");
        }

        Assert.True(process.ExitCode == 0,
            $"eng/rules-parity.ps1 exited {process.ExitCode} (1 DRIFT, 2 CANNOT-READ) for {facts} Facts:\n{await stdout}{await stderr}");
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
