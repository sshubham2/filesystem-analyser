namespace FsAnalyser.Core.Tests;

/// <summary>
/// The counting-rule fixture refuses a tree it cannot trust (HF-027). A folder made under an NTFS-compressed one is
/// compressed too, and there <see cref="TempTree.TextFile"/> takes a fraction of its length on disk with no
/// <see cref="TempTree.Compress"/>, so every on-disk and probe count would shift with no cause named.
/// </summary>
public class TempTreeTests
{
    [Fact]
    public void ATreeRootUnderACompressedFolderIsRefusedByName()
    {
        using var tree = new TempTree();
        string compressed = tree.Folder("compressed");
        TempTree.Compress(compressed); // folders made in it from now on are compressed, as under a compressed %TEMP%
        string root = Path.Combine(compressed, "root");

        var refused = Assert.Throws<InvalidOperationException>(() => TempTree.CreateRoot(root));

        Assert.Contains("NTFS-compressed", refused.Message);
        Assert.False(Directory.Exists(root), $"the refused root {root} was left behind");
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
}
