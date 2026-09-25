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

    /// <summary>
    /// Under the 2 s gate, negative spans included, there is nothing to show, so there is never a negative
    /// count (case C-013). In ticks: TimeSpan.MinValue, -5 s, -1 ms, 0, 1.999 s and one tick short of 2 s.
    /// </summary>
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-50_000_000)]
    [InlineData(-10_000)]
    [InlineData(0)]
    [InlineData(19_990_000)]
    [InlineData(19_999_999)]
    public void UnderTheGateNothingIsShown(long ticks)
    {
        Assert.Null(StoppingWait.For(TimeSpan.FromTicks(ticks)));
    }

    /// <summary>
    /// The gate is inclusive at 2 s. From there to just under 90 s the line counts whole seconds, floored,
    /// never goes down, and always names closing the window (case C-014).
    /// </summary>
    [Fact]
    public void FromTwoSecondsTheLineCountsUpInWholeSeconds()
    {
        Assert.Equal(string.Format(MinuteOrMore, 2), StoppingWait.For(TimeSpan.FromSeconds(2)));
        Assert.Equal(string.Format(MinuteOrMore, 2), StoppingWait.For(TimeSpan.FromMilliseconds(2_999)));
        Assert.Equal(string.Format(MinuteOrMore, 59), StoppingWait.For(TimeSpan.FromMilliseconds(59_999)));

        long previous = 0;
        for (long ms = 2_000; ms < 90_000; ms += 7)
        {
            string? text = StoppingWait.For(TimeSpan.FromMilliseconds(ms));
            long seconds = ms / 1000;
            Assert.Equal(string.Format(MinuteOrMore, seconds), text);
            Assert.True(seconds >= previous, $"the count went down at {ms} ms: {seconds} s after {previous} s");
            Assert.Contains("Closing the window quits at once.", text);
            previous = seconds;
        }
    }

    /// <summary>
    /// At 90 s the line switches to the wording with no estimate and keeps it, at an hour and beyond, with
    /// no overflow (case C-015).
    /// </summary>
    [Fact]
    public void FromNinetySecondsThereIsNoEstimate()
    {
        Assert.Equal(string.Format(MinuteOrMore, 89), StoppingWait.For(TimeSpan.FromMilliseconds(89_999)));
        Assert.Equal(string.Format(MinuteOrMore, 89), StoppingWait.For(TimeSpan.FromSeconds(90) - TimeSpan.FromTicks(1)));
        Assert.Equal(string.Format(NoEstimate, 90), StoppingWait.For(TimeSpan.FromSeconds(90)));
        Assert.Equal(string.Format(NoEstimate, 90), StoppingWait.For(TimeSpan.FromMilliseconds(90_999)));
        Assert.Equal(string.Format(NoEstimate, 3600), StoppingWait.For(TimeSpan.FromHours(1)));
        Assert.Equal(string.Format(NoEstimate, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond), StoppingWait.For(TimeSpan.MaxValue));

        for (long ms = 90_000; ms <= 3_600_000; ms += 997)
            Assert.Equal(string.Format(NoEstimate, ms / 1000), StoppingWait.For(TimeSpan.FromMilliseconds(ms)));
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
