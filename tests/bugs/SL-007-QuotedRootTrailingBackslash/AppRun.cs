using System.Diagnostics;
using System.Reflection;

namespace SL007.QuotedRootTrailingBackslash.Tests;

/// <summary>
/// Starting the built FsAnalyser.App.exe with a raw command line and watching what it does, for the facts beside the
/// reproduction. Arguments is the command line after the exe's name, handed over exactly as written, so the process meets
/// what cmd or Start-Process with one string would give it. Every worst case is a few small WPF processes that live for at most
/// the budget they are given and are killed in a finally block, and a work folder of a few KB under the temp folder.
/// </summary>
internal static class AppRun
{
    /// <summary>What a run did: whether it ended inside its budget, its exit code, how long that took, and whether it ever had a window.</summary>
    internal sealed record Result(bool Exited, int ExitCode, long Ms, bool WindowSeen);

    public static string Exe()
    {
        string appFolder = typeof(AppRun).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FsaAppFolder").Value!;
        string exe = Path.Combine(appFolder, "FsAnalyser.App.exe");
        Assert.True(File.Exists(exe), $"the app was not built at {exe}; the project reference to the App should have built it");
        return exe;
    }

    // %TEMP%\fsa-sl007\<guid>, or under the user's own LocalAppData\Temp when %TEMP% is at or under the Windows folder (a shell
    // that inherited the machine's TEMP, as the engine's does), where a scan's file times move on their own (SL-096).
    public static string WorkFolder()
    {
        string temp = Path.GetTempPath();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && temp.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        string work = Path.Combine(temp, "fsa-sl007", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    /// <summary>A tree of three files (1000, 2000 and 3000 bytes, one of them in a subfolder) under <paramref name="parent"/>.</summary>
    public static string MakeTree(string parent, string name)
    {
        string tree = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllBytes(Path.Combine(tree, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(tree, "b.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(tree, "sub", "c.bin"), new byte[3000]);
        return tree;
    }

    /// <summary>
    /// Starts the exe with <paramref name="arguments"/> as the raw command line. The window cases' hook variables are taken out of
    /// its environment, so that a run under a harness that has them set meets the exe as it ships.
    /// </summary>
    public static Process Start(string exe, string arguments)
    {
        var info = new ProcessStartInfo(exe) { Arguments = arguments, UseShellExecute = false };
        foreach (string name in info.Environment.Keys.Where(k =>
            k.StartsWith("FSA_HOOK_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("FSA_T005_", StringComparison.OrdinalIgnoreCase)
            || k.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            info.Environment.Remove(name);
        }
        return Process.Start(info) ?? throw new InvalidOperationException($"{exe} did not start");
    }

    /// <summary>
    /// Waits for <paramref name="process"/> to end for at most <paramref name="budgetMs"/>, looking for a window of its own every 10 ms,
    /// so that a run that opened a window and one that never did are told apart even when both end.
    /// </summary>
    public static Result Watch(Process process, int budgetMs)
    {
        var clock = Stopwatch.StartNew();
        bool windowSeen = false;
        while (true)
        {
            if (process.WaitForExit(10))
                return new Result(true, process.ExitCode, clock.ElapsedMilliseconds, windowSeen);
            try
            {
                process.Refresh();
                windowSeen |= process.MainWindowHandle != IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                // it ended between the wait and the refresh: the next wait says so
            }
            if (clock.ElapsedMilliseconds >= budgetMs)
                return new Result(false, 0, clock.ElapsedMilliseconds, windowSeen);
        }
    }

    public static void Stop(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    public static void RemoveQuietly(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover folder of a few KB under the temp folder is not a reason to fail the test.
        }
    }
}
