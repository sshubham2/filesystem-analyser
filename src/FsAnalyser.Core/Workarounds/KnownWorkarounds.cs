using System.Collections.Frozen;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Workarounds;

/// <summary>
/// One precaution the app takes against a defect that is not this project's: in Windows, a driver, a runtime or a
/// service the app depends on. Each is one checkbox in the window's Settings, on at every launch until a person turns it
/// off, and the window words it from these fields: what the outside defect is and where it lives, what the app
/// does to avoid it, and what turning it off brings back. The endings' own words for a folder a precaution left out are
/// written once, in the App's UnreadableRows, from these fields and the record of what was left out.
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
    /// wcifs.sys keeps kernel memory and a System handle for each live container layer entry a scan touches, until a full
    /// restart, and touching the same entries again was measured adding more (microsoft/Windows-Containers#646, measured
    /// on this PC). With it on, a root at or inside the layer store is refused and scans of a folder above the store leave
    /// the store out; with it off, both are lifted (Q-186). Only <see cref="ContainerLayerStore"/> acts on it.
    /// </summary>
    public static KnownWorkaround WcifsLayerStore { get; } = new(
        Id: "wcifs-layer-store",
        Title: "Skip Windows container layers in scans",
        Defect: "Windows keeps a little kernel memory and a handle in the System process for each entry of a live Windows "
            + "container layer that a program reads, and gives them back only at a full restart (Start > Restart). One layer "
            + "holds about 210,000 entries, and reading them again can keep more. This is a defect in Windows 11 25H2, not in "
            + "this app (microsoft/Windows-Containers#646).",
        WhereItLives: "wcifs.sys, the Windows Container Isolation filter driver that Microsoft ships with Windows (10.0.26100.x "
            + "on Windows 11 25H2). No fixed version has shipped.",
        Precaution: "A scan of a folder that holds the Windows container layer store, " + ContainerLayerStore.StoreDisplayPath
            + ", leaves the store out: it is shown as one folder whose size is not counted, and nothing in it is opened. A "
            + "folder picked at or inside the store is not scanned.",
        IfTurnedOff: "The store is read like any other folder: a scan of a folder that holds it counts it, and a folder picked "
            + "at or inside it is scanned as picked. wcifs.sys then leaks kernel memory (about 1.5 KB) and a System handle for "
            + "each store entry the scan touches, until you restart Windows, and every later scan of it can add more.",
        DefaultOn: true);

    /// <summary>All of them, in the order the Settings window lists them.</summary>
    public static IReadOnlyList<KnownWorkaround> All { get; } = [WcifsLayerStore];

    /// <summary>The ids of every entry that is on by default: what a scan takes when nobody turned anything off.</summary>
    public static IReadOnlySet<string> DefaultEnabled { get; } = All.Where(w => w.DefaultOn).Select(w => w.Id).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The entry with <paramref name="id"/>, or null.</summary>
    public static KnownWorkaround? Find(string? id) => All.FirstOrDefault(w => w.Id == id);
}
