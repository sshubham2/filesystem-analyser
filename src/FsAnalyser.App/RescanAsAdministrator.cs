using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.App;

/// <summary>
/// 'Rescan as administrator' (SL-008): the offer, its probe, its press and its words. Pure: no WPF in it, so that every branch
/// runs in the default suite (the file is compiled into FsAnalyser.Core.Tests by link) with Windows' answers handed in.
/// </summary>
/// <remarks>
/// A finished scan of a whole local drive that left folders unread offers the person a copy of the app started again with the
/// full token of the same account, after Windows' own prompt, to scan the same drive. The window that offers never elevates
/// itself, and neither does a start: the one place the app asks Windows for elevation is <see cref="Start"/>, which the
/// button's Click alone reaches (the settled architecture rules about elevation and RescanElevationGuardTests say so). The new copy is the same program
/// started with the drive as its only argument, so it takes the route every launch with a path takes; it never gets the
/// report option, and the window it makes does not offer itself again (<see cref="Offers"/>: a copy already elevated).
/// The offer is for a letter the new copy can open: a SUBST or mapped letter belongs to this logon session alone
/// (<see cref="DriveTypeAnElevatedCopySees(string)"/>). The elevated copy does not enable any privilege: it reads what the full
/// token's ordinary rights list.
/// </remarks>
internal static class RescanAsAdministrator
{
    /// <summary>The button's text.</summary>
    public const string ButtonText = "Rescan as administrator";

    /// <summary>The button's tooltip: what it starts and what that is for.</summary>
    public const string ButtonTip =
        "Starts this app again as administrator, after the Windows prompt, to scan this drive again with the rights to read folders this scan could not";

    /// <summary>What the status line says when Windows' prompt was declined.</summary>
    public const string DeclinedLine = "The rescan as administrator was not started: the Windows prompt was declined.";

    /// <summary>What it says when the app has no program file to start again.</summary>
    public const string NoProgramLine = "The rescan as administrator was not started: the app could not find its own program file.";

    /// <summary>What it says when the root is not a whole drive: only a drive is rescanned this way.</summary>
    public const string NotADriveLine = "The rescan as administrator was not started: it rescans a whole drive only.";

    /// <summary>What it says once Windows took the request.</summary>
    public const string StartedLine = "A copy running as administrator was started; it scans this drive again in its own window.";

    /// <summary>What it says when Windows refused for any other reason: the code, never the program's path or the drive.</summary>
    public static string RefusedLine(int error) =>
        $"The rescan as administrator was not started: Windows did not start it (error {error}).";

    // Win32's ERROR_CANCELLED: the person chose No at the consent prompt (or closed it).
    private const int ErrorCancelled = 1223;

    // The shell's verb for the consent prompt. Written here and nowhere else in src (architecture rule).
    private const string ElevationVerb = "runas";

    /// <summary>
    /// Whether a scan that ended with <paramref name="unreadableFolders"/> unread folders is worth asking Windows about the
    /// drive's letter: it finished, it left folders unread, and this copy is not elevated already. The window probes the letter
    /// only when this holds, so a finished scan with nothing to offer costs no I/O, and <see cref="Offers"/> starts with it, so
    /// the probe and the offer cannot drift apart.
    /// </summary>
    public static bool WorthProbing(ScanStatus status, int unreadableFolders, bool elevated) =>
        status == ScanStatus.Completed && unreadableFolders > 0 && !elevated;

    /// <summary>
    /// Whether <paramref name="type"/> is a kind of drive the offer is for: a fixed disk or a removable one. A network drive,
    /// an optical drive, a RAM disk and the rest never are. The one place the set is written: the probe's open and the offer
    /// both ask it.
    /// </summary>
    public static bool IsRescannableType(DriveType type) => type is DriveType.Fixed or DriveType.Removable;

    /// <summary>
    /// Whether <paramref name="root"/> is a whole drive in its display form (<see cref="LongPath.ToDisplay"/>): one ASCII
    /// letter in either case, a colon and a backslash, so <c>\\?\C:\</c> is one and <c>C:</c>, a folder and a share in any
    /// spelling are not. The shape Core's volume read tests, which is internal there.
    /// </summary>
    public static bool IsDriveRootShape(string? root)
    {
        if (string.IsNullOrEmpty(root))
            return false;
        string path = LongPath.ToDisplay(root);
        return path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    }

