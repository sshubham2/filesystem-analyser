namespace FsAnalyser.Core.Tests;

/// <summary>
/// W-001's root classifier against other spellings of a whole local drive that the app itself scans as that whole drive
/// (LongPath.NormalizeRoot: GetFullPath folds 'C:/', 'C:\\' and 'C:\.' to 'C:\', and '\\?\C:\' is C:\ in extended form).
/// Each must be the local letter, kept as docs/proof-runs/w001-c-drive.json, or be refused; never a folder or a UNC root.
/// </summary>
public class W001RootSpellingTests
{
    [Fact]
    public async Task AWholeLocalDriveSpelledAnotherWayIsStillTheLocalLetter()
    {
        string loaded = WindowCasesHelperTests.Load("Get-W001RootShape") + WindowCasesHelperTests.Load("Get-ProofRecordPath");
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + loaded + """
            $fixed = { param($letter) 'Fixed' }
            foreach ($root in 'C:/', 'C:\\', 'C:\.', '\\?\C:\') {
                try {
                    $shape = Get-W001RootShape $root $fixed
                    $file = Get-ProofRecordPath 'R' 'W-001' $shape $root
                    "root [$root] $shape $file"
                }
                catch { "root [$root] refused" }
            }
            """);

        string[] lines = [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("root [", StringComparison.Ordinal))];
        Assert.Equal(4, lines.Length);
        foreach (string line in lines)
            Assert.True(line.EndsWith(@" local-letter R\docs\proof-runs\w001-c-drive.json", StringComparison.Ordinal) || line.EndsWith(" refused", StringComparison.Ordinal), line);
    }
}
