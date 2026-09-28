using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A real folder tree under %TEMP%\fsa-tests\&lt;guid&gt;, on NTFS and not compressed. Every step that fails throws with
/// Windows' own error text; nothing is skipped. Dispose removes deny ACEs first, then links, then the
/// trees, and throws if anything was left behind.
/// </summary>
internal sealed class TempTree : IDisposable
{
    public const long MiB = 1024 * 1024;

    private static readonly string TestsRoot = Path.Combine(Path.GetTempPath(), "fsa-tests");
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly List<(string Path, bool IsDirectory, string Sddl)> _denied = [];
    private readonly List<(string Path, bool IsDirectory)> _links = [];
    private readonly List<string> _outside = [];

    public TempTree()
    {
        string temp = Path.GetTempPath();
        string format = new DriveInfo(Path.GetPathRoot(temp)!).DriveFormat;
        if (format != "NTFS")
            throw new InvalidOperationException($"%TEMP% ({temp}) is on {format}. The counting-rule tests need NTFS.");
        Root = Path.Combine(TestsRoot, _id);
        CreateRoot(Root);
    }

    // A folder made under an NTFS-compressed one is compressed too, and there TextFile compresses with no Compress():
    // every on-disk and probe count would shift with no cause named (HF-027). Refused by name, and removed again. The
    // refusal names the folder the root was made in, which carries the attribute (fsa-tests, made compressed under a
    // compressed %TEMP% and left so), and the one remedy measured to clear it and all it holds: compact /u /s:"folder".
    // Plain compact /u, or /u /s "folder", clears that folder's own attribute only (SL-073 review, R2-m2).
    internal static void CreateRoot(string root)
    {
        Directory.CreateDirectory(root);
        if (System.IO.File.GetAttributes(root).HasFlag(FileAttributes.Compressed))
        {
            Directory.Delete(root);
            string parent = Path.GetDirectoryName(root)!;
            throw new InvalidOperationException(
                $"{root} came out NTFS-compressed and was removed: {parent} is NTFS-compressed, so every tree made in it is too, " +
                "and the counting-rule tests need files that take their own size on disk. Uncompress it and all it holds with " +
                $"compact /u /s:\"{parent}\", or point TEMP and TMP at a folder that is not compressed.");
        }
    }

    /// <summary>The tree's root folder, as a person would write it.</summary>
    public string Root { get; }

    /// <summary>The root in extended form, for names Win32 would otherwise rewrite.</summary>
    public string ExtendedRoot => @"\\?\" + Root;

    public string PathOf(string relative) => relative.Length == 0 ? Root : Path.Combine(Root, relative);

    /// <summary>A folder beside the tree, outside its root, removed with it.</summary>
    public string Outside(string name)
    {
        string path = Path.Combine(TestsRoot, $"{_id}-{name}");
        Directory.CreateDirectory(path);
        _outside.Add(path);
        return path;
    }

    public string Folder(string relative)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A file of <paramref name="length"/> bytes written for real (not sparse).</summary>
    public string File(string relative, long length = 0) => WriteFile(PathOf(relative), length);

