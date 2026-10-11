<#
SL-099 (merged SC-130): an arm the machine guard stopped, and that is then interrupted during its 10-minute settle wait, is
reported by tests/window/PoolWatch.ps1 -Report as an exe that exited, with the guard stop and its net dropped.

Bug: Get-Missing (PoolWatch.ps1) tests $a.interrupted first and `continue`s, before the $a.guardStop branch. Invoke-ExeArm
writes both on one arm line when the guard kills FsAnalyser.exe (guardStop set, exitCode -1, killed false) and Ctrl+C or a
terminating error then lands in the settle wait (interrupted true, no /settled mark).

Expected: -Report lists that arm as stopped by the machine guard: the guard's own reason ('available 2900 MB, pool +310 MiB
over X1/before') and the arm's net from /before to /exit ('paged +310 MiB ... from /before to /exit'), the figures the
guard stop exists to preserve. Being interrupted may be said as well; it must not replace them.

Actual: the only line for the arm is 'arm X1: interrupted after FsAnalyser.exe exited (exit -1), before its /settled mark'.
The guard stop and the +310 MiB net it was killed at appear nowhere in the report.

The fixture is a hand-built, complete run in the shape -Protocol writes (the PoolWatchCases idiom), one arm changed. X2 is a
control: stopped by the guard and not interrupted, which -Report already reports with its reason and net, so the strings
looked for below are the ones it prints. Nothing is scanned or read from the machine; -Report reads the fixture's two JSONL
files and prepare.json. Worst case on this machine: four small files in one folder under the user's temp folder (or
LocalAppData\Temp when that is in the Windows folder), removed at the end. About 2 s.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-GuardStopSurvivesInterrupt.Tests.ps1

