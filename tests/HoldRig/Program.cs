using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HoldRig;

/// <summary>
/// SL-056's held-open rig, a process of its own. Two verbs:
/// <list type="bullet">
/// <item><c>hold &lt;path&gt; &lt;bytes&gt; &lt;none|read&gt;</c> creates the file (CREATE_NEW) with that share mode,
/// sets its own last-access time back 48 hours through its own handle, then grows it to <c>bytes</c> in 1 MiB writes
/// with no flush and no SetEndOfFile, and prints <c>READY</c>. Each <c>report</c> line on stdin is answered with one
/// JSON line: the file's metadata as its own handle reads it. It holds the handle until stdin reaches end of file.</item>
/// <item><c>snapshot &lt;manifest&gt; &lt;out.json&gt;</c> opens each path the manifest lists (one per line) for
/// FILE_READ_ATTRIBUTES only, reads its metadata and closes it, and writes them all as one JSON array. It never
/// lists a folder: a listing moves a folder's last-access time.</item>
/// </list>
/// Any failure goes to stderr as <c>ERR ...</c> with Windows' error, and the exit code is 2. Nothing here knows the
/// code it checks: it is the observer the scan is compared against.
/// </summary>
internal static class Program
{
    private const int ExitError = 2;
    private const long MiB = 1024 * 1024;

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;

    private static readonly StreamWriter Out = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["hold", string path, string bytes, "none" or "read"] when long.TryParse(bytes, out long length) && length >= 0 =>
                    Hold(path, length, args[3] == "none" ? FileShare.None : FileShare.Read),
                ["snapshot", string manifest, string output] => Snapshot(manifest, output),
                _ => Fail($"usage: hold <path> <bytes> <none|read> | snapshot <manifest> <out.json>; got: {string.Join(' ', args)}"),
            };
        }
        catch (Exception e)
        {
            return Fail($"{e.GetType().Name}: {e.Message}");
        }
    }

    private static int Hold(string path, long length, FileShare share)
    {
        // bufferSize 0: every Write is a WriteFile of its own, and nothing is flushed by FileStream behind our back.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, share, bufferSize: 0);
        // Back-dated through this handle before anything is written, so an access by another process would show,
        // and NTFS keeps a time set through a handle for the rest of that handle's life.
        File.SetLastAccessTimeUtc(stream.SafeFileHandle, DateTime.UtcNow.AddHours(-48));
        byte[] block = new byte[MiB];
        new Random(56).NextBytes(block);
        for (long written = 0; written < length; written += block.Length)
            stream.Write(block, 0, (int)Math.Min(block.Length, length - written));

        Out.WriteLine("READY");
        while (Console.In.ReadLine() is { } line)
        {
            if (line == "report")
                Out.WriteLine(JsonSerializer.Serialize(Read(stream.SafeFileHandle, path)));
            else
                Console.Error.WriteLine($"ignored: {line}");
        }
        return 0;
    }

    private static int Snapshot(string manifest, string output)
    {
        var records = new List<Metadata>();
        foreach (string path in File.ReadAllLines(manifest).Where(l => l.Length > 0))
        {
            using SafeFileHandle handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
            if (handle.IsInvalid)
                throw Win32(Marshal.GetLastPInvokeError(), $"open {path}");
            records.Add(Read(handle, path));
        }
        File.WriteAllText(output, JsonSerializer.Serialize(records));
        Out.WriteLine($"SNAPSHOT {records.Count}");
        return 0;
    }

    private static Metadata Read(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()))
            throw Win32(Marshal.GetLastPInvokeError(), $"FileBasicInfo of {path}");
        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out FILE_STANDARD_INFO standard, Marshal.SizeOf<FILE_STANDARD_INFO>()))
            throw Win32(Marshal.GetLastPInvokeError(), $"FileStandardInfo of {path}");
        return new Metadata(path, basic.CreationTime, basic.LastAccessTime, basic.LastWriteTime, basic.ChangeTime,
            standard.EndOfFile, standard.AllocationSize, basic.FileAttributes);
    }

    private static Win32Exception Win32(int error, string what) =>
        new(error, $"{what} failed with Win32 error {error}: {new Win32Exception(error).Message}");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ERR {message}");
        return ExitError;
    }

    /// <summary>One entry's metadata as a handle reads it. Times are FILETIMEs, as Windows keeps them.</summary>
    private sealed record Metadata(string Path, long CreationTime, long LastAccessTime, long LastWriteTime, long ChangeTime,
        long EndOfFile, long AllocationSize, uint Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_STANDARD_INFO info, int size);
}
