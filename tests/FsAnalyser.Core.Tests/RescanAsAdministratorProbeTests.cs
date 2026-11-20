using System.Security.Principal;
using FsAnalyser.App;
using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-008: what an elevated copy would see of the scanned letter. The elevated copy runs in a logon session of its own, which
/// holds only the global DOS-device namespace's letters; a letter made in this session (a SUBST letter, a mapped drive) is
/// not one of them. The probe reads the drive's type first and opens the namespace only for a fixed or removable drive, so no
/// network letter is ever opened from it; the tests give it the types and the namespace's answers, and use no network drive
/// (a mapped letter keeps its type while its server is down, and a test that opened it would pass or fail with that host).
/// </summary>
public class RescanAsAdministratorProbeTests
{
    // A SUBST letter made by SYSTEM lands in the global namespace (Microsoft's DefineDosDevice documentation), so under that
    // account the letter IS global; under every other it is this logon session's own. The test states the namespace rule, and so
    // holds in both (review m1).
    private static readonly bool RunsAsSystem = WindowsIdentity.GetCurrent().IsSystem;

    private static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory)!;

    // O-008, the global half: the system drive's letter is in the global namespace.
    [Fact]
    public void TheSystemDrivesLetterIsInTheGlobalNamespace()
    {
        Assert.True(GlobalDosDevices.HoldsDriveLetter(SystemDrive[0]));
        Assert.True(GlobalDosDevices.HoldsDriveLetter(char.ToLowerInvariant(SystemDrive[0])));
    }

    // O-008, the session-local half: a letter SubstDrive made for this session.
    [Fact]
    public void ASubstLetterIsGlobalOnlyWhenSubstMadeItInTheGlobalNamespace()
    {
        using var tree = new TempTree();
        using var subst = new SubstDrive(tree.Root);
        Assert.Equal(RunsAsSystem, GlobalDosDevices.HoldsDriveLetter(subst.Root[0]));
        Assert.Equal(RunsAsSystem, GlobalDosDevices.HoldsDriveLetter(char.ToLowerInvariant(subst.Root[0])));
    }

    [Fact]
    public void TheProbeGivesTheSystemDriveItsTypeAndASubstLetterNone()
    {
        using var tree = new TempTree();
        using var subst = new SubstDrive(tree.Root);

        DriveType system = new DriveInfo(SystemDrive).DriveType;
        Assert.True(RescanAsAdministrator.IsRescannableType(system), $"the system drive reads as {system}");
        Assert.Equal(system, RescanAsAdministrator.DriveTypeAnElevatedCopySees(SystemDrive));
        Assert.Equal(system, RescanAsAdministrator.DriveTypeAnElevatedCopySees(@"\\?\" + SystemDrive));
        Assert.Equal(system, RescanAsAdministrator.DriveTypeAnElevatedCopySees(SystemDrive.ToLowerInvariant()));

        // The letter reads as a fixed drive (measured), and only the namespace tells it from the system drive's.
        Assert.Equal(DriveType.Fixed, new DriveInfo(subst.Root).DriveType);
        DriveType? seen = RescanAsAdministrator.DriveTypeAnElevatedCopySees(subst.Root);
        Assert.Equal(RunsAsSystem ? DriveType.Fixed : (DriveType?)null, seen);
        Assert.Equal(RunsAsSystem, RescanAsAdministrator.Offers(FsAnalyser.Core.Scanning.ScanStatus.Completed, subst.Root, 1, seen, false));
    }

    // O-005's probe half: a root that is not a drive gets no answer and nothing is read for it.
    [Theory]
    [InlineData(@"C:\dir")]
    [InlineData(@"C:\dir\")]
    [InlineData(@"C:")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\UNC\server\share")]
    [InlineData("")]
    public void ARootThatIsNotADriveGetsNullWithNothingRead(string root)
    {
        int reads = 0;
        DriveType? seen = RescanAsAdministrator.DriveTypeAnElevatedCopySees(root, _ => { reads++; return DriveType.Fixed; }, _ => { reads++; return true; });
        Assert.Null(seen);
        Assert.Equal(0, reads);
        Assert.Null(RescanAsAdministrator.DriveTypeAnElevatedCopySees(root));
    }

    // O-007: a type that is neither fixed nor removable is handed back as it is, and the namespace is never opened for it.
    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void ATypeThatIsNotLocalIsReturnedAsItIsAndTheNamespaceIsNeverAsked(DriveType type)
    {
        foreach (string root in new[] { @"Z:\", @"z:\", @"\\?\Z:\" })
        {
            int asked = 0;
            DriveType? seen = RescanAsAdministrator.DriveTypeAnElevatedCopySees(root, _ => type, _ => { asked++; return true; });
            Assert.Equal(type, seen);
            Assert.Equal(0, asked);
            Assert.False(RescanAsAdministrator.Offers(FsAnalyser.Core.Scanning.ScanStatus.Completed, root, 1, seen, false), $"{type} {root}");
        }
    }

    // m5: the namespace is asked exactly for the types Offers accepts, and a letter the namespace does not hold gives null for
    // every one of them, so Offers can never be widened to a type the probe lets through unchecked.
    [Fact]
    public void ForEveryRescannableTypeALetterTheNamespaceDoesNotHoldGivesNull()
    {
        int rescannable = 0;
        foreach (DriveType type in Enum.GetValues<DriveType>())
        {
            int asked = 0;
            DriveType? seen = RescanAsAdministrator.DriveTypeAnElevatedCopySees(@"Q:\", _ => type, _ => { asked++; return false; });
            if (RescanAsAdministrator.IsRescannableType(type))
            {
                rescannable++;
                Assert.Null(seen);
                Assert.Equal(1, asked);
                DriveType? held = RescanAsAdministrator.DriveTypeAnElevatedCopySees(@"Q:\", _ => type, _ => true);
                Assert.Equal(type, held);
            }
            else
            {
                Assert.Equal(type, seen);
                Assert.Equal(0, asked);
            }
        }
        Assert.Equal(2, rescannable);
    }

    // O-009: the probe runs inside the scan's task; an exception out of it would turn a finished scan into 'Scan failed'.
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))] // an ArgumentException
    [InlineData(typeof(DirectoryNotFoundException))] // an IOException
    [InlineData(typeof(DriveNotFoundException))] // an IOException
    public void AReadThatThrowsAnIoAccessOrArgumentExceptionGivesNull(Type exception)
    {
        int asked = 0;
        DriveType? seen = RescanAsAdministrator.DriveTypeAnElevatedCopySees(@"C:\", _ => throw (Exception)Activator.CreateInstance(exception)!, _ => { asked++; return true; });
        Assert.Null(seen);
        Assert.Equal(0, asked);

        // The namespace read failing the same way is no different.
        DriveType? again = RescanAsAdministrator.DriveTypeAnElevatedCopySees(@"C:\", _ => DriveType.Fixed, _ => throw (Exception)Activator.CreateInstance(exception)!);
        Assert.Null(again);
    }

    // The letter the namespace is asked about is the root's own, as written: HoldsDriveLetter upper-cases it.
    [Theory]
    [InlineData(@"C:\", 'C')]
    [InlineData(@"c:\", 'c')]
    [InlineData(@"\\?\d:\", 'd')]
    public void TheNamespaceIsAskedAboutTheRootsOwnLetter(string root, char letter)
    {
        char? asked = null;
        Assert.Equal(DriveType.Removable, RescanAsAdministrator.DriveTypeAnElevatedCopySees(root, _ => DriveType.Removable, c => { asked = c; return true; }));
        Assert.Equal(letter, asked);
    }

    [Theory]
    [InlineData('1')]
    [InlineData(':')]
    [InlineData('\\')]
    [InlineData(' ')]
    [InlineData('\0')]
    [InlineData('é')]
    [InlineData('\uFF21')] // a full-width A, which upper-casing does not turn into an ASCII letter
    public void AnythingButAnAsciiLetterIsNotAHeldLetter(char letter) =>
        Assert.False(GlobalDosDevices.HoldsDriveLetter(letter));
}
