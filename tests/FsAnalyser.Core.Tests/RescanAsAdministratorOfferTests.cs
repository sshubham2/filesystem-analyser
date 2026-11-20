using FsAnalyser.App;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-008: whether an ending offers 'Rescan as administrator' (RescanAsAdministrator.Offers), branch by branch, with every
/// answer Windows would give handed in. The offer is made only for a scan that finished, of a whole drive, that left at least one
/// folder unread, in a copy that is not elevated, on a letter an elevated copy sees as a fixed or removable drive; each test
/// feeds one of those conditions in as the thing that varies, and holds the others at the shape that offers.
/// </summary>
public class RescanAsAdministratorOfferTests
{
    private const string Drive = @"C:\";

    private static bool Offers(ScanStatus status = ScanStatus.Completed, string? root = Drive, int unreadable = 1,
        DriveType? type = DriveType.Fixed, bool elevated = false) =>
        RescanAsAdministrator.Offers(status, root, unreadable, type, elevated);

    // O-003: the one shape that shows the button.
    [Theory]
    [InlineData(@"C:\", DriveType.Fixed)]
    [InlineData(@"c:\", DriveType.Fixed)]
    [InlineData(@"Z:\", DriveType.Fixed)]
    [InlineData(@"z:\", DriveType.Fixed)]
    [InlineData(@"E:\", DriveType.Removable)]
    [InlineData(@"e:\", DriveType.Removable)]
    [InlineData(@"\\?\C:\", DriveType.Fixed)] // the display form of an extended drive root is C:\
    [InlineData(@"\\?\c:\", DriveType.Removable)]
    public void AFinishedScanOfAFixedOrRemovableDriveThatLeftFoldersUnreadOffersIt(string root, DriveType type)
    {
        Assert.True(Offers(root: root, type: type));
        Assert.True(Offers(root: root, type: type, unreadable: 1));
        Assert.True(Offers(root: root, type: type, unreadable: 5_000));
    }

    // O-004: a scan that did not finish has no whole picture of what it could not read, and a refused or unreadable root has
    // nothing to scan again. Every value of the enum, so a status added later is a row somebody decides.
    [Fact]
    public void OnlyAScanThatCompletedOffersIt()
    {
        ScanStatus[] all = Enum.GetValues<ScanStatus>();
        Assert.Contains(ScanStatus.Cancelled, all);
        Assert.Contains(ScanStatus.RootUnreadable, all);
        Assert.Contains(ScanStatus.Refused, all);
        foreach (ScanStatus status in all)
            Assert.True(Offers(status: status) == (status == ScanStatus.Completed), $"{status}: offered {Offers(status: status)}");
    }

    // O-010: the copy the button starts is elevated, and has nothing to start again.
    [Fact]
    public void AnElevatedCopyNeverOffersItWhateverElseHolds()
    {
        Assert.False(Offers(elevated: true));
        Assert.True(Offers(elevated: false));
        foreach (DriveType type in Enum.GetValues<DriveType>())
            Assert.False(Offers(type: type, elevated: true));
    }

    // O-006: a drive that was read in full promises nothing to gain.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(-1, false)]
    public void ADriveThatWasReadInFullDoesNotOfferIt(int unreadable, bool expected) =>
        Assert.Equal(expected, Offers(unreadable: unreadable));

    // O-007's other half: whatever the probe hands back, only a fixed or removable drive is offered; a network drive and the
    // rest never are, and null (a letter an elevated copy would not see, or a root that is not a drive) is not either.
    [Fact]
    public void OnlyAFixedOrRemovableDriveTypeOffersIt()
    {
        foreach (DriveType type in Enum.GetValues<DriveType>())
            Assert.True(Offers(type: type) == (type is DriveType.Fixed or DriveType.Removable), $"{type}");
        Assert.False(Offers(type: null));
        Assert.Contains(DriveType.Network, Enum.GetValues<DriveType>());
        Assert.False(Offers(type: DriveType.Network));
    }

    // O-005: the expected value of every root spelling, written out. A drive root is one letter, a colon and a backslash in its
    // display form, so the extended spelling of a drive is one and the extended spelling of a share is not.
    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"c:\", true)]
    [InlineData(@"\\?\C:\", true)]
    [InlineData(@"C:", false)] // no backslash: not a root as the scan reports it
    [InlineData(@"C:\dir", false)]
    [InlineData(@"C:\dir\", false)] // a folder, with its trailing backslash
    [InlineData(@"C:\\", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\server\share\", false)]
    [InlineData(@"\\?\UNC\server\share", false)] // the extended spelling of a share is no drive
    [InlineData(@"\\?\UNC\c\d", false)]
    [InlineData(@"1:\", false)] // not a letter
    [InlineData(@"é:\", false)] // not an ASCII letter
    [InlineData(@"CC:\", false)]
    [InlineData(@"\", false)]
    [InlineData(@"/", false)]
    [InlineData(@"C:/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ARootIsADriveOnlyInTheShapeOfOne(string? root, bool expected)
    {
        Assert.Equal(expected, RescanAsAdministrator.IsDriveRootShape(root));
        Assert.Equal(expected, Offers(root: root));
    }

    // m5: the probe and the offer ask the same questions, so neither can be widened or narrowed on its own. Every combination
    // of every input: the offer is the probe's condition, the type's and the shape's, and nothing more.
    [Fact]
    public void OffersIsFalseWheneverWorthProbingIsFalseAndIsNothingMoreThanItsThreeQuestions()
    {
        string?[] roots = [@"C:\", @"c:\", @"\\?\C:\", @"C:\dir\", @"\\?\UNC\s\r", "", null];
        int[] unread = [-1, 0, 1, 7];
        DriveType?[] types = [null, .. Enum.GetValues<DriveType>().Select(t => (DriveType?)t)];
        int rows = 0;
        foreach (ScanStatus status in Enum.GetValues<ScanStatus>())
        foreach (string? root in roots)
        foreach (int count in unread)
        foreach (DriveType? type in types)
        foreach (bool elevated in new[] { false, true })
        {
            rows++;
            bool offered = RescanAsAdministrator.Offers(status, root, count, type, elevated);
            bool worth = RescanAsAdministrator.WorthProbing(status, count, elevated);
            if (!worth)
                Assert.False(offered, $"offered though not worth probing: {status} {root} {count} {type} {elevated}");
            bool expected = worth && type is { } t && RescanAsAdministrator.IsRescannableType(t) && RescanAsAdministrator.IsDriveRootShape(root);
            Assert.True(offered == expected, $"{status} {root} {count} {type} {elevated}: offered {offered}, the three questions say {expected}");
        }
        Assert.True(rows > 500);
    }

    [Theory]
    [InlineData(ScanStatus.Completed, 1, false, true)]
    [InlineData(ScanStatus.Completed, 0, false, false)]
    [InlineData(ScanStatus.Completed, 1, true, false)]
    [InlineData(ScanStatus.Cancelled, 1, false, false)]
    [InlineData(ScanStatus.RootUnreadable, 1, false, false)]
    [InlineData(ScanStatus.Refused, 1, false, false)]
    public void ALetterIsProbedOnlyForAFinishedScanWithFoldersUnreadInACopyThatIsNotElevated(ScanStatus status, int unreadable, bool elevated, bool expected) =>
        Assert.Equal(expected, RescanAsAdministrator.WorthProbing(status, unreadable, elevated));

    [Fact]
    public void OnlyAFixedOrRemovableDriveIsRescannable()
    {
        foreach (DriveType type in Enum.GetValues<DriveType>())
            Assert.True(RescanAsAdministrator.IsRescannableType(type) == (type is DriveType.Fixed or DriveType.Removable), $"{type}");
    }

    // The words are the app's own: plain, ASCII, and with no path or letter in them (Q-016 keeps those out of what a check reads).
    [Fact]
    public void TheButtonsWordsAreAsciiAndHoldNoPath()
    {
        string[] words =
        [
            RescanAsAdministrator.ButtonText, RescanAsAdministrator.ButtonTip, RescanAsAdministrator.DeclinedLine,
            RescanAsAdministrator.NoProgramLine, RescanAsAdministrator.NotADriveLine, RescanAsAdministrator.StartedLine,
            RescanAsAdministrator.RefusedLine(5),
        ];
        Assert.Equal("Rescan as administrator", RescanAsAdministrator.ButtonText);
        foreach (string word in words)
        {
            Assert.All(word, c => Assert.InRange(c, ' ', '~'));
            Assert.DoesNotContain(@":\", word);
        }
    }
}
