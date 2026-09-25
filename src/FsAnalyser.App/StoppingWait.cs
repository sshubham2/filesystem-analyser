namespace FsAnalyser.App;

/// <summary>
/// What the Stopping state says while a cancelled scan has not returned (SL-038). A share whose server
/// stopped answering holds the scan until the SMB client gives up (57 to 66 s on the loopback rig), and no
/// in-process cancel returns it sooner, so the window says it is waiting instead of looking hung.
/// </summary>
/// <remarks>
/// A pure function of the time since Cancel: no clock, no drive, no share, no WPF. It runs on the UI
/// thread every tick, and a call that touched the stalled share would hang the line that explains the
/// hang. It is also compiled into Core.Tests, which has no WPF, so its boundaries are tested there.
/// <para>The wording makes no claim about how long a real disconnect takes (Wi-Fi off, VPN down, sleep):
/// none was measured. "A minute or more" rests on the rig and this PC's 60 s SMB SessionTimeout. From
/// 90 s it gives no estimate at all. The counter counts up, never down. "Closing the window quits at
/// once" was measured on the rig only (window gone within 29 ms, process within 345 ms).</para>
/// </remarks>
internal static class StoppingWait
{
    /// <summary>The cancel bound SL-020 promises for every measured shape but this one: an ordinary cancel never shows the line.</summary>
    internal static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(2);

    /// <summary>From here the line gives no estimate: past what the rig ever took, and past StalledShareTests' own 90 s.</summary>
    internal static readonly TimeSpan NoEstimateAfter = TimeSpan.FromSeconds(90);

    /// <summary>The line for <paramref name="sinceCancel"/>, or null when there is nothing to say (under 2 s, or negative).</summary>
    internal static string? For(TimeSpan sinceCancel)
    {
        if (sinceCancel < ShowAfter)
            return null;
        long seconds = sinceCancel.Ticks / TimeSpan.TicksPerSecond; // floored: positive here
        return sinceCancel < NoEstimateAfter
            ? $"Still waiting for the drive to answer ({seconds} s). If a network connection dropped, this can take a minute or more. Closing the window quits at once."
            : $"Still waiting for the drive to answer ({seconds} s). Closing the window quits at once.";
    }
}
