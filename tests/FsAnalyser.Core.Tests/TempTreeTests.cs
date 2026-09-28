using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The counting-rule fixture refuses a tree it cannot trust (HF-027). A folder made under an NTFS-compressed one is
/// compressed too, and there <see cref="TempTree.TextFile"/> takes a fraction of its length on disk with no
/// <see cref="TempTree.Compress"/>, so every on-disk and probe count would shift with no cause named.
/// </summary>
public class TempTreeTests
{
    // As a person meets it (SL-073 review, R2-m2): %TEMP% compressed, so the fsa-tests folder CreateRoot makes on the way
    // is compressed too, and stays so once the refused root is removed. The refusal must name that folder, and its remedy,
    // run as written, must let the next tree be made, although %TEMP% above it is still compressed.
    [Fact]
    public void ATreeRootUnderACompressedFolderIsRefusedByName()
    {
        using var tree = new TempTree();
        string temp = tree.Folder("temp");
        TempTree.Compress(temp); // folders made in it from now on are compressed, as under a compressed %TEMP%
        string parent = Path.Combine(temp, "fsa-tests");
        string root = Path.Combine(parent, "root");

        var refused = Assert.Throws<InvalidOperationException>(() => TempTree.CreateRoot(root));

        Assert.Contains($"{parent} is NTFS-compressed", refused.Message);
        Assert.False(Directory.Exists(root), $"the refused root {root} was left behind");
        Match remedy = Regex.Match(refused.Message, "compact /u /s:\"[^\"]+\"");
        Assert.True(remedy.Success, $"the refusal gives no 'compact /u /s:\"folder\"' to run: {refused.Message}");
        (int exit, string output) = Cmd(remedy.Value);
        Assert.True(exit == 0, $"{remedy.Value} exited {exit}: {output}");

        TempTree.CreateRoot(root);

        Assert.False(File.GetAttributes(root).HasFlag(FileAttributes.Compressed), $"{root} is still compressed after {remedy.Value}");
    }

    [Fact]
    public void ATreeRootUnderAPlainFolderIsMade()
    {
        using var tree = new TempTree();
        string root = Path.Combine(tree.Folder("plain"), "root");

        TempTree.CreateRoot(root);

        Assert.True(Directory.Exists(root));
        Assert.False(File.GetAttributes(root).HasFlag(FileAttributes.Compressed));
    }

    // One line through cmd.exe, as a person would paste it.
    private static (int Exit, string Output) Cmd(string line)
    {
        var start = new ProcessStartInfo("cmd.exe", "/c " + line)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start cmd.exe.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }
}
