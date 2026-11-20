using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SC-028's probe against a folder that takes a new file but does not let this account delete or rename one in it (an inherited
/// Deny DELETE on its files and a Deny FILE_DELETE_CHILD on the folder, a create-only drop folder). The write's move needs DELETE on
/// the temporary, so the report cannot be put there; the probe has to say so up front, as it does for a folder that takes no file.
/// </summary>
public class ReportFileDropFolderTests
{
    [Fact]
    public void AProbeIsRefusedWhereTheWritesMoveWouldBeRefused()
    {
        using var tree = new TempTree();
        string folder = tree.Folder("drop");
        string report = Path.Combine(folder, "r.json");
        var directory = new DirectoryInfo(folder);
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        var noDeleteChild = new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var noDeleteOfFiles = new FileSystemAccessRule(me, FileSystemRights.Delete,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Deny);
        DirectorySecurity security = directory.GetAccessControl();
        security.AddAccessRule(noDeleteChild);
        security.AddAccessRule(noDeleteOfFiles);
        directory.SetAccessControl(security);
        try
        {
            Exception? probe = Record.Exception(() => new ReportFile(report).Probe());
            Exception? write = Record.Exception(() => new ReportFile(report).Write(Encoding.UTF8.GetBytes("{ \"schema\": 1 }")));

            Assert.True(write is IOException or UnauthorizedAccessException,
                $"premise: the write's move must be refused in this folder, and it gave {write?.GetType().Name ?? "no exception"}");
            Assert.True(probe is IOException or UnauthorizedAccessException,
                "the probe passed a folder whose report the write then could not put in place, so the app would scan first and exit 3 only at the end");
        }
        finally
        {
            DirectorySecurity restored = directory.GetAccessControl();
            restored.RemoveAccessRule(noDeleteChild);
            restored.RemoveAccessRule(noDeleteOfFiles);
            directory.SetAccessControl(restored);
        }
    }
}
