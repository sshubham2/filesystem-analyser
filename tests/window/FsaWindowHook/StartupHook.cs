using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

/// <summary>
/// The window cases' driver (SL-033): presses what a person would press in the published app, from inside
/// its process, with no UI Automation.
/// </summary>
/// <remarks>
/// <code>
///   DOTNET_STARTUP_HOOKS=&lt;absolute path to FsaWindowHook.dll&gt;  FSA_HOOK_MODE=stop|throw|close  FSA_HOOK_LOG=&lt;file&gt;
/// </code>
/// The runtime calls <see cref="Initialize"/> before the app's Main, in every .NET process that inherits the
/// variable: pwsh 7, dotnet.exe and MSBuild nodes too. So it does nothing unless FSA_HOOK_MODE is set and the
/// process is FsAnalyser.App.exe, and nothing it does can end the host.
///
/// Every mode waits for the same thing: the window's CancelButton pressable (<see cref="Pressable"/>), which
/// is only while a scan is running. It reads that and acts in one dispatcher callback, so the state it read
/// is the state it acted on. stop raises the button's Click; close closes the main window; throw posts an
/// exception to the UI thread. If nothing is pressable within 20 s, or the mode is unknown, it logs
/// "refused" and does nothing. The hook never ends the process or picks its exit code: every exit code a
/// case reads is the app's own.
///
/// FSA_HOOK_LOG gets one ASCII line per event: "loaded mode=m pid=n", "waiting found=b enabled=b visible=b"
/// when that changes, "acted mode=m enabled=True visible=True ms=n" (ms since load), and
/// "refused mode=m reason=text".
/// </remarks>
internal class StartupHook
{
    private const string TargetExe = "FsAnalyser.App.exe";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    private static readonly Stopwatch Clock = new();
    private static readonly object LogGate = new();
    private static string? _logPath;

    public static void Initialize()
    {
        try
        {
            string? mode = Environment.GetEnvironmentVariable("FSA_HOOK_MODE");
            if (string.IsNullOrEmpty(mode) ||
                !string.Equals(Path.GetFileName(Environment.ProcessPath), TargetExe, StringComparison.OrdinalIgnoreCase))
                return;
            _logPath = Environment.GetEnvironmentVariable("FSA_HOOK_LOG");
            Clock.Start();
            new Thread(() => Run(mode)) { IsBackground = true, Name = "FsaWindowHook" }.Start();
        }
        catch (Exception)
        {
            // An exception out of Initialize would end the host before its Main.
        }
    }

    /// <summary>
    /// Whether a person could press <paramref name="element"/> now: enabled, and on screen with every
    /// ancestor visible. A raw RaiseEvent or handler call goes around both (spike SL-033
    /// in-process-click-respects-disabled-and-hidden), so every action checks this first.
    /// </summary>
    internal static bool Pressable(FrameworkElement element) => element.IsEnabled && element.IsVisible;

    private static void Run(string mode)
    {
        try
        {
            Log($"loaded mode={mode} pid={Environment.ProcessId}");
            if (mode is not ("stop" or "throw" or "close"))
            {
                Log($"refused mode={mode} reason=unknown mode");
                return;
            }
            Drive(mode);
        }
        catch (Exception e)
        {
            Log($"refused mode={mode} reason={e.GetType().Name}: {e.Message}");
        }
    }

    // Every WPF type is behind this call, so a host without WPF fails here, inside Run's catch, and not
    // while the runtime compiles Initialize.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drive(string mode)
    {
        Application? app;
        while ((app = Application.Current) is null)
        {
            if (Clock.Elapsed > Deadline)
            {
                Log($"refused mode={mode} reason=no Application within {Deadline.TotalSeconds:N0} s");
                return;
            }
            Thread.Sleep(Poll);
        }

        string seen = "found=False enabled=False visible=False";
        bool logged = false;
        while (Clock.Elapsed < Deadline)
        {
            bool acted = false;
            string state = seen;
            try
            {
                app.Dispatcher.Invoke(() =>
                {
                    var button = app.MainWindow?.FindName("CancelButton") as FrameworkElement;
                    state = button is null
                        ? "found=False enabled=False visible=False"
                        : $"found=True enabled={button.IsEnabled} visible={button.IsVisible}";
                    if (button is null || !Pressable(button))
                        return;
                    Log($"acted mode={mode} enabled={button.IsEnabled} visible={button.IsVisible} ms={Clock.ElapsedMilliseconds}");
                    Act(mode, app, button);
                    acted = true;
                }, DispatcherPriority.Normal);
            }
            catch (OperationCanceledException) when (app.Dispatcher.HasShutdownStarted)
            {
                Log($"refused mode={mode} reason=the app shut down first; last seen {state}");
                return;
            }
            if (acted)
                return;
            if (!logged || state != seen)
            {
                Log($"waiting {state}");
                seen = state;
                logged = true;
            }
            Thread.Sleep(Poll);
        }
        Log($"refused mode={mode} reason=CancelButton not pressable within {Deadline.TotalSeconds:N0} s; last seen {seen}");
    }

    // On the UI thread, straight after the check.
    private static void Act(string mode, Application app, FrameworkElement cancelButton)
    {
        switch (mode)
        {
            case "stop":
                // What a click raises: reaches OnCancel through the Click wiring in MainWindow.xaml.
                cancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "close":
                app.MainWindow!.Close();
                break;
            case "throw":
                // BeginInvoke, not InvokeAsync: an exception in an InvokeAsync callback goes to its Task. No
                // SetErrorMode: interop lives only in Native (ArchitectureRulesTests), and with the app's
                // handler removed the process still ended at once, 0xE0434352 and no crash dialog.
                app.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => throw new InvalidOperationException("FsaWindowHook throw mode: an unhandled exception on the UI thread.")));
                break;
        }
    }

    private static void Log(string line)
    {
        if (string.IsNullOrEmpty(_logPath))
            return;
        try
        {
            lock (LogGate)
                File.AppendAllText(_logPath, line.ReplaceLineEndings(" ") + "\r\n", Encoding.ASCII);
        }
        catch (Exception)
        {
            // A lost line shows up as a case failure ("never acted"), never as a crash of the app.
        }
    }
}
