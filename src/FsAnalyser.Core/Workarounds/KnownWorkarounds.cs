using System.Collections.Frozen;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Workarounds;

/// <summary>
/// One precaution the app takes against a defect that is not this project's: in Windows, a driver, a runtime or a
/// service the app depends on. Each is one checkbox in the window's Settings, on at every launch until a person turns it
/// off, and the window words it from these fields alone: what the outside defect is and where it lives, what the app
/// does to avoid it, and what turning it off brings back.
/// </summary>
/// <param name="Id">A short, stable id, as the window report names it.</param>
/// <param name="Title">The checkbox's label.</param>
/// <param name="Defect">The outside defect, in plain words.</param>
/// <param name="WhereItLives">The component the defect is in, and who ships it.</param>
/// <param name="Precaution">What the app does while the checkbox is ticked.</param>
/// <param name="IfTurnedOff">What happens with it off: the consequence the confirmation asks about.</param>
/// <param name="DefaultOn">Always true: a precaution is on until a person turns it off.</param>
public sealed record KnownWorkaround(string Id, string Title, string Defect, string WhereItLives, string Precaution, string IfTurnedOff, bool DefaultOn);

/// <summary>
/// Every precaution against an outside defect, in one list (SL-095). The next one found is a new entry here, guarded on
/// one path where it acts; the window's Settings render the list as it is and name no entry, and a scan takes the ids
/// that are on in <see cref="ScanOptions.EnabledWorkarounds"/>.
/// </summary>
public static class KnownWorkarounds
{
    /// <summary>
    /// wcifs.sys keeps kernel memory and a System handle for every live container layer entry read for the first time
    /// since a restart (microsoft/Windows-Containers#646, measured on this PC): scans of a folder above the layer store
    /// leave the store out. Only <see cref="ContainerLayerStore"/> acts on it. A root at or inside the store is refused
    /// whatever this says (Q-182, review F1), so turning it off never lets a scan start inside the store.
    /// </summary>
    public static KnownWorkaround WcifsLayerStore { get; } = new(
        Id: "wcifs-layer-store",
        Title: "Skip Windows container layers in scans",
        Defect: "Windows keeps a little kernel memory and a handle in the System process for every entry of a live Windows "
            + "container layer that is read for the first time since the PC was restarted, and gives them back only at a full "
            + "restart (Start > Restart). One layer holds about 210,000 entries. This is a defect in Windows 11 25H2, not in "
            + "this app (microsoft/Windows-Containers#646).",
        WhereItLives: "wcifs.sys, the Windows Container Isolation filter driver that Microsoft ships with Windows (10.0.26100.x "
            + "on Windows 11 25H2). No fixed version has shipped.",
        Precaution: "A scan of a folder that holds the Windows container layer store, " + ContainerLayerStore.StoreDisplayPath
            + ", leaves the store out: it is shown as one folder whose size is not counted, and nothing in it is opened.",
        IfTurnedOff: "A scan of a folder that holds the store reads it like any other folder, and Windows then keeps kernel "
            + "memory (about 1.5 KB) and a System handle for every layer entry read for the first time since the last restart, "
            + "until you restart Windows. A folder picked at or inside the store is still not scanned: that does not depend "
            + "on this setting.",
        DefaultOn: true);

    /// <summary>All of them, in the order the Settings window lists them.</summary>
    public static IReadOnlyList<KnownWorkaround> All { get; } = [WcifsLayerStore];

    /// <summary>The ids of every entry that is on by default: what a scan takes when nobody turned anything off.</summary>
    public static IReadOnlySet<string> DefaultEnabled { get; } = All.Where(w => w.DefaultOn).Select(w => w.Id).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The entry with <paramref name="id"/>, or null.</summary>
    public static KnownWorkaround? Find(string? id) => All.FirstOrDefault(w => w.Id == id);
}
