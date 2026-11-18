<#
SL-005: a folder deleted during the scan is noted as gone, not listed as unreadable. The published app, in report mode, on a tree whose
three folders (z1, z2, z3, each with a folder and a file in it) are deleted by this script while the walk is still busy in a large
subtree before them. The walk lists the root, queues a and z1 to z3, and opens each later; z1 to z3 are gone by then, so each open
fails with 2 and the scan has to call them deleted, not unreadable.

  A  the run with the deletion: core.foldersVanished is 3, core.unreadable is 0, the ending's note says it, neither caption says
     'may be missing', the Unreadable folders tab is hidden, and every schema-1 key the report always has is still there.
  B  the control, the same tree with nothing deleted: no vanished folders and no note, so a note that is always shown cannot pass A.
  C  the verdict on every ending a run can have (a stubbed read for each row): a deletion that missed its window is INCONCLUSIVE and
     retried, never FAILED; a defect is FAILED.

Nothing can time the deletion from inside the published app, which has no test seam, so this script times it: from the moment the app
starts, after a delay that it moves on from what the run showed. The verdict comes from core.folders against the tree: exactly the
tree's folders plus 3 means z1 to z3 were listed and then gone (the deletion landed and the checks apply); the tree's folders alone
means the deletion came before the root was listed, and plus 6 means after the walk had opened them, and in both the run says
INCONCLUSIVE and the deletion moves earlier or later, at most twice. Exit codes 2 and 70 and the throw, missing-value and
unwritable-path runs are the existing cases' (WindowCases.ps1); this script checks exit 0 and the report's schema-1 keys.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-005-VanishedFolders.Tests.ps1 [-Case A,B,C] [-Publish]

Needs the published app (artifacts/publish), built here by eng/dotnet.ps1 when it is missing or older than src. The tree is built under
%LOCALAPPDATA%\Temp (never the engine shell's %TEMP%, which is C:\WINDOWS\TEMP there) and removed at the end: 50,201 folders, a few
seconds to make. A and B start the app's window on the desktop, as the window cases do. Exit 0 when every check held (INCONCLUSIVE
cases are named and are not failures), 1 when any failed.
#>

param(
    [string[]] $Case = @('A', 'B', 'C'),
    [switch] $Publish,
    [string] $Exe = ''
)

$ErrorActionPreference = 'Stop'
$Case = @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) # -File passes 'A,B' as one string
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Reader = Join-Path $Repo 'eng\read-window.ps1'
$ExeGiven = $PSBoundParameters.ContainsKey('Exe')
if (-not $Exe) { $Exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe' }
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Inconclusive = New-Object System.Collections.Generic.List[string]

# The tree: a\d<0..199>\e<0..249> is 1 + 200 + 50,000 folders, the walk's long first stretch; z1..z3 each hold inner\ and a file.
$Outer = 200
$Inner = 250
$BaseFolders = 1 + $Outer + $Outer * $Inner
$Note = '3 folders were deleted while the scan ran, before they could be read; they are counted as empty folders.'
$CoreKeys = @('status', 'rootError', 'files', 'folders', 'links', 'bytes', 'bytesOnDisk', 'elapsedMs', 'unreadable', 'skippedStores', 'notScanned', 'opensUnderExcludedStore', 'foldersVanished')
$TopKeys = @('schema', 'root', 'window', 'largestFilesShown', 'largestFileOnDisk', 'largestFilesMs', 'core', 'outcome', 'refusal', 'workarounds', 'coverage')

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else { Write-Output "  FAIL  $what"; $script:Failures.Add($what) }
}
function CheckEqual($expected, $actual, [string] $what) {
    Check ("$expected" -ceq "$actual") "$what (expected $expected, got $actual)"
}

function Get-NewestSource([string] $folder) {
    (Get-ChildItem -LiteralPath $folder -Recurse -File -Include *.cs, *.xaml, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
}
function Update-Published {
    if ($ExeGiven) { return }
    $stale = $Publish -or -not (Test-Path -LiteralPath $Exe -PathType Leaf) -or (Get-Item -LiteralPath $Exe).LastWriteTimeUtc -lt (Get-NewestSource (Join-Path $Repo 'src'))
    if (-not $stale) { return }
    Write-Output '  (publishing the app)'
    $result = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') publish (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Join-Path $Repo 'artifacts\publish') 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
        $result | Select-Object -Last 15 | ForEach-Object { "  $_" }
        throw "the app did not publish (exit $LASTEXITCODE)"
    }
}

# ---- the tree ----

