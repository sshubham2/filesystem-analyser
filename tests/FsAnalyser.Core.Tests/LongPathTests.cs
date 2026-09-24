using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class LongPathTests
{
    [Fact]
    public void ReachesPathsWin32WouldAlter()
    {
        using var tree = new TempTree();
        string ext = tree.ExtendedRoot;
        string component = new string('L', 251) + ".dir"; // 255 characters, the NTFS limit
        string deep = $@"{component}\{new string('m', 120)}\{new string('n', 60)}";
        Directory.CreateDirectory($@"{ext}\{deep}");
        File.WriteAllBytes($@"{ext}\{deep}\deep-file.txt", new byte[42]);
        Directory.CreateDirectory($@"{ext}\dot.");
        File.WriteAllBytes($@"{ext}\dot.\file.", new byte[3]);
        File.WriteAllBytes($@"{ext}\space ", new byte[5]);
        File.WriteAllBytes($@"{ext}\NUL", new byte[7]);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Empty(result.Unreadable); // every folder, including "dot." and the deep ones, was listed
        ScanStore store = result.Store;

        string deepFilePath = $@"{tree.Root}\{deep}\deep-file.txt";
        Assert.True(deepFilePath.Length > 400, $"the deep path is {deepFilePath.Length} characters");
        Assert.Equal(42, store.Size(Scans.Find(result, deepFilePath)));
        Assert.Equal(EntryKind.Folder, store.Kind(Scans.Find(result, $@"{tree.Root}\{component}")));

        Assert.Equal(EntryKind.Folder, store.Kind(Scans.Find(result, $@"{tree.Root}\dot.")));
        Assert.Equal(3, store.Size(Scans.Find(result, $@"{tree.Root}\dot.\file.")));
        Assert.Equal(5, store.Size(Scans.Find(result, $@"{tree.Root}\space ")));
        Assert.Equal(7, store.Size(Scans.Find(result, $@"{tree.Root}\NUL")));
        Assert.Equal(new ScanTotals(Files: 4, Folders: 4, Links: 0, Bytes: 57, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);

        // A share root cannot be made in a test; its mapping is checked as a string.
        Assert.Equal((@"\\server\share\folder", @"\\?\UNC\server\share\folder"), LongPath.NormalizeRoot(@"\\server\share\folder"));
        Assert.Equal(@"\\?\UNC\server\share\folder\dot.", LongPath.Child(@"\\?\UNC\server\share\folder", "dot."));
    }

    [Theory]
    [InlineData("C:", @"C:\", @"\\?\C:\")]
    [InlineData("d:", @"d:\", @"\\?\d:\")]
    [InlineData(@"C:\", @"C:\", @"\\?\C:\")]
    [InlineData(@"C:\Users\", @"C:\Users", @"\\?\C:\Users")]
    [InlineData(@"C:/Users/x", @"C:\Users\x", @"\\?\C:\Users\x")]
    [InlineData(@"C:\Users\..\Windows", @"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"\\server\share\folder", @"\\server\share\folder", @"\\?\UNC\server\share\folder")]
    [InlineData(@"\\server\share", @"\\server\share\", @"\\?\UNC\server\share\")]
    [InlineData(@"\\server\share\", @"\\server\share\", @"\\?\UNC\server\share\")]
    [InlineData(@"\\?\C:\x\name.", @"C:\x\name.", @"\\?\C:\x\name.")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x", @"\\?\UNC\server\share\x")]
    public void NormalisesTheRootOnce(string root, string display, string win32)
    {
        Assert.Equal((display, win32), LongPath.NormalizeRoot(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("folder")]
    [InlineData(@"folder\child")]
    [InlineData("C:folder")]
    [InlineData(@"\folder")]
    public void ARelativeOrEmptyRootIsRejected(string root)
    {
        Assert.Throws<ArgumentException>(() => LongPath.NormalizeRoot(root));
    }

    [Theory]
    [InlineData(@"\\?\C:\", "a.", @"\\?\C:\a.")]
    [InlineData(@"\\?\C:\x", "NUL", @"\\?\C:\x\NUL")]
    [InlineData(@"\\?\C:\x", "b ", @"\\?\C:\x\b ")]
    [InlineData(@"\\?\UNC\server\share\", "d", @"\\?\UNC\server\share\d")]
    public void AChildPathIsPlainConcatenation(string parent, string name, string child)
    {
        Assert.Equal(child, LongPath.Child(parent, name));
    }

    [Theory]
    [InlineData(@"\\?\C:\x", @"C:\x")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"C:\x", @"C:\x")]
    public void DisplayIsTheInverseOfExtended(string win32, string display)
    {
        Assert.Equal(display, LongPath.ToDisplay(win32));
        if (win32.StartsWith(@"\\?\", StringComparison.Ordinal))
            Assert.Equal(win32, LongPath.ToExtended(display));
    }
}