Exit 0 when every check held, 1 when any failed.
#>

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$PoolWatch = Join-Path $Repo 'tests\window\PoolWatch.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# The temp folder, unless that is at or under the Windows folder (the engine's C:\WINDOWS\TEMP, which a user may create in
# but not list or delete from): then LocalAppData\Temp, as tests/rules/RuleCases.ps1 does.
function Get-ScratchParent {
    $temp = [System.IO.Path]::GetTempPath()
    $windows = [Environment]::GetFolderPath('Windows').TrimEnd('\')
    $under = ($temp.TrimEnd('\') + '\').StartsWith($windows + '\', [StringComparison]::OrdinalIgnoreCase)
    if (-not $under) { return $temp }
    [System.IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')
}

# ---- A complete run, as -Protocol writes it ----

$Start = [DateTime]::new(2026, 10, 5, 0, 0, 0, [DateTimeKind]::Utc)
$script:T = 0.0
$script:Paged = 5000MB
$Marks = [System.Collections.Generic.List[object]]::new()
$Arms = [System.Collections.Generic.List[object]]::new()

function Add-Mark([string] $label) {
    $Marks.Add([ordered]@{
            kind = 'mark'; label = $label; utc = $Start.AddSeconds($script:T).ToString('o'); uptimeSec = 600 + [long]$script:T
            pagedBytes = $script:Paged; nonpagedBytes = 3000MB; availableMB = 12000; systemHandles = 9000; system120089 = 1600
            tags = @([ordered]@{ tag = 'WCsc'; pa = 0; pf = 0; pagedBytes = 0; na = 1600; nf = 0; nonpagedBytes = 0 }, [ordered]@{ tag = 'FMfn'; pa = 900000; pf = 0; pagedBytes = 0; na = 0; nf = 0; nonpagedBytes = 0 })
            rehearsal = $false
        })
}

function Add-Arm([string] $name, [string] $type) {
    foreach ($label in "$name/before", "$name/exit", "$name/settled") { Add-Mark $label; $script:T += 300; $script:Paged += 1MB }
    $arm = [ordered]@{ kind = 'arm'; arm = $name; type = $type; exitCode = 0; report = [ordered]@{ status = 'Completed'; notScanned = 1; skippedStores = 1; opensUnderExcludedStore = 0 };
        hookLog = @(); acted = $false; foldersAtCancel = $null; ranFull = $false; guardStop = $null; guardAtStart = $false; cascadeFrom = $null; interrupted = $false; killed = $false; rehearsal = $false }
    if ($type -ne 'exe-full') {
        $mode = if ($type -eq 'exe-close') { 'close' } else { 'stop' }
        $arm.hookLog = @("acted mode=$mode past=C:\Users pastfolders=1000 folders=41000 sized=- current=C:\Windows\WinSxS ms=120000")
        $arm.acted = $true
        $arm.foldersAtCancel = 41000
        if ($type -eq 'exe-close') { $arm.exitCode = 70; $arm.report = $null } else { $arm.report.status = 'Cancelled' }
    }
    $Arms.Add($arm)
    $arm
}

function Find-Item($items, [string] $key, [string] $value) { @($items | Where-Object { $_[$key] -eq $value })[0] }

Add-Mark 'R0'
foreach ($label in 'I0/start', 'I0/end') { Add-Mark $label; $script:T += 1200 }
foreach ($n in 1..5) { $null = Add-Arm "F$n" 'exe-full' }
foreach ($n in 1..4) { $null = Add-Arm "X$n" 'exe-cancel' }
$null = Add-Arm 'X5' 'exe-close'
foreach ($label in 'I1/start', 'I1/end') { Add-Mark $label; $script:T += 1200 }

# X1: the guard killed FsAnalyser.exe at +310 MiB over its /before, then Ctrl+C landed in the 10-minute settle wait. That is
# the arm line Invoke-ExeArm's finally writes: guardStop kept, exit code -1 (the killed process), interrupted, killed false
# (the process was already gone), and no /settled mark and no report.
$x1 = Find-Item $Arms 'arm' 'X1'
$x1.guardStop = 'available 2900 MB, pool +310 MiB over X1/before'; $x1.exitCode = -1; $x1.report = $null
$x1.interrupted = $true; $x1.killed = $false
(Find-Item $Marks 'label' 'X1/exit').pagedBytes = [long](Find-Item $Marks 'label' 'X1/before').pagedBytes + 310MB
$Marks.Remove((Find-Item $Marks 'label' 'X1/settled')) | Out-Null

# X2, the control: the same guard stop, not interrupted, every mark written.
$x2 = Find-Item $Arms 'arm' 'X2'
$x2.guardStop = 'available 2900 MB, pool +310 MiB over X2/before'; $x2.exitCode = -1; $x2.report = $null
(Find-Item $Marks 'label' 'X2/exit').pagedBytes = [long](Find-Item $Marks 'label' 'X2/before').pagedBytes + 310MB

# ---- Write it, run -Report over it ----

$dir = Join-Path (Get-ScratchParent) ('fsa-sl099-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'prepare') | Out-Null
    $plan = [ordered]@{ kind = 'plan'; utc = $Start.ToString('o'); root = 'C:\'; windows = @('I0', 'I1'); arms = @('F1', 'F2', 'F3', 'F4', 'F5', 'X1', 'X2', 'X3', 'X4', 'X5'); rehearsal = $false }
    @($plan) + @($Arms) | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 6 } | Set-Content -LiteralPath (Join-Path $dir 'arms.jsonl') -Encoding UTF8
    $Marks | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 6 } | Set-Content -LiteralPath (Join-Path $dir 'marks.jsonl') -Encoding UTF8
    [ordered]@{ kind = 'prepare'; environment = [ordered]@{ os = 'Windows 11 (fixture)'; wcifsVersion = '10.0.26100.9278'; drivers = @(); antivirus = @(); oneDriveRunning = $false; fltmcFilters = 'not supplied (fixture)' } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dir 'prepare\prepare.json') -Encoding UTF8

    $shell = (Get-Process -Id $PID).Path
    $lines = @(& $shell -NoProfile -ExecutionPolicy Bypass -File $PoolWatch -Report -RunDir $dir 2>&1 | ForEach-Object { "$_" })
    $exit = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($l in $lines) { Write-Output "        | $l" }
$missing = @($lines | Where-Object { $_ -like '  MISSING *' })
$ofX1 = @($missing | Where-Object { $_ -like '  MISSING arm X1:*' })
$ofX2 = @($missing | Where-Object { $_ -like '  MISSING arm X2:*' })

# ---- Checks ----

Write-Output 'the control: X2, stopped by the guard and not interrupted'
Check ($exit -eq 2 -and @($lines | Where-Object { $_ -ceq 'RUN INCOMPLETE' }).Count -eq 1) "-Report reads the fixture as an incomplete run, exit 2 (exit $exit)"
Check ($ofX2.Count -ge 1 -and @($ofX2 | Where-Object { $_ -like '*stopped by the machine guard (available 2900 MB, pool +310 MiB over X2/before)*' }).Count -ge 1) "X2 is listed as stopped by the machine guard, with the guard's reason ($($ofX2 -join ' | '))"
Check (@($ofX2 | Where-Object { $_ -like '*paged +310 MiB*from /before to /exit*' }).Count -ge 1) "X2's line carries its net from /before to /exit ($($ofX2 -join ' | '))"
Check (($missing.Count - $ofX1.Count - $ofX2.Count) -eq 0) "nothing but X1 and X2 is missing, so the rest of the fixture is the clean run ($($missing -join ' | '))"

Write-Output 'the bug: X1, stopped by the guard and then interrupted in its settle wait'
Check ($ofX1.Count -ge 1) 'X1 is listed as missing something (it has no /settled mark)'
Check (@($ofX1 | Where-Object { $_ -like '*pool +310 MiB over X1/before*' }).Count -ge 1) "X1's guard stop is reported, with the guard's reason 'available 2900 MB, pool +310 MiB over X1/before' (got: $($ofX1 -join ' | '))"
Check (@($ofX1 | Where-Object { $_ -like '*paged +310 MiB*from /before to /exit*' }).Count -ge 1) "X1's net at the stop, paged +310 MiB from /before to /exit, is reported (got: $($ofX1 -join ' | '))"

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
