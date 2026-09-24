using System.IO;
using System.Windows;
using FsAnalyser.Core;

namespace FsAnalyser.App;

public partial class App : Application
{
    private const string ReportOption = "--report";

    // Report-mode exit codes; WindowReport.cs has the whole contract.
    private const int ReportWritten = 0;
    private const int BadArguments = 2;
    private const int CannotWriteReport = 3;
    private const int Crashed = 70;

    // FsAnalyser.App.exe [path]: an optional first argument names a drive or folder to scan at launch.
    // FsAnalyser.App.exe <path> --report <file>: scan it, write what the window shows to <file>, and exit.
    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.Contains(ReportOption))
        {
            StartReportMode(e.Args);
            return;
        }
        var window = new MainWindow(e.Args.Length > 0 ? CommandLine.RootArgument(e.Args[0]) : null);
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
        try
        {
            if (Directory.Exists(report) || !Directory.Exists(Path.GetDirectoryName(report)))
                throw new IOException($"{report} is a folder, or its folder does not exist.");
            File.Delete(report);
            File.Delete(report + ".tmp");
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
            WindowReport.Discard(report);
            Shutdown(Crashed);
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
                await WindowReport.WriteAsync(window, root, report);
                Shutdown(ReportWritten);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Shutdown(CannotWriteReport);
            }
        };
        window.Show();
    }
}
