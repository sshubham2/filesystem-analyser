using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The parts of the opt-in cloud placeholder rig (CloudPlaceholderTests, SL-049) that make no cloud-files
/// call, kept out of that Trait-gated class so the default suite can check them: which folders under %TEMP%
/// are the rig's roots and which of those a sweep may remove, the provider's line protocol, and where the
/// running runtime's dotnet.exe is. Nothing here registers a sync root or starts a process.
/// </summary>
internal static partial class CloudRig
{
    public const string RootPrefix = "fsa-cf-";

    /// <summary>A root's folder name: the prefix, the test process's id, and a fresh GUID in lower-case hex.</summary>
    public static string RootName(int pid, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{RootPrefix}{pid}-{id:N}");

    // Strict on purpose: the sweep deletes what this matches, under the person's %TEMP%.
    [GeneratedRegex(@"^fsa-cf-(?<pid>[1-9][0-9]{0,9})-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RootPattern();

    /// <summary>Whether <paramref name="name"/> is exactly a root name this rig makes, and the id of the process that made it.</summary>
    public static bool TryParseRootName(string name, out int pid)
    {
        pid = 0;
        Match match = RootPattern().Match(name);
        return match.Success && int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }

    /// <summary>
    /// Whether a sweep may remove the folder <paramref name="name"/>: a root this rig made, in a process that is
    /// no longer running. A root of a run still going, in another worktree or session, is never stale.
    /// </summary>
    public static bool IsStaleRoot(string name, Func<int, bool> isRunning) =>
        TryParseRootName(name, out int pid) && !isRunning(pid);

    /// <summary>Whether a process with this id is running. One this token may not look at counts as running.</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // it exited while being looked at
        }
        catch (Win32Exception)
        {
            return true; // it exists, and this token may not open it
        }
    }

    /// <summary>
    /// The folder the rig's roots go in, made ready: <see cref="TempTree.DefaultTestsRoot"/> (listable by this user and
    /// outside the Windows folder under any temp folder), created, then swept. In that order and in this one place:
    /// the folder does not exist until something makes it, and <see cref="Sweep"/> of a missing or unlistable folder
    /// throws, which is what stopped every Fact of the rig under the engine's temp folder (SL-098).
    /// </summary>
    /// <returns>The folder, and the names of the roots the sweep removed.</returns>
    public static (string Parent, List<string> Swept) PrepareParent(Func<int, bool> isRunning, Action<string> unregister, Action<string> log) =>
        PrepareParent(TempTree.DefaultTestsRoot(), isRunning, unregister, log);

    /// <summary><see cref="PrepareParent(Func{int, bool}, Action{string}, Action{string})"/> for a folder of the caller's choosing.</summary>
    public static (string Parent, List<string> Swept) PrepareParent(string parent, Func<int, bool> isRunning, Action<string> unregister, Action<string> log)
    {
        Directory.CreateDirectory(parent);
        return (parent, Sweep(parent, isRunning, unregister, log));
    }

    /// <summary>
    /// Removes each folder directly in <paramref name="parent"/> that <see cref="IsStaleRoot"/> names: first
    /// <paramref name="unregister"/>, then the folder and everything in it. Nothing else in the parent is
    /// touched, nor is a link that happens to carry a root's name. Best effort: each failure goes to
    /// <paramref name="log"/> and the sweep goes on.
    /// </summary>
    /// <returns>The names of the folders it removed.</returns>
    public static List<string> Sweep(string parent, Func<int, bool> isRunning, Action<string> unregister, Action<string> log)
    {
        var swept = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(parent, RootPrefix + "*"))
        {
            string name = Path.GetFileName(folder);
            if (!IsStaleRoot(name, isRunning) || new DirectoryInfo(folder).LinkTarget is not null)
                continue;
            try
            {
                unregister(folder);
            }
            catch (Exception e)
            {
                log($"sweep: could not unregister {folder}: {e.Message}");
            }
            try
            {
                Directory.Delete(folder, recursive: true);
                swept.Add(name);
            }
            catch (Exception e)
            {
                log($"sweep: could not delete {folder}: {e.Message}");
            }
        }
        return swept;
    }

    /// <summary>One callback the provider reported: its type, the id of the process whose I/O raised it, and the path.</summary>
    public readonly record struct ProviderCallback(string Type, int Pid, string Path, long? Offset, long? Length);

    // The path runs to the end of the line, or to a FETCH_DATA's offset and length, and may hold spaces.
    [GeneratedRegex(@"^CB (?<type>[A-Z_]+) pid=(?<pid>[0-9]{1,10}) path=(?<path>.*?)(?: offset=(?<offset>-?[0-9]+) length=(?<length>-?[0-9]+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CallbackPattern();

    /// <summary>Reads a <c>CB &lt;TYPE&gt; pid=&lt;n&gt; path=&lt;p&gt;[ offset=&lt;o&gt; length=&lt;l&gt;]</c> line.</summary>
    public static bool TryParseCallback(string line, out ProviderCallback callback)
    {
        callback = default;
        Match match = CallbackPattern().Match(line);
        if (!match.Success || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            return false;
        callback = new ProviderCallback(match.Groups["type"].Value, pid, match.Groups["path"].Value,
            match.Groups["offset"].Success ? long.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture) : null,
            match.Groups["length"].Success ? long.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture) : null);
        return true;
    }

    /// <summary>
    /// The callbacks of <paramref name="type"/> raised by process <paramref name="pid"/>, the scan's own when it is
    /// the test process. The indexer, Defender or Explorer can touch the same root at any moment; their
    /// callbacks are not the scan's and never count, either way.
    /// </summary>
    public static List<ProviderCallback> CallbacksFrom(IEnumerable<string> lines, int pid, string type)
    {
        var callbacks = new List<ProviderCallback>();
        foreach (string line in lines)
        {
            if (TryParseCallback(line, out ProviderCallback callback) && callback.Pid == pid && callback.Type == type)
                callbacks.Add(callback);
        }
        return callbacks;
    }

    /// <summary>
    /// The dotnet.exe of the runtime this process runs on. <paramref name="runtimeDirectory"/> is
    /// <c>&lt;dotnet root&gt;\shared\Microsoft.NETCore.App\&lt;version&gt;\</c> as RuntimeEnvironment gives it,
    /// with a trailing separator, and the muxer is in the folder three levels above it.
    /// </summary>
    public static string MuxerFor(string runtimeDirectory)
    {
        DirectoryInfo? dotnetRoot = new DirectoryInfo(Path.TrimEndingDirectorySeparator(runtimeDirectory)).Parent?.Parent?.Parent;
        if (dotnetRoot is null)
            throw new InvalidOperationException($"The runtime directory {runtimeDirectory} is not three levels below a dotnet root.");
        return Path.Combine(dotnetRoot.FullName, "dotnet.exe");
    }
}
