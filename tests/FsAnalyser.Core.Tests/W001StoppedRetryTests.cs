namespace FsAnalyser.Core.Tests;

/// <summary>
/// W-001 -Stopped's attempts: an attempt whose app never reached an ending (TIMEOUT after Stop was pressed) is a Stop that
/// hung, which the design fails; a later partial attempt must not turn the run into 'held'.
/// </summary>
public class W001StoppedRetryTests
{
    [Fact]
    public async Task AStoppedAttemptThatTimedOutIsNotLaunderedByALaterAttempt()
    {
        string loaded = string.Concat(new[] { "Invoke-W001StoppedRun", "Read-ActedLine", "Test-StoppedPartial", "Compare-StoppedRows", "Get-StoppedVerdict",
            "New-StoppedPart", "Get-RecordString", "Read-RowsDump", "Read-HookJson", "Check", "CheckEqual", "Show", "Inconclusive", "Save-ProofRecord",
            "Write-W001Record" }.Select(WindowCasesHelperTests.Load));
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + loaded + """
            $script:Failures = [System.Collections.Generic.List[string]]::new()
            $script:Inconclusive = [System.Collections.Generic.List[string]]::new()
            $Root = 'X:\'
            $script:calls = 0
            function Invoke-HookedRun($mode, $root, $report, $variables, $timeoutSec) {
                $script:calls++
                if ($script:calls -eq 1) {
                    return [pscustomobject]@{ Exit = 'TIMEOUT'; Report = $null; Text = $null; Log = @('acted mode=stop atfolders=50 folders=60 sized=- current=X:\b ms=900 utc=2026-11-18T00:00:00Z') }
                }
                $scan = [pscustomobject]@{ core = [pscustomobject]@{ status = 'Cancelled'; folders = 30; unreadable = 0 }; outcome = 'cancelled' }
                [pscustomobject]@{ Exit = '0'; Report = $scan; Text = '{}'; Log = @('acted mode=stop atfolders=25 folders=30 sized=- current=X:\c ms=800 utc=2026-11-18T00:00:01Z', '{"event":"rows","count":0,"elevated":false,"stopUtc":"2026-11-18T00:00:01Z"}') }
            }
            function CheckPanelAgrees { }
            $finished = [pscustomobject]@{ Report = [pscustomobject]@{ core = [pscustomobject]@{ folders = 100 } } }
            $record = [ordered]@{ verdict = 'held' }
            $file = Join-Path ([IO.Path]::GetTempPath()) ('w001-stopped-' + [guid]::NewGuid().ToString('N') + '.json')
            try { Invoke-W001StoppedRun 'unused-case' $finished @() ([DateTime]::UtcNow.AddHours(-1)) $record $file @() } catch { "threw: $($_.Exception.Message)" }
            "result verdict=$($record['stopped'].verdict)"
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
            """);

        Assert.Contains("result verdict=failed", output);
    }
}
