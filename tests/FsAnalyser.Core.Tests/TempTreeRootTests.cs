using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// Where the counting-rule trees are built (SL-096): in %TEMP%\fsa-tests, unless %TEMP% is at or under the Windows folder,
/// as the engine's inherited TEMP=C:\WINDOWS\TEMP is. There a child's write-attributes open moves its parent's last-access
/// time, so the trees go to the user's own %LOCALAPPDATA%\Temp\fsa-tests, and a run whose own profile is under the Windows
/// folder too is refused by name. Fed through <see cref="TempTree.ChooseTestsRoot"/> with every input given, so no
/// branch depends on this process's environment.
/// </summary>
public class TempTreeRootTests
{
    private const string Windows = @"C:\WINDOWS";
    private const string LocalAppData = @"C:\Users\u\AppData\Local";
    private const string Redirected = @"C:\Users\u\AppData\Local\Temp\fsa-tests";

    [Theory]
    [InlineData(@"C:\WINDOWS\TEMP\")] // the engine's, as GetTempPath gives it
    [InlineData(@"c:\windows\temp")]
    [InlineData(@"C:\Windows\Temp\deeper\")]
    [InlineData(@"C:\Windows\")] // the Windows folder itself
    public void ATempAtOrUnderTheWindowsFolderIsReplacedByTheUsersOwn(string temp)
    {
        Assert.Equal(Redirected, TempTree.ChooseTestsRoot(temp, Windows, LocalAppData));
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\", @"C:\Users\u\AppData\Local\Temp\fsa-tests")] // the person's own
    [InlineData(@"D:\Temp", @"D:\Temp\fsa-tests")]
    public void AnyOtherTempIsUsedAsItIs(string temp, string expected)
    {
        Assert.Equal(expected, TempTree.ChooseTestsRoot(temp, Windows, LocalAppData));
    }

    /// <summary>Compared at a separator, as the scanner compares: sharing the Windows folder's name is not being in it.</summary>
    [Theory]
    [InlineData(@"C:\WindowsTemp\", @"C:\WindowsTemp\fsa-tests")]
    [InlineData(@"C:\Windows.old\Temp\", @"C:\Windows.old\Temp\fsa-tests")]
    [InlineData(@"C:\Windows2\Temp", @"C:\Windows2\Temp\fsa-tests")]
    public void ATempThatOnlySharesTheWindowsFoldersNameIsUsedAsItIs(string temp, string expected)
    {
        Assert.Equal(expected, TempTree.ChooseTestsRoot(temp, Windows, LocalAppData));
    }

    /// <summary>A SYSTEM or service-profile run: its own %LOCALAPPDATA% is under the Windows folder too, or it has none.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\config\systemprofile\AppData\Local")]
    [InlineData(@"c:\windows\ServiceProfiles\LocalService\AppData\Local")]
    [InlineData("")]
    public void ARedirectTargetThatIsNotOutsideTheWindowsFolderIsRefusedByName(string localAppData)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => TempTree.ChooseTestsRoot(@"C:\WINDOWS\TEMP\", Windows, localAppData));

        Assert.Contains(@"%TEMP% (C:\WINDOWS\TEMP\) is under the Windows folder (C:\WINDOWS)", refused.Message);
        Assert.Contains("system or service profile", refused.Message);
        Assert.Contains("point TEMP and TMP at a folder outside the Windows folder", refused.Message);
    }

    [Fact]
    public void WithNoWindowsFolderNamedTheTempIsUsedAsItIs()
    {
        Assert.Equal(@"C:\Temp\fsa-tests", TempTree.ChooseTestsRoot(@"C:\Temp\", "", LocalAppData));
    }

    /// <summary>This process's own trees, whatever TEMP it was started with, are outside the Windows folder.</summary>
    [Fact]
    public void ThisRunsTreesAreBuiltOutsideTheWindowsFolder()
    {
        using var tree = new TempTree();
        string windows = LongPath.NormalizeRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Win32;
        Assert.False(Scanner.IsAtOrUnder(LongPath.ToExtended(tree.Root), windows), $"{tree.Root} is under {windows}");
    }
}