    public static string WriteFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        Write(stream, length);
        return path;
    }

    /// <summary>Appends <paramref name="length"/> bytes through this name.</summary>
    public static void Append(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        Write(stream, length);
    }

    /// <summary>A file of repetitive text, which compresses well.</summary>
    public string TextFile(string relative, long length)
    {
        string path = PathOf(relative);
        byte[] line = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. 0123456789\r\n");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        for (long written = 0; written < length; written += line.Length)
            stream.Write(line, 0, (int)Math.Min(line.Length, length - written));
        return path;
    }

    /// <summary>A sparse file of <paramref name="length"/> bytes with only <paramref name="writeBytes"/> written.</summary>
    public string SparseFile(string relative, long length, long writeOffset = 0, int writeBytes = 0)
    {
        string path = PathOf(relative);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        TestNative.SetSparse(stream.SafeFileHandle, path);
        stream.SetLength(length);
        if (writeBytes > 0)
        {
            stream.Position = writeOffset;
            stream.Write(Pattern(writeBytes));
            // NTFS allocates a sparse file's clusters when the cached write is flushed; until then the
            // file itself (not only its listing) reports nothing allocated.
            stream.Flush(flushToDisk: true);
        }
        return path;
    }

    /// <summary>NTFS (LZNT1) compression.</summary>
    public static void Compress(string path) => Run("compact.exe", "/c", path);

    /// <summary>WOF compression, which hides its reparse point from the listing.</summary>
    public static void WofCompress(string path) => Run("compact.exe", "/c", "/exe:xpress4k", path);

    public static void AddAttributes(string path, FileAttributes attributes) =>
        System.IO.File.SetAttributes(path, System.IO.File.GetAttributes(path) | attributes);

    public string Junction(string relativeLink, string target)
    {
        string link = PathOf(relativeLink);
        Run("cmd.exe", "/c", "mklink", "/J", link, target);
        _links.Add((link, true));
        return link;
    }

    public string DirectorySymlink(string relativeLink, string target)
    {
        string link = PathOf(relativeLink);
        CreateSymlink(link, () => Directory.CreateSymbolicLink(link, target));
        _links.Add((link, true));
        return link;
    }

    public string FileSymlink(string relativeLink, string target)
    {
        string link = PathOf(relativeLink);
        CreateSymlink(link, () => System.IO.File.CreateSymbolicLink(link, target));
        _links.Add((link, false));
        return link;
    }

    // .NET already asks for unprivileged creation, which works only under Developer Mode, so error 1314
    // has no workaround here: it means Developer Mode is off or blocked by policy. The test fails
    // saying so and is never skipped (Q-001).
    private static void CreateSymlink(string link, Action create)
    {
        const int ErrorPrivilegeNotHeld = 1314;
        try
        {
            create();
        }
        catch (Exception e) when ((e.HResult & 0xFFFF) == ErrorPrivilegeNotHeld)
        {
            throw new InvalidOperationException(
                $"Windows refused to create the symbolic link {link} (error 1314, a required privilege is not held). " +
                "Developer Mode is off or blocked by policy on this machine; the link tests need it and are not skipped.", e);
        }
    }

    /// <summary>A second name for an existing file.</summary>
    public static string Hardlink(string newName, string existing)
    {
        Run("cmd.exe", "/c", "mklink", "/H", newName, existing);
        return newName;
    }

    /// <summary>
    /// Adds an explicit deny ACE for the current user with a raw access mask. SDDL is used because
    /// FileSystemAccessRule silently drops SYNCHRONIZE from deny rules.
    /// </summary>
    public void Deny(string path, uint accessMask)
    {
        bool isDirectory = Directory.Exists(path);
        FileSystemSecurity security = GetSecurity(path, isDirectory);
        string original = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string ace = $"(D;;0x{accessMask:X};;;{sid})";
        int firstAce = original.IndexOf('(');
        string denied = firstAce < 0 ? original + ace : original.Insert(firstAce, ace);

        security.SetSecurityDescriptorSddlForm(denied, AccessControlSections.Access);
        SetSecurity(path, isDirectory, security);
        _denied.Add((path, isDirectory, original));
    }

    public void Dispose()
    {
        var failures = new List<string>();

        for (int i = _denied.Count - 1; i >= 0; i--)
        {
            (string path, bool isDirectory, string sddl) = _denied[i];
            Attempt(failures, $"restore the ACL of {path}", () =>
            {
                FileSystemSecurity security = GetSecurity(path, isDirectory);
                security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
                SetSecurity(path, isDirectory, security);
            });
        }

        foreach ((string link, bool isDirectory) in _links)
        {
            Attempt(failures, $"remove the link {link}", () =>
            {
                if (isDirectory && Directory.Exists(link))
                    Directory.Delete(link);
                else if (!isDirectory && System.IO.File.Exists(link))
                    System.IO.File.Delete(link);
            });
        }

        foreach (string tree in _outside.Prepend(Root))
        {
            Attempt(failures, $"delete {tree}", () =>
            {
                string extended = @"\\?\" + tree;
                if (Directory.Exists(extended))
                    Directory.Delete(extended, recursive: true);
            });
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("The temp tree was not fully removed: " + string.Join("; ", failures));
    }

    private static void Attempt(List<string> failures, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            failures.Add($"could not {what}: {e.Message}");
        }
    }

    private static FileSystemSecurity GetSecurity(string path, bool isDirectory) =>
        isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    private static void SetSecurity(string path, bool isDirectory, FileSystemSecurity security)
    {
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)security);
    }

    /// <summary>Writes <paramref name="length"/> incompressible bytes at the stream's position.</summary>
    public static void Write(FileStream stream, long length)
    {
        byte[] block = Pattern((int)Math.Min(length, MiB));
        for (long written = 0; written < length; written += block.Length)
            stream.Write(block, 0, (int)Math.Min(block.Length, length - written));
    }

    // Bytes that do not compress, so a plain file's allocation is its length rounded up to clusters.
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        new Random(20).NextBytes(bytes);
        return bytes;
    }

    private static void Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {exe}.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {string.Join(' ', args)} exited {process.ExitCode}: {output.Trim()}");
    }
}
