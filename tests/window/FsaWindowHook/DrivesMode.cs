using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/// <summary>
/// drives (SL-004): reads the drive picker as a person would see it, button by button, every time it changes, and holds
/// the --report until every drive letter FSA_HOOK_WANT names has a button, so a share that answers long after
/// "Drives" is shown still has its button in the report.
/// </summary>
/// <remarks>
/// <code>
///   FSA_HOOK_MODE=drives  FSA_HOOK_WANT=&lt;letters, comma-separated, as M:,Z:&gt;  [FSA_HOOK_WANT_DEADLINE_S=&lt;n&gt;]
/// </code>
/// For a report-mode launch (a root and --report). A class handler on the main window's Loaded, which runs before the
/// window's own Loaded handler reads the drives, watches OutcomePanel; when the first ending is on screen it keeps one
/// Render-priority item queued, as the size mode's drive hold does, so the report, which reads the window at Loaded
/// priority, waits. Each time the item runs it reads DriveStatus's text and the drive buttons' letters in DrivePanel's
/// order (the first two characters of each button's label, which the app writes as "X:  label (format)"), and logs a
/// line whenever either has changed since the line before. The drives' own answers are posted at Normal priority, above
/// the item, so one that arrives is read at the next turn. The hold ends, and the report is read, when every wanted
/// letter has a button, or after FSA_HOOK_WANT_DEADLINE_S (150 s by default, above a share's ~120 s wake).
///
/// It logs "drives-armed want=M:,Z: deadline=n ms=n", "drives-seen ms=n utc=t status=text buttons=C:,M:,Z:" at each change
/// (the first reading at the ending included), and a last "drives-done ms=n utc=t held=n found=True|False status=text
/// buttons=..." as it lets the report go; found is whether every wanted letter has a button. A deadline met first is
/// "refused mode=drives reason=...", with the missing letters. The hook never ends the process: the app writes the
/// report and ends through its own Shutdown, which a stuck drive read does not hold up, as closing the window does.
///
/// FSA_HOOK_WANT is a list of letters the run is told to expect, never read from the machine: the hook shares no code
/// with the app, so what the app drew is the only thing it reports. It takes none of the other hook variables.
/// </remarks>
internal static class DrivesMode
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(150);
    private static bool _held;

    internal static void Drive(string? want, string? deadlineSeconds)
    {
        if (!TryParse(want, deadlineSeconds, out string[] letters, out TimeSpan deadline, out string reason))
        {
            StartupHook.Log($"refused mode=drives reason={reason}");
            return;
        }
        if (StartupHook.WaitForApplication("drives") is not { } app)
            return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (_held || !ReferenceEquals(sender, app.MainWindow))
                return;
            _held = true;
            Window window = (Window)sender;
            if (window.FindName("OutcomePanel") is not UIElement outcome || window.FindName("DriveStatus") is not TextBlock status
                || window.FindName("DrivePanel") is not Panel panel)
            {
                StartupHook.Log("refused mode=drives reason=the window has no OutcomePanel, DriveStatus or DrivePanel");
                return;
            }
            StartupHook.Log($"drives-armed want={string.Join(',', letters)} deadline={deadline.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} ms={StartupHook.Clock.ElapsedMilliseconds}");
            bool queued = false;
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, outcome.GetType()).AddValueChanged(outcome, (_, _) =>
            {
                if (queued || outcome.Visibility != Visibility.Visible)
                    return;
                queued = true;
                var since = Stopwatch.StartNew();
                app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => Hold(app, status, panel, letters, deadline, since, null)));
            });
        }));
        if (app.Dispatcher.Invoke(() => !_held && app.MainWindow is { IsLoaded: true }))
            StartupHook.Log("refused mode=drives reason=the window loaded before the hook could watch its drives");
    }

    /// <summary>
    /// The letters FSA_HOOK_WANT names, each one upper-case letter and a colon, with none twice; the deadline in whole
    /// seconds, 1 to 600. False, with why, for anything else.
    /// </summary>
    internal static bool TryParse(string? want, string? deadlineSeconds, out string[] letters, out TimeSpan deadline, out string reason)
    {
        letters = [];
        deadline = DefaultDeadline;
        reason = "";
        if (string.IsNullOrEmpty(want))
        {
            reason = "drives mode needs FSA_HOOK_WANT, the drive letters to wait for, as M:,Z:";
            return false;
        }
        string[] parts = want.Split(',');
        if (parts.Any(p => p.Length != 2 || p[0] is < 'A' or > 'Z' || p[1] != ':') || parts.Distinct().Count() != parts.Length)
        {
            reason = $"FSA_HOOK_WANT '{want}' is not a list of different upper-case drive letters, as M:,Z:";
            return false;
        }
        if (!string.IsNullOrEmpty(deadlineSeconds))
        {
            if (!int.TryParse(deadlineSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 600)
            {
                reason = $"FSA_HOOK_WANT_DEADLINE_S '{deadlineSeconds}' is not a whole number of seconds from 1 to 600";
                return false;
            }
            deadline = TimeSpan.FromSeconds(seconds);
        }
        letters = parts;
        return true;
    }

    // One turn of the hold: read, log a change, and either let the report go or queue the next turn. The 1 ms sleep keeps
    // the queue from spinning a core for minutes; the drives' answers arrive at Normal priority and run before it.
    private static void Hold(Application app, TextBlock status, Panel panel, string[] letters, TimeSpan deadline, Stopwatch since, string? last)
    {
        string buttons = Letters(panel);
        string reading = $"status={status.Text} buttons={buttons}";
        if (reading != last)
            StartupHook.Log($"drives-seen ms={StartupHook.Clock.ElapsedMilliseconds} utc={Utc()} {reading}");
        string[] have = buttons.Length == 0 ? [] : buttons.Split(',');
        string[] missing = letters.Where(l => !have.Contains(l)).ToArray();
        if (missing.Length == 0 || since.Elapsed > deadline)
        {
            StartupHook.Log($"drives-done ms={StartupHook.Clock.ElapsedMilliseconds} utc={Utc()} held={since.ElapsedMilliseconds} found={missing.Length == 0} {reading}");
            if (missing.Length > 0)
                StartupHook.Log($"refused mode=drives reason=no button for {string.Join(',', missing)} within {deadline.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s of the ending");
            return;
        }
        Thread.Sleep(1);
        app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => Hold(app, status, panel, letters, deadline, since, reading)));
    }

    // The drive buttons' letters in the panel's order: a label's first two characters, "?" where a button has none.
    private static string Letters(Panel panel) => string.Join(',', panel.Children.OfType<Button>().Select(b =>
        b.Content is TextBlock { Text.Length: >= 2 } label && label.Text[1] == ':' ? label.Text[..2] : "?"));

    private static string Utc() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
