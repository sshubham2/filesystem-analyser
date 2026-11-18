<#
SL-004 (SC-059): a mapped share that answers only after "Drives" is shown must get its button, in letter order. The real check
needs a share that stops answering, which changes SMB services and mappings and so runs only in a Windows Sandbox
(tests/window/SL-004-LateShareInSandbox.ps1, run through the Sandbox tool). This script is what runs anywhere, with no administrator
and no SMB change, and proves the instrument that check stands on:

  A  the verdict, on every shape of run it has to tell apart (tests/window/SL-004-LateShareCorpus.json): rows captured from real
     Sandbox runs (a late share that got its button after 20 s and after 120 s, one whose read ended at the wake and never got
     one, one that never woke) and rows made from them by one change each. Each row's exact set of failed checks and its cause are
     what Get-LateShareVerdict (SL-004-LateShareVerdict.ps1) gives, including a button appended at the end instead of in letter
     order, a share that was not late, a report read before the wake, and a read that was still blocked or had ended.
  B  the hook's drives mode on the published exe, over two SUBST letters: it holds the report until both letters have a button,
     logs the buttons in the picker's order, and the report's DrivePanel.buttons is the count it logged.
  C  the same mode with a letter that never gets a button and a 3 s wait: it gives up, says which letter, and the app still ends
     through its report (exit 0).
  D  the mode refuses a bad FSA_HOOK_WANT, and refuses FSA_HOOK_WANT in any other mode, without ending the app.
  E  the Sandbox runner and the verdict parse under Windows PowerShell 5.1 (the Sandbox's) and pwsh.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-004-LateShareButton.Tests.ps1 [-Case A,B,C,D,E] [-NoBuild]

Needs the published app (artifacts/publish) for B to D and the built hook (artifacts/window-hook): both are built here, by
eng/dotnet.ps1, when they are missing or older than their sources, unless -NoBuild. B to D start the app's window on the desktop, as
the window cases do. Exit 0 when every check held, 1 when any failed; a case whose shape could not be made on this machine (no free
drive letters) says INCONCLUSIVE and is not a failure.
#>

param(
    [string[]] $Case = @('A', 'B', 'C', 'D', 'E'),
    [switch] $NoBuild,
    [string] $Exe = '',
    [string] $HookDll = ''
)

$ErrorActionPreference = 'Stop'
$Case = @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) # -File passes 'A,B' as one string
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Reader = Join-Path $Repo 'eng\read-window.ps1'
if (-not $Exe) { $Exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe' }
if (-not $HookDll) { $HookDll = Join-Path $Repo 'artifacts\window-hook\FsaWindowHook.dll' }
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Inconclusive = New-Object System.Collections.Generic.List[string]
. (Join-Path $PSScriptRoot 'SL-004-LateShareVerdict.ps1')

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else { Write-Output "  FAIL  $what"; $script:Failures.Add($what) }
}

# ---- A: the verdict, on the corpus ----

