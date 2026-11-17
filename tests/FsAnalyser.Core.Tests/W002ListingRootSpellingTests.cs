namespace FsAnalyser.Core.Tests;

/// <summary>
/// W-002's lister against spellings of a root that the app scans as that same folder (LongPath.NormalizeRoot folds '/' and a
/// trailing '\.' through GetFullPath): it must list the same files, or W-002 judges a scan against an empty listing.
/// </summary>
public class W002ListingRootSpellingTests
{
    [Fact]
    public async Task ARootSpelledWithForwardSlashesOrATrailingDotListsTheSameFiles()
    {
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + WindowCasesHelperTests.Load("Initialize-SeamWin32") + """
            Initialize-SeamWin32
            $t = Join-Path ([IO.Path]::GetTempPath()) ('w002-list-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory("$t\a\b")
            [IO.File]::WriteAllText("$t\a\b\f.txt", 'x')
            [IO.File]::WriteAllText("$t\g.txt", 'x')
            try {
                "listed plain=$(([FsaWindowCases.Seam]::ListFiles($t)).Count) slashes=$(([FsaWindowCases.Seam]::ListFiles(($t -replace '\\', '/'))).Count) dot=$(([FsaWindowCases.Seam]::ListFiles("$t\.")).Count)"
            }
            finally { Remove-Item -LiteralPath $t -Recurse -Force }
            """);

        Assert.Contains("listed plain=2 slashes=2 dot=2", output);
    }
}
