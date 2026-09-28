using FsAnalyser.App;
using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The window fits itself to the work area of the monitor it opened on, not the primary's (HF-019). This PC has one
/// monitor, so the monitors here are made: a primary 1920 x 1080 with a 48 px taskbar (work area 0 to 1032, this PC's),
/// and a second one arranged below it or taller than it. The window is SL-044's default, 860 tall.
/// </summary>
public class WorkAreaFitTests
{
    private const double Default = 860;
    private const double PrimaryTop = 0, PrimaryBottom = 1032;

    [Fact]
    public void AWindowPlacedOnAMonitorBelowThePrimaryStaysOnIt()
    {
        const double BelowTop = 1080, BelowBottom = 2112; // the second 1920 x 1080, under the primary
        const double PlacedAt = 1106; // where Windows put it: 26 px into the second monitor, as it does on the primary

        Assert.Equal((PlacedAt, Default), WorkAreaFit.Fit(PlacedAt, Default, BelowTop, BelowBottom));

        // The primary's work area, which the window measured itself against before, moved it up onto the primary.
        (double top, _) = WorkAreaFit.Fit(PlacedAt, Default, PrimaryTop, PrimaryBottom);
        Assert.True(top + Default <= BelowTop, $"against the primary's work area its top was {top}, above the second monitor");
    }

    [Fact]
    public void AWindowOnAMonitorTallerThanThePrimaryKeepsItsHeight()
    {
        const double TallTop = -600, TallBottom = 1872; // a portrait 2560 tall beside the primary, with its taskbar
        const double Tall = 2000; // a height the primary's work area would cut to 1032
        const double PlacedAt = TallTop + 26;

        Assert.Equal((PlacedAt, Tall), WorkAreaFit.Fit(PlacedAt, Tall, TallTop, TallBottom));
        Assert.Equal(PrimaryBottom - PrimaryTop, WorkAreaFit.Fit(PlacedAt, Tall, PrimaryTop, PrimaryBottom).Height);
    }

    [Fact]
    public void AWindowWhoseFootIsBelowItsMonitorsWorkAreaIsMovedUpOnThatMonitor()
    {
        const double BelowTop = 1080, BelowBottom = 2112;

        Assert.Equal((BelowBottom - Default, Default), WorkAreaFit.Fit(1300, Default, BelowTop, BelowBottom));
        Assert.Equal((PrimaryBottom - Default, Default), WorkAreaFit.Fit(234, Default, PrimaryTop, PrimaryBottom)); // SL-044's 62 px
        Assert.Equal((26.0, Default), WorkAreaFit.Fit(26, Default, PrimaryTop, PrimaryBottom)); // already inside: left alone
    }

    [Fact]
    public void AWindowTallerThanItsMonitorsWorkAreaTakesAllOfItFromItsTop()
    {
        const double SmallTop = 1080, SmallBottom = 1800; // a second monitor 768 tall, less its taskbar

        Assert.Equal((SmallTop, SmallBottom - SmallTop), WorkAreaFit.Fit(1106, Default, SmallTop, SmallBottom));
    }

    /// <summary>
    /// The monitor query the window makes, on a real window handle: the desktop window is on the primary monitor, so
    /// its monitor's work area is the one Windows reports for the primary. A query that failed would leave the window
    /// on the primary's work area, as before, with nothing to show for it.
    /// </summary>
    [Fact]
    public void TheWorkAreaOfTheMonitorAWindowIsOnIsReadFromWindows()
    {
        WorkArea? area = Monitors.WorkAreaOf(TestNative.GetDesktopWindow());

        Assert.NotNull(area);
        (int left, int top, int right, int bottom) = TestNative.PrimaryWorkArea();
        Assert.Equal(new WorkArea(left, top, right, bottom), area);
        Assert.True(area.Value.Right > area.Value.Left && area.Value.Bottom > area.Value.Top, $"an empty work area: {area}");
    }
}
