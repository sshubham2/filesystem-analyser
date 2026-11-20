using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SL007.QuotedRootTrailingBackslash.Tests;

/// <summary>
/// SC-028: a report path in a folder the account cannot put a report in is refused up front, with exit 3 before any window or scan.
/// On the base the app opened its window, scanned the whole tree and found out only at the write (measured: a window at 1062 ms,
/// then exit 3 at 1401 ms), which on a whole drive is a full scan and then exit 3. Two folders: one that takes no new file, and
/// a create-only drop folder that takes new files and lets nobody delete or rename one, where the write's move is refused.
/// </summary>
public class UnwritableReportFolderTests
{
    [Fact]
    public void AFolderThatTakesNoNewFileIsRefusedWithExit3BeforeAnyWindow()
    {
        // Deny CreateFiles (FILE_ADD_FILE) to this account on the folder itself: it exists, it can be listed, and no new file can be made in it.
        RefusedBeforeAnyWindow(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
    }

    [Fact]
    public void ACreateOnlyDropFolderIsRefusedWithExit3BeforeAnyWindow()
    {
        // A new file can be made, and nothing in the folder can be deleted or renamed: the probe's own creating open is granted DELETE
        // whatever the folder says, so only deleting the file it made, as the write's move will, finds this.
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        RefusedBeforeAnyWindow(
            new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny),
            new FileSystemAccessRule(me, FileSystemRights.Delete, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Deny));
    }

    private static void RefusedBeforeAnyWindow(params FileSystemAccessRule[] denied)
    {
        string work = AppRun.WorkFolder();
        Process? process = null;
        var folder = new DirectoryInfo(Path.Combine(work, "locked"));
        folder.Create();
        DirectorySecurity security = folder.GetAccessControl();
        foreach (FileSystemAccessRule rule in denied)
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
            foreach (FileSystemAccessRule rule in denied)
                restored.RemoveAccessRule(rule);
            folder.SetAccessControl(restored);
            AppRun.RemoveQuietly(work);
        }
    }
}
