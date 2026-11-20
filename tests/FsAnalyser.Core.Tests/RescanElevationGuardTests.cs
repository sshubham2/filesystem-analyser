using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-008 review M2: the app never elevates by itself. The one place it asks Windows to start it with more rights is
/// RescanAsAdministrator.Start, and the one caller is the rescan button's Click handler in MainWindow.xaml.cs. The settled
/// fitness entries keep every other file from calling or naming it, and from spelling the verb; they cannot see inside
/// MainWindow.xaml.cs, where OnLoaded and the endings also live (a report-mode run goes through them, from App.xaml.cs and
/// WindowReport.cs), so this reads that file: Start is called once, inside
/// the handler's body, the handler is named nowhere else, and nothing in the file raises a click or invokes a button, so that a
/// route that reached the call without a person pressing the button would fail here. The text is read whole, comments included,
/// which is stricter than the entries.
/// </summary>
public class RescanElevationGuardTests
{
    private const string Handler = "OnRescanAsAdministrator";

    // The method's name as the class is called by it, with room for spaces and line breaks about the dot, and not StartedLine.
    private const string StartName = @"\bRescanAsAdministrator\s*\.\s*Start\b";

    private static string Source(params string[] relative) =>
        File.ReadAllText(Path.Combine([FailCriticalErrorsTests.FindRepo(), .. relative]));

    private static string MainWindow() => Source("src", "FsAnalyser.App", "MainWindow.xaml.cs");

    private static int[] Occurrences(string text, string needle)
    {
        var at = new List<int>();
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            at.Add(i);
        return [.. at];
    }

    // The text between the braces of the method whose signature starts at the given index.
    private static (int Open, int Close) BodyOf(string text, int signature)
    {
        int open = text.IndexOf('{', signature);
        Assert.True(open > signature, "the handler has no body");
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            depth += text[i] == '{' ? 1 : text[i] == '}' ? -1 : 0;
            if (depth == 0)
                return (open, i);
        }
        throw new Xunit.Sdk.XunitException("the handler's braces do not close");
    }

    [Fact]
    public void StartIsCalledOnceAndOnlyInsideTheClickHandlersBody()
    {
        string text = MainWindow();
        int[] declared = Occurrences(text, $"void {Handler}(");
        Assert.True(declared.Length == 1, $"MainWindow.xaml.cs declares {Handler} {declared.Length} times");
        (int open, int close) = BodyOf(text, declared[0]);

        int[] calls = [.. Occurrences(text, "RescanAsAdministrator.Start(")];
        Assert.True(calls.Length == 1, $"MainWindow.xaml.cs calls RescanAsAdministrator.Start( {calls.Length} times, and it is to be once, in {Handler}");
        Assert.True(calls[0] > open && calls[0] < close, $"the one call of RescanAsAdministrator.Start( is outside {Handler}'s body");
        // However it is spelled, nothing else in the file names the method: a method group, a space before the dot, a line break.
        Assert.Single(Regex.Matches(text, StartName));
    }

    [Fact]
    public void TheHandlerIsNamedOnlyWhereItIsDeclaredAndWhereTheXamlWiresIt()
    {
        string text = MainWindow();
        Assert.True(Occurrences(text, Handler).Length == 1, $"{Handler} is named {Occurrences(text, Handler).Length} times in MainWindow.xaml.cs, and it is to be once, where it is declared");
        string xaml = Source("src", "FsAnalyser.App", "MainWindow.xaml");
        Assert.True(Occurrences(xaml, Handler).Length == 1, $"MainWindow.xaml names {Handler} {Occurrences(xaml, Handler).Length} times, and it is to be once, as the button's Click");
        Assert.Contains($"Click=\"{Handler}\"", xaml, StringComparison.Ordinal);
        // The same count over every other file of src, which the settled entry also holds.
        string src = Path.Combine(FailCriticalErrorsTests.FindRepo(), "src");
        string[] elsewhere = [.. Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Split('\\').Any(part => part is "bin" or "obj") && (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
            .Where(f => !Path.GetFileName(f).StartsWith("MainWindow.xaml", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(Handler, StringComparison.Ordinal))];
        Assert.True(elsewhere.Length == 0, "the handler is named in " + string.Join(", ", elsewhere));
    }

    // A click raised from code, or an automation peer's invoke, reaches the handler without a person pressing the button.
    [Theory]
    [InlineData("ClickEvent")]
    [InlineData("RaiseEvent(")]
    [InlineData("AutomationPeer")]
    [InlineData("IInvokeProvider")]
    [InlineData("InvokePattern")]
    public void MainWindowRaisesNoClickAndInvokesNoButton(string needle) =>
        Assert.DoesNotContain(needle, MainWindow(), StringComparison.Ordinal);

    [Fact]
    public void MainWindowDoesNotImportOrAliasTheClass()
    {
        string text = MainWindow();
        Assert.DoesNotMatch(@"\busing\s+static\s+[\w.]*\bRescanAsAdministrator\b", text);
        Assert.DoesNotMatch(@"\busing\s+\w+\s*=\s*[\w.]*\bRescanAsAdministrator\b", text);
    }

    // The one call asks for what the design says: this program, the ending's own root, and Process.Start.
    [Fact]
    public void TheCallGivesTheRunningProgramTheEndingsRootAndTheSystemsStart()
    {
        string text = MainWindow();
        Assert.Contains("RescanAsAdministrator.Start(Environment.ProcessPath, last.Root, Process.Start)", text, StringComparison.Ordinal);
    }

    // Nothing starts a rescan when the window loads (OnLoaded), when a scan starts (StartScan) or at an ending (ShowOutcome,
    // ShowProblem, AddVolumeInUse): these methods of MainWindow.xaml.cs hold neither the call nor the handler's name. Report mode's
    // own files are the next test.
    [Theory]
    [InlineData("OnLoaded")]
    [InlineData("StartScan")]
    [InlineData("ShowOutcome")]
    [InlineData("ShowProblem")]
    [InlineData("AddVolumeInUse")]
    public void NoOtherMethodOfMainWindowCallsStart(string method)
    {
        string text = MainWindow();
        MatchCollection declared = Regex.Matches(text, $@"\b(?:void|Task)\s+{method}\(");
        Assert.NotEmpty(declared);
        foreach (Match signature in declared)
        {
            (int open, int close) = BodyOf(text, signature.Index);
            Assert.DoesNotMatch(StartName, text[open..close]);
            Assert.DoesNotContain(Handler, text[open..close], StringComparison.Ordinal);
        }
    }

    // The App's own start, and report mode, are in App.xaml.cs, WindowReport.cs and ReportFile.cs: none names the rescan's start or its handler.
    [Theory]
    [InlineData("App.xaml.cs")]
    [InlineData("WindowReport.cs")]
    [InlineData("ReportFile.cs")]
    public void TheStartAndReportModeNeverNameTheRescanStart(string file)
    {
        string text = Source("src", "FsAnalyser.App", file);
        Assert.DoesNotMatch(StartName, text);
        Assert.DoesNotContain(Handler, text, StringComparison.Ordinal);
    }
}
