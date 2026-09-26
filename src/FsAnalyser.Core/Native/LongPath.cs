namespace FsAnalyser.Core.Native;

/// <summary>
/// Path handling for the scanner. The root a person chose is normalised exactly once; every Win32 call
/// then takes an extended-length <c>\\?\</c> path (<c>\\?\UNC\</c> for shares), and child paths are plain
/// concatenation that is never normalised again. Normalising a child would strip trailing dots and
/// spaces, turn names such as NUL into devices, or hit MAX_PATH.
/// </summary>
public static class LongPath
{
    private const string Extended = @"\\?\";
    private const string ExtendedUnc = @"\\?\UNC\";
    private const string Device = @"\\.\";

    /// <summary>
    /// Turns the root a person chose into its display form (<c>C:\Users</c>, <c>\\server\share\x</c>) and
    /// the extended form every Win32 call takes. A bare drive such as <c>C:</c> means the drive, not the
    /// current directory on it. A root already in <c>\\?\</c> form is taken as-is, without normalising.
    /// </summary>
    /// <exception cref="ArgumentException">The root is empty or not an absolute path.</exception>
    public static (string Display, string Win32) NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A root folder is required.", nameof(root));

        string display;
        if (root.StartsWith(ExtendedUnc, StringComparison.OrdinalIgnoreCase))
        {
            display = @"\\" + root[ExtendedUnc.Length..];
        }
        else if (root.StartsWith(Extended, StringComparison.Ordinal))
        {
            display = root[Extended.Length..];
        }
        else
        {
            if (root.Length == 2 && char.IsAsciiLetter(root[0]) && root[1] == ':')
                root += '\\';
            if (!Path.IsPathFullyQualified(root))
                throw new ArgumentException($"'{root}' is not an absolute path. Pick a drive or a folder such as C:\\Users.", nameof(root));
            display = Path.GetFullPath(root);
        }

        display = TrimSeparators(display);
        return (display, ToExtended(display));
    }

    /// <summary>Maps an absolute path to its extended-length form. Pure string mapping.</summary>
    public static string ToExtended(string fullPath)
    {
        if (fullPath.StartsWith(Extended, StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(Device, StringComparison.Ordinal))
            return Extended + fullPath[Device.Length..];
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return ExtendedUnc + fullPath[2..];
        return Extended + fullPath;
    }

    /// <summary>The inverse of <see cref="ToExtended"/>, for showing a path to a person.</summary>
    public static string ToDisplay(string win32Path)
    {
        if (win32Path.StartsWith(ExtendedUnc, StringComparison.OrdinalIgnoreCase))
            return @"\\" + win32Path[ExtendedUnc.Length..];
        if (win32Path.StartsWith(Extended, StringComparison.Ordinal))
            return win32Path[Extended.Length..];
        return win32Path;
    }

    /// <summary>A child's path: the parent's path, one separator, the name exactly as listed.</summary>
    public static string Child(string parentPath, ReadOnlySpan<char> name) =>
        parentPath.EndsWith('\\')
            ? string.Concat(parentPath.AsSpan(), name)
            : string.Concat(parentPath.AsSpan(), @"\".AsSpan(), name);

    // A drive or share root keeps its trailing separator (\\?\C: is the volume, \\?\C:\ is its root
    // folder); any other folder loses it.
    private static string TrimSeparators(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string pathRoot = (Path.GetPathRoot(path) ?? string.Empty).TrimEnd('\\', '/');
        return trimmed.Length <= pathRoot.Length ? pathRoot + '\\' : trimmed;
    }
}
