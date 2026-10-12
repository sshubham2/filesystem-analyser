using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FsAnalyser.App;
using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;

namespace SL100.ResultsListRefresh.Tests;

/// <summary>
/// The shapes the fix has to hold for (SL-100), on the real MainWindow driven as the repro does: its own
/// RefreshTopLevel with hand-made snapshots, a window off screen, no scan, no disk, no Explorer. After a refresh the
/// results list has to give back what the Reset took: the selection of a row the tracker rebuilt as a new object,
/// and the keyboard focus on that row's new container, so Ctrl+Enter, the arrow keys and the menu act on the row
/// the person was on. It must also leave alone every focus that is not on the list, and every row the refresh moved
/// out of view.
/// </summary>
public partial class ResultsListRefreshTests
{
    // ---- Rows that were unchanged, rebuilt and reordered ----

    [Fact]
    public void FocusFollowsAnUnchangedRowAcrossARefresh() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        TopLevelRow beta = Row("beta", TopLevelState.Done, 4000);
        TopLevelRow gamma = Row("gamma", TopLevelState.Done, 3000);
        rig.Show(alpha, beta, gamma, Row("delta", TopLevelState.Reading, 2000));
        rig.FocusRow(beta);
        rig.AssertPressReaches(beta, "before the refresh");

