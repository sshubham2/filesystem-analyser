using System.Globalization;
using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A folder tree row's share and bar (SL-044 review m2), on their own (HF-012): until FolderTreeRowText stopped calling
/// the window to write its sizes, they were reached only through the published exe's case T-001.
/// </summary>
public class FolderTreeRowTextTests
{
    [Fact]
    public void AShareIsWrittenToOneDecimalWithAMidpointRoundedAwayFromZero()
    {
        Assert.Equal(Percent(6.3), FolderTreeRowText.Share(1 / 16.0)); // 6.25: an exact binary midpoint
        Assert.Equal(Percent(93.8), FolderTreeRowText.Share(15 / 16.0)); // 93.75
        Assert.Equal(Percent(33.3), FolderTreeRowText.Share(1 / 3.0));
        Assert.Equal(Percent(0), FolderTreeRowText.Share(0)); // every child's, when the parent takes nothing on disk
        Assert.Equal(Percent(0), FolderTreeRowText.Share(4096 / (8.0 * 1024 * 1024))); // 4 KiB of 8 MiB: 0.05 % rounds down
        Assert.Equal(Percent(100), FolderTreeRowText.Share(1));
    }

    [Fact]
    public void ABarIsItsShareOfTheTrackAndNeverNarrowerThanOnePixel()
    {
        Assert.Equal(0, FolderTreeRowText.BarWidth(0));
        Assert.Equal(1, FolderTreeRowText.BarWidth(4096 / (8.0 * 1024 * 1024))); // the row that reads 0.0 % still draws
        Assert.Equal(1, FolderTreeRowText.BarWidth(1 / FolderTreeRowText.BarTrack));
        Assert.Equal(1.5, FolderTreeRowText.BarWidth(1.5 / FolderTreeRowText.BarTrack));
        Assert.Equal(FolderTreeRowText.BarTrack / 2, FolderTreeRowText.BarWidth(0.5));
        Assert.Equal(FolderTreeRowText.BarTrack, FolderTreeRowText.BarWidth(1));
    }

    private static string Percent(double value) => value.ToString("F1", CultureInfo.CurrentCulture) + " %";
}