function New-Tree {
    $root = Join-Path $env:LOCALAPPDATA ("Temp\fsa-sl005\{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($root)
    for ($i = 0; $i -lt $Outer; $i++) {
        for ($j = 0; $j -lt $Inner; $j++) { [void][System.IO.Directory]::CreateDirectory((Join-Path $root "a\d$i\e$j")) }
    }
    [System.IO.File]::WriteAllBytes((Join-Path $root 'a\d0\f.txt'), (New-Object byte[] 1024))
    [System.IO.File]::WriteAllBytes((Join-Path $root 'a\d0\e0\g.bin'), (New-Object byte[] 2048))
    $root
}
function Add-Gone([string] $root) {
    foreach ($n in 1, 2, 3) {
        [void][System.IO.Directory]::CreateDirectory((Join-Path $root "z$n\inner"))
        [System.IO.File]::WriteAllBytes((Join-Path $root "z$n\inner\f.txt"), (New-Object byte[] 100))
    }
}
function Remove-Gone([string] $root) {
    foreach ($n in 1, 2, 3) {
        $path = Join-Path $root "z$n"
        if (Test-Path -LiteralPath $path) { [System.IO.Directory]::Delete($path, $true) }
    }
}
function Remove-Tree([string] $root) {
    if ($root -and (Test-Path -LiteralPath $root)) { try { [System.IO.Directory]::Delete($root, $true) } catch { Write-Output "  (could not remove $root : $($_.Exception.Message))" } }
}

# ---- one run ----

# The app through eng/read-window.ps1 in a job of its own, with this process free to delete z1..z3 $DeleteAfterMs after the app started.
# Returns the exit text, the report (null when there was none) and when, in ms from the app's start, the deletion ran (null: not asked).
function Invoke-Run([string] $root, [Nullable[int]] $DeleteAfterMs) {
    $leaf = Split-Path -Leaf $root
    $job = Start-Job -ScriptBlock {
        param($reader, $root, $exe)
        & pwsh -NoProfile -ExecutionPolicy Bypass -File $reader -Root $root -Exe $exe -TimeoutSec 300
    } -ArgumentList $Reader, $root, $Exe
    $deletedAtMs = $null
    if ($null -ne $DeleteAfterMs) {
        $started = $null
        $give = [DateTime]::UtcNow.AddSeconds(60)
        while ($null -eq $started -and [DateTime]::UtcNow -lt $give) {
            $app = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'FsAnalyser.App.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -and $_.CommandLine.Contains($leaf) } | Select-Object -First 1
            if ($app) { $started = $app.CreationDate.ToUniversalTime() } else { Start-Sleep -Milliseconds 25 }
        }
        if ($started) {
            $due = $started.AddMilliseconds($DeleteAfterMs)
            while ([DateTime]::UtcNow -lt $due) { Start-Sleep -Milliseconds 5 }
            Remove-Gone $root
            $deletedAtMs = [int]([DateTime]::UtcNow - $started).TotalMilliseconds
        }
    }
    $lines = @($job | Wait-Job -Timeout 420 | Receive-Job)
    Remove-Job $job -Force
    $text = ($lines | Select-Object -Skip 1) -join "`n"
    [pscustomobject]@{
        Exit        = if ($lines.Count) { [string]$lines[0] } else { 'NOTHING' }
        Report      = if (-not $lines.Count -or $text -eq 'NO REPORT' -or -not $text) { $null } else { $text | ConvertFrom-Json }
        DeletedAtMs = $deletedAtMs
    }
}

# ---- the verdict: one row for every ending a run can have ----
# held / FAILED (a defect, or a run that did not end as one should) / INCONCLUSIVE-EARLY / INCONCLUSIVE-LATE / INCONCLUSIVE (the
# deletion missed its window, which says nothing about the code). Decided from the exit and core.folders first, the checks after.
function Get-RunVerdict($read, [bool] $deleting) {
    if ($read.Exit -ceq 'TIMEOUT') { return [pscustomobject]@{ Verdict = 'FAILED'; Why = 'the app did not end within its timeout' } }
    if ($null -eq $read.Report) { return [pscustomobject]@{ Verdict = 'FAILED'; Why = "no report (exit $($read.Exit))" } }
    if ($read.Exit -cne 'EXIT 0') { return [pscustomobject]@{ Verdict = 'FAILED'; Why = "the app exited with '$($read.Exit)', not EXIT 0" } }
    $core = $read.Report.core
    if ($null -eq $core -or $core.status -cne 'Completed') { return [pscustomobject]@{ Verdict = 'FAILED'; Why = "the scan did not complete (status $($core.status))" } }
    if (-not $deleting) { return [pscustomobject]@{ Verdict = 'held'; Why = 'a scan with nothing deleted' } }
    $folders = [int]$core.folders
    if ($folders -eq $BaseFolders + 3) { return [pscustomobject]@{ Verdict = 'held'; Why = 'the deletion landed: z1 to z3 were listed and then gone' } }
    if ($folders -le $BaseFolders + 2) { return [pscustomobject]@{ Verdict = 'INCONCLUSIVE-EARLY'; Why = "core.folders is ${folders}: the deletion came before the root's listing" } }
    if ($folders -eq $BaseFolders + 6) { return [pscustomobject]@{ Verdict = 'INCONCLUSIVE-LATE'; Why = "core.folders is ${folders}: the walk had opened z1 to z3 before they were deleted" } }
    [pscustomobject]@{ Verdict = 'INCONCLUSIVE'; Why = "core.folders is ${folders}: the deletion landed partway (the tree's folders are $BaseFolders)" }
}

# The run, moved by what it showed: first at 3 s from the app's start, then earlier or later, at most three runs in all.
function Invoke-DeletionRun {
    $delay = 3000
    $last = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $root = New-Tree
        try {
            Add-Gone $root
            $read = Invoke-Run $root $delay
        }
        finally { Remove-Tree $root }
        $verdict = Get-RunVerdict $read $true
        Write-Host "  run $attempt, deleted $(if ($null -ne $read.DeletedAtMs) { "$($read.DeletedAtMs) ms" } else { 'never' }) after the app started: $($verdict.Verdict) ($($verdict.Why))"
        $last = [pscustomobject]@{ Read = $read; Verdict = $verdict }
        if ($verdict.Verdict -notlike 'INCONCLUSIVE*') { return $last }
        $delay = switch ($verdict.Verdict) { 'INCONCLUSIVE-EARLY' { $delay + 1500 } 'INCONCLUSIVE-LATE' { [Math]::Max(500, $delay - 1000) } default { $delay + 500 } }
    }
    $last
}

