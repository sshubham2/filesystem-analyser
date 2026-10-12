using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/// <summary>
/// FSA_HOOK_ROWS=1 (SL-089): what the unreadable-folders panel shows, row by row, written to FSA_HOOK_LOG as JSON lines
/// once the app shuts down. The schema-1 report carries no row and never a path (Q-016), so this is the one way a case
/// reads what a row says, and it reads the panel the person sees, never the engine's record: each line is one item of
/// UnreadableList, its Path, Kind, Win32Error and Reason read by reflection from the app's own UnreadableRow, and the
/// texts its container draws.
/// <code>
///   FSA_HOOK_MODE=rows|stop  FSA_HOOK_ROWS=1  FSA_HOOK_LOG=&lt;file&gt;  [FSA_HOOK_FAIL_LISTING=...]
/// </code>
/// With FSA_HOOK_MODE=rows the hook presses nothing and has no deadline of its own: the scan runs to whatever ending it
/// reaches, however long a whole drive takes, and the listing failpoint is armed as under any mode. With stop it runs
/// beside stop mode's press. Any other mode, or a value other than 1, is refused.
///
/// A class handler for the main window's first Loaded, which runs ahead of the window's own handler that starts the scan,
/// subscribes to the window's EndingShown (internal, reached by reflection). That event comes before the report's
/// Loaded-priority read, and after the ending has filled the panel and selected its first section. There the unreadable
/// folders' tab is selected, as a click on its header would, and the window laid out, so the first screenful of rows has
/// containers (review m2: a tab selected any earlier is undone by the scan's start and the ending's own selection). Every
/// item is read then and kept: the item's four values, and, when its container was realised, every non-empty TextBlock
/// text under it ("shown"); a row never realised has "shown": null, which is how most rows of a whole drive's panel are.
/// At Dispatcher.ShutdownStarted, which comes after the report is written, the kept rows are written, one line each,
/// then a closing line:
/// <code>
///   {"event":"notScanned","i":n,"path":text,"shown":[text]|null}
///   {"event":"row","i":n,"path":text,"kind":"NothingListed"|"OpenedNothingListed"|"ListedPartway","code":n,"reason":text,"shown":[text]|null}
///   {"event":"rows","count":n,"notScanned":n,"selected":bool,"elevated":bool,"stopUtc":time|null,"atUtc":time}
/// </code>
/// A notScanned line is a folder SL-095 left out on purpose (the Windows container layer store), which the same list
/// shows ahead of the failed folders: it is never a row and never in count.
/// selected is whether the tab was selected when the rows were read; elevated is this process's token in the
/// Administrators role; stopUtc is when stop mode pressed Cancel, null when nothing was pressed. When the panel cannot be
/// read (no UnreadableList, an UnreadableRow member renamed, no ending before shutdown) the closing line is
/// {"event":"rows","error":text} and no row line is written, so a case never reads an empty list as an empty panel.
/// Plain lines go to the log too: "rows armed ms=n", and "rows read count=n realized=n selected=b ms=n" at the ending.
/// </summary>
internal static class RowsMode
{
    private static readonly string[] Members = ["Path", "Kind", "Win32Error", "Reason"];

    // Written and read on the UI thread only.
    private static bool _attached;
    private static bool _read;
    private static string? _error;
    private static bool _selected;
    private static readonly List<Dictionary<string, object?>> Rows = [];
    private static readonly List<Dictionary<string, object?>> NotScanned = [];

    /// <summary>When stop mode pressed Cancel, round-trip UTC; null until it does.</summary>
    internal static string? StopUtc;

