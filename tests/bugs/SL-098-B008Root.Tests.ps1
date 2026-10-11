<#
SL-098: WindowCases B-008 under the engine's TEMP and under an ordinary one. B-008 builds its tree under a root named 'long-path-' * 14,
so the root followed the case folder, which is 18 characters shorter under C:\WINDOWS\TEMP (65 characters) than under
%LOCALAPPDATA%\Temp (83): the stopped ending's title and detail came to 384 characters against the 400 it must pass, and the case
failed. The root is now padded to a fixed 250 characters. This runs the real case in a child process whose TEMP and TMP are set here and
holds it to what the pad promises:

  - 'PASSED: B-008' and exit 0, with no FAIL line;
  - the case says its root is 250 characters, and the title and detail it measured are over 400, the same number under both TEMPs
    (-Temp Both, the default, runs the two and compares them).

It needs the interactive desktop (the case launches the published app with its hook), and publishes the app first when it is missing
or older than the sources, as WindowCases.ps1 does: a few minutes the first time, about 1 minute a run after that.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-B008Root.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-B008Root.Tests.ps1 -Temp Engine

Exit 0 when every check held, 1 when any failed.
#>

param(
    [ValidateSet('Both', 'Engine', 'Ordinary')]
    [string] $Temp = 'Both'
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Window = Join-Path $Repo 'tests\window\WindowCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

$windows = [Environment]::GetFolderPath('Windows')
$local = [Environment]::GetFolderPath('LocalApplicationData')
$temps = [ordered]@{ Engine = (Join-Path $windows 'TEMP'); Ordinary = (Join-Path $local 'Temp') }
$lengths = @{}

foreach ($name in $temps.Keys) {
    if ($Temp -ne 'Both' -and $Temp -ne $name) { continue }
    $asked = $temps[$name]
    Write-Output "WindowCases.ps1 -Case B-008 with TEMP and TMP set to $asked"
    $shell = (Get-Process -Id $PID).Path
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $ErrorActionPreference = 'Continue'
    try {
        $env:TEMP = $asked
        $env:TMP = $asked
        $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $Window -Case B-008 2>&1 | Out-String
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = 'Stop'
        $env:TEMP = $saved.TEMP
        $env:TMP = $saved.TMP
    }
    $fails = ((($output -split "`r?`n") | Where-Object { $_ -match '^\s+FAIL\b|^FAILED' } | Select-Object -First 6) -join "`n")
    Check ($exit -eq 0 -and $output -match '(?m)^PASSED: B-008\s*$') "B-008 passes (exit $exit)`n$fails`n$(($output -split "`r?`n" | Select-Object -Last 5) -join "`n")"
    Check ([string]::IsNullOrEmpty($fails)) "no FAIL line`n$fails"
    $roots = @([regex]::Matches($output, 'a root of (\d+) characters') | ForEach-Object { [int]$_.Groups[1].Value })
    Check ($roots.Count -gt 0 -and @($roots | Where-Object { $_ -ne 250 }).Count -eq 0) "the root is 250 characters in every pass (got $($roots -join ', '))"
    $measured = @([regex]::Matches($output, "run longer than a system drive's \((\d+) characters\)") | ForEach-Object { [int]$_.Groups[1].Value })
    Check ($measured.Count -gt 0 -and @($measured | Where-Object { $_ -le 400 }).Count -eq 0) "the title and detail are over 400 characters in every pass (got $($measured -join ', '))"
    $lengths[$name] = ($measured | Select-Object -Unique) -join ','
}

if ($lengths.Count -eq 2) {
    Check ($lengths['Engine'] -eq $lengths['Ordinary']) "the title and detail measure the same under both TEMPs (engine: $($lengths['Engine']); ordinary: $($lengths['Ordinary']))"
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
