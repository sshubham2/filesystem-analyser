using FsAnalyser.Core.Native;
using FsAnalyser.Core.Workarounds;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Scanning;

/// <summary>What a scan does about the Windows container layer store, decided once, before anything is listed.</summary>
internal enum StoreVerdictKind
{
    /// <summary>The store is not below the root, or this PC has none: the scan is as it always was.</summary>
    None,

    /// <summary>The store lies below the root: the walk reaches it, records it as not scanned, and never opens it.</summary>
    Exclude,

    /// <summary>The root is the store or a folder inside it: nothing is listed or probed (Q-179, Q-182).</summary>
    Refuse,
}

/// <summary>A <see cref="StoreVerdictKind"/>, with the store's path in the walk's own spelling for Exclude.</summary>
internal readonly record struct StoreVerdict(StoreVerdictKind Kind, string? Excluded)
{
    public static StoreVerdict None { get; } = new(StoreVerdictKind.None, null);

    public static StoreVerdict Refuse { get; } = new(StoreVerdictKind.Refuse, null);

    public static StoreVerdict Exclude(string win32Path) => new(StoreVerdictKind.Exclude, win32Path);
}

/// <summary>
/// The Windows container layer store, %ProgramData%\Microsoft\Windows\Containers\Layers, and the one place in src that
/// spells where it is (SL-095). A live layer's Files tree is a tree of WCI reparse points that wcifs.sys, Microsoft's
/// container isolation minifilter, resolves on the first open of each entry since a restart, and on Windows 11 25H2
/// (wcifs.sys 10.0.26100.x) it never frees what it allocates there: a stream context (WCsc), a file context (WCfc), a
/// reparse buffer (WCri), a work item (FMwi), a file-list control (FMfc), and a handle in the System process
/// (microsoft/Windows-Containers#646, measured on this PC). Only a full restart gives them back. So a root at or inside
/// the store is refused (Q-179, Q-182), always; and while the precaution 'wcifs-layer-store' is on, as it is unless a
/// person turns it off in Settings, a scan that reaches the store below its root records it as one folder whose size is
/// not counted and never opens it (Q-180, Q-181). This file is the one place that reads that switch, and it decides
/// only the exclusion: Q-182 refuses a root inside the store with no user override (review F1).
/// </summary>
/// <remarks>
/// The store is matched by its path, never by an entry's attributes or reparse tag: the live layer's folders carry no
/// ReparsePoint or Offline attribute (measured), so a rule on attributes walks straight in. The whole store is left out,
/// not only Layers\&lt;id&gt;\Files: its other children (Hives, Bindings, Specialization, dotnet.bcf) have not been shown
/// to be safe to list (Q-180, Q-181).
/// <para>Resolution is two steps. The first compares the root and the store as strings, with no open, in \\?\ form and
/// without case: a root the picker, a drive button or the command line gives, in any of \\?\, a trailing separator or
/// another case, is decided here. Only when that decides nothing are two paths opened for their final path, on a guard
/// of the resolution's own: the root, and the store's anchor, %ProgramData% (the planted store's parent under the test
/// override), never the store itself. That catches a subst drive, a junction or a symbolic link, an 8.3 name and a
/// \\?\Volume{GUID}\ root, on either side, and maps the store back into the root's own spelling, which is how the walk
/// spells every folder it pops. Its worst case, a root that is a link into an untouched layer entry, is that one entry
/// opened for its attributes, which #646's own negative control says does not leak.</para>
/// <para>The walk compares each folder it pops with the one excluded path for equality, without case and as listed:
/// never through GetFullPath, which would fold a sibling named 'Layers.' or 'Layers ' onto the store (review m2).
/// Nothing below the store can be reached except through it, because links are never followed.</para>
/// <para>Not covered, and not looked for: the same store reached over a loopback share (\\localhost\C$), and other WCI
/// stores such as ProgramData\Docker\windowsfilter.</para>
/// </remarks>
internal static class ContainerLayerStore
{
    private const string LayerStoreWords = @"%ProgramData%\Microsoft\Windows\Containers\Layers";

    /// <summary>
    /// The store as every notice names it: this PC's own path, or its %ProgramData% form when the known folder resolves
    /// to nothing usable. Notices take it from here and never spell the path themselves (architecture rule).
    /// </summary>
    public static string StoreDisplayPath => Locate(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) ?? LayerStoreWords;

    /// <summary>The precaution that governs the exclusion, as a scan's result names it for the window.</summary>
    internal static string WorkaroundId => KnownWorkarounds.WcifsLayerStore.Id;