function Test-ReportKeys($report) {
    CheckEqual 1 $report.schema 'the report is schema 1'
    $missing = @($TopKeys | Where-Object { $_ -notin $report.PSObject.Properties.Name })
    Check ($missing.Count -eq 0) "every top-level key schema 1 always had is there$(if ($missing.Count) { " (missing: $($missing -join ', '))" })"
    $missingCore = @($CoreKeys | Where-Object { $_ -notin $report.core.PSObject.Properties.Name })
    Check ($missingCore.Count -eq 0) "every key of core is there, foldersVanished included$(if ($missingCore.Count) { " (missing: $($missingCore -join ', '))" })"
}

# ---- A: the run with the deletion ----

function Test-A {
    Write-Output 'A  a report-mode run in which z1 to z3 are deleted while queued'
    $run = Invoke-DeletionRun
    if ($run.Verdict.Verdict -like 'INCONCLUSIVE*') { $script:Inconclusive.Add("A: $($run.Verdict.Why) in all three runs"); Write-Output "  INCONCLUSIVE: $($run.Verdict.Why)"; return }
    if ($run.Verdict.Verdict -ceq 'FAILED') { Check $false "the run ended as one should: $($run.Verdict.Why)"; return }
    $report = $run.Read.Report
    $window = $report.window
    CheckEqual 'EXIT 0' $run.Read.Exit 'the app exited 0'
    CheckEqual 3 $report.core.foldersVanished 'core.foldersVanished is the three deleted folders'
    CheckEqual 0 $report.core.unreadable 'core.unreadable is 0: nothing in the scan is unreadable'
    CheckEqual ($BaseFolders + 3) $report.core.folders 'the three stay in core.folders, as empty folders'
    CheckEqual 'completed' $report.outcome 'the outcome is completed'
    $lines = @($window.OutcomeNotes.lines)
    Check ($lines -ccontains $Note) "the ending's notes carry the folders note, word for word ($($lines -join ' | '))"
    # Not a check: whether the note is in view is the outcome texts' layout (their cap while a tab is shown), the same for every note.
    $texts = $window.OutcomeTexts
    Write-Output "  info  OutcomeTexts: actualHeight $($texts.actualHeight), extentHeight $($texts.extentHeight), verticalOffset $($texts.verticalOffset); the note is in view when the texts are not capped below their extent"
    Check (-not ($lines | Where-Object { $_ -cmatch '[^\x20-\x7E]' })) 'every note line is ASCII'
    Check (-not ($lines | Where-Object { $_ -match 'fsa-sl005|\\z[123]\b|\\a\\' })) 'no note line holds a path or a folder name'
    Check ($window.LargestFilesCaption -and $window.LargestFilesCaption -cnotmatch 'missing') "the largest files caption is shown and does not say files may be missing ('$($window.LargestFilesCaption)')"
    Check ($window.KindsCaption -cnotmatch 'missing') "the file kinds caption does not say files may be missing ('$($window.KindsCaption)')"
    CheckEqual $false $window.UnreadableTab.visible 'the Unreadable folders tab is hidden'
    Test-ReportKeys $report
}

