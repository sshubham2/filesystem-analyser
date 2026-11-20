/*
 * BUG (SL-007): report mode accepts a quoted root ending in a backslash instead of hanging in an interactive window.
 *
 * A root typed quoted with a trailing backslash and followed by more arguments, for example
 *
 *     FsAnalyser.App.exe "D:\Data\" --report "D:\out\r.json"
 *
 * reaches the app as ONE argument:  D:\Data" --report D:\out\r.json  (Windows argv rules: \" is a literal quote and the
 * quoting goes on). At the base, App.OnStartup (src/FsAnalyser.App/App.xaml.cs:21 there) then found no "--report" among
 * e.Args, so it did not enter report mode and opened an interactive MainWindow on that nonsense root (line 26 there).
 * CommandLine.RootArgument only repairs an argument that ENDS in a quote, so it did not help here.
 *
 * EXPECTED: the app takes the root as <tree>\ and "--report" as an option, scans, writes the report, and exits 0, exactly
 * as it does for the same line without the trailing backslash.
 * ACTUAL: the process never exits and never writes a report; its 'Filesystem Analyser' window stays up on the nonsense
 * root until something kills it, so an unattended caller hangs for ever.
 *
 * The trigger is the real command line of the real exe (Windows splits it before any of the app's code runs), so this
 * starts the built FsAnalyser.App.exe as a process with ProcessStartInfo.Arguments, which is passed as written. Two runs
 * start together: the CONTROL, "<tree>" --report "<report>", which must exit 0 with a report (if it does not, the app
 * cannot run in report mode in this session and the test says so as a control failure, not as the bug), and the CASE,
 * "<tree>\" --report "<report>", which is the bug. No path holds a space or a quote.
 *
 * Cost, worst case on this machine: two small WPF processes (a three-file tree each) that live for at most seven seconds
 * and are killed in the finally block, and a work folder under the temp folder (a few KB) that is removed at the end. The
 * whole test is under ten seconds: a few seconds when the bug is fixed, and about eight when it is present.
 */
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace SL007.QuotedRootTrailingBackslash.Tests;

public class QuotedRootTrailingBackslashTests
{
    // How long both apps get, from their start, to write their report and exit. A report-mode run on a tiny tree takes
    // a second or two; the bug never ends, so more would only make the failing run slower.
    private const int BudgetMs = 7000;

    [Fact]
    public void ReportModeSeesTheReportOptionAfterAQuotedRootEndingInABackslash()
    {
        string appFolder = typeof(QuotedRootTrailingBackslashTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "FsaAppFolder").Value!;
        string exe = Path.Combine(appFolder, "FsAnalyser.App.exe");
        Assert.True(File.Exists(exe), $"the app was not built at {exe}; the project reference to the App should have built it");

        string work = WorkFolder();
        Directory.CreateDirectory(work);
        Process? control = null;
        Process? trailing = null;
        try
        {
            string controlTree = MakeTree(work, "control");
            string trailingTree = MakeTree(work, "trailing");
            string controlReport = Path.Combine(work, "control-report.json");
            string trailingReport = Path.Combine(work, "trailing-report.json");

            // The two command lines, as typed in cmd or given to Start-Process with one string: nothing is escaped for the
            // case, because the case is what a person types. The trailing backslash is the only difference between them.
            string controlArguments = $"\"{controlTree}\" --report \"{controlReport}\"";
            string trailingArguments = $"\"{trailingTree}\\\" --report \"{trailingReport}\"";

            var clock = Stopwatch.StartNew();
            control = Start(exe, controlArguments);
            trailing = Start(exe, trailingArguments);

            bool controlExited = control.WaitForExit(Remaining(clock));
            long controlMs = clock.ElapsedMilliseconds;
            bool trailingExited = trailing.WaitForExit(Remaining(clock));
            long trailingMs = clock.ElapsedMilliseconds;

            // The control first: when it fails, the app cannot be run in report mode here and the case proves nothing.
            Assert.True(controlExited && control.ExitCode == 0 && File.Exists(controlReport),
                "CONTROL FAILED, which is not the bug: the same line without the trailing backslash did not give exit 0 and a report. " +
                $"[{controlArguments}] exited={controlExited} code={(controlExited ? control.ExitCode.ToString() : "-")} " +
                $"report={File.Exists(controlReport)} after {controlMs} ms, so this session cannot run the app in report mode.");

            Assert.True(trailingExited,
                $"The app did not exit within {BudgetMs} ms ({trailingMs} ms) on [{trailingArguments}], while the control without the trailing " +
                $"backslash exited 0 with a report after {controlMs} ms. Report written: {File.Exists(trailingReport)}. " +
                "A quoted root ending in a backslash reaches the app as the one argument <root>\" --report <file>, so \"--report\" is " +
                "not among its arguments and it waits in an interactive window on a nonsense root.");
            Assert.True(trailing.ExitCode == 0,
                $"The app exited with {trailing.ExitCode}, not 0, on [{trailingArguments}]; the control exited 0.");
            Assert.True(File.Exists(trailingReport),
                $"The app exited 0 on [{trailingArguments}] and wrote no report at {trailingReport}.");

            // It scanned the root it was given, not some other path: the same tree shape as the control's gives the same counts.
            (string status, long files) controlRead = ReadCore(controlReport);
            (string status, long files) trailingRead = ReadCore(trailingReport);
            Assert.Equal(controlRead, trailingRead);
        }
        finally
        {
            Stop(control);
            Stop(trailing);
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover folder of a few KB under the temp folder is not a reason to fail the test.
            }
        }
    }

    // %TEMP%\fsa-sl007\<guid>, or under the user's own LocalAppData\Temp when %TEMP% is at or under the Windows folder (a shell
    // that inherited the machine's TEMP, as the engine's does), where a scan's file times move on their own (SL-096).
    private static string WorkFolder()
    {
        string temp = Path.GetTempPath();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && temp.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        return Path.Combine(temp, "fsa-sl007", Guid.NewGuid().ToString("N"));
    }

    private static string MakeTree(string work, string name)
    {
        string tree = Path.Combine(work, name);
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllBytes(Path.Combine(tree, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(tree, "b.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(tree, "sub", "c.bin"), new byte[3000]);
        return tree;
    }

    // Arguments is the raw command line after the exe's name: the process receives the text exactly as written here.
    private static Process Start(string exe, string arguments) =>
        Process.Start(new ProcessStartInfo(exe) { Arguments = arguments, UseShellExecute = false })
        ?? throw new InvalidOperationException($"{exe} did not start");

    private static int Remaining(Stopwatch clock) => Math.Max(0, BudgetMs - (int)clock.ElapsedMilliseconds);

    private static (string Status, long Files) ReadCore(string report)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        JsonElement core = document.RootElement.GetProperty("core");
        return (core.GetProperty("status").ToString(), core.GetProperty("files").GetInt64());
    }

    private static void Stop(Process? process)
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
}
