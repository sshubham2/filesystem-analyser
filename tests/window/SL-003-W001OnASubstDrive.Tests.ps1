<#
SL-003 (criteria 1, 2 and 3 of the slice, and the record's place): W-001 -Stopped on a whole SUBST drive letter over a %TEMP%
tree, as a person would run it on C:\ but with nothing of the machine's own in the scan. The tree is two folders this account
may not list, which sort first, then 30,000 empty folders (300 x 100), so that the progress trigger of the stopped run, at half
the folders the finished run counted, lands while folders are still being listed (the hook's trigger comes from a 250 ms tick:
a short tree would end before it).

It runs tests/window/WindowCases.ps1 -Case W-001 -Root <letter>:\ -Stopped, then reads the record the case wrote at
docs/proof-runs/w001-<letter>-drive.json and checks it: the finished part held with outcome completed and the coverage figures,
the stopped part partial (sized null) and held with no row cut short, a record with no path in it, and nothing written under
the engine's slice folder, apart from what was there. It removes the record, the SUBST, the deny ACEs and the tree, however it
ends. Needs the interactive desktop the window cases need and a free drive letter; an elevated session ends W-001 INCONCLUSIVE.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-003-W001OnASubstDrive.Tests.ps1

Exit 0 when every check held, 1 when any failed.
#>

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" } else { Write-Output "  FAIL  $what"; $failures.Add($what) }
}

$used = @([System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1).ToUpperInvariant() })
$letter = @('X', 'Y', 'W', 'V', 'U', 'T' | Where-Object { $_ -notin $used })[0]
if (-not $letter) { throw 'no free drive letter among X, Y, W, V, U and T' }

$tree = Join-Path ([System.IO.Path]::GetTempPath()) ('sl003-w001-' + [guid]::NewGuid().ToString('N'))
$record = Join-Path $Repo ('docs\proof-runs\w001-{0}-drive.json' -f $letter.ToLowerInvariant())
$recordsFolderExisted = Test-Path -LiteralPath (Join-Path $Repo 'docs\proof-runs')
$denied = [System.Collections.Generic.List[string]]::new()
$underTaadBefore = @(& git -C $Repo status --porcelain --untracked-files=all -- .taad)
$mapped = $false

function Set-Deny([string] $folder, [bool] $add) {
    $info = [System.IO.DirectoryInfo]::new($folder)
    $acl = [System.IO.FileSystemAclExtensions]::GetAccessControl($info)
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new([System.Security.Principal.WindowsIdentity]::GetCurrent().User, 'ListDirectory', 'None', 'None', 'Deny')
    if ($add) { $acl.AddAccessRule($rule) } else { [void]$acl.RemoveAccessRule($rule) }
    [System.IO.FileSystemAclExtensions]::SetAccessControl($info, $acl)
}

try {
    foreach ($name in '0-deny-a', 'z-deny-z') {
        $folder = Join-Path $tree $name
        [void][System.IO.Directory]::CreateDirectory($folder)
        Set-Deny $folder $true
        $denied.Add($folder)
    }
    for ($i = 0; $i -lt 300; $i++) {
        for ($j = 0; $j -lt 100; $j++) { [void][System.IO.Directory]::CreateDirectory((Join-Path $tree ('b{0:D3}\s{1:D3}' -f $i, $j))) }
    }
    & subst "${letter}:" $tree
    $mapped = $true
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'tests\window\WindowCases.ps1') -Case W-001 -Root "${letter}:\" -Stopped 2>&1 | ForEach-Object { [string]$_ })
    $exit = $LASTEXITCODE
    $lines | ForEach-Object { Write-Output "        $_" }
    Check ($exit -eq 0) "W-001 -Stopped on ${letter}:\ exits 0 (it exited $exit; 3 is inconclusive)"
    Check (Test-Path -LiteralPath $record) "the record is at docs/proof-runs/w001-$($letter.ToLowerInvariant())-drive.json"
    if (Test-Path -LiteralPath $record) {
        $text = [System.IO.File]::ReadAllText($record)
        $r = $text | ConvertFrom-Json
        Check ($r.verdict -ceq 'held' -and $r.status -ceq 'Completed' -and $r.outcome -ceq 'completed') "the finished part held: Completed, outcome completed ($($r.verdict), $($r.status), $($r.outcome))"
        Check ($r.rootShape -ceq 'local-letter' -and $r.scope -ceq 'whole-drive') "the root was a local whole-drive letter ($($r.rootShape), $($r.scope))"
        Check ($null -ne $r.coverage -and $null -ne $r.coverage.panelTop) "the coverage panel figures are in the record, with where the panel stands"
        Check ($r.rows -eq 2 -and $r.header -eq 2 -and $r.coreUnreadable -eq 2 -and $r.reasonCodeMismatches -eq 0) "the two refused folders are the panel's rows, each with its code's reason ($($r.rows), $($r.header), $($r.coreUnreadable))"
        Check ($r.oracle.refusedAnotherCode -eq 0 -and $r.oracle.openRefusedButOpenable -eq 0) 'no row failed the plain open with another code, and none opened'
        Check ($null -ne $r.stopped) 'the record carries the stopped part'
        if ($null -ne $r.stopped) {
            Check ($null -eq $r.stopped.sizedAtStop -and $r.stopped.foldersAtStop -gt 0 -and $r.stopped.status -ceq 'Cancelled') "the stopped run was partial: Cancelled with sized '-' at $($r.stopped.foldersAtStop) folders"
            Check ($r.stopped.verdict -ceq 'held' -and $r.stopped.aborted -eq 0 -and $r.stopped.notInFinished -eq 0 -and $r.stopped.codeChanged -eq 0) "every folder of the stopped run's tab is in the finished run's, none cut short ($($r.stopped.verdict))"
        }
        Check ($text -notmatch '/' -and $text -notmatch '\\(?!["u])' -and $text -notmatch '"path"\s*:') 'the record holds no path'
    }
    $named = @(Select-String -LiteralPath (Join-Path $Repo 'tests\window\WindowCases.ps1') -Pattern 'taad' -CaseSensitive:$false)
    Check ($named.Count -eq 0) 'WindowCases.ps1 holds no line naming the engine folder, so W-001 can write nothing under it (Q-010, the first half)'
    $underTaadAfter = @(& git -C $Repo status --porcelain --untracked-files=all -- .taad)
    Check (@($underTaadAfter | Where-Object { $_ -notin $underTaadBefore }).Count -eq 0) 'nothing was written under .taad by the run (Q-010, the second half)'
}
finally {
    if ($mapped) { & subst "${letter}:" /d | Out-Null }
    foreach ($folder in $denied) { try { Set-Deny $folder $false } catch { } }
    if (Test-Path -LiteralPath $tree) { Remove-Item -LiteralPath $tree -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $record) { Remove-Item -LiteralPath $record -Force }
    $folder = Join-Path $Repo 'docs\proof-runs'
    if (-not $recordsFolderExisted -and (Test-Path -LiteralPath $folder) -and -not (Get-ChildItem -LiteralPath $folder -Force)) { Remove-Item -LiteralPath $folder -Force }
}

Write-Output ''
if ($failures.Count) { Write-Output "$($failures.Count) check(s) failed."; exit 1 }
Write-Output 'All checks held.'
exit 0
