namespace FsAnalyser.Core.Native;

/// <summary>
/// Which drive letters an elevated copy of the app can see (SL-008). Windows gives each logon session its own map of
/// DOS devices and falls back to the global one, and the elevated copy of an ordinary account's app runs in a logon session
/// of its own: a letter made in the first (a SUBST letter, a mapped network drive) does not resolve in the second, so a
/// rescan that was handed such a letter would end as "Could not read". The letters of the local volumes are in the
/// global map. The test is an open and nothing more: nothing is listed and no data is read.
/// </summary>
public static class GlobalDosDevices
{
    /// <summary>
    /// Whether <paramref name="letter"/> is held by the global DOS-device namespace, so that every logon session, an elevated
    /// one included, resolves it to the same volume. False for anything that is not an ASCII letter, and for a letter that does
    /// not open (not in the namespace, or a removable drive with no media: nothing waits for a disk to be put in). Opens the
    /// letter's root, so a caller asks it of a letter whose drive type it already knows to be local (Fixed or Removable) and
    /// never of a network letter; it can wait on a drive that does not answer, so not on the UI thread.
    /// </summary>
    public static bool HoldsDriveLetter(char letter) =>
        char.IsAsciiLetter(letter) && Kernel32.OpensThroughGlobalDosDevices(char.ToUpperInvariant(letter));
}
