using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// Nothing says the stall rig's lock spans the machine (SL-059). RigLock's mutex is Local\FsAnalyser-SmbRig, one
/// per sign-in session, and after SL-038 corrected SmbRig.cs three comments went on saying "on this machine" for a
/// whole slice. This reads every line of tests/, docs/ and eng/ for a lock, a serialisation, a rig run or RigLock
/// named on the same line as a machine scope. The sweep's other hits are about something else and stay as written,
/// such as eng/dotnet.ps1's machine-wide SDK and WindowCases.ps1's measurements "on this machine": the pattern is
/// held to leave them alone, so a pattern widened to catch them fails here before a correct sentence is rewritten.
/// .taad/ is not read: it is the record of what was claimed, and when.
/// </summary>
public class RigLockScopeWordingTests
{
    private static readonly Regex Claim = new(
        @"\b(?:lock|serialised|serialized|RigLock|rig runs?)\b.*\b(?:on this machine|machine-wide)\b" +
        @"|\b(?:on this machine|machine-wide)\b.*\b(?:lock|RigLock|rig runs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] TextFiles = [".cs", ".ps1", ".md", ".csproj", ".py", ".json"];

    [Fact]
    public void NothingSaysTheRigLockSpansTheMachine()
    {
        // The three comments as SL-038 left them, so the pattern cannot pass by matching nothing.
        Assert.Matches(Claim, "// It waits up to --wait-sec (default 150) for another rig run on this machine to finish (LoopbackShare,");
        Assert.Matches(Claim, "/// Rig/SmbRig.cs, shared with tests/window/StallRig; it waits for any other rig run on this machine.");
        Assert.Matches(Claim, "# name, so rig runs are serialised by one lock on this machine (RigLock): a second run either fails at once,");

        // Hits of the sweep's grep that are about something else, true as written, and left alone.
        Assert.DoesNotMatch(Claim, "# A machine-wide 10.0.x SDK is used when `dotnet --list-sdks` shows one. Otherwise Microsoft's");
        Assert.DoesNotMatch(Claim, "# Repo-local .NET SDK installed by eng/dotnet.ps1 when no machine-wide 10.0 SDK exists.");
        Assert.DoesNotMatch(Claim, "# scan's came to 190 on this machine and a finished one's to about 320 with the volume line. Every list must");
        Assert.DoesNotMatch(Claim, "# Files here are whole multiples of 4 KiB, at least 4 KiB, or empty: on this machine's NTFS (4 KiB clusters) the");

        string repo = FindRepo();
        // This file quotes the claim above, to hold the pattern to it.
        string self = $"tests/FsAnalyser.Core.Tests/{nameof(RigLockScopeWordingTests)}.cs";
        var hits = new List<string>();
        int read = 0;
        foreach (string root in new[] { "tests", "docs", "eng" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(repo, root), "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (rel == self || rel.Contains("/bin/") || rel.Contains("/obj/") ||
                    !TextFiles.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                read++;
                int n = 0;
                foreach (string line in File.ReadLines(file))
                {
                    n++;
                    if (Claim.IsMatch(line))
                        hits.Add($"{rel}:{n}: {line.Trim()}");
                }
            }
        }
        Assert.True(read > 0, $"No text files read under tests/, docs/ or eng/ of {repo}.");
        Assert.True(hits.Count == 0, "RigLock serialises runs per sign-in session, not per machine:\n" + string.Join("\n", hits));
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
