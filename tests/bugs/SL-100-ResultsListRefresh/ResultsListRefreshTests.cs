using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FsAnalyser.App;
using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace SL100.ResultsListRefresh.Tests;

/// <summary>
/// Bug (SL-100, raised by the SL-047 reflection; also the left-over SC-181 check): a live scan refreshes the results
/// list by assigning a fresh array to TopLevelList.ItemsSource (MainWindow.ShowTopLevel, about every 250 ms). WPF
/// treats that as a Reset: every item container is unlinked (its DataContext becomes {DisconnectedItem} and it leaves
/// the visual tree), the keyboard focus that sat on the focused row's container moves to the ListBox (DataContext
/// null), and a selected row that the tracker rebuilt as a new TopLevelRow loses its selection. The window's
/// Ctrl+Enter gesture (MainWindow.OpenInExplorer) resolves the row from Keyboard.FocusedElement.DataContext, so
/// from the ListBox it finds no row, CanExecute is false and the press does nothing (SL-047 validate: "focus had
/// moved to the ListBox (focusedDC=null), canExecute=false").
///
/// Expected: Ctrl+Enter pressed on a focused results row after a live refresh still opens that row in Explorer, as it
/// does before the refresh: rows, focus and selection are kept alive across the refresh.
///
/// Actual: after one refresh (the focused "reading" row rebuilt as a new object, as TopLevelTracker does for a row
/// still being read) keyboard focus is on the ListBox, CanExecute is false and the gesture executes nothing.
///
/// Measured: the test shows a real MainWindow off screen (no activation, no taskbar button) and drives its own
/// RefreshTopLevel with two hand-made snapshots of five rows; no scan, no disk, no Explorer. The window's real
/// OnOpenInExplorer handler (which starts Explorer) is shadowed by a PreviewExecuted handler on the window that
/// records the press and handles it, so it never runs. Cost: one window, a few hundred milliseconds, nothing left
/// behind; the thread's keyboard state is put back after each press.
/// </summary>
public partial class ResultsListRefreshTests
{
    private const string FocusedName = "reading-row";

    [Fact]
    public void CtrlEnterOnTheFocusedRowStillWorksAfterTheLiveRefresh()
    {
        Outcome outcome = OnStaThread(Scenario);

        // The harness itself: the same press did work on the same row before any refresh, so a failure below is the
        // refresh's doing and not the press's.
        Assert.True(outcome.BeforeRefresh.Count == 1 && outcome.BeforeRefresh[0].StartsWith(FocusedName, StringComparison.Ordinal),
            $"precondition: before any refresh Ctrl+Enter on the focused '{FocusedName}' row executed on [{string.Join("; ", outcome.BeforeRefresh)}]");

        Assert.True(outcome.AfterRefresh.Count == 1 && outcome.AfterRefresh[0].StartsWith(FocusedName, StringComparison.Ordinal),
            $"Ctrl+Enter on the focused '{FocusedName}' row after one live refresh executed on [{string.Join("; ", outcome.AfterRefresh)}], " +
            $"expected exactly one execution on '{FocusedName}'. {outcome.AfterRefreshState}");
    }

    private sealed record Outcome(List<string> BeforeRefresh, List<string> AfterRefresh, string AfterRefreshState);

    private static Outcome Scenario()
    {
        var window = new MainWindow(null)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };
        var executed = new List<string>();
        // The press must never reach the window's own handler: that starts Explorer. The row it would act on is the
        // one the window's handler resolves too, the focused element's.
        CommandManager.AddPreviewExecutedHandler(window, (_, e) =>
        {
            if (e.Command != MainWindow.OpenInExplorer)
                return;
            executed.Add(FocusedRowTitle());
            e.Handled = true;
        });

