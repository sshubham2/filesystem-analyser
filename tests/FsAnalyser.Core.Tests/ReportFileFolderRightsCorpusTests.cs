using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-007 O-046 / RD-m1, closed as its class (ReportFileFolderRightsCorpus.json): rights a freshly created file is granted by
/// its creating open but which the write's later open of that file is checked for. Each row is a folder whose rights are set
/// by Deny entries for this account; the probe and then the write are run in it, and the probe must be refused exactly when
/// the write is, so that the app finds a folder it cannot use before it scans and not after.
/// </summary>
public class ReportFileFolderRightsCorpusTests
{
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("{ \"schema\": 1 }\n");

    private static string Outcome(Exception? exception) => exception switch
    {
        null => "ok",
        IOException or UnauthorizedAccessException => "refused",
        _ => "other: " + exception.GetType().Name + ": " + exception.Message,
    };

    [Fact]
    public void TheProbeIsRefusedInEveryFolderExactlyWhenTheWriteIs()
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "FsAnalyser.Core.Tests", "ReportFileFolderRightsCorpus.json")));
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        var wrong = new List<string>();
        int rows = 0;
        foreach (JsonElement row in corpus.RootElement.GetProperty("rows").EnumerateArray())
        {
            rows++;
            string id = row.GetProperty("id").GetString()!;
            using var tree = new TempTree();
            string folder = tree.Folder("r");
            string report = Path.Combine(folder, "r.json");
            var directory = new DirectoryInfo(folder);

            FileSystemAccessRule[] rules = [.. row.GetProperty("rules").EnumerateArray().Select(rule =>
            {
                bool files = rule.GetProperty("applies").GetString() == "files";
                return new FileSystemAccessRule(me, Enum.Parse<FileSystemRights>(rule.GetProperty("rights").GetString()!),
                    files ? InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                    files ? PropagationFlags.InheritOnly : PropagationFlags.None, AccessControlType.Deny);
            })];
            DirectorySecurity security = directory.GetAccessControl();
            foreach (FileSystemAccessRule rule in rules)
                security.AddAccessRule(rule);
            directory.SetAccessControl(security);
            string probe;
            string write;
            try
            {
                probe = Outcome(Record.Exception(() => new ReportFile(report).Probe()));
                write = Outcome(Record.Exception(() => new ReportFile(report).Write(Json)));
            }
            finally
            {
                DirectorySecurity restored = directory.GetAccessControl();
                foreach (FileSystemAccessRule rule in rules)
                    restored.RemoveAccessRule(rule);
                directory.SetAccessControl(restored);
            }

            string expectedProbe = row.GetProperty("probe").GetString()!;
            string expectedWrite = row.GetProperty("write").GetString()!;
            if (probe != expectedProbe || write != expectedWrite)
                wrong.Add($"{id}: the probe was {probe} (row says {expectedProbe}) and the write {write} (row says {expectedWrite})");
            if (probe != write)
                wrong.Add($"{id}: the probe was {probe} and the write {write}: the app would scan a folder it cannot use, or refuse one it can");
        }
        Assert.True(rows > 0);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
