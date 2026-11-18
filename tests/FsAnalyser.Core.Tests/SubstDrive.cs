using System.Diagnostics;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A subst drive letter for this logon session, mapped to a folder of a tree and removed again: the first letter from Z: down
/// that no drive uses. Shared by every test class that needs one (ContainerLayerStoreTests, VanishedFolderTests): test classes
/// run in parallel in one process, so choosing a letter and mapping it is one step under a lock, and two classes never see the
/// same letter free and then fail the second subst.exe (SL-005, review M1).
/// </summary>
internal sealed class SubstDrive : IDisposable
{
    private static readonly object s_pick = new();
    private readonly string _letter;
    private bool _removed;

    public SubstDrive(string target)
    {
        lock (s_pick)
        {
            HashSet<char> used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            char free = Enumerable.Range('M', 'Z' - 'M' + 1).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
            Assert.True(free != default, "no drive letter from M: to Z: is free for subst");
            _letter = free + ":";
            RunTool("subst.exe", _letter, target);
        }
        Root = _letter + @"\";
    }

    /// <summary>The letter's root, as <c>Z:\</c>.</summary>
    public string Root { get; }

    /// <summary>
    /// Removes the mapping now, as pulling a drive or <c>net use /d</c> would, so that a path through the letter stops
    /// resolving while a scan of it runs. Dispose after it does nothing more.
    /// </summary>
    public void Remove()
    {
        if (_removed)
            return;
        _removed = true;
        RunTool("subst.exe", _letter, "/d");
    }

    public void Dispose()
    {
        try
        {
            Remove();
        }
        catch (Exception)
        {
            // Best effort: a mapping left behind is gone at the next sign-in.
        }
    }

    private static void RunTool(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), $"{file} did not exit");
        Assert.True(process.ExitCode == 0, $"{file} {string.Join(' ', args)} exited {process.ExitCode}: {output} {error}");
    }
}
