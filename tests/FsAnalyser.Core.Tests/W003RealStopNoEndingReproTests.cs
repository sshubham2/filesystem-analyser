namespace FsAnalyser.Core.Tests;

/// <summary>
/// Review repro (SL-003): W-003 on a real share where the hook pressed Stop and the app reached no stopped ending before
/// the 600 s wait killed it. The contract says FAILED when the app timed out, so the record's verdict is 'failed' and the
/// case records a failure. Test-W003Real is loaded from the script's parse; the launch is stubbed.
/// </summary>
public class W003RealStopNoEndingReproTests
{
    [Fact]
    public async Task ARealShareRunWhoseStopReachesNoEndingIsFailed()
    {
        string[] functions = ["Test-W003Real", "Read-ActedLine", "Get-StopToEndingMs", "Get-CutFolderRows", "Get-ShareVerdict", "New-ShareRecord", "Get-RecordString", "Get-RecordByCode", "Get-ProofRecordPath", "Show", "Check", "CheckEqual", "Inconclusive"];
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + string.Concat(functions.Select(WindowCasesHelperTests.Load)) + """
            $script:Failures = [System.Collections.Generic.List[string]]::new()
            $script:Inconclusive = [System.Collections.Generic.List[string]]::new()
            $Repo = Join-Path ([System.IO.Path]::GetTempPath()) ('w003-real-' + [guid]::NewGuid().ToString('N'))
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
            function Invoke-WithHook($mode, $hookDll, $action, $armed) { [pscustomobject]@{ Result = [pscustomobject]@{ Exit = 'TIMEOUT'; Answer = 'cut' }; Log = @('loaded mode=stop pid=1', 'armed ms=30000', 'acted mode=stop enabled=True visible=True ms=32000 sincearmed=2000') } }
            try { [void](Test-W003Real) } catch { 'threw: ' + $_.Exception.Message }
            Remove-Item -LiteralPath $Repo -Recurse -Force
            "result verdict=$($global:saved.verdict) failed=$($script:Failures.Count -gt 0)"
            """);

        Assert.Contains("result verdict=failed failed=True", output);
    }
}
