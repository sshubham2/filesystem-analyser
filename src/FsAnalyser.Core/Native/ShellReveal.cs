using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FsAnalyser.Core.Native;

public enum RevealKind
{
    Exact,
    Failed
}

public enum RevealReason
{
    None,
    NameUnaddressable,
    NoLongerExists,
    ParsedAsAnotherItem,
    HiddenByExplorerSettings
}

/// <summary>Which of Explorer's view settings hide an item: what a refusal names (SL-091).</summary>
[Flags]
public enum ExplorerHides
{
    None = 0,
    /// <summary>The item is Hidden and Explorer is set not to show hidden files, folders, or drives.</summary>
    HiddenFiles = 1,
    /// <summary>The item is Hidden and System and Explorer is set to hide protected operating system files.</summary>
    ProtectedFiles = 2
}

/// <summary>
/// What Explorer's view lists (SL-091): hidden items when ShowsHidden, and of those the ones that are also System when
/// ShowsProtected too. Only an item's own attributes decide it: an item under a hidden folder is listed like any other
/// (measured at Windows' defaults, +H and +H+S folders above the item, 18 of 18 each).
/// </summary>
public sealed record ExplorerView(bool ShowsHidden, bool ShowsProtected)
{
    /// <summary>Whether Explorer lists, and so can select, an item with these attributes.</summary>
    public bool Shows(bool hidden, bool system) => !hidden || (ShowsHidden && (!system || ShowsProtected));

    /// <summary>The settings that keep an item with these attributes from being listed; None exactly when Shows.</summary>
    public ExplorerHides Hides(bool hidden, bool system) =>
        (hidden && !ShowsHidden ? ExplorerHides.HiddenFiles : ExplorerHides.None) |
        (hidden && system && !ShowsProtected ? ExplorerHides.ProtectedFiles : ExplorerHides.None);

    /// <summary>
    /// The first DWORD of the SHELLSTATE the shell fills in: fShowAllObjects is bit 0 and fShowSuperHidden bit 15. Every
    /// other bit is another setting (fShowSysFiles is bit 3) and is left alone.
    /// </summary>
    internal static ExplorerView FromShellState(uint state) => new((state & 0x1) != 0, (state & 0x8000) != 0);

    /// <summary>Field by field AND: what is shown only where both views show it.</summary>
    internal ExplorerView And(ExplorerView mask) => new(ShowsHidden && mask.ShowsHidden, ShowsProtected && mask.ShowsProtected);
}

/// <summary>
/// What a reveal will ask the shell for: the item itself, in display form, or null when one of its names is one the
/// shell cannot address (UnaddressableName: a trailing dot or space, or a bare device name), in which case nothing is
/// asked.
/// </summary>
public sealed record RevealPlan(string? Target, string? UnaddressableName, string Requested = "");

/// <summary>
/// How a reveal ended. Hidden and System are the item's own attributes, read before the open. HiddenBy is set on a
/// HiddenByExplorerSettings refusal, and View is the Explorer view the reveal was decided under: null when none was read
/// (an item that is not Hidden, a volume root, or a reveal refused before the attributes were read), so 'not read' is
/// never taken for 'nothing shown'.
/// </summary>
public sealed record RevealOutcome(
    RevealKind Kind,
    string Requested,
    string? Shown,
    RevealReason Reason,
    string? UnaddressableName,
    bool Hidden,
    bool System,
    int HResult,
    ExplorerHides HiddenBy = ExplorerHides.None,
    ExplorerView? View = null);

/// <summary>
/// Reveals an item in Windows Explorer by opening its parent with the item selected. The reveal lands on the item or
/// is refused: an item that no longer exists, a name the shell cannot address, a parse that stands for another item,
/// an item Explorer's own view settings keep it from listing (SL-091), or a shell call that fails opens no window at all,
/// and never another folder in the item's place (SL-088, reversing SL-047's nearest-ancestor fallback). Explorer's
/// settings are read, never changed.
/// </summary>
public static partial class ShellReveal
{
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int ERROR_FILE_NOT_FOUND_HR = unchecked((int)0x80070002);
    private const int ERROR_PATH_NOT_FOUND_HR = unchecked((int)0x80070003);
    private const int ERROR_INVALID_NAME_HR = unchecked((int)0x8007007B);

