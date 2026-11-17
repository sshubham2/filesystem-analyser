namespace FsAnalyser.Core.Tests;

/// <summary>
/// Review repro (SL-003): W-001 -Stopped makes its stopped run only after a finished run that ended Completed; a finished
/// run that ended RootUnreadable starts no second scan. Test-W001 is loaded from the script's parse, the launch stubbed.
/// </summary>
public class W001StoppedAfterIncompleteReproTests
{
    [Fact]
    public async Task NoStoppedRunFollowsAFinishedRunThatDidNotComplete()
    {
        string[] functions = ["Test-W001", "Initialize-SeamWin32", "Get-W001RootShape", "Get-ProofRecordPath", "Test-KeptStoppedPart", "Get-WholeDriveReportGaps", "Get-LeakCount", "Get-ReasonCodeMismatches", "Get-W001Verdict", "Get-RecordString", "Get-HeaderCount", "Get-RecordByCode", "Get-CoverageRecord", "Get-CountsOnly", "Test-CountsOnlyKept", "Get-RoundedOrNull", "Show", "Check", "CheckEqual", "Inconclusive"];
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + string.Concat(functions.Select(WindowCasesHelperTests.Load)) + """
            $script:Failures = [System.Collections.Generic.List[string]]::new()
            $script:Inconclusive = [System.Collections.Generic.List[string]]::new()
            $Repo = Join-Path ([System.IO.Path]::GetTempPath()) ('w001-incomplete-' + [guid]::NewGuid().ToString('N'))
            [void][System.IO.Directory]::CreateDirectory($Repo)
            $Root = $Repo
            $Stopped = $true
            $Force = $false
            $global:stoppedStarted = $false
            function Test-Elevated { $false }
            function New-CaseFolder { $f = Join-Path $Repo ('case-' + [guid]::NewGuid().ToString('N')); [void][System.IO.Directory]::CreateDirectory($f); $f }
            function Remove-CaseFolder($folder) { }
            function Get-CheckerControls($case) { [pscustomobject]@{ Openable = $true; Denied = $true; RefusedThenListed = $true; Folders = @() } }
            function Invoke-HookedRun($mode, $root, $report, $variables, $timeoutSec) {
                [pscustomobject]@{ Exit = '0'; Report = [pscustomobject]@{ core = [pscustomobject]@{ status = 'RootUnreadable'; folders = 0; files = 0; unreadable = 0; elapsedMs = 5 }; outcome = 'root-unreadable'; workarounds = @(); window = [pscustomobject]@{} }; Text = '{}'; Log = @() }
            }
            function Read-RowsDump($log) { [pscustomobject]@{ Rows = @(); NotScanned = @(); Closing = [pscustomobject]@{ count = 0; elevated = $false } } }
            function CheckPanelAgrees { }
            function Save-ProofRecord($file, $record, $reasons) { }
            function Invoke-W001StoppedRun { $global:stoppedStarted = $true }
            try { [void](Test-W001) } catch { 'threw: ' + $_.Exception.Message }
            Remove-Item -LiteralPath $Repo -Recurse -Force
            "result stopped run started=$global:stoppedStarted"
            """);

        Assert.Contains("result stopped run started=False", output);
    }
}
