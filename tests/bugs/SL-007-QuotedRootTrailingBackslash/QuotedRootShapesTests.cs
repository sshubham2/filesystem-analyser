using System.Diagnostics;
using System.Text.Json;

namespace SL007.QuotedRootTrailingBackslash.Tests;

/// <summary>
/// SL-007's other finish lines at the exe, beside the reproduction (which is finish line 1): the lines that fail on the base
/// because the repair reads the command line as typed and not the arguments Windows made of it, and the line that must keep working.
/// Each starts the built Debug apphost with a raw command line, as cmd or Start-Process with one string would.
/// </summary>
public class QuotedRootShapesTests
{
    [Fact]
    public void ASpacedRootAndAReportPathWithTwoSpacesAreTakenWhole()
    {
        string work = AppRun.WorkFolder();
        Process? process = null;
        try
        {
            string tree = AppRun.MakeTree(work, "a quoted tree");
            string reportFolder = Path.Combine(work, "out  dir"); // two spaces: Windows' split loses them, and the app has to find the folder
            Directory.CreateDirectory(reportFolder);
            string report = Path.Combine(reportFolder, "r.json");
            string arguments = $"\"{tree}\\\" --report \"{report}\"";

            process = AppRun.Start(AppRun.Exe(), arguments);
            AppRun.Result run = AppRun.Watch(process, 7000);

            Assert.True(run.Exited, $"the app did not exit within 7000 ms on [{arguments}]: after the \\\" Windows hands it the report path in two pieces and without its two spaces, so only the line as typed holds it");
            Assert.True(run.ExitCode == 0, $"the app exited with {run.ExitCode}, not 0, on [{arguments}]");
            Assert.True(File.Exists(report), $"no report at the exact path {report}");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal(tree + "\\", document.RootElement.GetProperty("root").GetString());
            JsonElement core = document.RootElement.GetProperty("core");
            Assert.Equal("Completed", core.GetProperty("status").GetString());
            Assert.Equal(3, core.GetProperty("files").GetInt64());
        }
        finally
        {
            AppRun.Stop(process);
            AppRun.RemoveQuietly(work);
        }
    }

    [Fact]
    public void ABackslashQuoteWithNoOpeningQuoteStillRunsReportMode()
    {
        string work = AppRun.WorkFolder();
        Process? process = null;
        try
        {
            string tree = AppRun.MakeTree(work, "tree");
            string report = Path.Combine(work, "r.json");
            // Typed with a closing quote and no opening one, as tab completion leaves it: Windows splits this cleanly into three
            // arguments and only the root carries a trailing quote. Joining them hid --report and left a window that never ends.
            string arguments = $"{tree}\\\" --report {report}";

            process = AppRun.Start(AppRun.Exe(), arguments);
            AppRun.Result run = AppRun.Watch(process, 7000);

            Assert.True(run.Exited, $"the app did not exit within 7000 ms on [{arguments}]: the unpaired quote was paired with nothing and swallowed the line");
            Assert.True(run.ExitCode == 0, $"the app exited with {run.ExitCode}, not 0, on [{arguments}]");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal(tree + "\\", document.RootElement.GetProperty("root").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("core").GetProperty("files").GetInt64());
        }
        finally
        {
            AppRun.Stop(process);
            AppRun.RemoveQuietly(work);
        }
    }

    [Fact]
    public void AQuotedRootEndingInABackslashAloneOpensTheWindow()
    {
        string work = AppRun.WorkFolder();
        Process? process = null;
        try
        {
            string tree = AppRun.MakeTree(work, "tree");
            string arguments = $"\"{tree}\\\"";

            process = AppRun.Start(AppRun.Exe(), arguments);
            AppRun.Result run = AppRun.Watch(process, 3000);

            Assert.False(run.Exited, $"the app ended with {run.ExitCode} after {run.Ms} ms on [{arguments}], and a person's own launch of a folder has to leave its window open");
            Assert.True(run.WindowSeen, $"the app was running after {run.Ms} ms on [{arguments}] and never had a window");
        }
        finally
        {
            AppRun.Stop(process);
            AppRun.RemoveQuietly(work);
        }
    }

    [Fact]
    public void AnArgumentAfterTheQuotedRootIsRefusedWithExit2AndNoWindow()
    {
        string work = AppRun.WorkFolder();
        Process? process = null;
        try
        {
            string tree = AppRun.MakeTree(work, "tree");
            string report = Path.Combine(work, "r.json");
            string arguments = $"\"{tree}\\\" extra --report \"{report}\"";

            process = AppRun.Start(AppRun.Exe(), arguments);
            AppRun.Result run = AppRun.Watch(process, 5000);

            Assert.True(run.Exited, $"the app did not exit within 5000 ms on [{arguments}]: it is waiting in a window that never ends");
            Assert.True(run.ExitCode == 2, $"the app exited with {run.ExitCode}, not 2, on [{arguments}]: two roots are refused, not scanned");
            Assert.False(run.WindowSeen, "the app showed a window before it refused the line");
            Assert.Equal(["tree"], Directory.GetFileSystemEntries(work).Select(e => Path.GetFileName(e)!).ToArray()); // no r.json, no r.json.tmp, no .fsa-* entry
        }
        finally
        {
            AppRun.Stop(process);
            AppRun.RemoveQuietly(work);
        }
    }
}