function Test-A {
    Write-Output 'A  the verdict on every shape of run'
    $corpus = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SL-004-LateShareCorpus.json') -Raw | ConvertFrom-Json
    Check (@($corpus.rows).Count -ge 10) "the corpus holds its rows ($(@($corpus.rows).Count))"
    Check (@($corpus.rows | Where-Object { $_.expect.verdict -ceq 'held' }).Count -ge 3 -and @($corpus.rows | Where-Object { $_.expect.verdict -ceq 'FAILED' }).Count -ge 8) 'it holds runs that held (the late button came in at 20 s and 120 s) and many that did not'
    foreach ($row in $corpus.rows) {
        $in = $row.input
        $report = if ($null -ne $in.ReportButtons) { [pscustomobject]@{ window = [pscustomobject]@{ DrivePanel = [pscustomobject]@{ buttons = [int]$in.ReportButtons } } } } else { $null }
        $observations = @($in.Observations | ForEach-Object { [pscustomobject]@{ Utc = (ConvertTo-Utc $_.Utc); Alive = @($_.Alive | ForEach-Object { [int]$_ }); Named = $_.Named } })
        $v = Get-LateShareVerdict -HookLog @($in.HookLog) -Report $report -Exit ([string]$in.Exit) -LaunchUtc (ConvertTo-Utc $in.LaunchUtc) `
            -WakeUtc (ConvertTo-Utc $in.WakeUtc) -WakeSeconds ([int]$in.WakeSeconds) -Share $in.Share -Observations $observations -ShareReadIndex ([int]$in.ShareReadIndex)
        $failed = @($v.Checks | Where-Object { -not $_.Ok } | ForEach-Object { $_.Key } | Sort-Object)
        $expected = @($row.expect.failed | Sort-Object)
        Check ($v.Verdict -ceq $row.expect.verdict) "$($row.id): the verdict is $($row.expect.verdict) ($($row.source)); got $($v.Verdict)"
        Check (($failed -join ',') -ceq ($expected -join ',')) "$($row.id): the failed checks are exactly [$($expected -join ', ')]; got [$($failed -join ', ')]"
        if ($null -eq $row.expect.cause) { Check ($null -eq $v.Cause) "$($row.id): a share that got its button has no cause; got '$($v.Cause)'" }
        else { Check ($v.Cause -and $v.Cause.Contains($row.expect.cause)) "$($row.id): the cause says '$($row.expect.cause)'; got '$($v.Cause)'" }
    }
}

# ---- B to D: the hook's drives mode on the published exe ----

function Update-Build {
    if ($NoBuild) { return }
    $dotnet = Join-Path $Repo 'eng\dotnet.ps1'
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $Repo 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
        @(Get-Item -LiteralPath (Join-Path $Repo 'Directory.Build.props'), (Join-Path $Repo 'global.json'))
    $newest = ($sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    if (-not (Test-Path -LiteralPath $Exe) -or (Get-Item -LiteralPath $Exe).LastWriteTimeUtc -lt $newest) {
        Write-Output '  (publishing the app)'
        $null = & pwsh -NoProfile -ExecutionPolicy Bypass -File $dotnet publish (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Join-Path $Repo 'artifacts\publish') 2>&1
        if ($LASTEXITCODE -ne 0) { throw "publish failed with exit $LASTEXITCODE" }
    }
    Write-Output '  (building the hook)'
    $null = & pwsh -NoProfile -ExecutionPolicy Bypass -File $dotnet build (Join-Path $Repo 'tests\window\FsaWindowHook\FsaWindowHook.csproj') -c Release -o (Split-Path -Parent $HookDll) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "the hook did not build (exit $LASTEXITCODE)" }
}

function Get-FreeDriveLetters([int] $count) {
    $used = @([System.Environment]::GetLogicalDrives() | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() })
    $free = @('ZYXWVUTSRQPONMLKJIHGFED'.ToCharArray() | ForEach-Object { [string]$_ } | Where-Object { $used -notcontains $_ })
    if ($free.Count -lt $count) { return @() }
    @($free[0..($count - 1)] | Sort-Object)
}

# One report-mode launch under the hook (through eng/read-window.ps1): the exit, the report as an object (null when none) and the log.
# $variables are set for this process and removed however it ends, never left for a later launch.
function Invoke-DrivesRun([string] $mode, [hashtable] $variables, [string] $root) {
    $log = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-sl004-{0}.log" -f [guid]::NewGuid().ToString('N'))
    $names = @('DOTNET_STARTUP_HOOKS', 'FSA_HOOK_MODE', 'FSA_HOOK_LOG') + @($variables.Keys)
    try {
        $env:DOTNET_STARTUP_HOOKS = $HookDll
        $env:FSA_HOOK_MODE = $mode
        $env:FSA_HOOK_LOG = $log
        foreach ($name in $variables.Keys) { Set-Item -LiteralPath "Env:$name" -Value $variables[$name] }
        $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec 120)
        $text = ($lines | Select-Object -Skip 1) -join "`n"
        [pscustomobject]@{
            Exit   = ([string]$lines[0]) -replace '^EXIT ', ''
            Report = if ($text -eq 'NO REPORT') { $null } else { $text | ConvertFrom-Json }
            Log    = @(if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | ForEach-Object { [string]$_ } })
        }
    }
    finally {
        foreach ($name in $names) { if (Test-Path -LiteralPath "Env:$name") { Remove-Item -LiteralPath "Env:$name" } }
        if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    }
}

