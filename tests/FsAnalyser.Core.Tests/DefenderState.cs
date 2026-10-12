using System.Diagnostics;
using System.Text.Json;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-101: Controlled Folder Access, recorded in the test output beside fsutil's disablelastaccess (SL-056's design asked
/// for it, docs/design/SL-056-probe-every-file.md:107-108): the scan asks to keep a folder's last-access time with a
/// FILE_WRITE_ATTRIBUTES open, which CFA might block or announce on Documents, Desktop and Pictures. This only records
/// what Defender says; it asserts nothing, skips nothing and throws nothing, because a machine's Defender setting is no test
/// outcome and the counting-rule tests that print it are never skipped. Every failure is a line saying "not read" and why.
/// </summary>
/// <remarks>
/// The read is Windows PowerShell 5.1's Get-MpPreference and Get-MpComputerStatus, the one route measured working for an
/// ordinary user (SL-101 survey). The child prints raw values as one JSON object and the lists are counted here, in
/// <see cref="Describe"/>, where a Theory pins them: counted in PowerShell, @($null).Count is 1. The child is started the
/// way VolumeUsageTests.GetVolumeUsed starts its own: output read asynchronously, a timeout, the whole process tree killed.
/// PSModulePath is dropped from the child's environment: testhost is started by eng/dotnet.ps1 under pwsh 7, whose module
/// path 5.1 would otherwise inherit.
/// </remarks>
internal static class DefenderState
{
    internal const int TimeoutSeconds = 20;

    internal static readonly string PowerShellPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    // Read-only cmdlets. A terminating error (Defender not running, a third-party antivirus, the Sandbox's 0x800106ba) is a
    // non-zero exit with the message on stderr.
    internal static readonly string[] ReadArguments =
    [
        "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference='Stop'; $p=Get-MpPreference; $s=Get-MpComputerStatus; " +
        "[pscustomobject]@{enable=$p.EnableControlledFolderAccess; protected=$p.ControlledFolderAccessProtectedFolders; " +
        "allowed=$p.ControlledFolderAccessAllowedApplications; mode=$s.AMRunningMode; rtp=$s.RealTimeProtectionEnabled; " +
        "platform=$s.AMProductVersion} | ConvertTo-Json -Compress",
    ];

    private static readonly Lazy<string> Recorded = new(() => ReadOnce());

    /// <summary>The one line, read the first time it is asked for and once per test process (thread-safe).</summary>
    internal static string Line => Recorded.Value;

    private static string ReadOnce()
    {
        try
        {
            return Read(PowerShellPath, ReadArguments, TimeSpan.FromSeconds(TimeoutSeconds));
        }
        catch (Exception e)
        {
            // Read cannot throw. A Lazy would keep and rethrow whatever got out to every test that asks, so this is the
            // last line of the promise that recording never fails a test.
            return NotRead($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Starts the program, describes what came back. Never throws.</summary>
    internal static string Read(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        ChildResult child = Run(fileName, arguments, timeout);
        return Describe(child.ExitCode, child.Stdout, child.Stderr, child.TimedOut, child.StartException, (int)Math.Ceiling(timeout.TotalSeconds));
    }

    internal readonly record struct ChildResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, Exception? StartException);

    /// <summary>Runs the program with no PSModulePath, for at most the timeout; a child past it is killed with its tree. Never throws.</summary>
    internal static ChildResult Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Remove("PSModulePath");

        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(timeout);
            if (!exited)
            {
                Kill(process);
                return new ChildResult(-1, "", "", TimedOut: true, null);
            }

            // Both pipes close with the process; a grandchild that kept one open must not hold this call.
            if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5)))
                return new ChildResult(-1, "", "", TimedOut: true, null);
            return new ChildResult(process.ExitCode, stdout.Result, stderr.Result, TimedOut: false, null);
        }
        catch (Exception e)
        {
            return new ChildResult(-1, "", "", TimedOut: false, e);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill: the timed-out line is written either way.
        }
    }

    internal static string NotRead(string reason) => $"Controlled Folder Access: not read ({reason})";

    /// <summary>
    /// The record line for what a child gave back. Total: it throws for no input. A start failure, a timeout, a non-zero
    /// exit and output that is not one JSON object each become "not read (reason)".
    /// </summary>
    internal static string Describe(int exitCode, string? stdout, string? stderr, bool timedOut, Exception? startException, int timeoutSeconds = TimeoutSeconds)
    {
        if (startException is not null)
            return NotRead(Capped($"{startException.GetType().Name}: {startException.Message}"));
        if (timedOut)
            return NotRead($"timed out after {timeoutSeconds} s");
        if (exitCode != 0)
            return NotRead(FirstLine(stderr) ?? $"exit code {exitCode}");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout ?? "");
        }
        catch (JsonException)
        {
            return NotRead("unparsed output");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return NotRead("unparsed output");
            return $"Controlled Folder Access: EnableControlledFolderAccess={Enable(root)}, protected folders={Count(root, "protected")}, " +
                   $"allowed apps={Count(root, "allowed")}; Defender AMRunningMode={Text(root, "mode")}, RealTimeProtection={Text(root, "rtp")}, platform={Text(root, "platform")}";
        }
    }

    // 0..4 are Microsoft's names for the setting (recalled, not measured); the raw value is always printed beside the name.
    private static string Enable(JsonElement root)
    {
        if (!root.TryGetProperty("enable", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return "?";
        string raw = Capped(value.GetRawText());
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            return $"{raw} (unknown)";
        string name = number switch
        {
            0 => "Disabled",
            1 => "Enabled",
            2 => "AuditMode",
            3 => "BlockDiskModificationOnly",
            4 => "AuditDiskModificationOnly",
            _ => "unknown",
        };
        return $"{raw} ({name})";
    }

    // A list as the child serialised it: null (empty) 0, a bare string 1, an array its length, a property that is not there "?".
    // Except that a caller who is not an administrator is not given the list: Get-MpPreference hands them one entry in its
    // place, "N/A: Must be an administrator to view exclusions" (measured here, unelevated), and that is not a count of anything.
    // Whatever list holds such an entry reads as hidden (review M1, DefenderStateCorpus.json).
    private static string Count(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement list))
            return "?";
        return list.ValueKind switch
        {
            JsonValueKind.Null => "0",
            JsonValueKind.String => IsPlaceholder(list) ? HiddenFromNonAdministrators : "1",
            JsonValueKind.Array => list.EnumerateArray().Any(IsPlaceholder)
                ? HiddenFromNonAdministrators
                : list.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "?",
        };
    }

    internal const string HiddenFromNonAdministrators = "hidden from non-administrators";

    // The text after the prefix is Defender's prose and may be reworded or localised, so only the prefix is read. No path
    // begins "N/A:": a colon in a path is the second character after a drive letter, or part of a stream name after a file.
    private static bool IsPlaceholder(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.String && entry.GetString()!.TrimStart().StartsWith("N/A:", StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value))
            return "?";
        return value.ValueKind switch
        {
            JsonValueKind.String when value.GetString() is { Length: > 0 } text => Capped(text),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => Capped(value.GetRawText()),
            _ => "?",
        };
    }

    private static string? FirstLine(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(Capped).FirstOrDefault();

    private static string Capped(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
