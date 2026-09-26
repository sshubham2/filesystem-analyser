using System.Collections.Frozen;

namespace FsAnalyser.Core.Analysis;

/// <summary>
/// The kinds of file the breakdown sorts files into (SL-046), in a fixed order that breaks ties between rows.
/// A file's kind comes from its extension alone; see <see cref="KindBreakdown"/>.
/// </summary>
public enum FileKind
{
    Videos,
    Images,
    Audio,
    Documents,
    Archives,
    DiskImages,

    /// <summary>Package formats only (.msi, .msix, .appx and the like). A setup program that ships as .exe is a
    /// <see cref="Programs"/> file: its name does not say what it is (spike installers-identifiable-by-name-alone,
    /// no-go: a name rule found 6.5% of the installer bytes in a real Downloads folder).</summary>
    Installers,

    /// <summary>Programs and system files: .exe, .dll, .sys and the like, pagefile.sys and hiberfil.sys included.</summary>
    Programs,

    /// <summary>A name with no extension, or one that ends in a dot.</summary>
    NoExtension,

    /// <summary>Every extension the table does not name, a dotfile's (.gitignore) included.</summary>
    Other,
}

/// <summary>
/// The fixed table from extension to kind. Internal, so that nothing outside Core classifies a file on its own:
/// the window shows the <see cref="KindTotal.Id"/> and <see cref="KindTotal.Label"/> Core hands it.
/// </summary>
/// <remarks>
/// The spike extension-map-covers-real-drive-bytes measured the first version of this table on a real C:\ and
/// left half its on-disk bytes in Other (no-go), with no extension at 17.6% and .aax at 9.0%. Rather than grow the
/// table until one drive passes, no extension is a row of its own, aax, aa and m4b are audio, and every row lists
/// its biggest extensions, Other's included. ts is not a video: TypeScript sources outnumber MPEG transport
/// streams on a developer's drive.
/// </remarks>
internal static class FileKinds
{
    // Extensions as Path.GetExtension gives them, with the dot. Case is ignored.
    private static readonly FrozenDictionary<string, FileKind> Table = Build(
        (FileKind.Videos, "mp4 m4v mkv avi mov wmv webm flv mpg mpeg m2ts mts 3gp"),
        (FileKind.Images, "jpg jpeg png gif bmp webp heic heif tif tiff svg ico psd raw cr2 cr3 nef arw dng avif"),
        (FileKind.Audio, "mp3 flac wav aac m4a ogg opus wma aiff aax aa m4b"),
        (FileKind.Documents, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub"),
        (FileKind.Archives, "zip 7z rar tar gz tgz bz2 xz zst cab"),
        (FileKind.DiskImages, "iso img vhd vhdx vmdk qcow2 wim esd"),
        (FileKind.Installers, "msi msp msu msix msixbundle appx appxbundle"),
        (FileKind.Programs, "exe dll sys ocx drv mui"));

    private static readonly FrozenDictionary<string, FileKind>.AlternateLookup<ReadOnlySpan<char>> ByExtension =
        Table.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Every extension the table names, with its dot, and its kind.</summary>
    internal static IEnumerable<KeyValuePair<string, FileKind>> Mapped => Table;

    /// <summary>
    /// The kind of a file with this extension, as <see cref="Path.GetExtension(ReadOnlySpan{char})"/> gives it
    /// from the name: empty is <see cref="FileKind.NoExtension"/>, and anything the table does not name is
    /// <see cref="FileKind.Other"/>. Case is ignored. Never throws.
    /// </summary>
    internal static FileKind Of(ReadOnlySpan<char> extension) =>
        extension.IsEmpty ? FileKind.NoExtension
        : ByExtension.TryGetValue(extension, out FileKind kind) ? kind
        : FileKind.Other;

    /// <summary>The kind's stable id, as the window's report writes it.</summary>
    internal static string Id(FileKind kind) => kind switch
    {
        FileKind.Videos => "videos",
        FileKind.Images => "images",
        FileKind.Audio => "audio",
        FileKind.Documents => "documents",
        FileKind.Archives => "archives",
        FileKind.DiskImages => "disk-images",
        FileKind.Installers => "installers",
        FileKind.Programs => "programs",
        FileKind.NoExtension => "no-extension",
        FileKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The kind as the window names it.</summary>
    internal static string Label(FileKind kind) => kind switch
    {
        FileKind.Videos => "Videos",
        FileKind.Images => "Images",
        FileKind.Audio => "Audio",
        FileKind.Documents => "Documents",
        FileKind.Archives => "Archives",
        FileKind.DiskImages => "Disk images",
        FileKind.Installers => "Installers",
        FileKind.Programs => "Programs & system files",
        FileKind.NoExtension => "No extension",
        FileKind.Other => "Other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static FrozenDictionary<string, FileKind> Build(params (FileKind Kind, string Extensions)[] rows)
    {
        var table = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach ((FileKind kind, string extensions) in rows)
        {
            foreach (string extension in extensions.Split(' '))
                table.Add("." + extension, kind); // throws on an extension listed twice
        }
        return table.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
