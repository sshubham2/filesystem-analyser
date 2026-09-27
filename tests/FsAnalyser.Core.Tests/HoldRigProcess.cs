using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// One entry's metadata as a handle reads it, in tests/HoldRig's own words: the four times (FILETIMEs),
/// EndOfFile, AllocationSize and the attributes.
/// </summary>
internal sealed record HandleMetadata(string Path, long CreationTime, long LastAccessTime, long LastWriteTime, long ChangeTime,
    long EndOfFile, long AllocationSize, uint Attributes)
{
    /// <summary>Everything but the path, for comparing two readings of one entry taken through different names.</summary>
    public (long, long, long, long, long, long, uint) Fields =>
        (CreationTime, LastAccessTime, LastWriteTime, ChangeTime, EndOfFile, AllocationSize, Attributes);
}

/// <summary>
/// Starts tests/HoldRig (SL-056) as processes of its own, through the running runtime's dotnet.exe and this test
/// project's runtimeconfig, as the cloud tests start their provider. HoldRig shares no code with Core: it is the
/// observer the scan is checked against.
/// </summary>
internal static class HoldRigProcess
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    /// <summary>A file created, grown to <paramref name="bytes"/> and held open by a process of its own.</summary>
    public static HeldFile Hold(string path, long bytes, bool shareNone) =>
        new(path, Start("hold", path, bytes.ToString(), shareNone ? "none" : "read"));

    /// <summary>
    /// Each path's metadata, read through a FILE_READ_ATTRIBUTES handle by a process of its own that lists no
    /// folder. In the manifest's order.
    /// </summary>
    public static List<HandleMetadata> Snapshot(IReadOnlyList<string> paths, string scratchFolder)
    {
        string id = Guid.NewGuid().ToString("N");
        string manifest = Path.Combine(scratchFolder, $"snapshot-{id}.txt");
        string output = Path.Combine(scratchFolder, $"snapshot-{id}.json");
        File.WriteAllLines(manifest, paths);
        try
        {
            using Process process = Start("snapshot", manifest, output);
            process.StandardInput.Close();
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(Deadline))
            {
                process.Kill();
                throw new XunitException($"HoldRig snapshot did not end within {Deadline.TotalSeconds} s.");
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new XunitException($"HoldRig snapshot exited {process.ExitCode}: {stdout} {process.StandardError.ReadToEnd()}");
            List<HandleMetadata> records = JsonSerializer.Deserialize<List<HandleMetadata>>(File.ReadAllText(output))
                ?? throw new XunitException("HoldRig snapshot wrote no records.");
            Assert.True(records.Count == paths.Count, $"HoldRig snapshot read {records.Count} of {paths.Count} paths");
            return records;
        }
        finally
        {
            File.Delete(manifest);
            File.Delete(output);
        }
    }

    private static Process Start(params string[] args)
    {
        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string muxer = CloudRig.MuxerFor(runtime);
        string rig = Path.Combine(AppContext.BaseDirectory, "HoldRig.dll");
        string config = Path.Combine(AppContext.BaseDirectory, "FsAnalyser.Core.Tests.runtimeconfig.json");
        if (!File.Exists(muxer))
            throw new XunitException($"No dotnet.exe at {muxer}, three levels above the running runtime's directory {runtime}.");
        if (!File.Exists(rig))
            throw new XunitException($"No HoldRig.dll at {rig}. The ProjectReference to tests/HoldRig/HoldRig.csproj in FsAnalyser.Core.Tests.csproj puts it there.");

        var start = new ProcessStartInfo(muxer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "exec", "--runtimeconfig", config, rig }.Concat(args))
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new XunitException($"Could not start HoldRig {string.Join(' ', args)}.");
    }

    /// <summary>
    /// A file another process holds open. Its metadata is read only through that process's own handle
    /// (<see cref="Report"/>): opening it from here would make Windows refresh its stale directory entry (SL-045).
    /// Disposing closes the holder's stdin, which closes the handle; a holder still alive 10 s later is killed.
    /// </summary>
    internal sealed class HeldFile : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();

        public HeldFile(string path, Process process)
        {
            Path = path;
            _process = process;
            _process.ErrorDataReceived += (_, e) =>
            {
                lock (_stderr)
                    _stderr.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
            string? ready = ReadLine();
            if (ready != "READY")
            {
                string exit = _process.WaitForExit(TimeSpan.FromSeconds(5)) ? _process.ExitCode.ToString() : "none yet";
                string why = $"HoldRig could not hold {path}: {ready ?? "it ended"}, exit {exit}. {Stderr}";
                Dispose();
                throw new XunitException(why);
            }
        }

        public string Path { get; }

        /// <summary>The file's metadata as the holder's own handle reads it.</summary>
        public HandleMetadata Report()
        {
            _process.StandardInput.WriteLine("report");
            _process.StandardInput.Flush();
            string line = ReadLine() ?? throw new XunitException($"HoldRig holding {Path} ended without a report. {Stderr}");
            return JsonSerializer.Deserialize<HandleMetadata>(line) ?? throw new XunitException($"HoldRig's report on {Path} was empty.");
        }

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // it has already ended
            }
            if (!_process.WaitForExit(TimeSpan.FromSeconds(10)))
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            _process.Dispose();
        }

        private string Stderr
        {
            get
            {
                lock (_stderr)
                    return _stderr.ToString().Trim();
            }
        }

        private string? ReadLine()
        {
            Task<string?> line = _process.StandardOutput.ReadLineAsync();
            if (!line.Wait(Deadline))
                throw new XunitException($"HoldRig holding {Path} printed nothing within {Deadline.TotalSeconds} s. {Stderr}");
            return line.Result;
        }
    }
}