    /// <summary>
    /// Whether an ending offers 'Rescan as administrator': a scan that finished, left at least one folder unread, in a copy that is
    /// not elevated (<see cref="WorthProbing"/>), of a whole drive (<see cref="IsDriveRootShape"/>) whose letter an elevated
    /// copy sees as a local drive (<paramref name="driveType"/>, what <see cref="DriveTypeAnElevatedCopySees(string)"/> answered:
    /// null for a letter it would not see, or a root that is not a drive). A scan that was stopped, refused or could not read its
    /// root offers nothing: its unread count is partial or there is nothing to rescan.
    /// </summary>
    public static bool Offers(ScanStatus status, string? root, int unreadableFolders, DriveType? driveType, bool elevated) =>
        WorthProbing(status, unreadableFolders, elevated)
        && driveType is { } type && IsRescannableType(type)
        && IsDriveRootShape(root);

    /// <summary>
    /// Asks Windows to start this program again with the consent prompt, scanning <paramref name="root"/>. Returns null when
    /// Windows took the request, and otherwise the line to show, in plain words and with no path in it: the prompt was declined
    /// (<see cref="DeclinedLine"/>), Windows refused for another reason (<see cref="RefusedLine"/>, with its code), there is no
    /// program file (<see cref="NoProgramLine"/>), or the root is not a whole drive (<see cref="NotADriveLine"/>). Windows is
    /// called at most once, never again, and only after those two checks, so an empty file name never reaches it. The program's
    /// only argument is the root, quoted and with its trailing backslashes doubled, so that it reaches the new copy as written
    /// (a backslash before the closing quote would escape it); the report option is never given. An exception that is not
    /// Windows' refusal is a bug and is not caught.
    /// </summary>
    /// <param name="exePath">The running program's file; null or empty gives <see cref="NoProgramLine"/>.</param>
    /// <param name="root">The scan's root as the ending shows it, a whole drive.</param>
    /// <param name="start">Starts a process, <see cref="Process.Start(ProcessStartInfo)"/> in the window. The process it returns
    /// is disposed here: nothing waits on the new copy.</param>
    public static string? Start(string? exePath, string root, Func<ProcessStartInfo, Process?> start)
    {
        if (string.IsNullOrEmpty(exePath))
            return NoProgramLine;
        if (!IsDriveRootShape(root))
            return NotADriveLine;
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = ElevationVerb,
            Arguments = QuoteRoot(root),
        };
        try
        {
            start(info)?.Dispose();
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return DeclinedLine;
        }
        catch (Win32Exception e)
        {
            return RefusedLine(e.NativeErrorCode);
        }
    }

    // The root as one argument that Windows' own splitting hands back as it was: in quotes, with the run of backslashes at its
    // end doubled, because a backslash before the closing quote would escape it (SL-007). A drive root holds no other quote.
    private static string QuoteRoot(string root)
    {
        int trailing = root.Length - root.TrimEnd('\\').Length;
        return $"\"{root}{new string('\\', trailing)}\"";
    }

    /// <summary>
    /// The kind of drive an elevated copy would see <paramref name="root"/> as, or null when it would not see it as the same
    /// drive. Null with no I/O when the root is not a whole drive. The drive's type is read first (no drive is opened for it);
    /// a type that is not fixed or removable (a network drive, an optical drive, a RAM disk, no drive) is returned as it is
    /// and nothing is opened, so a network letter is never touched from here, and <see cref="Offers"/> refuses it. For a fixed
    /// or removable one the letter must be in the global DOS-device namespace (<see cref="GlobalDosDevices"/>), which a SUBST
    /// letter made in this session is not: then null. Any read that fails with an I/O, access or argument exception gives null,
    /// so a finished scan can never turn into a failed one here. Runs off the UI thread, because the open can wait on a drive.
    /// </summary>
    public static DriveType? DriveTypeAnElevatedCopySees(string root) =>
        DriveTypeAnElevatedCopySees(root, drive => new DriveInfo(drive).DriveType, GlobalDosDevices.HoldsDriveLetter);

    /// <summary>
    /// <see cref="DriveTypeAnElevatedCopySees(string)"/> with its two reads handed in, as VolumeSpace.Start takes its reader, so
    /// a test can say what the drive's type is, and whether its letter is global, without a network drive or a second
    /// logon session; and can see that the namespace read is never made for a network letter.
    /// </summary>
    /// <param name="typeOf">The drive's type, given the root in its display form.</param>
    /// <param name="holdsLetter">Whether the global namespace holds the root's letter, upper or lower case as it is written;
    /// called only for a type <see cref="IsRescannableType"/> accepts.</param>
    internal static DriveType? DriveTypeAnElevatedCopySees(string root, Func<string, DriveType> typeOf, Func<char, bool> holdsLetter)
    {
        if (!IsDriveRootShape(root))
            return null;
        string drive = LongPath.ToDisplay(root);
        try
        {
            DriveType type = typeOf(drive);
            if (!IsRescannableType(type))
                return type;
            return holdsLetter(drive[0]) ? type : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