        // The tracker kept the focused row's object: the selection survives the Reset by itself, the focus does not.
        rig.Show(alpha, beta, gamma, Row("delta", TopLevelState.Reading, 2100));
        rig.AssertPressReaches(beta, "after a refresh that kept the focused row's object");
    });

    [Fact]
    public void FocusAndSelectionFollowARebuiltRowThatMovesToTheTop() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        TopLevelRow gamma = Row("gamma", TopLevelState.Done, 3000);
        TopLevelRow delta = Row("delta", TopLevelState.Done, 2000);
        TopLevelRow reading = Row(FocusedName, TopLevelState.Reading, 4000);
        rig.Show(alpha, reading, gamma, delta);
        rig.FocusRow(reading);
        rig.AssertPressReaches(reading, "before the refresh");

        TopLevelRow first = Row(FocusedName, TopLevelState.Reading, 4500);
        rig.Show(alpha, first, gamma, delta);
        Assert.Equal(1, rig.List.SelectedIndex);
        rig.AssertPressReaches(first, "after the first refresh (rebuilt, still second)");

        // It grows past the largest row: the refresh that moves it is the one that broke every Move-based design.
        TopLevelRow second = Row(FocusedName, TopLevelState.Reading, 6000);
        rig.Show(alpha, second, gamma, delta);
        Assert.Equal(0, rig.List.SelectedIndex);
        rig.AssertPressReaches(second, "after the second refresh (rebuilt, now first)");

        TopLevelRow third = Row(FocusedName, TopLevelState.Reading, 6500);
        rig.Show(alpha, third, gamma, delta);
        Assert.Equal(0, rig.List.SelectedIndex);
        rig.AssertPressReaches(third, "after the third refresh (rebuilt, still first)");
    });

    [Fact]
    public void ArrowKeysMoveOnFromTheRestoredRow() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        TopLevelRow gamma = Row("gamma", TopLevelState.Done, 3000);
        TopLevelRow delta = Row("delta", TopLevelState.Done, 2000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000), gamma, delta);
        rig.FocusRow(rig.Current(FocusedName));

        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4500), gamma, delta);
        PressKey(rig.Window, Key.Down);
        Settle(rig.Window);

        // From a focused container Down goes to the next row; from the list itself it would start again at the first.
        Assert.True(ReferenceEquals(rig.List.SelectedItem, gamma) && ReferenceEquals((Keyboard.FocusedElement as ListBoxItem)?.DataContext, gamma),
            $"Down on the restored row should have moved the selection and the focus to 'gamma'. {rig.Describe()}");
        rig.AssertNoFaults();
    });

    // ---- The place in the list ----

    [Fact]
    public void AScrolledFocusedRowKeepsFocusAndOffset() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow[] rows = Rows(80);
        rig.Show(rows);
        ScrollViewer scroll = rig.Scroll;
        scroll.ScrollToVerticalOffset(30);
        rig.Window.UpdateLayout();
        Settle(rig.Window);
        Assert.True(scroll.VerticalOffset == 30 && scroll.ViewportHeight >= 4,
            $"precondition: the list scrolled to row 30 (offset {scroll.VerticalOffset}, viewport {scroll.ViewportHeight} rows)");

        // A row fully in view a little way down: Focus() brings a partly visible row into view, which would move the offset.
        TopLevelRow focused = rows[32];
        rig.FocusRow(focused);
        Assert.Equal(30, scroll.VerticalOffset);

        TopLevelRow rebuilt = Rebuilt(focused, focused.Bytes + 10);
        rig.Show(Replace(rows, focused, rebuilt));
        Assert.Equal(30, scroll.VerticalOffset);
        rig.AssertPressReaches(rebuilt, "after a refresh of a list scrolled to row 30");
    });

    [Fact]
    public void ARowPushedOutOfViewStaysSelectedAndRegainsFocus() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow[] rows = Rows(80);
        rig.Show(rows);
        TopLevelRow focused = rows[3];
        rig.FocusRow(focused);
        ScrollViewer scroll = rig.Scroll;

        // It shrinks below every other row, far outside the viewport: it has no container to focus.
        TopLevelRow pushed = Rebuilt(focused, 1);
        rig.Show(Replace(rows, focused, pushed));
        Assert.True(ReferenceEquals(rig.List.SelectedItem, pushed), $"the pushed row should stay selected. {rig.Describe()}");
        Assert.True(ReferenceEquals(Keyboard.FocusedElement, rig.List), $"focus should stay on the list. {rig.Describe()}");
        Assert.Null(rig.List.ItemContainerGenerator.ContainerFromItem(pushed));
        Assert.Equal(0, scroll.VerticalOffset);

        // A later refresh brings it back into view: the focus is on the list itself, so the row is focused again.
        TopLevelRow back = Rebuilt(focused, rows[1].Bytes - 5);
        rig.Show(Replace(rows, focused, back));
        Assert.Equal(0, scroll.VerticalOffset);
        rig.AssertPressReaches(back, "after a refresh that brought the row back into view");
    });

    // ---- Focus that is not on the list ----

    [Fact]
    public void FocusOutsideTheListIsNeverMoved() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));

        // The scanning panel, and with it the Cancel button, is only shown by a scan: this is a button that is shown.
        var button = (Button)rig.Window.FindName("SettingsButton");
        button.Focus();
        Settle(rig.Window);
        Assert.True(button.IsKeyboardFocused, $"precondition: the Settings button has the keyboard focus. {rig.Describe()}");

        TopLevelRow rebuilt = Row(FocusedName, TopLevelState.Reading, 4500);
        rig.Show(alpha, rebuilt);
        Assert.True(button.IsKeyboardFocused, $"a refresh took the focus from the Settings button. {rig.Describe()}");
        Assert.True(ReferenceEquals(rig.List.SelectedItem, rebuilt), $"the selected row should still be reselected. {rig.Describe()}");
        rig.AssertNoFaults();
    });

    [Fact]
    public void FocusThatMovesAwayBeforeTheRestoreRunsIsNotTakenBack() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));
        var button = (Button)rig.Window.FindName("SettingsButton");

        // The focus sat on the row when the swap unlinked its container, and something else takes it before the tick's
        // Loaded work runs (that work is queued behind this one): the restore finds it somewhere a person put it.
        rig.Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => button.Focus());
        TopLevelRow rebuilt = Row(FocusedName, TopLevelState.Reading, 4500);
        rig.Swap(alpha, rebuilt);
        Settle(rig.Window);

        Assert.True(button.IsKeyboardFocused, $"the restore took the focus back from the Settings button. {rig.Describe()}");
        Assert.True(ReferenceEquals(rig.List.SelectedItem, rebuilt), $"the selected row should still be reselected. {rig.Describe()}");
        rig.AssertNoFaults();
    });

    [Fact]
    public void NoKeyboardFocusIsNeverTakenByARefresh() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));

        // A real inactive window: another window of this thread is the active one, so WPF has taken the keyboard focus away.
        rig.Deactivate();
        Assert.True(!rig.Window.IsActive && Keyboard.FocusedElement is null,
            $"precondition: the main window is inactive and nothing has the keyboard focus (active {rig.Window.IsActive}). {rig.Describe()}");

        TopLevelRow rebuilt = Row(FocusedName, TopLevelState.Reading, 4500);
        rig.Show(alpha, rebuilt);
        Assert.True(Keyboard.FocusedElement is null, $"a refresh of an inactive window took the keyboard focus. {rig.Describe()}");
        Assert.True(ReferenceEquals(rig.List.SelectedItem, rebuilt), $"the selected row should still be reselected. {rig.Describe()}");
        rig.AssertNoFaults();
    });

    // ---- A list that was replaced ----

    [Fact]
    public void ARestoreForAReplacedListDoesNothing() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));

        // The tick's swap, then the list is cleared before the tick's Loaded work has run (the scan ended, or a new
        // one started): that work has no list left to restore.
        rig.Swap(alpha, Row(FocusedName, TopLevelState.Reading, 4500));
        typeof(MainWindow).GetMethod("ClearTopLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Window, null);
        Settle(rig.Window);

        Assert.Null(rig.List.ItemsSource);
        Assert.Null(rig.List.SelectedItem);
        Assert.True(Keyboard.FocusedElement is not ListBoxItem, $"no row can have the focus in a list that has no rows. {rig.Describe()}");
        rig.AssertNoFaults();
    });

    [Fact]
    public void AStopThatLandsBetweenTheSwapAndItsRestoreKeepsTheRowSelectedAndFocused() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));

        // Cancel's ending runs at Normal priority, before the tick's Loaded work. The tick's own restore is for a list
        // that is gone by then, and the final snapshot is the one that has to find the row again.
        rig.Swap(alpha, Row(FocusedName, TopLevelState.Reading, 4500));
        TopLevelRow stopped = Row(FocusedName, TopLevelState.StoppedPartway, 4600);
        typeof(MainWindow).GetMethod("ShowFinalTopLevel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.Window, [FinalSnapshot(3, alpha, stopped)]);
        Settle(rig.Window);

        rig.AssertPressReaches(stopped, "after a stop that landed between a tick's swap and its restore");
    });

    [Fact]
    public void AKeyQueuedRightAfterTheSwapReachesTheRow() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));

        // A key that arrived in the same tick, straight after the swap: queued at Input priority, behind the Loaded work.
        TopLevelRow rebuilt = Row(FocusedName, TopLevelState.Reading, 4500);
        List<object?> ran = [];
        rig.Swap(alpha, rebuilt);
        rig.Window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ran = rig.Press());
        Settle(rig.Window);

        Assert.True(ran.Count == 1 && ReferenceEquals(ran[0], rebuilt),
            $"Ctrl+Enter queued straight after the swap executed on [{string.Join("; ", ran.Select(RowName))}], expected once on '{FocusedName}'. {rig.Describe()}");
        rig.AssertNoFaults();
    });

    // ---- The item type ----

    [Fact]
    public void TheListStillHoldsANewArrayOfTopLevelRowsPerRefresh() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        object? before = rig.List.ItemsSource;
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4500));

        // The readers (WindowReport, the window hook, the SL-063 tests) enumerate TopLevelList.Items as TopLevelRow.
        Assert.IsType<TopLevelRow[]>(rig.List.ItemsSource);
        Assert.NotSame(before, rig.List.ItemsSource);
        Assert.All(rig.List.Items.Cast<object>(), item => Assert.IsType<TopLevelRow>(item));
    });

    [Fact]
    public void TwoRowsThatDifferOnlyByCaseKeepTheExactOneSelected() => Sta(() =>
    {
        using var rig = new Rig();
        // A case-sensitive folder can hold both; the rows come in the order they are listed and are sorted by size.
        rig.Show(Row("README", TopLevelState.Reading, 5000), Row("readme", TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current("readme"));

        TopLevelRow upper = Row("README", TopLevelState.Reading, 5100);
        TopLevelRow lower = Row("readme", TopLevelState.Reading, 4100);
        rig.Show(upper, lower);
        rig.AssertPressReaches(lower, "after a refresh of a list with two names that differ only by case");
    });

    // ---- The rig ----

    private static void Sta(Action work) => OnStaThread<object?>(() =>
    {
        work();
        return null;
    });

    private static string RowName(object? row) => row is TopLevelRow r ? r.Name : row?.GetType().Name ?? "null";

    private static TopLevelRow[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Row($"r{i:D2}", TopLevelState.Done, 1_000_000 - i * 1000L))];

    private static TopLevelRow Rebuilt(TopLevelRow row, long bytes) => Row(row.Name, TopLevelState.Reading, bytes);

    private static TopLevelRow[] Replace(TopLevelRow[] rows, TopLevelRow old, TopLevelRow replacement) =>
        [.. rows.Select(row => ReferenceEquals(row, old) ? replacement : row)];

    private static TopLevelSnapshot FinalSnapshot(long sequence, params TopLevelRow[] rows)
    {
        ConstructorInfo ctor = typeof(TopLevelSnapshot).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (TopLevelSnapshot)ctor.Invoke([@"C:\fsa-sl100", sequence, true, false, rows, new ScanTotals(rows.Length, rows.Length, 0, 0, 0)]);
    }

    // A KeyDown raised on the focused element, with no modifier held: the list's own key handling takes it from there.
    private static void PressKey(Window window, Key key)
    {
        if (Keyboard.FocusedElement is UIElement target)
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
    }

    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);

    private sealed partial class Rig : IDisposable
    {
        private readonly List<Window> _others = [];
        private readonly List<Exception> _faults = [];
        private readonly List<object?> _executed = [];
        private long _sequence;

        public Rig()
        {
            Window = new MainWindow(null)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
            };
            // The press never reaches the window's own handler, which starts Explorer; what it would act on is recorded.
            CommandManager.AddPreviewExecutedHandler(Window, (_, e) =>
            {
                if (e.Command != MainWindow.OpenInExplorer)
                    return;
                _executed.Add(e.Parameter ?? (Keyboard.FocusedElement as FrameworkElement)?.DataContext);
                e.Handled = true;
            });
            // A fault in the restore would otherwise end the thread's dispatcher; it is kept to be asserted on.
            Window.Dispatcher.UnhandledException += (_, e) =>
            {
                _faults.Add(e.Exception);
                e.Handled = true;
            };
            Window.Show();
            Settle(Window);
            Settle(Window);
            List = (ListBox)Window.FindName("TopLevelList");
            SetField(Window, "_progress", Progress);
        }

        public MainWindow Window { get; }
        public ListBox List { get; }
        public ScanProgress Progress { get; } = new();
        public ScrollViewer Scroll => Descendants(List).OfType<ScrollViewer>().First();

        /// <summary>One timer tick with a new snapshot of these rows, not yet settled: its Loaded work is still queued.</summary>
        public void Swap(params TopLevelRow[] rows) => Tick(Window, Progress, Snapshot(++_sequence, rows));

        /// <summary>One tick with a new snapshot, then the time the dispatcher has before the next.</summary>
        public void Show(params TopLevelRow[] rows)
        {
            Swap(rows);
            Settle(Window);
        }

        public TopLevelRow Current(string name) => List.Items.Cast<TopLevelRow>().Single(row => row.Name == name);

        public ListBoxItem Container(TopLevelRow row) =>
            List.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem
            ?? throw new InvalidOperationException($"precondition: row {row.Name} was not realised by the results list");

        /// <summary>A person's click on the row: selected, and the keyboard focus on its container.</summary>
        public void FocusRow(TopLevelRow row)
        {
            ListBoxItem container = Container(row);
            List.SelectedItem = row;
            container.Focus();
            Keyboard.Focus(container);
            Settle(Window);
            if (!ReferenceEquals(Keyboard.FocusedElement, container))
                throw new InvalidOperationException($"precondition: the keyboard focus did not land on row {row.Name}. {Describe()} (window active: {Window.IsActive})");
        }

        public List<object?> Press()
        {
            _executed.Clear();
            PressCtrlEnter(Window);
            return [.. _executed];
        }

        public void AssertPressReaches(TopLevelRow expected, string when)
        {
            string state = Describe();
            Assert.True(Keyboard.FocusedElement is ListBoxItem { IsKeyboardFocused: true } item && ReferenceEquals(item.DataContext, expected),
                $"{when}: keyboard focus should be on the container of {expected.Name}'s current row object. {state}");
            Assert.True(ReferenceEquals(List.SelectedItem, expected), $"{when}: {expected.Name}'s current row object should be selected. {state}");
            List<object?> ran = Press();
            Assert.True(ran.Count == 1 && ReferenceEquals(ran[0], expected),
                $"{when}: Ctrl+Enter executed on [{string.Join("; ", ran.Select(RowName))}], expected exactly once on {expected.Name}. {state}");
            AssertNoFaults();
        }

        public void AssertNoFaults() => Assert.True(_faults.Count == 0, $"the dispatcher caught: {string.Join(" | ", _faults.Select(f => f.ToString()))}");

        public string Describe()
        {
            IInputElement? focus = Keyboard.FocusedElement;
            string where = focus switch
            {
                null => "nothing",
                ListBoxItem { DataContext: TopLevelRow row } => $"row {row.Name}",
                FrameworkElement element => $"{element.GetType().Name} (DataContext {FocusedDataContext()})",
                _ => focus.GetType().Name,
            };
            return $"Keyboard focus on {where}; selected {(List.SelectedItem is TopLevelRow selected ? selected.Name : "nothing")} " +
                   $"(index {List.SelectedIndex}); CanExecute from the focus {CanExecuteFromFocus()}; window active {Window.IsActive}.";
        }

        /// <summary>
        /// Another window of this thread becomes the active one and nothing in this thread has the keyboard focus, as when
        /// another program has the foreground: the main window is not active and WPF reports no focused element.
        /// </summary>
        public Window Deactivate()
        {
            var other = new Window
            {
                Width = 120,
                Height = 80,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -3800,
            };
            _others.Add(other);
            other.Show();
            SetActiveWindow(new WindowInteropHelper(other).Handle);
            SetFocus(IntPtr.Zero);
            Settle(Window);
            return other;
        }

        /// <summary>The main window becomes the active one again, as when the person comes back to it.</summary>
        public void Reactivate()
        {
            SetActiveWindow(new WindowInteropHelper(Window).Handle);
            Settle(Window);
        }

        public void Dispose()
        {
            foreach (Window other in _others)
                other.Close();
            Window.Close();
        }
    }
}
