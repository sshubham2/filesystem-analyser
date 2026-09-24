using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A real folder tree under %TEMP%\fsa-tests\&lt;guid&gt;, on NTFS. Every step that fails throws with
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
        Directory.CreateDirectory(Root);
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
        Directory.CreateSymbolicLink(link, target);
        _links.Add((link, true));
        return link;
    }

    public string FileSymlink(string relativeLink, string target)
    {
        string link = PathOf(relativeLink);
        System.IO.File.CreateSymbolicLink(link, target);
        _links.Add((link, false));
        return link;
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

    private static void Write(FileStream stream, long length)
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
