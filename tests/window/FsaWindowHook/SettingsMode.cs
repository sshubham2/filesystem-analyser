using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

/// <summary>
/// settings (SL-095): turns the first precaution in the app's Settings off as a person would, and answers the question
/// the app asks first with FSA_HOOK_ANSWER=yes|no, before the first scan starts, so the report the scan's ending writes
/// reads the window's precautions as that answer left them.
/// </summary>
/// <remarks>
/// A class handler on the main window's Loaded, which runs ahead of the window's own Loaded handler that starts the scan,
/// presses SettingsButton (its Click, if a person could press it). The app opens its Settings window modally there, and
/// the steps queued before the press run inside that window's own message loop, at Background priority so each runs
/// after the layout that puts what it presses on screen: untick the first checkbox (IsChecked, which a click sets), which
/// makes the app ask in its confirmation window; press its Yes or No; then press the Settings window's Close. The press
/// returns when Settings has closed, and the window's own Loaded handler then starts the scan with the precautions as
/// they are. It logs "acted mode=settings answer=a ms=n", "settings box=name id=id checked=b", "settings question=text",
/// "settings answered=a checked=b", and "settings closed button=text", the main window's button once Settings closed.
/// What it cannot find or press within 20 s is "refused mode=settings reason=...". The app's windows are found by their
/// type's name and their parts by Name, because the hook shares no code with the app.
/// </remarks>
internal static class SettingsMode
{
    private static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);
    private static bool _done;

    internal static void Drive(string answer)
    {
        if (StartupHook.WaitForApplication("settings") is not { } app)
            return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (_done || !ReferenceEquals(sender, app.MainWindow))
                return;
            _done = true;
            try
            {
                Press(app, (Window)sender, answer);
            }
            catch (Exception e)
            {
                StartupHook.Log($"refused mode=settings reason={e.GetType().Name}: {e.Message}");
            }
        }));
        if (app.Dispatcher.Invoke(() => !_done && app.MainWindow is { IsLoaded: true }))
            StartupHook.Log("refused mode=settings reason=the window loaded before the hook could open its settings");
    }

    private static void Press(Application app, Window window, string answer)
    {
        if (window.FindName("SettingsButton") is not Button settings || !StartupHook.Pressable(settings))
        {
            StartupHook.Log("refused mode=settings reason=SettingsButton is not pressable as the window loads");
            return;
        }
        // Queued first, so that it runs inside the modal Settings window's loop the press is about to start.
        Step(app, "the Settings window's first checkbox", () => Untick(app, answer));
        StartupHook.Log($"acted mode=settings answer={answer} ms={StartupHook.Clock.ElapsedMilliseconds}");
        settings.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        StartupHook.Log($"settings closed button={settings.Content}");
    }

    // Untick the first checkbox; the app asks before it lets it go, and the answer is queued to run in that question's loop.
    private static bool Untick(Application app, string answer)
    {
        if (Find<Window>(app, "SettingsWindow") is not { } settings || Named<CheckBox>(settings, "WorkaroundBox0") is not { } box || !StartupHook.Pressable(box))
            return false;
        StartupHook.Log($"settings box={box.Name} id={box.Tag} checked={box.IsChecked}");
        Step(app, "the confirmation's answer", () => Answer(app, answer));
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        StartupHook.Log($"settings answered={answer} checked={box.IsChecked}");
        Step(app, "the Settings window's Close", () => CloseSettings(app));
        return true;
    }

    private static bool Answer(Application app, string answer)
    {
        if (Find<Window>(app, "ConfirmTurnOffWindow") is not { } confirm
            || Named<Button>(confirm, answer == "yes" ? "ConfirmYes" : "ConfirmNo") is not { } button || !StartupHook.Pressable(button))
            return false;
        StartupHook.Log($"settings question={Named<TextBlock>(confirm, "ConfirmQuestion")?.Text}");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return true;
    }

    private static bool CloseSettings(Application app)
    {
        if (Find<Window>(app, "SettingsWindow") is not { } settings || Named<Button>(settings, "SettingsClose") is not { } close || !StartupHook.Pressable(close))
            return false;
        close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return true;
    }

    // Runs step at Background priority until it reports done, or logs a refusal after StepDeadline.
    private static void Step(Application app, string what, Func<bool> step)
    {
        DateTime until = DateTime.UtcNow + StepDeadline;
        void Attempt()
        {
            try
            {
                if (step())
                    return;
            }
            catch (Exception e)
            {
                StartupHook.Log($"refused mode=settings reason={e.GetType().Name} at {what}: {e.Message}");
                return;
            }
            if (DateTime.UtcNow > until)
                StartupHook.Log($"refused mode=settings reason={what} was not pressable within {StepDeadline.TotalSeconds:N0} s");
            else
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Attempt));
        }
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Attempt));
    }

    private static T? Find<T>(Application app, string typeName) where T : Window =>
        app.Windows.OfType<T>().FirstOrDefault(w => w.GetType().Name == typeName && w.IsVisible);

    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match && match.Name == name)
                return match;
            if (child is DependencyObject inner && Named<T>(inner, name) is { } found)
                return found;
        }
        return null;
    }
}
