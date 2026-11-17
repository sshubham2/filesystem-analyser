using System.ComponentModel;
using System.Text.RegularExpressions;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Workarounds;

namespace FsAnalyser.App;

/// <summary>
/// One row of the unreadable-folders list: the folder's path, for reveal and display, how much of it was listed and
/// why it failed, as the engine recorded them. The list shows <see cref="Text"/>, one line.
/// </summary>
public sealed record UnreadableRow(string Path, UnreadableKind Kind, int Win32Error, string Reason)
{
    public string Text => $"{Path}  —  {UnreadableRows.KindText(Kind)}: {Reason}";

    public override string ToString() => Text;
}

/// <summary>
/// One row for a folder the scan left out on purpose (SL-095): the Windows container layer store. Not a failed folder,
/// so not an <see cref="UnreadableRow"/>, and the window gives it no 'Show in Explorer'.
/// </summary>
public sealed record NotScannedRow(string Path, NotScannedReason Reason, string WorkaroundId)
{
    public string Text => $"{Path}  —  {UnreadableRows.NotScannedText(Reason)}";

    public override string ToString() => Text;
}

/// <summary>
/// The unreadable-folders list (SL-087), from the scan's record of failed folders and nothing else: one row per folder
/// in the record, flat and in path order, none left out and none added. Each says what the engine recorded of it,
/// could not be opened, opened but nothing could be listed, or only partly read, and why in words a person can
/// read. The same words, from the same function, are the folder tree's note and the results list's row. It never asks the disk about a
/// folder: the list is of the scan it belongs to, and asking again could hang on a dropped share or meet the same
/// refusals on the UI thread. No WPF either, so Core.Tests compiles it as source.
/// </summary>
public static partial class UnreadableRows
{
    // The codes that get a plain phrase, named as winerror.h names them (review m3). Every other code keeps Windows'
    // own message; U-111 found no other family common enough to word here.
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NETWORK_ACCESS_DENIED = 65;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_REM_NOT_LIST = 51;
    private const int ERROR_BAD_NETPATH = 53;
    private const int ERROR_NETWORK_BUSY = 54;
    private const int ERROR_DEV_NOT_EXIST = 55;
    private const int ERROR_BAD_NET_RESP = 58;
    private const int ERROR_UNEXP_NET_ERR = 59;
    private const int ERROR_NETNAME_DELETED = 64;
    private const int ERROR_BAD_NET_NAME = 67;
    private const int ERROR_SHARING_PAUSED = 70;
    private const int ERROR_REQ_NOT_ACCEP = 71;
    private const int ERROR_REDIR_PAUSED = 72;
    private const int ERROR_NO_NET_OR_BAD_PATH = 1203;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_NO_NETWORK = 1222;
    private const int ERROR_NETWORK_UNREACHABLE = 1231;
    private const int ERROR_HOST_UNREACHABLE = 1232;
    private const int ERROR_CONNECTION_ABORTED = 1236;
    private const int ERROR_NOT_CONNECTED = 2250;

    internal const string AccessDenied = "access denied";
    internal const string InUse = "in use by another program";
    internal const string NetworkOrShare = "network or share error";

