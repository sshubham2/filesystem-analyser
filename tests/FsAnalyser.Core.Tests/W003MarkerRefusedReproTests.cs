namespace FsAnalyser.Core.Tests;

/// <summary>
/// Review repro (SL-003, O-003): W-003 on a real share whose marker the hook refused after its 60 s deadline is
/// INCONCLUSIVE, even when the wait then timed out: the case reports it as inconclusive and records no failure.
/// </summary>
public class W003MarkerRefusedReproTests
{
    [Fact]
    public async Task ARefusedMarkerEndsTheRealShareRunInconclusive()
    {
        string[] functions = ["Test-W003Real", "Read-ActedLine", "Get-StopToEndingMs", "Get-CutFolderRows", "Get-ShareVerdict", "New-ShareRecord", "Get-RecordString", "Get-RecordByCode", "Get-ProofRecordPath", "Show", "Check", "CheckEqual", "Inconclusive"];
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + string.Concat(functions.Select(WindowCasesHelperTests.Load)) + """
            $script:Failures = [System.Collections.Generic.List[string]]::new()
            $script:Inconclusive = [System.Collections.Generic.List[string]]::new()
            $Repo = Join-Path ([System.IO.Path]::GetTempPath()) ('w003-refused-' + [guid]::NewGuid().ToString('N'))
            [void][System.IO.Directory]::CreateDirectory($Repo)
            $Root = $Repo
            $Network = 'home'
            $Link = 'wifi-off'
            $HookDll = 'unused.dll'
            $global:saved = $null
            function Read-Host { '' }
            function New-CaseFolder { $f = Join-Path $Repo ('case-' + [guid]::NewGuid().ToString('N')); [void][System.IO.Directory]::CreateDirectory($f); $f }
            function Remove-CaseFolder($folder) { }
            function Read-RowsDump($log) { [pscustomobject]@{ Rows = @(); NotScanned = @(); Closing = $null } }
            function Save-ProofRecord($file, $record, $reasons) { $global:saved = $record }
            function Invoke-WithHook($mode, $hookDll, $action, $armed) { [pscustomobject]@{ Result = [pscustomobject]@{ Exit = 'TIMEOUT'; Answer = 'cut' }; Log = @('loaded mode=stop pid=1', "refused mode=stop reason=the rig's marker C:\case\cut.armed did not appear within 60 s, so the stall never armed") } }
            try { [void](Test-W003Real) } catch { 'threw: ' + $_.Exception.Message }
            Remove-Item -LiteralPath $Repo -Recurse -Force
            "result verdict=$($global:saved.verdict) failures=$($script:Failures.Count) inconclusive=$($script:Inconclusive.Count -gt 0)"
            """);

        Assert.Contains("result verdict=inconclusive failures=0 inconclusive=True", output);
    }
}
