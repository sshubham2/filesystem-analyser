namespace FsAnalyser.Core.Tests;

/// <summary>
/// Review repro (SL-003, decision n1): W-002 on a scan that did not end Completed (here Refused) is INCONCLUSIVE, not a
/// failure: it read nothing, so nothing is shown either way. Test-W002 is loaded from the script's parse; the app is stubbed
/// and the root is a plain temp folder, so no sync root is registered.
/// </summary>
public class W002NotCompletedReproTests
{
    [Fact]
    public async Task AScanThatWasRefusedEndsW002InconclusiveNotFailed()
    {
        string[] functions = ["Test-W002", "Initialize-SeamWin32", "Get-HydrationTally", "Get-HydrationVerdict", "New-HydrationRecord", "Get-DataFetches", "Get-RecordString", "Get-ProofRecordPath", "Show", "Check", "CheckEqual", "Inconclusive"];
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + string.Concat(functions.Select(WindowCasesHelperTests.Load)) + """
            $script:Failures = [System.Collections.Generic.List[string]]::new()
            $script:Inconclusive = [System.Collections.Generic.List[string]]::new()
            $Repo = Join-Path ([System.IO.Path]::GetTempPath()) ('w002-refused-' + [guid]::NewGuid().ToString('N'))
            $Root = Join-Path $Repo 'root'
            [void][System.IO.Directory]::CreateDirectory($Root)
            [System.IO.File]::WriteAllText((Join-Path $Root 'a.txt'), 'x')
            $global:saved = $null
            function Read-Host { '' }
            function New-CaseFolder { $global:caseFolder = Join-Path $Repo ('case-' + [guid]::NewGuid().ToString('N')); [void][System.IO.Directory]::CreateDirectory($global:caseFolder); $global:caseFolder }
            function Remove-CaseFolder($folder) { }
            function Format-AppArgument($value) { [string]$value }
            function Start-App($arguments) {
                [System.IO.File]::WriteAllText((Join-Path $global:caseFolder 'report.json'), '{"core":{"status":"Refused","unreadable":0},"outcome":"refused"}')
                [pscustomobject]@{ Id = 4242 }
            }
            function Wait-App($process, $timeoutSec) { '0' }
            function Save-ProofRecord($file, $record, $reasons) { $global:saved = $record }
            try { [void](Test-W002) } catch { 'threw: ' + $_.Exception.Message }
            Remove-Item -LiteralPath $Repo -Recurse -Force
            "result verdict=$($global:saved.verdict) failures=$($script:Failures.Count) inconclusive=$($script:Inconclusive.Count -gt 0)"
            """);

        Assert.Contains("result verdict=inconclusive failures=0 inconclusive=True", output);
    }
}