function New-Folder {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-sl004\{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($folder)
    Set-Content -LiteralPath (Join-Path $folder 'a.txt') 'x'
    $folder
}

function Remove-Folder([string] $folder) {
    if ($folder -and (Test-Path -LiteralPath $folder)) { try { [System.IO.Directory]::Delete($folder, $true) } catch { Write-Output "  (could not remove $folder : $($_.Exception.Message))" } }
}

function Test-BCD([string[]] $cases) {
    Update-Build
    $letters = Get-FreeDriveLetters 3
    if ($letters.Count -lt 3) { $script:Inconclusive.Add('B-D: fewer than three free drive letters'); Write-Output '  INCONCLUSIVE: fewer than three free drive letters'; return }
    $a, $b, $never = $letters[0], $letters[1], $letters[2]
    $folders = @()
    $made = @()
    try {
        $scan = New-Folder; $folders += $scan
        foreach ($letter in $a, $b) {
            $target = New-Folder; $folders += $target
            $out = & subst.exe "${letter}:" $target 2>&1
            if ($LASTEXITCODE -ne 0) { throw "subst ${letter}: exited ${LASTEXITCODE}: $out" }
            $made += $letter
        }

        if ('B' -in $cases) {
            Write-Output "B  drives mode holds the report for ${a}: and ${b}: (SUBST letters) and logs the buttons in order"
            $run = Invoke-DrivesRun 'drives' @{ FSA_HOOK_WANT = "${a}:,${b}:" } $scan
            foreach ($line in $run.Log) { Write-Output "        hook: $line" }
            $read = Read-DrivesHookLog $run.Log
            Check ($run.Exit -ceq '0' -and $null -ne $run.Report) "the app exited 0 with a report (exit $($run.Exit))"
            Check ($null -ne $read.Armed -and ($read.Armed.Want -join ',') -ceq "${a}:,${b}:") 'the hook logged what it waited for (drives-armed)'
            $done = $read.Done
            Check ($null -ne $done -and $done.Found) 'it let the report go with both letters in (drives-done found=True)'
            $final = if ($done) { @($done.Buttons) } else { @() }
            Check ($final -contains "${a}:" -and $final -contains "${b}:") "both letters have a button: $($final -join ',')"
            Check ((($final | Sort-Object) -join ',') -ceq ($final -join ',')) 'the buttons are logged in letter order, the order the picker draws them'
            Check ($null -ne $run.Report -and $run.Report.window.DrivePanel.buttons -eq $final.Count) "the report's DrivePanel.buttons is the number logged ($($final.Count)); report says $($run.Report.window.DrivePanel.buttons)"
            Check (@($read.Seen).Count -ge 1 -and @($read.Refused).Count -eq 0) 'at least one reading was logged and nothing was refused'
        }

        if ('C' -in $cases) {
            Write-Output "C  a letter that never gets a button (${never}:): the hook gives up after 3 s, names it, and the app still ends through its report"
            $run = Invoke-DrivesRun 'drives' @{ FSA_HOOK_WANT = "${a}:,${never}:"; FSA_HOOK_WANT_DEADLINE_S = '3' } $scan
            foreach ($line in $run.Log) { Write-Output "        hook: $line" }
            $read = Read-DrivesHookLog $run.Log
            Check ($run.Exit -ceq '0' -and $null -ne $run.Report) "the app exited 0 with a report (exit $($run.Exit))"
            Check ($null -ne $read.Done -and -not $read.Done.Found) 'drives-done says found=False'
            Check ($null -ne $read.Done -and $read.Done.HeldMs -ge 3000 -and $read.Done.HeldMs -lt 20000) "it held for about its 3 s wait ($($read.Done.HeldMs) ms)"
            Check (@($read.Refused | Where-Object { $_ -like "refused mode=drives reason=no button for ${never}: *" }).Count -eq 1) "it refused, naming the missing letter (${never}:) and not ${a}:"
            Check ($null -ne $run.Report -and $run.Report.window.DrivePanel.buttons -eq @($read.Done.Buttons).Count) "the report's DrivePanel.buttons is still the number logged ($(@($read.Done.Buttons).Count))"
        }

        if ('D' -in $cases) {
            Write-Output 'D  drives mode refuses what it was not given right, and FSA_HOOK_WANT in another mode, without ending the app'
            foreach ($bad in @(@{ Name = 'no FSA_HOOK_WANT'; Variables = @{}; Mode = 'drives'; Reason = 'needs FSA_HOOK_WANT' },
                    @{ Name = 'a lower-case letter'; Variables = @{ FSA_HOOK_WANT = 'z:' }; Mode = 'drives'; Reason = 'not a list of different upper-case drive letters' },
                    @{ Name = 'a letter twice'; Variables = @{ FSA_HOOK_WANT = "${a}:,${a}:" }; Mode = 'drives'; Reason = 'not a list of different upper-case drive letters' },
                    @{ Name = 'a deadline of 0'; Variables = @{ FSA_HOOK_WANT = "${a}:"; FSA_HOOK_WANT_DEADLINE_S = '0' }; Mode = 'drives'; Reason = 'FSA_HOOK_WANT_DEADLINE_S' },
                    @{ Name = 'FSA_HOOK_WANT in size mode'; Variables = @{ FSA_HOOK_WANT = "${a}:" }; Mode = 'size'; Reason = 'FSA_HOOK_WANT and FSA_HOOK_WANT_DEADLINE_S are for drives only' })) {
                $run = Invoke-DrivesRun $bad.Mode $bad.Variables $scan
                $refused = @($run.Log | Where-Object { $_ -like "refused mode=$($bad.Mode) reason=*$($bad.Reason)*" })
                Check ($refused.Count -eq 1) "$($bad.Name): refused, with '$($bad.Reason)' (log: $(@($run.Log) -join ' | '))"
                Check ($run.Exit -ceq '0' -and $null -ne $run.Report) "$($bad.Name): the app still ended through its report (exit $($run.Exit))"
            }
        }
    }
    finally {
        foreach ($letter in $made) { & subst.exe "${letter}:" /D 2>&1 | Out-Null }
        foreach ($folder in $folders) { Remove-Folder $folder }
    }
}

# ---- E: the Sandbox's script parses where the Sandbox runs it ----

function Test-E {
    Write-Output 'E  the Sandbox runner and the verdict parse under Windows PowerShell 5.1 and pwsh'
    foreach ($name in 'SL-004-LateShareInSandbox.ps1', 'SL-004-LateShareVerdict.ps1') {
        $path = Join-Path $PSScriptRoot $name
        foreach ($shell in 'powershell.exe', 'pwsh') {
            $command = "`$e = `$null; `$t = `$null; [void][System.Management.Automation.Language.Parser]::ParseFile('$($path.Replace("'", "''"))', [ref]`$t, [ref]`$e); if (`$e) { `$e | ForEach-Object { `$_.Message + ' (line ' + `$_.Extent.StartLineNumber + ')' }; exit 1 }"
            $out = @(& $shell -NoProfile -ExecutionPolicy Bypass -Command $command 2>&1)
            Check ($LASTEXITCODE -eq 0) "$name parses under $shell $(if ($LASTEXITCODE -ne 0) { "($($out -join '; '))" })"
        }
    }
}

try {
    if ('A' -in $Case) { Test-A }
    $live = @($Case | Where-Object { $_ -in 'B', 'C', 'D' })
    if ($live.Count) { Test-BCD $live }
    if ('E' -in $Case) { Test-E }
}
finally {
    foreach ($name in 'DOTNET_STARTUP_HOOKS', 'FSA_HOOK_MODE', 'FSA_HOOK_LOG', 'FSA_HOOK_WANT', 'FSA_HOOK_WANT_DEADLINE_S') {
        if (Test-Path -LiteralPath "Env:$name") { Remove-Item -LiteralPath "Env:$name" }
    }
}

Write-Output ''
foreach ($note in $script:Inconclusive) { Write-Output "INCONCLUSIVE: $note" }
if ($script:Failures.Count) { Write-Output "$($script:Failures.Count) check(s) failed."; exit 1 }
Write-Output 'All checks held.'
exit 0
