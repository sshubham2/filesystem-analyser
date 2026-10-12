using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FsAnalyser.App;
using FsAnalyser.Core;

namespace SL100.ResultsListRefresh.Tests;

/// <summary>
/// Where the keyboard focus goes when it comes back to a row container that a refresh unlinked (SL-100, review m7):
/// a row menu that closes, a window that is activated again, a modal dialog that closes. WPF hands the focus to the
/// window itself, not to the list; a refresh has to put it back on the selected row. The class is enumerated in
/// FocusReturnCorpus.json, one row per ordinary way of leaving and coming back, and every row runs here.
/// </summary>
public partial class ResultsListRefreshTests
{
    private static readonly string[] CorpusSteps =
    [
        "focus-row", "open-menu", "close-menu", "choose-menu-item", "deactivate", "reactivate", "modal-dialog-with-refresh",
        "focus-button", "clear-focus", "refresh",
    ];

    private static readonly string[] CorpusExpects = ["press-reaches-row", "press-dead", "focus-untouched-button", "menu-open-and-acts-on-row"];

    public static TheoryData<string> CorpusRowIds() => [.. LoadCorpus().GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()!)];

    private static JsonElement LoadCorpus() =>
        JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "FocusReturnCorpus.json"))).RootElement;

    [Fact]
    public void TheCorpusUsesOnlyTheWordsItDefines()
    {
        JsonElement corpus = LoadCorpus();
        string[] ids = [.. corpus.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()!)];
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(CorpusSteps.Order(), corpus.GetProperty("steps").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(CorpusExpects.Order(), corpus.GetProperty("expects").EnumerateObject().Select(p => p.Name).Order());
        foreach (JsonElement row in corpus.GetProperty("rows").EnumerateArray())
        {
            Assert.All(row.GetProperty("steps").EnumerateArray().Select(step => step.GetString()), step => Assert.Contains(step, CorpusSteps));
            Assert.Contains(row.GetProperty("expect").GetString(), CorpusExpects);
            Assert.Equal("focus-row", row.GetProperty("steps")[0].GetString());
        }
    }

    [Theory]
    [MemberData(nameof(CorpusRowIds))]
    public void EveryFocusReturnInTheCorpusEndsOnTheRowOrIsAMeasuredGap(string id)
    {
        JsonElement row = LoadCorpus().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        string[] steps = [.. row.GetProperty("steps").EnumerateArray().Select(step => step.GetString()!)];
        string expect = row.GetProperty("expect").GetString()!;
        string producer = row.GetProperty("producer").GetString()!;
        Sta(() => RunCorpusRow(id, producer, steps, expect));
    }

    private static void RunCorpusRow(string id, string producer, string[] steps, string expect)
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        ContextMenu? menu = null;
        int refreshes = 0;
        string when = $"{id} ({producer})";

        void Refresh() => rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4500 + ++refreshes));

        foreach (string step in steps)
        {
            switch (step)
            {
                case "focus-row":
                    rig.FocusRow(rig.Current(FocusedName));
                    break;
                case "open-menu":
                    menu = rig.OpenMenu(rig.Current(FocusedName));
                    break;
                case "close-menu":
                    menu!.IsOpen = false;
                    Settle(rig.Window);
                    break;
                case "choose-menu-item":
                    List<object?> ran = rig.ChooseMenuItem(menu!);
                    Assert.True(ran.Count == 1 && ran[0] is TopLevelRow { Name: FocusedName }, $"{when}: the menu entry executed on [{string.Join("; ", ran.Select(RowName))}], expected once on the row it was opened on");
                    Settle(rig.Window);
                    break;
                case "deactivate":
                    rig.Deactivate();
                    break;
                case "reactivate":
                    rig.Reactivate();
                    break;
                case "modal-dialog-with-refresh":
                    rig.ModalDialogAround(Refresh);
                    break;
                case "focus-button":
                    ((Button)rig.Window.FindName("SettingsButton")).Focus();
                    Settle(rig.Window);
                    Assert.True(((Button)rig.Window.FindName("SettingsButton")).IsKeyboardFocused, $"{when}: precondition: the Settings button has the focus. {rig.Describe()}");
                    break;
                case "clear-focus":
                    Keyboard.ClearFocus();
                    Settle(rig.Window);
                    Assert.True(rig.Window.IsActive && Keyboard.FocusedElement is null, $"{when}: precondition: an active window with nothing focused. {rig.Describe()}");
                    break;
                case "refresh":
                    Refresh();
                    break;
                default:
                    throw new InvalidOperationException($"{id}: unknown step {step}");
            }
        }

        TopLevelRow current = rig.Current(FocusedName);
        switch (expect)
        {
            case "press-reaches-row":
                rig.AssertPressReaches(current, when);
                break;
            case "press-dead":
                Assert.True(ReferenceEquals(rig.List.SelectedItem, current), $"{when}: the row should still be selected. {rig.Describe()}");
                List<object?> dead = rig.Press();
                Assert.True(dead.Count == 0, $"{when}: Ctrl+Enter was measured dead here, and executed on [{string.Join("; ", dead.Select(RowName))}]. {rig.Describe()}");
                break;
            case "focus-untouched-button":
                Assert.True(((Button)rig.Window.FindName("SettingsButton")).IsKeyboardFocused, $"{when}: a refresh took the focus from the Settings button. {rig.Describe()}");
                Assert.True(ReferenceEquals(rig.List.SelectedItem, current), $"{when}: the row should still be selected. {rig.Describe()}");
                break;
            case "menu-open-and-acts-on-row":
                Assert.True(menu!.IsOpen && Keyboard.FocusedElement is not ListBoxItem, $"{when}: the open menu lost the focus to a row, or closed. Menu open {menu.IsOpen}. {rig.Describe()}");
                List<object?> chosen = rig.ChooseMenuItem(menu);
                Assert.True(chosen.Count == 1 && chosen[0] is TopLevelRow { Name: FocusedName }, $"{when}: the menu entry executed on [{string.Join("; ", chosen.Select(RowName))}], expected once on the row it was opened on");
                break;
            default:
                throw new InvalidOperationException($"{id}: unknown expectation {expect}");
        }
        rig.AssertNoFaults();
    }

    // ---- The named shapes the corpus rows generalise ----

    [Fact]
    public void AnOpenRowMenuSurvivesARefresh() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        TopLevelRow row = rig.Current(FocusedName);
        rig.FocusRow(row);
        ContextMenu menu = rig.OpenMenu(row);
        Assert.True(Keyboard.FocusedElement is ContextMenu, $"precondition: an open row menu holds the keyboard focus. {rig.Describe()}");

        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4500));
        Assert.True(menu.IsOpen, $"a refresh closed the open row menu. {rig.Describe()}");
        Assert.True(Keyboard.FocusedElement is not ListBoxItem, $"a refresh moved the focus out of the open menu onto a row. {rig.Describe()}");

        // The entry still acts on the row the menu was opened on, though its container is gone (Tag, CommandTarget = window).
        List<object?> ran = rig.ChooseMenuItem(menu);
        Assert.True(ran.Count == 1 && ReferenceEquals(ran[0], row), $"the menu entry executed on [{string.Join("; ", ran.Select(RowName))}], expected once on the row it was opened on");

        // Closed, WPF hands the focus back to a container that is gone: the window gets it. The next refresh gives it to the row.
        menu.IsOpen = false;
        Settle(rig.Window);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4600));
        rig.AssertPressReaches(rig.Current(FocusedName), "after the menu closed and one more refresh");
    });

    [Fact]
    public void AWindowActivatedAgainAfterARefreshGetsItsRowFocusedAtTheNextRefresh() => Sta(() =>
    {
        using var rig = new Rig();
        TopLevelRow alpha = Row("alpha", TopLevelState.Done, 5000);
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4000));
        rig.FocusRow(rig.Current(FocusedName));

        rig.Deactivate();
        Assert.True(!rig.Window.IsActive && Keyboard.FocusedElement is null, $"precondition: an inactive window with nothing focused. {rig.Describe()}");
        TopLevelRow whileAway = Row(FocusedName, TopLevelState.Reading, 4500);
        rig.Show(alpha, whileAway);
        Assert.True(ReferenceEquals(rig.List.SelectedItem, whileAway) && Keyboard.FocusedElement is null,
            $"a refresh of an inactive window should reselect the row and take no focus. {rig.Describe()}");

        // Back in the window WPF has no container to return the focus to, so the window has it: measured, not repaired until a refresh.
        rig.Reactivate();
        Assert.True(Keyboard.FocusedElement is Window, $"on return the focus should be on the window itself. {rig.Describe()}");
        rig.Show(alpha, Row(FocusedName, TopLevelState.Reading, 4600));
        rig.AssertPressReaches(rig.Current(FocusedName), "after the window was activated again and the next refresh");
    });

    // ---- More of the rig ----

    private sealed partial class Rig
    {
        /// <summary>The row's context menu opens as the context-menu service opens it: its placement target set, then open.</summary>
        public ContextMenu OpenMenu(TopLevelRow row)
        {
            ListBoxItem container = Container(row);
            ContextMenu menu = container.ContextMenu ?? throw new InvalidOperationException("precondition: the row container has no context menu");
            menu.PlacementTarget = container;
            menu.IsOpen = true;
            Settle(Window);
            if (!menu.IsOpen || Keyboard.FocusedElement is not ContextMenu)
                throw new InvalidOperationException($"precondition: the row menu is open and holds the keyboard focus. {Describe()}");
            return menu;
        }

        /// <summary>A click on the menu's first entry (MenuItem.OnClick, which runs its command); the menu then closes.</summary>
        public List<object?> ChooseMenuItem(ContextMenu menu)
        {
            _executed.Clear();
            typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu.Items.OfType<MenuItem>().First(), null);
            Settle(Window);
            List<object?> ran = [.. _executed];
            menu.IsOpen = false;
            Settle(Window);
            return ran;
        }

        /// <summary>A modal dialog of the main window opens, <paramref name="whileOpen"/> runs inside it, and it closes.</summary>
        public void ModalDialogAround(Action whileOpen)
        {
            var dialog = new Window
            {
                Owner = Window,
                Width = 120,
                Height = 80,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -3800,
            };
            bool mainWasActive = true;
            Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                mainWasActive = Window.IsActive;
                whileOpen();
                dialog.Close();
            });
            dialog.ShowDialog();
            Settle(Window);
            if (mainWasActive)
                throw new InvalidOperationException("precondition: the main window was still active while the modal dialog was open");
        }
    }
}
