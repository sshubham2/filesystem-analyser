using System.Windows;
using FsAnalyser.Core;

namespace FsAnalyser.App;

public partial class App : Application
{
    // FsAnalyser.App.exe [path]: an optional first argument names a drive or folder to scan at launch.
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow(e.Args.Length > 0 ? CommandLine.RootArgument(e.Args[0]) : null);
        window.Show();
    }
}
