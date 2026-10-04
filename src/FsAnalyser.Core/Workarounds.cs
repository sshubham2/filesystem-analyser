using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core;

/// <summary>
/// One precaution the app takes against a defect that is not this project's: in Windows, a driver, a runtime or a
/// service the app depends on. Each is one switch a person can turn off, on unless they do, and the window words it
/// from these fields: what the outside defect is and where it lives, what the app does to avoid it, and what turning it
/// off brings back.
/// </summary>
/// <param name="Id">A short, stable id, as the window report names it.</param>
/// <param name="Title">The switch's label.</param>
/// <param name="Defect">The outside defect, and where it lives.</param>
/// <param name="Precaution">What the app does while the switch is on.</param>
/// <param name="WhenOff">What happens with the switch off: the question asked before it is turned off.</param>
/// <param name="OnByDefault">Always true: a precaution is on until a person turns it off.</param>
public sealed record Workaround(string Id, string Title, string Defect, string Precaution, string WhenOff, bool OnByDefault);

/// <summary>
/// Every precaution against an outside defect, in one list. The next one found is a new entry here, guarded on one
/// path where it acts, and the window's Precautions list shows them all from this list.
/// </summary>
public static class Workarounds
{
    /// <summary>
    /// wcifs.sys keeps kernel memory and a System handle for every live container layer entry read for the first time
    /// since a restart (SL-095, microsoft/Windows-Containers#646): scans skip the layer store and refuse a root inside it.
    /// </summary>
    public static Workaround ContainerLayerStore => Scanning.ContainerLayerStore.Precaution;

    /// <summary>All of them, in the order the window lists them.</summary>
    public static IReadOnlyList<Workaround> All { get; } = [ContainerLayerStore];
}

/// <summary>
/// Which precautions are on, for one scan: every one in <see cref="Workarounds.All"/> unless turned off here. Immutable,
/// so a scan reads the switches it started with whatever the window does meanwhile.
/// </summary>
public sealed class WorkaroundSwitches
{
    private readonly HashSet<string> _off;

    private WorkaroundSwitches(HashSet<string> off) => _off = off;

    /// <summary>Every precaution on, as the app starts.</summary>
    public static WorkaroundSwitches Defaults { get; } = new([]);

    /// <summary>Whether the precaution <paramref name="id"/> is on. One that was never turned off is.</summary>
    public bool IsOn(string id) => !_off.Contains(id);

    /// <summary>These switches with <paramref name="id"/> turned on or off.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> names no precaution in <see cref="Workarounds.All"/>.</exception>
    public WorkaroundSwitches With(string id, bool on)
    {
        if (!Workarounds.All.Any(w => w.Id == id))
            throw new ArgumentException($"'{id}' is not a precaution this app takes.", nameof(id));
        var off = new HashSet<string>(_off);
        if (on)
            off.Remove(id);
        else
            off.Add(id);
        return new WorkaroundSwitches(off);
    }
}