    /// <summary>The store under <paramref name="programData"/>, or null when the known folder resolved to nothing usable.</summary>
    /// <remarks>GetFolderPath answers "" when it fails, and an empty prefix would put every path inside the store.</remarks>
    internal static string? Locate(string? programData) =>
        string.IsNullOrWhiteSpace(programData) || !IsAbsolute(programData)
            ? null
            : Path.Join(programData, "Microsoft", "Windows", "Containers", "Layers");

    /// <summary>
    /// Decides, once per scan and before anything is listed, whether the store is below <paramref name="rootWin32"/>,
    /// is it or holds it, or neither. Never throws: a root that cannot be opened, or a final path Windows will not give,
    /// leaves the string verdict standing, which is None, and the scan goes on to its usual handling of the root.
    /// </summary>
    /// <param name="rootWin32">The root as the scan spells it (<see cref="LongPath.NormalizeRoot"/>'s Win32 form).</param>
    /// <param name="storeOverride">Tests only: the store's path. Null for this PC's own; empty for a PC with none.</param>
    /// <param name="ct">The scan's token: Cancel interrupts the resolution's opens as it does the walk's.</param>
    /// <param name="enabledWorkarounds">The precautions on for this scan; null for the defaults. Without
    /// 'wcifs-layer-store' an Exclude becomes None, so the store is walked like any folder and the reader has no
    /// rule; a Refuse stays a Refuse (Q-182, review F1).</param>
    internal static StoreVerdict Resolve(string rootWin32, string? storeOverride, CancellationToken ct, IReadOnlySet<string>? enabledWorkarounds = null)
    {
        StoreVerdict verdict = Decide(rootWin32, storeOverride, ct);
        if (verdict.Kind == StoreVerdictKind.Exclude && !(enabledWorkarounds ?? KnownWorkarounds.DefaultEnabled).Contains(WorkaroundId))
            return StoreVerdict.None;
        return verdict;
    }

    private static StoreVerdict Decide(string rootWin32, string? storeOverride, CancellationToken ct)
    {
        string? programData = storeOverride is null ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) : null;
        string? store = storeOverride is null ? Locate(programData)
            : string.IsNullOrWhiteSpace(storeOverride) || !IsAbsolute(storeOverride) ? null
            : storeOverride;
        if (store is null)
            return StoreVerdict.None;

        string storeWin32 = LongPath.NormalizeRoot(store).Win32;
        if (Scanner.IsAtOrUnder(rootWin32, storeWin32))
            return StoreVerdict.Refuse;
        if (Scanner.IsAtOrUnder(storeWin32, rootWin32))
            return StoreVerdict.Exclude(storeWin32);

        // By handle: the anchor is an ancestor of the store, never the store, so nothing in it is opened (review Y5).
        string anchorWin32 = programData is not null ? LongPath.NormalizeRoot(programData).Win32 : ParentOf(storeWin32);
        using var io = new SynchronousIoCancel(ct, announce: false);
        string? finalRoot = FinalPath(io, rootWin32);
        string? finalAnchor = finalRoot is null ? null : FinalPath(io, anchorWin32);
        if (finalRoot is null || finalAnchor is null)
            return StoreVerdict.None;

        string finalStore = finalAnchor.TrimEnd('\\') + storeWin32[anchorWin32.TrimEnd('\\').Length..];
        if (Scanner.IsAtOrUnder(finalRoot, finalStore))
            return StoreVerdict.Refuse;
        if (Scanner.IsAtOrUnder(finalStore, finalRoot))
            return StoreVerdict.Exclude(LongPath.Child(rootWin32, finalStore[finalRoot.Length..].TrimStart('\\')));
        return StoreVerdict.None;
    }

    // The path a folder is reached by once links, subst drives, 8.3 names and volume GUIDs are resolved, or null when
    // it cannot be opened or Windows will not say.
    private static string? FinalPath(SynchronousIoCancel io, string win32Path)
    {
        if (io.OpenForFinalPath(win32Path, out SafeFileHandle? handle) != 0)
            return null;
        try
        {
            return io.QueryFinalPath(handle!, out string? path) == 0 ? path : null;
        }
        finally
        {
            io.Close(handle!);
        }
    }

    // A \\?\ path's parent folder; a drive's root keeps its separator (\\?\C: is the volume, \\?\C:\ its root folder).
    private static string ParentOf(string win32Path)
    {
        string parent = win32Path[..win32Path.LastIndexOf('\\')];
        return parent.EndsWith(':') ? parent + '\\' : parent;
    }

    private static bool IsAbsolute(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) || Path.IsPathFullyQualified(path);
}