# ---- B: the control ----

function Test-B {
    Write-Output 'B  the same tree with nothing deleted'
    $root = New-Tree
    try {
        Add-Gone $root
        $read = Invoke-Run $root $null
    }
    finally { Remove-Tree $root }
    $verdict = Get-RunVerdict $read $false
    if ($verdict.Verdict -cne 'held') { Check $false "the run ended as one should: $($verdict.Why)"; return }
    $report = $read.Report
    CheckEqual 0 $report.core.foldersVanished 'core.foldersVanished is 0'
    CheckEqual 0 $report.core.unreadable 'core.unreadable is 0'
    CheckEqual ($BaseFolders + 6) $report.core.folders 'the walk listed every folder of the tree'
    Check (-not (@($report.window.OutcomeNotes.lines) | Where-Object { $_ -match 'deleted while the scan ran' })) 'no note about deleted folders'
    Test-ReportKeys $report
}

# ---- C: the verdict on stubbed reads ----

function Test-C {
    Write-Output 'C  the verdict on every ending a run can have'
    function Stub($exit, [int] $folders, $status = 'Completed') {
        $report = [pscustomobject]@{ core = [pscustomobject]@{ status = $status; folders = $folders } }
        [pscustomobject]@{ Exit = $exit; Report = $report }
    }
    $rows = @(
        @{ Id = 'timeout'; Read = [pscustomobject]@{ Exit = 'TIMEOUT'; Report = $null }; Deleting = $true; Expect = 'FAILED' },
        @{ Id = 'no report'; Read = [pscustomobject]@{ Exit = 'EXIT 0'; Report = $null }; Deleting = $true; Expect = 'FAILED' },
        @{ Id = 'nothing came back'; Read = [pscustomobject]@{ Exit = 'NOTHING'; Report = $null }; Deleting = $true; Expect = 'FAILED' },
        @{ Id = 'exit 70'; Read = (Stub 'EXIT 70' ($BaseFolders + 3)); Deleting = $true; Expect = 'FAILED' },
        @{ Id = 'cancelled'; Read = (Stub 'EXIT 0' ($BaseFolders + 3) 'Cancelled'); Deleting = $true; Expect = 'FAILED' },
        @{ Id = 'landed'; Read = (Stub 'EXIT 0' ($BaseFolders + 3)); Deleting = $true; Expect = 'held' },
        @{ Id = 'too early (nothing of z listed)'; Read = (Stub 'EXIT 0' $BaseFolders); Deleting = $true; Expect = 'INCONCLUSIVE-EARLY' },
        @{ Id = 'too early (some of z listed)'; Read = (Stub 'EXIT 0' ($BaseFolders + 2)); Deleting = $true; Expect = 'INCONCLUSIVE-EARLY' },
        @{ Id = 'too late (z1 to z3 opened first)'; Read = (Stub 'EXIT 0' ($BaseFolders + 6)); Deleting = $true; Expect = 'INCONCLUSIVE-LATE' },
        @{ Id = 'landed partway'; Read = (Stub 'EXIT 0' ($BaseFolders + 4)); Deleting = $true; Expect = 'INCONCLUSIVE' },
        @{ Id = 'control, complete'; Read = (Stub 'EXIT 0' ($BaseFolders + 6)); Deleting = $false; Expect = 'held' },
        @{ Id = 'control, cancelled'; Read = (Stub 'EXIT 0' 10 'Cancelled'); Deleting = $false; Expect = 'FAILED' }
    )
    foreach ($row in $rows) { CheckEqual $row.Expect (Get-RunVerdict $row.Read $row.Deleting).Verdict "the verdict for '$($row.Id)'" }

}

Update-Published
try {
    if ('A' -in $Case) { Test-A }
    if ('B' -in $Case) { Test-B }
    if ('C' -in $Case) { Test-C }
}
finally {
    $leftover = Join-Path $env:LOCALAPPDATA 'Temp\fsa-sl005'
    if ((Test-Path -LiteralPath $leftover) -and -not (Get-ChildItem -LiteralPath $leftover -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $leftover -Force }
}

Write-Output ''
foreach ($note in $script:Inconclusive) { Write-Output "INCONCLUSIVE: $note" }
if ($script:Failures.Count -gt 0) {
    Write-Output "FAILED: $($script:Failures.Count) check(s)"
    exit 1
}
Write-Output 'ALL CHECKS HELD'
exit 0