    /// <summary>The shell's parse of a path into an item ID list, as SHParseDisplayName makes it.</summary>
    internal delegate int ParseName(string path, out IntPtr pidl);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHGetPathFromIDListEx(IntPtr pidl, [Out] char[] pszPath, int cchPath, int uOpts);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    // Reads (the last argument false) or writes the shell's view state. Only ever called here, to read.
    [LibraryImport("shell32.dll")]
    private static partial void SHGetSetSettings([Out] byte[] lpss, uint dwMask, [MarshalAs(UnmanagedType.Bool)] bool bSet);

    private const uint SSF_SHOWALLOBJECTS = 0x00000001;
    private const uint SSF_SHOWSUPERHIDDEN = 0x00040000;

    // At least the size of SHELLSTATE, zeroed, so bits the shell leaves alone read 0.
    private const int ShellStateBytes = 64;

    private const int MaxLongPath = 32767;

    // A test's view (FSA_HOOK_EXPLORER_VIEW): set once before the app's Main and read on the reveal's STA thread.
    private static ExplorerView? _viewMask;

    /// <summary>
    /// Pure planner: turns a path into the one target a reveal asks the shell for, and detects unaddressable names.
    /// Does no I/O.
    /// </summary>
    public static RevealPlan Plan(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));

        string display = LongPath.ToDisplay(path);
        if (display.Length == 2 && char.IsAsciiLetter(display[0]) && display[1] == ':')
            display += '\\';

        display = display.Replace('/', '\\');

        if (!Path.IsPathFullyQualified(display))
            throw new ArgumentException($"'{path}' is not an absolute path.", nameof(path));

        string root;
        string remainder;

        if (display.StartsWith(@"\\"))
        {
            // UNC path: \\server\share[\more]
            int serverEnd = display.IndexOf('\\', 2);
            if (serverEnd < 0)
                throw new ArgumentException($"'{path}' is not a valid UNC path.", nameof(path));

            int shareEnd = display.IndexOf('\\', serverEnd + 1);
            if (shareEnd < 0)
            {
                root = display;
                remainder = string.Empty;
            }
            else
            {
                root = display[..shareEnd];
                remainder = display[(shareEnd + 1)..];
            }
        }
        else
        {
            // Drive path: X:\...
            if (display.Length < 3 || display[1] != ':' || display[2] != '\\')
                throw new ArgumentException($"'{path}' is not a valid drive path.", nameof(path));

            root = display[..3]; // e.g. "C:\"
            remainder = display[3..];
        }

        string[] parts = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            // The shell cannot address the name as written ('dup.' parses as 'dup', 'trail ' as not found, a bare 'con'
            // as the device), so it is not asked at all.
            if (IsMisparsed(part, out string badName))
                return new RevealPlan(null, badName, display);
        }

        return new RevealPlan(Combine(root, parts, parts.Length), null, display);
    }

    // A trailing dot or space, or a whole name that is a device's. A device name with an extension ('con.txt', 'aux.h')
    // is left to the shell, which addresses it exactly on Windows 11 (SL-088 review R2-m2, measured); wherever it would
    // not, the parse's failure or its resolving to another item refuses the reveal in Walk.
    private static bool IsMisparsed(string component, out string name)
    {
        name = component;
        if (component.EndsWith('.') || component.EndsWith(' '))
            return true;

        string baseName = component.TrimEnd(' ');

        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && baseName.Length >= 4)
        {
            string suffix = baseName[3..];
            if (suffix.Length == 1 && (char.IsDigit(suffix[0]) || suffix[0] is '¹' or '²' or '³'))
                return true;
        }

        if (baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) && baseName.Length >= 4)
        {
            string suffix = baseName[3..];
            if (suffix.Length == 1 && (char.IsDigit(suffix[0]) || suffix[0] is '¹' or '²' or '³'))
                return true;
        }

        return false;
    }

    private static string Combine(string root, string[] parts, int count)
    {
        if (count == 0)
            return root;

        var sb = new StringBuilder(root);
        if (!root.EndsWith('\\'))
            sb.Append('\\');

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append('\\');
            sb.Append(parts[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reveals the plan's target once: parse it, check the ID list stands for the target itself, check Explorer's view
    /// lists it, open its parent with it selected, free the ID list. The parse's HRESULT is the existence check. A target
    /// that does not parse, whose ID list resolves to another path (as 'dup.' parses to 'dup'), that Explorer's view
    /// settings hide, or whose open fails is refused, and no other folder is tried; 0x80070002 and 0x80070003 say it no
    /// longer exists. Exact needs the ID list to resolve back to the target, Explorer's view to list it and the open to
    /// return S_OK: whether Explorer's window then shows the item selected is for the window cases to see (SL-088,
    /// criterion 4).
    /// The view is asked of <paramref name="explorer"/> at most once, and only for a Hidden target that is not a volume
    /// root: the open returns S_OK for an item Explorer hides and selects nothing (SL-091, 0 of 13 at Windows' defaults),
    /// so such a target is refused before the open, with HiddenByExplorerSettings and the settings that hide it. Only the
    /// target's own attributes decide; the folders above it are never read.
    /// Unit-testable without calling Explorer, through the shell calls it is given.
    /// </summary>
    internal static RevealOutcome Walk(RevealPlan plan, string? requested, ParseName parse, Func<IntPtr, string?> pathOf, Func<IntPtr, int> open,
        Action<IntPtr> free, Func<ExplorerView> explorer)
    {
        string requestedPath = !string.IsNullOrEmpty(requested)
            ? requested
            : (!string.IsNullOrEmpty(plan.Requested) ? plan.Requested : plan.Target ?? string.Empty);

        if (plan.Target is null)
            return Refused(requestedPath, RevealReason.NameUnaddressable, plan.UnaddressableName, ERROR_INVALID_NAME_HR);

        IntPtr pidl = IntPtr.Zero;
        bool isHidden = false;
        bool isSystem = false;
        ExplorerView? view = null;
        try
        {
            int hr = parse(plan.Target, out pidl);
            if (hr != 0)
            {
                bool gone = hr == ERROR_FILE_NOT_FOUND_HR || hr == ERROR_PATH_NOT_FOUND_HR;
                return Refused(requestedPath, gone ? RevealReason.NoLongerExists : RevealReason.None, null, hr);
            }

            if (!SamePath(pathOf(pidl), plan.Target))
                return Refused(requestedPath, RevealReason.ParsedAsAnotherItem, null, ERROR_INVALID_NAME_HR);

            // A volume root carries Hidden and System, and Explorer shows it all the same. An item this user cannot open
            // has its attributes read through its parent's listing; a read that fails all the same counts as not hidden.
            if (!IsRoot(plan.Target))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(LongPath.ToExtended(requestedPath));
                    isHidden = (attrs & FileAttributes.Hidden) != 0;
                    isSystem = (attrs & FileAttributes.System) != 0;
                }
                catch
                {
                    isHidden = false;
                    isSystem = false;
                }
            }

            if (isHidden)
            {
                view = explorer();
                if (!view.Shows(isHidden, isSystem))
                    return new RevealOutcome(RevealKind.Failed, requestedPath, null, RevealReason.HiddenByExplorerSettings, null, isHidden, isSystem, 0,
                        view.Hides(isHidden, isSystem), view);
            }

            hr = open(pidl);
            if (hr != 0)
                return new RevealOutcome(RevealKind.Failed, requestedPath, null, RevealReason.None, null, isHidden, isSystem, hr, ExplorerHides.None, view);
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                free(pidl);
        }

        return new RevealOutcome(RevealKind.Exact, requestedPath, plan.Target, RevealReason.None, null, isHidden, isSystem, 0, ExplorerHides.None, view);
    }

    private static RevealOutcome Refused(string requested, RevealReason reason, string? unaddressableName, int hr) =>
        new(RevealKind.Failed, requested, null, reason, unaddressableName, false, false, hr);

    /// <summary>
    /// Explorer's view as the shell holds it for this user, read fresh: the state is asked for, never written. The shell
    /// gives no error back, so a read that failed reads as nothing shown, which refuses rather than lands.
    /// </summary>
    internal static ExplorerView ReadExplorerView()
    {
        var state = new byte[ShellStateBytes];
        SHGetSetSettings(state, SSF_SHOWALLOBJECTS | SSF_SHOWSUPERHIDDEN, false);
        return ExplorerView.FromShellState(BitConverter.ToUInt32(state, 0));
    }

    /// <summary>
    /// A test's view, set before the app's Main by the window cases' hook (FSA_HOOK_EXPLORER_VIEW), or null to clear it.
    /// It can only hide: the view a reveal is decided under is the real one ANDed with it, so no value set here turns a
    /// refusal under the real settings into a landing.
    /// </summary>
    internal static void OverrideExplorerView(ExplorerView? mask) => Volatile.Write(ref _viewMask, mask);

    /// <summary>The view a reveal is decided under: the real one, ANDed with a test's when one is set.</summary>
    internal static ExplorerView EffectiveExplorerView() => Effective(ReadExplorerView(), Volatile.Read(ref _viewMask));

    internal static ExplorerView Effective(ExplorerView real, ExplorerView? mask) => mask is null ? real : real.And(mask);

    private static bool IsRoot(string target) =>
        string.Equals(Path.GetPathRoot(target)?.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    internal static int ParseDisplayName(string path, out IntPtr pidl) =>
        SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);

    internal static int OpenFolderAndSelect(IntPtr pidl) =>
        SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);

    internal static void FreeIdList(IntPtr pidl) => ILFree(pidl);

    /// <summary>The file-system path an ID list stands for, or null when it stands for none.</summary>
    internal static string? PathOfIdList(IntPtr pidl)
    {
        var buffer = new char[MaxLongPath];
        if (!SHGetPathFromIDListEx(pidl, buffer, buffer.Length, 0))
            return null;
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    // Whether the path an ID list stands for is the target. Past MAX_PATH the shell gives the path back in 8.3 form
    // (measured at 300 and 1000 characters), so a path that differs as text is compared again with every alias expanded.
    private static bool SamePath(string? landed, string target)
    {
        if (landed is null)
            return false;
        if (string.Equals(landed.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return true;
        return LongName(landed) is { } a && LongName(target) is { } b &&
            string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string? LongName(string path)
    {
        var buffer = new char[MaxLongPath];
        uint n = GetLongPathNameW(LongPath.ToExtended(path), buffer, (uint)buffer.Length);
        return n == 0 || n >= buffer.Length ? null : LongPath.ToDisplay(new string(buffer, 0, (int)n));
    }

    /// <summary>
    /// Launches a background STA thread to reveal the item in Explorer without blocking the caller.
    /// The returned Task never faults.
    /// </summary>
    public static Task<RevealOutcome> RevealAsync(string path)
    {
        var tcs = new TaskCompletionSource<RevealOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                RevealPlan plan;
                try
                {
                    plan = Plan(path);
                }
                catch (ArgumentException)
                {
                    tcs.SetResult(new RevealOutcome(
                        RevealKind.Failed,
                        path ?? string.Empty,
                        null,
                        RevealReason.None,
                        null,
                        false,
                        false,
                        E_INVALIDARG));
                    return;
                }

                RevealOutcome outcome = Walk(plan, path, ParseDisplayName, PathOfIdList, OpenFolderAndSelect, FreeIdList, EffectiveExplorerView);
                tcs.SetResult(outcome);
            }
            catch (Exception ex)
            {
                tcs.SetResult(new RevealOutcome(
                    RevealKind.Failed,
                    path ?? string.Empty,
                    null,
                    RevealReason.None,
                    null,
                    false,
                    false,
                    ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005)));
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }
}
