using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SL007.QuotedRootTrailingBackslash.Tests;

/// <summary>
/// SC-028: a report path in a folder the account cannot create a file in is refused up front, with exit 3 before any window or scan.
/// On the base the app opened its window, scanned the whole tree and found out only at the write (measured: a window at 1062 ms,
/// then exit 3 at 1401 ms), which on a whole drive is a full scan and then exit 3.
/// </summary>
public class UnwritableReportFolderTests
{
    [Fact]
    public void AFolderThatTakesNoNewFileIsRefusedWithExit3BeforeAnyWindow()
    {
        string work = AppRun.WorkFolder();
        Process? process = null;
        var folder = new DirectoryInfo(Path.Combine(work, "locked"));
        folder.Create();
        // Deny CreateFiles (FILE_ADD_FILE) to this account on the folder itself: it exists, it can be listed, and no new file can be made in it.
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        DirectorySecurity security = folder.GetAccessControl();
        security.AddAccessRule(rule);
        folder.SetAccessControl(security);
        try
        {
            string tree = AppRun.MakeTree(work, "tree");
            string arguments = $"\"{tree}\" --report \"{Path.Combine(folder.FullName, "r.json")}\"";

            process = AppRun.Start(AppRun.Exe(), arguments);
            AppRun.Result run = AppRun.Watch(process, 5000);

            Assert.True(run.Exited, $"the app did not exit within 5000 ms on [{arguments}]");
            Assert.True(run.ExitCode == 3, $"the app exited with {run.ExitCode}, not 3, on [{arguments}]");
            Assert.False(run.WindowSeen, $"the app opened a window before it refused the report path (exit 3 after {run.Ms} ms): it scanned first, and found out at the write");
            Assert.Empty(Directory.GetFileSystemEntries(folder.FullName)); // the probe left nothing, and no report or temporary was made
        }
        finally
        {
            AppRun.Stop(process);
            DirectorySecurity restored = folder.GetAccessControl();
            restored.RemoveAccessRule(rule);
            folder.SetAccessControl(restored);
            AppRun.RemoveQuietly(work);
        }
    }
}
