using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The App's Stopping wording (SL-038, src/FsAnalyser.App/StoppingWait.cs, linked into this project as
/// source). The 90 s wording cannot be reached on the loopback rig, whose SMB client gives up at about
/// 60 s, so these boundaries are the only test it has.
/// </summary>
public class StoppingWaitTests
{
    private const string MinuteOrMore = "Still waiting for the drive to answer ({0} s). If a network connection dropped, this can take a minute or more. Closing the window quits at once.";
    private const string NoEstimate = "Still waiting for the drive to answer ({0} s). Closing the window quits at once.";

    /// <summary>Each boundary, with the wording pinned: none, the "minute or more" line, or the one with no estimate.</summary>
    [Theory]
    [InlineData(-5_000, null, 0)]
    [InlineData(-1, null, 0)]
    [InlineData(0, null, 0)]
    [InlineData(1_999, null, 0)]
    [InlineData(2_000, MinuteOrMore, 2)]
    [InlineData(89_999, MinuteOrMore, 89)]
    [InlineData(90_000, NoEstimate, 90)]
    [InlineData(3_600_000, NoEstimate, 3600)]
    public void TheWordingAtEachBoundary(long milliseconds, string? wording, int seconds)
    {
        string? expected = wording is null ? null : string.Format(wording, seconds);
        Assert.Equal(expected, StoppingWait.For(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void TheGateIsExactlyTwoSeconds()
    {
        Assert.Null(StoppingWait.For(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1)));
        Assert.NotNull(StoppingWait.For(TimeSpan.FromSeconds(2)));
        Assert.Equal(string.Format(MinuteOrMore, 89), StoppingWait.For(TimeSpan.FromSeconds(90) - TimeSpan.FromTicks(1)));
        Assert.Equal(string.Format(NoEstimate, 90), StoppingWait.For(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void ANegativeSpanShowsNothing()
    {
        Assert.Null(StoppingWait.For(TimeSpan.MinValue));
        Assert.Null(StoppingWait.For(TimeSpan.FromDays(-1)));
    }

    [Fact]
    public void TheSecondsAreFloored()
    {
        Assert.Equal(string.Format(MinuteOrMore, 2), StoppingWait.For(TimeSpan.FromMilliseconds(2_999)));
        Assert.Equal(string.Format(MinuteOrMore, 59), StoppingWait.For(TimeSpan.FromMilliseconds(59_999)));
        Assert.Equal(string.Format(NoEstimate, 90), StoppingWait.For(TimeSpan.FromMilliseconds(90_999)));
    }

    /// <summary>
    /// ASCII, so the hook's log survives Windows PowerShell 5.1's code page, and no frequency or upper
    /// bound on the wait: no real disconnect was measured (O-002).
    /// </summary>
    [Fact]
    public void TheTextIsAsciiAndGivesNoBoundOnTheWait()
    {
        for (long ms = 2_000; ms <= 200_000; ms += 500)
        {
            string text = StoppingWait.For(TimeSpan.FromMilliseconds(ms))!;
            Assert.All(text, c => Assert.InRange(c, ' ', '~'));
            Assert.DoesNotContain("usually", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("about a minute", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("remaining", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Closing the window quits at once.", text);
        }
    }
}
