using System.IO;
using System.Windows;
using FsAnalyser.Core;

namespace FsAnalyser.App;

public partial class App : Application
{
    private const string ReportOption = CommandLine.ReportOption;

    // Report-mode exit codes; WindowReport.cs has the whole contract.
    private const int ReportWritten = 0;
    private const int BadArguments = 2;
    private const int CannotWriteReport = 3;
    private const int Crashed = 70;

    // FsAnalyser.App.exe [path]: an optional first argument names a drive or folder to scan at launch.
    // FsAnalyser.App.exe <path> --report <file>: scan it, write what the window shows to <file>, and exit.
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Windows merges a quoted root that ends in a backslash with every argument after it, so the mode is decided
        // only on the arguments as typed (SL-007). Null: the arguments cannot be told apart, and nothing is opened.
        string[]? args = CommandLine.Arguments(e.Args);
        if (args is null)
        {
            Shutdown(BadArguments);
            return;
        }
        if (args.Contains(ReportOption))
        {
            StartReportMode(args);
            return;
        }
        var window = new MainWindow(args.Length > 0 ? CommandLine.RootArgument(args[0]) : null);
        window.Show();
    }

    // Unattended: every refusal exits at once, and nothing ever waits on a person (no folder picker).
    private void StartReportMode(string[] args)
    {
        string? reportArgument = null;
        bool unexpected = false;
        var roots = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != ReportOption)
                roots.Add(args[i]);
            else if (reportArgument is not null || i + 1 == args.Length)
                unexpected = true; // a second --report, or one with no value
            else
                reportArgument = args[++i];
        }
        if (reportArgument is null)
        {
            Shutdown(BadArguments);
            return;
        }

        // Delete-first: from here on, an absent file means this run wrote no report.
        string report;
        try
        {
            report = Path.GetFullPath(reportArgument);
        }
        catch (ArgumentException)
        {
            Shutdown(BadArguments);
            return;
        }
        var file = new ReportFile(report);
        try
        {
            if (Directory.Exists(report) || !Directory.Exists(Path.GetDirectoryName(report)))
                throw new IOException($"{report} is a folder, or its folder does not exist.");
            DeleteFirst(report);
            DeleteFirst(report + ".tmp");
            file.Probe(); // a folder that takes no new file is refused here, before any window or scan
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shutdown(CannotWriteReport);
            return;
        }

        // Start-Process does not quote an argument with a space in it, so a root split in two must be
        // refused, not scanned in part.
        if (unexpected || roots.Count != 1)
        {
            Shutdown(BadArguments);
            return;
        }
        string root = CommandLine.RootArgument(roots[0]);

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            file.Discard();
            Shutdown(Crashed);
        };

        // Exit 0 only with a report: closing the window before its ending exits 0 by default, and so does
        // an exception after Shutdown(ReportWritten), because WPF keeps the first code while Discard
        // deletes the file.
        Exit += (_, e) =>
        {
            if (e.ApplicationExitCode == ReportWritten && !File.Exists(report))
                e.ApplicationExitCode = Crashed;
        };

        var window = new MainWindow(root);
        bool reported = false;
        window.EndingShown += async (_, _) =>
        {
            if (reported)
                return; // only the first ending counts
            reported = true;
            try
            {
                await WindowReport.WriteAsync(window, root, file);
                Shutdown(ReportWritten);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Shutdown(CannotWriteReport);
            }
        };
        window.Show();
    }

    // A read-only file there is deleted too (HF-018): File.Delete refuses one, which left an earlier run's report at the
    // path after an exit that promises none. One another program holds open without sharing delete still throws.
    private static void DeleteFirst(string file)
    {
        var info = new FileInfo(file);
        if (info.Exists && info.IsReadOnly)
            info.IsReadOnly = false;
        info.Delete();
    }
}