    /// <summary>
    /// The rows for a scan's failed-folder record, <paramref name="pathOf"/> giving each entry's full path. One row per
    /// entry: an entry the record holds more than once keeps its first kind and code. Sorted by <see cref="ComparePaths"/>,
    /// with no cap, no grouping and no row of any other kind. Whether the scan finished or was stopped makes no
    /// difference here: a stopped scan's record is listed the same way.
    /// </summary>
    public static IReadOnlyList<UnreadableRow> Build(IReadOnlyList<UnreadableFolder> record, Func<int, string> pathOf)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pathOf);
        var seen = new HashSet<int>();
        var reasons = new Dictionary<int, string>();
        var rows = new List<UnreadableRow>(record.Count);
        foreach (UnreadableFolder folder in record)
        {
            if (!seen.Add(folder.Entry))
                continue;
            if (!reasons.TryGetValue(folder.Win32Error, out string? reason))
                reasons[folder.Win32Error] = reason = Reason(folder.Win32Error);
            rows.Add(new UnreadableRow(pathOf(folder.Entry), folder.Kind, folder.Win32Error, reason));
        }
        rows.Sort((a, b) => ComparePaths(a.Path, b.Path));
        return rows;
    }

    /// <summary>
    /// The rows for a scan's folders left out on purpose (SL-095), from its own record, never from the failed-folder one:
    /// one per entry, in path order. They are never counted among the folders that could not be opened.
    /// </summary>
    public static IReadOnlyList<NotScannedRow> NotScanned(IReadOnlyList<NotScannedFolder> record, Func<int, string> pathOf)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pathOf);
        var rows = record.DistinctBy(folder => folder.Entry).Select(folder => new NotScannedRow(pathOf(folder.Entry), folder.Reason, folder.WorkaroundId)).ToList();
        rows.Sort((a, b) => ComparePaths(a.Path, b.Path));
        return rows;
    }

    // ---- A folder left out on purpose (SL-095): the one place its words are written (SC-021, SC-046) ----
    //
    // The list's row, the folder tree's note, the results list's title, the ending's note, the refusal and the coverage panel
    // all word the Windows container layer store through the functions below, so no surface can say another thing about it (the
    // fitness rule 'in the App, only UnreadableRows words the container layer store'). Each is keyed on what the record carries,
    // NotScannedReason (or ScanRefusal), with one arm per named reason and a neutral fallback that is true of any folder left
    // out on purpose, as KindText does for a failed one: a second precaution that leaves a folder out gets the fallback, never
    // the first one's wcifs words, until it has an arm of its own, and UnreadableRowsTests turns a forgotten arm red.

    /// <summary>
    /// The reason the two surfaces that carry only a flag stand for: the folder tree's note reads EntryFlags.NotScanned and the
    /// results list's title reads TopLevelState.NotScanned, and neither carries a NotScannedReason. Core has one reason today, so
    /// the flag means it. A second reason makes this wrong, and UnreadableRowsTests.NotScannedReasonHasOneMember fails first,
    /// naming those two surfaces, so that the reason is carried to them before the second arm is written.
    /// </summary>
    internal const NotScannedReason FlaggedReason = NotScannedReason.ContainerLayerStore;

    /// <summary>Why a folder was left out, in the list's words: its size is not counted, and on purpose.</summary>
    internal static string NotScannedText(NotScannedReason reason) => reason switch
    {
        NotScannedReason.ContainerLayerStore =>
            "size not counted: Windows container layers, skipped to avoid a leak in Windows' wcifs.sys driver; see Settings",
        _ => "size not counted: left out on purpose; see Settings",
    };

    /// <summary>The same in the results list's title, short: after the folder's name, in brackets.</summary>
    internal static string NotScannedShortText(NotScannedReason reason) => reason switch
    {
        NotScannedReason.ContainerLayerStore => "size not counted: Windows container layers",
        _ => "size not counted: left out on purpose",
    };

    /// <summary>
    /// The ending's note for a folder left out on purpose: where it was, why, and, when the precaution that left it out is known,
    /// the outside defect it avoids and the checkbox that lifts it. <paramref name="why"/> is that precaution (null for an id the
    /// list does not hold, which drops the second sentence).
    /// </summary>
    internal static string NotScannedNote(NotScannedReason reason, string path, KnownWorkaround? why) => reason switch
    {
        NotScannedReason.ContainerLayerStore => $"Not scanned, size not counted: {path} (Windows container layers)." + (why is null ? "" :
            $" Skipped to avoid a leak in {why.WhereItLives} {why.Defect} The Settings checkbox '{why.Title}' left it out; " +
            "it can be turned off in Settings."),
        _ => $"Not scanned, size not counted: {path} (left out on purpose)." + (why is null ? "" :
            $" The Settings checkbox '{why.Title}' left it out; it can be turned off in Settings."),
    };

    /// <summary>
    /// The ending's text for a scan that was refused before anything was opened: why, and, when the precaution that refused it is
    /// known, the outside defect, where it lives, and the checkbox that lifts the refusal.
    /// </summary>
    internal static string RefusedText(ScanRefusal refusal, KnownWorkaround? why) => refusal switch
    {
        ScanRefusal.ContainerLayerStore =>
            "Windows container layers can't be scanned safely: the wcifs filter leaks kernel memory and a System handle " +
            "for each entry read, until a full restart. This folder is in the Windows container layer store, so nothing " +
            "in it was read." + (why is null ? "" :
            $"\n{why.Defect}\nWhere it lives: {why.WhereItLives}\nWhile the Settings checkbox '{why.Title}' is ticked, as " +
            "it is at every start, a folder at or inside the store is not scanned. You can turn this protection off in Settings."),
        _ => "This folder was not scanned, on purpose." + (why is null ? "" :
            $"\nWhile the Settings checkbox '{why.Title}' is ticked, as it is at every start, it is not scanned. " +
            "You can turn this protection off in Settings."),
    };

    /// <summary>
    /// What the coverage panel says of a folder left out on purpose, in the words of the space it leaves in the unseen part:
    /// named as left out, with no size, and included in the remainder, which is not a measured figure.
    /// </summary>
    internal static string NotScannedInRemainder(NotScannedReason reason) => reason switch
    {
        NotScannedReason.ContainerLayerStore => "It includes the Windows container layer store, which this scan left out unopened.",
        _ => "It includes a folder this scan left out on purpose.",
    };

    /// <summary>
    /// What the engine recorded of the folder, in words. Read from the kind, never worked out again, and the one place
    /// these words are written: the list, the folder tree's note and the top-level row's title all word a failed folder
    /// through here, so they cannot disagree about it (SL-106). One arm per named kind. A kind with no arm, and no kind
    /// at all, get the plain 'could not be read', which is true of every failed folder: this runs in the results list's
    /// converter while a scan is on, where a throw would end the app, and a forgotten arm must not word a folder as one
    /// whose open was refused. UnreadableRowsTests turns a forgotten arm red.
    /// </summary>
    internal static string KindText(UnreadableKind? kind) => kind switch
    {
        UnreadableKind.NothingListed => "could not be opened",
        UnreadableKind.OpenedNothingListed => "opened, but nothing could be listed",
        UnreadableKind.ListedPartway => "only partly read",
        _ => "could not be read",
    };

    /// <summary>
    /// Why the folder could not be read, never empty. The common codes get a short phrase with no number: access
    /// denied, in use by another program, network or share error. Any other code gets Windows' own message, which
    /// the runtime looks up in the user's language, on one line, and then the code in decimal, added here so that the
    /// line names its code whatever Windows says or does not say about it.
    /// </summary>
    public static string Reason(int win32Error) => Reason(win32Error, SystemMessage);

    /// <summary><see cref="Reason(int)"/> with the system's message lookup given, so a test can make it return nothing.</summary>
    internal static string Reason(int win32Error, Func<int, string?> systemMessage)
    {
        if (PlainPhrase(win32Error) is { } phrase)
            return phrase;
        string message = OneLine(systemMessage(win32Error));
        return message.Length == 0
            ? $"no description from Windows (Windows error {win32Error})"
            : $"{message} (Windows error {win32Error})";
    }

    /// <summary>The plain phrase for a code in one of the three families, or null for any other code.</summary>
    internal static string? PlainPhrase(int win32Error) => win32Error switch
    {
        ERROR_ACCESS_DENIED or ERROR_NETWORK_ACCESS_DENIED => AccessDenied,
        ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => InUse,
        ERROR_REM_NOT_LIST or ERROR_BAD_NETPATH or ERROR_NETWORK_BUSY or ERROR_DEV_NOT_EXIST or ERROR_BAD_NET_RESP
            or ERROR_UNEXP_NET_ERR or ERROR_NETNAME_DELETED or ERROR_BAD_NET_NAME or ERROR_SHARING_PAUSED
            or ERROR_REQ_NOT_ACCEP or ERROR_REDIR_PAUSED or ERROR_NO_NET_OR_BAD_PATH or ERROR_SESSION_CREDENTIAL_CONFLICT
            or ERROR_NO_NETWORK or ERROR_NETWORK_UNREACHABLE or ERROR_HOST_UNREACHABLE or ERROR_CONNECTION_ABORTED
            or ERROR_NOT_CONNECTED => NetworkOrShare,
        _ => null,
    };

    /// <summary>
    /// The list's order: character by character, ignoring case, with the path separator below every other character,
    /// so a folder comes straight before everything under it and a sibling named 'a b' or 'a.b' cannot come between.
    /// A path comes before the longer paths it begins. Two names equal but for case, as a case-sensitive folder can
    /// hold, are ordered ordinally where the name ends, one path component at a time, so each keeps what is under it
    /// directly below it (review R2-m2); no two different paths compare equal. Literal, not Explorer's numeric order:
    /// 'd10' comes before 'd2'.
    /// </summary>
    internal static int ComparePaths(string a, string b)
    {
        int shorter = Math.Min(a.Length, b.Length);
        int caseTie = 0; // the first ordinal difference in the current component, where case alone differs so far
        for (int i = 0; i < shorter; i++)
        {
            int x = Rank(a[i]), y = Rank(b[i]);
            if (x != y)
                return x < y ? -1 : 1;
            if (x == SeparatorRank)
            {
                if (caseTie != 0)
                    return caseTie; // both names end here, equal but for case
            }
            else if (caseTie == 0 && a[i] != b[i])
            {
                caseTie = a[i] < b[i] ? -1 : 1;
            }
        }
        if (a.Length == b.Length)
            return caseTie != 0 ? caseTie : string.CompareOrdinal(a, b);
        // One begins the other. Where the shorter one's last name also ends in the longer, the names are compared as above.
        string longer = a.Length > b.Length ? a : b;
        return caseTie != 0 && Rank(longer[shorter]) == SeparatorRank ? caseTie : a.Length.CompareTo(b.Length);
    }

    private const int SeparatorRank = -1;

    private static int Rank(char c) => c is '\\' or '/' ? SeparatorRank : char.ToUpperInvariant(c);

    // The runtime's lookup of a Win32 code's message, the one it gives an exception, in the user's language.
    private static string SystemMessage(int win32Error) => new Win32Exception(win32Error).Message;

    // A message on one line: each line break, with the spaces around it, becomes one space (review m5).
    private static string OneLine(string? message) =>
        message is null ? "" : LineBreak().Replace(message, " ").Trim();

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreak();
}
