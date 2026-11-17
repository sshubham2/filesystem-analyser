namespace FsAnalyser.Core.Tests;

/// <summary>
/// Review repro (SL-003): a W-001 -Stopped attempt where the hook pressed Stop while folders were being listed and the app
/// then timed out (killed, no report) must make the stopped part 'failed', not be retried into a 'held' one. The
/// launch is stubbed; Invoke-W001StoppedRun is loaded from the script's parse.
/// </summary>
public class W001StoppedTimeoutReproTests
{
    [Fact]
    public async Task AStoppedAttemptThatTimedOutAfterStopFailsTheStoppedPart()
    {
        string[] functions = ["Invoke-W001StoppedRun", "Read-ActedLine", "Test-StoppedPartial", "Compare-StoppedRows", "Get-StoppedVerdict", "New-StoppedPart", "Get-RecordString", "Show", "Check", "CheckEqual", "Inconclusive"];
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + string.Concat(functions.Select(WindowCasesHelperTests.Load)) + """
            $script:Failures = [System.Collections.Generic.List[string]]::new()
            $script:Inconclusive = [System.Collections.Generic.List[string]]::new()
            $Root = 'X:\'
            $global:calls = 0
            function Invoke-HookedRun($mode, $root, $report, $variables, $timeoutSec) {
                $global:calls++
                if ($global:calls -eq 1) { return [pscustomobject]@{ Exit = 'TIMEOUT'; Report = $null; Text = $null; Log = @('acted mode=stop atfolders=100 folders=120 sized=- current=X:\a ms=900 utc=2026-11-17T19:05:45Z') } }
                [pscustomobject]@{ Exit = '0'; Report = [pscustomobject]@{ core = [pscustomobject]@{ status = 'Cancelled'; folders = 80; unreadable = 0 }; outcome = 'cancelled' }; Text = '{}'; Log = @('acted mode=stop atfolders=50 folders=60 sized=- current=X:\a ms=900 utc=2026-11-17T19:05:45Z') }
            }
            function Read-RowsDump($log) { [pscustomobject]@{ Rows = @(); NotScanned = @(); Closing = [pscustomobject]@{ count = 0; elevated = $false } } }
            function CheckPanelAgrees { }
            function Save-ProofRecord($file, $record, $reasons) { }
            $record = [ordered]@{}
            $finished = [pscustomobject]@{ Report = [pscustomobject]@{ core = [pscustomobject]@{ folders = 200 } } }
            try { [void](Invoke-W001StoppedRun ([System.IO.Path]::GetTempPath()) $finished @() ([DateTime]::UtcNow) $record 'unused.json' @()) } catch { 'threw: ' + $_.Exception.Message }
            "result stopped verdict=$($record['stopped'].verdict)"
            """);

        Assert.Contains("result stopped verdict=failed", output);
    }
}