        try
        {
            window.Show();
            Settle(window);
            Settle(window);
            var list = (ListBox)window.FindName("TopLevelList");
            var progress = new ScanProgress();
            SetField(window, "_progress", progress);

            // The tracker keeps a clean row's object and rebuilds a changed one, the rows still being read, every time.
            TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
            TopLevelRow reading = Row(FocusedName, TopLevelState.Reading, 4000);
            TopLevelRow gamma = Row("gamma", TopLevelState.Done, 3000);
            TopLevelRow delta = Row("delta", TopLevelState.Reading, 2000);
            TopLevelRow epsilon = Row("epsilon", TopLevelState.Done, 1000);
            Tick(window, progress, Snapshot(1, alpha, reading, gamma, delta, epsilon));
            Settle(window);

            // A person's click on the row: selected, and the keyboard focus on its container.
            list.SelectedItem = reading;
            var container = list.ItemContainerGenerator.ContainerFromItem(reading) as ListBoxItem
                ?? throw new InvalidOperationException($"precondition: row {FocusedName} was not realised by the results list");
            container.Focus();
            Keyboard.Focus(container);
            Settle(window);
            if (!FocusedRowTitle().StartsWith(FocusedName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"precondition: the keyboard focus did not land on the '{FocusedName}' row (focus: {FocusedRowTitle()}, window active: {window.IsActive})");

            PressCtrlEnter(window);
            List<string> beforeRefresh = [.. executed];
            executed.Clear();

            // One live refresh: a new snapshot in which the reading rows are new objects.
            Tick(window, progress, Snapshot(2, alpha, Row(FocusedName, TopLevelState.Reading, 4500), gamma, Row("delta", TopLevelState.Reading, 2100), epsilon));
            Settle(window);

            string state = $"After the refresh: keyboard focus on {FocusedRowTitle()} (DataContext {FocusedDataContext()}), " +
                           $"list SelectedIndex {list.SelectedIndex}, Ctrl+Enter CanExecute from the focus {CanExecuteFromFocus()}.";
            PressCtrlEnter(window);
            return new Outcome(beforeRefresh, [.. executed], state);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- The press ----

    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    // Ctrl held in this thread's key state, then a KeyDown(Enter) raised on the focused element: CommandManager matches
    // the command's own KeyGesture as it does for a real press. The key state is put back after.
    private static void PressCtrlEnter(Window window)
    {
        var saved = new byte[256];
        GetKeyboardState(saved);
        var held = (byte[])saved.Clone();
        held[0x11] = 0x80; // VK_CONTROL
        held[0xA2] = 0x80; // VK_LCONTROL
        SetKeyboardState(held);
        try
        {
            if (Keyboard.FocusedElement is UIElement target)
            {
                var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                };
                target.RaiseEvent(press);
            }
        }
        finally
        {
            SetKeyboardState(saved);
        }
    }

    private static bool CanExecuteFromFocus() =>
        Keyboard.FocusedElement is IInputElement focus && MainWindow.OpenInExplorer.CanExecute(null, focus);

    // ---- The window ----

    // What the timer tick does with a new snapshot: the scanner publishes it, and the next tick shows it. A refresh
    // that ran long makes the next tick skip, which a cold first layout can do; it is cleared so the tick shows.
    private static void Tick(MainWindow window, ScanProgress progress, TopLevelSnapshot snapshot)
    {
        typeof(ScanProgress).GetMethod("PublishTopLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(progress, [snapshot]);
        SetField(window, "_skipNextRefresh", false);
        MethodInfo refresh = typeof(MainWindow).GetMethod("RefreshTopLevel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "RefreshTopLevel");
        refresh.Invoke(window, BindingFlags.DoNotWrapExceptions, null, null, null);
        if (!ReferenceEquals(window.ShownTopLevelForTest(), snapshot))
            throw new InvalidOperationException($"precondition: the tick did not show snapshot {snapshot.Sequence}");
    }

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);

    // Lets layout, the Loaded work and focus re-evaluation run, as the dispatcher would between two ticks.
    private static void Settle(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    // The first line of the focused row's container, which is its title, or the type of what has the focus instead.
    private static string FocusedRowTitle()
    {
        if (Keyboard.FocusedElement is not ListBoxItem item)
            return Keyboard.FocusedElement?.GetType().Name ?? "nothing";
        return Descendants(item).OfType<TextBlock>().FirstOrDefault()?.Text ?? "(a row with no title)";
    }

    private static string FocusedDataContext()
    {
        object? context = (Keyboard.FocusedElement as FrameworkElement)?.DataContext;
        return context is null ? "null" : context.GetType().Name;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject below in Descendants(child))
                yield return below;
        }
    }

    // ---- The rows ----

    // TopLevelRow and TopLevelSnapshot are built only by Core's tracker, and this assembly sees none of Core's internals.
    private static TopLevelRow Row(string name, TopLevelState state, long bytes)
    {
        ConstructorInfo ctor = typeof(TopLevelRow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (TopLevelRow)ctor.Invoke([name, $@"C:\fsa-sl100\{name}", EntryKind.Folder, state, bytes, bytes, 1L, 1L, 0L]);
    }

    private static TopLevelSnapshot Snapshot(long sequence, params TopLevelRow[] rows)
    {
        ConstructorInfo ctor = typeof(TopLevelSnapshot).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (TopLevelSnapshot)ctor.Invoke([@"C:\fsa-sl100", sequence, false, false, rows, new ScanTotals(rows.Length, rows.Length, 0, 0, 0)]);
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T value = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = work();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return value;
    }
}

internal static class MainWindowTestAccess
{
    // MainWindow.ShownTopLevel is internal and this assembly is not shown it.
    public static TopLevelSnapshot? ShownTopLevelForTest(this MainWindow window) =>
        (TopLevelSnapshot?)typeof(MainWindow).GetProperty("ShownTopLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
}