    internal static void Arm(string mode)
    {
        if (StartupHook.WaitForApplication(mode) is not { } app)
            return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach(app, (Window)sender)));
        StartupHook.Log($"rows armed ms={StartupHook.Clock.ElapsedMilliseconds}");
        if (app.Dispatcher.Invoke(() => !_attached && app.MainWindow is { IsLoaded: true }))
            StartupHook.Log($"refused mode={mode} reason=the window loaded before the hook could watch its ending");
    }

    private static void Attach(Application app, Window window)
    {
        if (_attached || !ReferenceEquals(window, app.MainWindow))
            return;
        _attached = true;
        app.Dispatcher.ShutdownStarted += (_, _) => Write();
        MethodInfo? add = window.GetType().GetEvent("EndingShown", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetAddMethod(nonPublic: true);
        if (add is null)
        {
            _error = "the window has no EndingShown";
            return;
        }
        EventHandler shown = (_, _) => Read(window);
        add.Invoke(window, [shown]);
    }

    // On the UI thread, inside EndingShown: only the first ending counts, as for the report.
    private static void Read(Window window)
    {
        if (_read)
            return;
        _read = true;
        try
        {
            if (window.FindName("UnreadableList") is not ListBox list)
            {
                _error = "the window has no UnreadableList";
                return;
            }
            if (window.FindName("UnreadableTab") is TabItem { Visibility: Visibility.Visible } tab && list.Items.Count > 0)
            {
                tab.IsSelected = true; // what a click on its header sets
                window.UpdateLayout();
                _selected = tab.IsSelected;
            }
            int realized = 0;
            for (int i = 0; i < list.Items.Count; i++)
            {
                object item = list.Items[i];
                List<string>? drawn = null;
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject drawnBy)
                {
                    drawn = [];
                    Texts(drawnBy, drawn);
                }
                // SL-095 puts the folders left out on purpose in the same list, ahead of the failed ones. They are not
                // failed folders: each is written as a line of its own kind and never counted among the rows.
                if (item.GetType().Name == "NotScannedRow")
                {
                    NotScanned.Add(new Dictionary<string, object?>
                    {
                        ["event"] = "notScanned",
                        ["i"] = i,
                        ["path"] = item.GetType().GetProperty("Path")?.GetValue(item) as string,
                        ["shown"] = drawn,
                    });
                    continue;
                }
                var values = new object?[Members.Length];
                for (int m = 0; m < Members.Length; m++)
                {
                    PropertyInfo? property = item.GetType().GetProperty(Members[m]);
                    if (property is null)
                    {
                        _error = $"the panel's row type {item.GetType().Name} has no {Members[m]}";
                        Rows.Clear();
                        NotScanned.Clear();
                        return;
                    }
                    values[m] = property.GetValue(item);
                }
                if (drawn is not null)
                    realized++;
                Rows.Add(new Dictionary<string, object?>
                {
                    ["event"] = "row",
                    ["i"] = i,
                    ["path"] = values[0] as string,
                    ["kind"] = values[1]?.ToString(),
                    ["code"] = values[2] is int code ? code : null,
                    ["reason"] = values[3] as string,
                    ["shown"] = drawn,
                });
            }
            StartupHook.Log($"rows read count={Rows.Count} notScanned={NotScanned.Count} realized={realized} selected={_selected} ms={StartupHook.Clock.ElapsedMilliseconds}");
        }
        catch (Exception e)
        {
            // Never onto the dispatcher: the app would take it for its own fault and exit 70.
            _error = $"{e.GetType().Name}: {e.Message}";
            Rows.Clear();
            NotScanned.Clear();
        }
    }

    private static void Texts(DependencyObject element, List<string> texts)
    {
        if (element is TextBlock { Text: { Length: > 0 } text })
            texts.Add(text);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            Texts(VisualTreeHelper.GetChild(element, i), texts);
    }

    private static void Write()
    {
        try
        {
            string? error = _error ?? (_read ? null : "no ending was shown before the app shut down");
            if (error is not null)
            {
                StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?> { ["event"] = "rows", ["error"] = error }));
                return;
            }
            foreach (Dictionary<string, object?> row in NotScanned)
                StartupHook.Log(JsonSerializer.Serialize(row));
            foreach (Dictionary<string, object?> row in Rows)
                StartupHook.Log(JsonSerializer.Serialize(row));
            bool elevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            StartupHook.Log(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["event"] = "rows",
                ["count"] = Rows.Count,
                ["notScanned"] = NotScanned.Count,
                ["selected"] = _selected,
                ["elevated"] = elevated,
                ["stopUtc"] = StopUtc,
                ["atUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            }));
        }
        catch (Exception e)
        {
            StartupHook.Log($"refused mode=rows reason={e.GetType().Name}: {e.Message}");
        }
    }
}
