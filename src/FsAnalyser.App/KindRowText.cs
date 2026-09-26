using System.Globalization;
using FsAnalyser.Core.Analysis;

namespace FsAnalyser.App;

/// <summary>One row of the file kinds list: the texts it shows, and the kind they were written from.</summary>
internal sealed record KindRow(KindTotal Total, string Label, string Files, string Size, string OnDisk, string Share,
    string Extensions, string? Tip);

/// <summary>
/// The words of the file kinds list (SL-046): each kind Core found, as Core ordered them, with its share of the space
/// on disk and its biggest extensions. Only words: which kind a file is, and every figure, come from Core.
/// </summary>
/// <remarks>
/// No WPF and no window: it is also compiled into Core.Tests, which has neither, so the list's edge cases (nothing on
/// disk, no extension, extensions left over) are tested there. Sizes are written by the formatter it is handed, the
/// window's own, so they read as the rest of the window's do.
/// </remarks>
internal static class KindRowText
{
    /// <summary>What the Installers and Programs rows say when pointed at: which of the two a setup program is.</summary>
    internal const string InstallersTip =
        "Installers are .msi/.msix/.appx packages. Setup programs that ship as .exe are counted under Programs.";

    /// <summary>The rows for <paramref name="kinds"/>, in Core's order; none when it holds no file.</summary>
    internal static KindRow[] Rows(KindBreakdownResult kinds, Func<long, string> size) =>
        kinds.Kinds.Select(k => new KindRow(k, k.Label, k.Files.ToString("N0", CultureInfo.CurrentCulture), size(k.Size),
            size(k.OnDisk), Share(k.OnDisk, kinds.OnDisk), Extensions(k, size), Tip(k.Kind))).ToArray();

    /// <summary>
    /// The row's share of everything on disk, as a percentage to one decimal place, a midpoint rounded away from zero,
    /// as the folder tree writes its shares. "—" when nothing takes space on disk (files that live only online): there
    /// is no whole to take a share of, and the rows are still listed.
    /// </summary>
    internal static string Share(long onDisk, long total) =>
        total == 0 ? "—"
        : Math.Round(100.0 * onDisk / total, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.CurrentCulture) + " %";

    /// <summary>
    /// The kind's biggest extensions with their space on disk, then what they leave out, so that no bytes of the row go
    /// unaccounted for: ".mp4 40.1 GB · .mkv 6.2 GB · + 12 more, 3.4 GB". A dotfile's extension is its whole name
    /// (".gitignore"). "—" for the files with no extension, which have none to list.
    /// </summary>
    internal static string Extensions(KindTotal kind, Func<long, string> size)
    {
        if (kind.TopExtensions.Count == 0 && kind.Remainder.Files == 0)
            return "—";
        var parts = kind.TopExtensions.Select(e => $"{e.Ext} {size(e.OnDisk)}").ToList();
        ExtensionRemainder rest = kind.Remainder;
        if (rest.Files > 0)
        {
            string count = rest.Extensions.ToString("N0", CultureInfo.CurrentCulture);
            parts.Add(rest.MoreUncounted ? $"+ more than {count} others, {size(rest.OnDisk)}" : $"+ {count} more, {size(rest.OnDisk)}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>What the row says when pointed at, or null when it says nothing more than its words.</summary>
    internal static string? Tip(FileKind kind) =>
        kind is FileKind.Installers or FileKind.Programs ? InstallersTip : null;
}
