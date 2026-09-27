# The App's window cases (SL-023). Each case builds a tree under %TEMP%, runs the published app on it
# in report mode and compares what the window showed with what the case built. The run goes through
# eng/read-window.ps1, as validate does, except where the case is about the raw arguments. There is no
# App test project: this reads the shipped exe's own window, which an in-process test cannot.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case C-001
#
# -Case All runs every case. Exit 0 when every check held, 1 when any failed. The app is published
# first (eng/dotnet.ps1, into artifacts/publish) when the exe is missing or older than any source.
#
# C-006, C-012 and O-016 press what a person would press mid-scan: Stop, a fault on the UI thread, closing
# the window. The driver is tests/window/FsaWindowHook, loaded into the unmodified exe by
# DOTNET_STARTUP_HOOKS and steered by FSA_HOOK_MODE (see its StartupHook.cs). It is built on every run
# that selects one of them, into artifacts/window-hook, and its variables are set for one launch only.
#
# The oracle is the tree each case builds, never the report's own "core" section. Every non-empty file
# is a multiple of 64 KiB and every other file is empty, so the size on disk equals the size on any
# NTFS volume with clusters of 64 KiB or less, without asking the volume.
#
# N-001 to N-004 (SL-038) run against a share whose server stops answering: tests/window/StallRig, an
# SMBLibrary server on 127.0.0.1 reached through a non-persistent SMB mapping that the rig makes and
# removes itself (tests/FsAnalyser.Core.Tests/Rig/SmbRig.cs, the only file allowed to). They change this machine's SMB client mappings while
# they run, and N-001 and N-002 take about 70 s each, so they run only with -IncludeNetwork, like the
# Network=SMB-LOOPBACK trait in Core.Tests. Without it they are reported NOT RUN, never passed: -Case All
# still exits 0 on the cases that ran, and a network case named on its own exits 2. The default run proves
# only that the Stopping state's wait line does NOT appear on an ordinary cancel (C-006); that it appears,
# counts up, keeps the UI live and goes when the scan returns is N-001's.
#
# L-001 to L-003 (SL-039) are the results list, TopLevelList: the root's direct children filling in while
# the scan runs. L-001 and L-002 read it mid-scan through the hook's watch mode, which presses Cancel once
# rows show; L-002 also slows every refresh of it (FSA_HOOK_SLOW_REFRESH_MS) so that ticks are skipped and
# the final snapshot arrives while a skip is armed. M-001 is a measurement, not a case: the list at 100,000
# rows, timed by the hook's measure mode against the limits spike live-refresh-expanded-100k-folder passed
# under. It builds 100,000 folders and runs only when named, never in All.
#
# B-001 to B-008 (SL-040) are the largest-files list: the report's largestFiles* scalars and the list's
# section against a planted tree, the caption for an unreadable folder, for a tree with nothing to rank and
# for a stopped scan (B-005, under the hook), and the list at 100 rows beside a filled unreadable-folders
# list, its first row a name with an unpaired surrogate, with that list closed and then opened (B-003, the
# second run under the hook's expand mode). Planted names carry "plantedname", which the report
# must never hold. An unreadable folder is made with an icacls deny of list-folder for this user, and the
# deny is removed before the tree is. B-006 is a second scan after the first has shown its list (the hook's
# rescan mode), read through eng/read-window.ps1 at that second point. B-007 is the largest-files list full
# beside the results list after Completed and Cancelled endings, the unreadable folders closed and opened,
# at the default size and at the window's minimum size; B-008 is a stopped scan of a long path at the
# minimum size. Every list must keep at least one full row in view (the report's fullRows), with 26 drive
# buttons added by the hook (FSA_HOOK_DRIVES), so these cases never depend on what is plugged into the machine.
#
# V-001 and P-001 (SL-043). V-001 is the only case whose root is a whole drive: a SUBST letter over a small
# tree, made for this user's session and removed however the case ends. Its ending must carry the volume line
# the window now builds from the scan's own figure (ScanStore.VolumeUsed); the same tree scanned by its folder
# path must not. P-001 is a tree whose folders and files run past 260 characters, the deepest past 400, read
# by the published exe, which has no longPathAware manifest, unlike the xUnit testhost that runs the Core tests.
#
# T-001 to T-006 (SL-044) are the folder tree a completed scan shows in the results list's place. They read it
# through the hook's tree mode, which runs the app without --report (the app stays open until the hook closes
# it) and logs one JSON line per thing it read: what each row's container draws, never only what the row holds
# (tests/window/FsaWindowHook/TreeMode.cs). The rows' names go to the hook's log, never to the report (Q-016).
# T-001 is every row of a planted tree against the tree: order by space on disk, totals, shares and bars, links,
# an empty folder and names as written. T-002 is expand state through recycling. T-003 opens a folder of 100,000
# files five times and presses the numpad * key. T-004 starts a second scan with a row selected and checks the
# first scan's store is collectable. T-005 samples the UI thread through a scan, the hand-off to the tree and a
# second scan over an open large folder. T-006 is the endings that show no tree. C-002, L-003, B-003, B-007 and
# M-001 read the tree where they read the results list after a completed scan.
#
# K-001 to K-003 (SL-046) are the files by kind, a view of the results cell chosen beside its header: the report's
# "kinds" (per kind its id, label and totals, never an extension or a name) against a planted tree of seven kinds, and
# the view on screen once the hook's kinds mode has chosen it, at the default and the minimum size, and against a tree
# with no file (K-001); added up against the root after Stop (K-002, the hook's stop mode); and gone from the report and
# the list as a second scan starts and at its Could not read ending (K-003, the hook's rescan mode, as B-006). B-001
# checks a missing root's Could not read ending carries no kinds. The Scan failed ending (Scanner.Scan throws) takes the
# same path in the window, ShowProblem, and no case drives it: no hook mode makes the scan fail.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-012', 'C-013', 'C-014', 'O-016',
        'L-001', 'L-002', 'L-003', 'M-001', 'B-001', 'B-002', 'B-003', 'B-004', 'B-005', 'B-006', 'B-007', 'B-008', 'V-001', 'P-001', 'N-001', 'N-002', 'N-003', 'N-004',
        'T-001', 'T-002', 'T-003', 'T-004', 'T-005', 'T-006', 'K-001', 'K-002', 'K-003', 'R-001')]
    [string] $Case,
    [switch] $IncludeNetwork
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe'
$Reader = Join-Path $Repo 'eng\read-window.ps1'
$HookProject = Join-Path $Repo 'tests\window\FsaWindowHook\FsaWindowHook.csproj'
$HookDll = Join-Path $Repo 'artifacts\window-hook\FsaWindowHook.dll'
$RigProject = Join-Path $Repo 'tests\window\StallRig\StallRig.csproj'
$RigExe = Join-Path $Repo 'artifacts\stall-rig\StallRig.exe'
$HookVariables = 'DOTNET_STARTUP_HOOKS', 'FSA_HOOK_MODE', 'FSA_HOOK_LOG', 'FSA_HOOK_ARMED', 'FSA_HOOK_CANCEL_DELAY_MS', 'FSA_HOOK_CLOSE_DELAY_MS',
    'FSA_HOOK_SLOW_REFRESH_MS', 'FSA_HOOK_MISSING', 'FSA_HOOK_EXPAND', 'FSA_HOOK_MINSIZE', 'FSA_HOOK_DRIVES',
    'FSA_HOOK_WALK', 'FSA_HOOK_RECYCLE', 'FSA_HOOK_MULTIPLY', 'FSA_HOOK_SELECT', 'FSA_HOOK_RELEASE', 'FSA_HOOK_GAP', 'FSA_HOOK_HEIGHT', 'FSA_HOOK_REVEAL', 'FSA_HOOK_STATUS'
$HookedCases = 'C-002', 'C-006', 'C-012', 'O-016', 'L-001', 'L-002', 'M-001', 'B-003', 'B-005', 'B-006', 'B-007', 'B-008', 'N-001', 'N-002',
    'T-001', 'T-002', 'T-003', 'T-004', 'T-005', 'T-006', 'K-001', 'K-002', 'K-003', 'R-001'
$NetworkCases = 'N-001', 'N-002', 'N-003', 'N-004'
$RigOnlyCases = 'N-003', 'N-004'
$KiB64 = 64KB
$script:Failures = [System.Collections.Generic.List[string]]::new()

# ---- Checks ----

# Non-ASCII shown as \uXXXX, so a failure prints the same whatever the console's code page.
function Show($value) {
    if ($null -eq $value) { return '<null>' }
    $text = [string]$value
    $builder = [System.Text.StringBuilder]::new()
    foreach ($c in $text.ToCharArray()) {
        if ([int]$c -lt 32 -or [int]$c -gt 126) { [void]$builder.AppendFormat('\u{0:X4}', [int]$c) } else { [void]$builder.Append($c) }
    }
    "'$builder'"
}

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

function CheckEqual($expected, $actual, [string] $what) {
    $same = ($null -eq $expected -and $null -eq $actual) -or
        ($null -ne $expected -and $null -ne $actual -and [string]$expected -ceq [string]$actual)
    Check $same "$what`n          expected $(Show $expected)`n          actual   $(Show $actual)"
}

# ---- The tree's own figures, in the window's words ----

function Format-Count([long] $n, [string] $noun) {
    if ($n -eq 1) { "1 $noun" } else { '{0:N0} {1}s' -f $n, $noun }
}

function Format-Size([long] $bytes) {
    $units = 'bytes', 'KB', 'MB', 'GB', 'TB', 'PB'
    [double] $value = $bytes
    $unit = 0
    while ($value -ge 1024 -and $unit -lt $units.Length - 1) {
        $value /= 1024
        $unit++
    }
    if ($unit -eq 0) { '{0:N0} bytes' -f $bytes } else { '{0:N1} {1}' -f $value, $units[$unit] }
}

# The first two lines of OutcomeDetail for a tree: counts, then bytes (the rest vary per run).
function Expected-Lines($manifest) {
    "$(Format-Count $manifest.Files 'file') and $(Format-Count $manifest.Folders 'folder'); $(Format-Count $manifest.Links 'link') not followed"
    "$(Format-Size $manifest.Bytes) in files, taking $(Format-Size $manifest.Bytes) on disk"
}

function CheckFinished($read, [string] $root, [string] $shownRoot, $manifest) {
    CheckEqual 'EXIT 0' $read.Exit 'exit code'
    Check ($null -ne $read.Report) 'a report was written'
    if ($null -eq $read.Report) { return }
    $window = $read.Report.window
    CheckEqual 1 $read.Report.schema 'schema'
    CheckEqual $root $read.Report.root 'root is the path passed'
    CheckEqual $true $window.OutcomePanel.visible 'OutcomePanel is on screen'
    Check ($window.OutcomePanel.actualHeight -gt 0) "OutcomePanel has been laid out (actualHeight $($window.OutcomePanel.actualHeight))"
    CheckEqual "Finished: $shownRoot" $window.OutcomeTitle 'OutcomeTitle'
    $lines = @(Expected-Lines $manifest)
    CheckEqual $lines[0] $window.OutcomeDetail[0] 'OutcomeDetail line 1 against the tree'
    CheckEqual $lines[1] $window.OutcomeDetail[1] 'OutcomeDetail line 2 against the tree'
}

# ---- Trees ----

function New-CaseFolder {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-window-cases\{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($folder)
    $folder
}

function New-File([string] $path, [long] $bytes) {
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllBytes($path, [byte[]]::new($bytes))
}

# About 20 files in 4 folders: scanned well inside one 250 ms timer tick.
function New-SmallTree([string] $root) {
    $bytes = 0
    for ($d = 1; $d -le 4; $d++) {
        for ($f = 1; $f -le 5; $f++) {
            New-File (Join-Path $root "folder$d\file$f.bin") ($f * $KiB64)
            $bytes += $f * $KiB64
        }
    }
    [pscustomobject]@{ Files = 20; Folders = 4; Links = 0; Bytes = $bytes }
}

# Enough for many timer ticks before the ending. Thousands of files alone scan in well under one tick
# here (30,000 empty files: 54 ms), so the tree also holds 30,150 nested folders, which is what takes
# the scan past a second (20,100 folders: 1.0 to 1.3 s).
function New-LargeTree([string] $root) {
    $tops = 150
    $files = 0
    $bytes = 0
    for ($i = 0; $i -lt $tops; $i++) {
        $top = Join-Path $root "n$i"
        for ($j = 0; $j -lt 200; $j++) {
            [void][System.IO.Directory]::CreateDirectory((Join-Path $top "m$j"))
        }
        New-File (Join-Path $top 'data.bin') $KiB64
        $bytes += $KiB64
        for ($f = 0; $f -lt 29; $f++) {
            New-File (Join-Path $top "empty$f.txt") 0
        }
        $files += 30
    }
    [pscustomobject]@{ Files = $files; Folders = $tops + $tops * 200; Links = 0; Bytes = $bytes }
}

# SL-039: 20 top-level folders with names no report may carry (Q-016), each holding a file of its own size
# and 1,500 empty folders. About as long a scan as New-LargeTree's, in few enough rows that most are still
# not read after the first second.
function New-NamedTree([string] $root) {
    $names = @(for ($i = 0; $i -lt 20; $i++) { 'Recognizable-Top-{0:D2}' -f $i })
    $bytes = 0
    for ($i = 0; $i -lt $names.Count; $i++) {
        $top = Join-Path $root $names[$i]
        for ($j = 0; $j -lt 1500; $j++) {
            [void][System.IO.Directory]::CreateDirectory((Join-Path $top "m$j"))
        }
        New-File (Join-Path $top 'data.bin') (($i + 1) * $KiB64)
        $bytes += ($i + 1) * $KiB64
    }
    [pscustomobject]@{ Files = $names.Count; Folders = $names.Count * 1501; Links = 0; Bytes = $bytes; Names = $names }
}

# SL-039: $tops top-level folders of $subs empty folders each, and a 64 KiB file in every hundredth: thousands
# of rows in the results list.
function New-WideTree([string] $root, [int] $tops, [int] $subs) {
    $files = 0
    for ($i = 0; $i -lt $tops; $i++) {
        $top = Join-Path $root ('w{0:D6}' -f $i)
        [void][System.IO.Directory]::CreateDirectory($top)
        for ($j = 0; $j -lt $subs; $j++) {
            [void][System.IO.Directory]::CreateDirectory((Join-Path $top "s$j"))
        }
        if ($i % 100 -eq 0) {
            New-File (Join-Path $top 'data.bin') $KiB64
            $files++
        }
    }
    [pscustomobject]@{ Files = $files; Folders = $tops * (1 + $subs); Links = 0; Bytes = $files * $KiB64 }
}

function Remove-CaseFolder([string] $folder) {
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
}

# ---- Running the app ----

# Publishes when the exe is missing or older than any source. The mutex keeps two cases run side by
# side from publishing over each other.
function Update-App {
    $mutex = [System.Threading.Mutex]::new($false, 'Local\FsAnalyser-WindowCases-Publish')
    [void]$mutex.WaitOne()
    try {
        $sources = @(Get-ChildItem -LiteralPath (Join-Path $Repo 'src') -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
            @(Get-Item -LiteralPath (Join-Path $Repo 'Directory.Build.props'), (Join-Path $Repo 'global.json'))
        $newest = ($sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
        if ((Test-Path -LiteralPath $Exe) -and (Get-Item -LiteralPath $Exe).LastWriteTimeUtc -ge $newest) {
            return
        }
        Write-Output 'publishing the app (eng/dotnet.ps1 publish)'
        & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') publish `
            (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Join-Path $Repo 'artifacts\publish') |
            ForEach-Object { "  $_" }
        if ($LASTEXITCODE -ne 0) {
            throw "publish failed with exit $LASTEXITCODE"
        }
    }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

# Builds the hook into artifacts/window-hook on every run that selects a hooked case, before any hook
# variable is set, and leaves staleness to MSBuild's incremental build. A failed build (a compile error,
# or the dll locked by an app a timed-out run left behind) ends the run with exit 1 and the build output:
# a dll from an earlier run never stands in for the source.
function Update-Hook {
    $mutex = [System.Threading.Mutex]::new($false, 'Local\FsAnalyser-WindowCases-Hook')
    [void]$mutex.WaitOne()
    try {
        Write-Output 'building the hook (eng/dotnet.ps1 build)'
        $output = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') build $HookProject `
                -c Release -o (Split-Path -Parent $HookDll) 2>&1)
        $exit = $LASTEXITCODE
    }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
    $output | ForEach-Object { "  $_" }
    if ($exit -ne 0) {
        Write-Output "FAILED: the hook did not build (exit $exit), so no case ran"
        exit 1
    }
    if (-not (Test-Path -LiteralPath $HookDll -PathType Leaf)) {
        Write-Output "FAILED: the hook built but $HookDll is not there, so no case ran"
        exit 1
    }
}

# Builds the stall rig into artifacts/stall-rig on every run that selects a network case, the way
# Update-Hook builds the hook, and ends the run the same way when it does not build.
function Update-StallRig {
    $mutex = [System.Threading.Mutex]::new($false, 'Local\FsAnalyser-WindowCases-StallRig')
    [void]$mutex.WaitOne()
    try {
        Write-Output 'building the stall rig (eng/dotnet.ps1 build)'
        $output = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') build $RigProject `
                -c Release -o (Split-Path -Parent $RigExe) 2>&1)
        $exit = $LASTEXITCODE
    }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
    $output | Select-Object -Last 3 | ForEach-Object { "  $_" }
    if ($exit -ne 0 -or -not (Test-Path -LiteralPath $RigExe -PathType Leaf)) {
        $output | ForEach-Object { "  $_" }
        Write-Output "FAILED: the stall rig did not build (exit $exit), so no case ran"
        exit 1
    }
}

# Through eng/read-window.ps1, the way validate reads the window.
function Read-Window([string] $root, [int] $timeoutSec = 120) {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec $timeoutSec)
    if ($LASTEXITCODE -ne 0) {
        throw "eng/read-window.ps1 failed with exit $LASTEXITCODE"
    }
    $text = ($lines | Select-Object -Skip 1) -join "`n"
    [pscustomobject]@{
        Exit   = $lines[0]
        Text   = $text
        Report = if ($text -eq 'NO REPORT') { $null } else { $text | ConvertFrom-Json }
    }
}

# Straight through Start-Process, for the cases about raw arguments. -ArgumentList joins an array with
# spaces and quotes nothing.
function Start-App($arguments, [string] $workingDirectory = [System.IO.Path]::GetTempPath()) {
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WorkingDirectory $workingDirectory -PassThru
    $null = $process.Handle # without it ExitCode reads empty after the wait
    $process
}

# The exit code, or TIMEOUT after killing the process: a run that waits on a person never ends.
function Wait-App($process, [int] $timeoutSec = 60) {
    if ($process.WaitForExit($timeoutSec * 1000)) {
        return [string]$process.ExitCode
    }
    $process.Kill()
    $process.WaitForExit()
    'TIMEOUT'
}

function Invoke-App($arguments, [int] $timeoutSec = 60) {
    Wait-App (Start-App $arguments) $timeoutSec
}

# ---- Running the app under the hook ----

# Runs $action with the hook's three variables set, in this process and whatever it launches, and
# removes them however $action ends, so a case that throws leaves every later case unhooked. Removed,
# never blanked: an empty DOTNET_STARTUP_HOOKS writes errors to the Application log (dotnet/runtime#82260).
# $armed holds the stall-rig timing variables (FSA_HOOK_ARMED and the delays), set and removed the same way.
# Returns what $action output and the hook's log lines.
function Invoke-WithHook([string] $mode, [string] $hookDll, [scriptblock] $action, [hashtable] $armed = @{}) {
    $log = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-window-hook\{0}.log" -f [guid]::NewGuid().ToString('N'))
    try {
        [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $log))
        $env:DOTNET_STARTUP_HOOKS = $hookDll
        $env:FSA_HOOK_MODE = $mode
        $env:FSA_HOOK_LOG = $log
        foreach ($name in $armed.Keys) {
            if ($name -notin $HookVariables) { throw "Invoke-WithHook: $name is not a hook variable" }
            Set-Item -LiteralPath "Env:$name" -Value $armed[$name]
        }
        $result = & $action
        [pscustomobject]@{
            Result = $result
            Log    = @(if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log })
        }
    }
    finally {
        foreach ($name in $HookVariables) {
            if (Test-Path -LiteralPath "Env:$name") { Remove-Item -LiteralPath "Env:$name" }
        }
        if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    }
}

# One report-mode launch with the hook in $mode: the exit code (or TIMEOUT), the report at $report parsed
# (null when there is none) and as text, and the hook's log. Straight through Start-App, like C-009, because
# these cases plant a stale report at a path of their own and eng/read-window.ps1 picks its own. $variables
# holds any other hook variables, set and removed as Invoke-WithHook does.
function Invoke-HookedRun([string] $mode, [string] $root, [string] $report, [hashtable] $variables = @{}, [int] $timeoutSec = 60) {
    $run = Invoke-WithHook $mode $HookDll { Wait-App (Start-App "`"$root`" --report `"$report`"") $timeoutSec } $variables
    $text = if (Test-Path -LiteralPath $report -PathType Leaf) { Get-Content -LiteralPath $report -Raw } else { $null }
    [pscustomobject]@{
        Exit   = $run.Result
        Report = if ($null -eq $text) { $null } else { $text | ConvertFrom-Json }
        Text   = $text
        Log    = $run.Log
    }
}

# Every hooked case asks this before it reads the exit code or the report: a hook that never loaded, never
# found Stop pressable or refused fails by that name, not as a wrong ending. So does a hook that failed
# after it acted ("refused" after "acted"): the exit code and the report would not be what a press gave. A
# "rethrown" line is not the hook's failure: the app's own handler threw, and the app was given it.
function CheckActed($run, [string] $mode) {
    foreach ($line in $run.Log) { Write-Output "        hook: $line" }
    $log = @($run.Log)
    $acted = @($log | Where-Object { $_.StartsWith("acted mode=$mode ") })
    Check ($acted.Count -eq 1) "the hook acted (one 'acted mode=$mode' line in FSA_HOOK_LOG); if not, the hook never acted and nothing after it is this case's ending"
    if ($acted.Count -eq 0) { return }
    $after = $log[([array]::IndexOf($log, $acted[0]) + 1)..($log.Count)] | Where-Object { $_ -and $_.StartsWith('refused ') }
    Check (@($after).Count -eq 0) "the hook did not fail after it acted ($(Show (@($after) -join ' | ')))"
}

# An exit code with its hex beside it: 0xE0434352 is the runtime's unhandled exception, not the app's 70.
function Show-Exit([string] $exit) {
    $code = 0
    if ([int]::TryParse($exit, [ref]$code)) { '{0} (0x{0:X8})' -f $code } else { $exit }
}

# ---- The Stopping state, as the hook logged it (SL-038) ----

# The hook's "stopping" and "returned" lines after the Cancel it pressed: ms since the press, the title,
# the wait line ('-' while StoppingDetail is collapsed or empty) and whether it is the last line, written
# as the app shut down.
function Read-Stopping($log) {
    foreach ($line in $log) {
        if ($line -cmatch '^(stopping|returned) ms=(\d+) title=(.*) detail=(.*?)( final=True)?$') {
            [pscustomobject]@{
                Kind   = $Matches[1]
                Ms     = [long]$Matches[2]
                Title  = $Matches[3]
                Detail = $Matches[4]
                Final  = [bool]$Matches[5]
            }
        }
    }
}

# The wait line the window must show for N whole seconds (src/FsAnalyser.App/StoppingWait.cs, whose
# boundaries Core.Tests pins): no estimate of any kind from 90 s.
function Expected-Wait([long] $seconds) {
    if ($seconds -lt 90) {
        "Still waiting for the drive to answer ($seconds s). If a network connection dropped, this can take a minute or more. Closing the window quits at once."
    }
    else {
        "Still waiting for the drive to answer ($seconds s). Closing the window quits at once."
    }
}

# The value of key=value in one hook line, or null. Values with spaces (detail) yield their first word.
function Field([string] $line, [string] $key) {
    if ($line -cmatch "(?:^| )$key=(\S+)") { $Matches[1] } else { $null }
}

# N-001's checks on the hook's log of an armed stop, after CheckActed: Cancel 3 s after the stall, the
# wait line from 1.9 to 3 s after Cancel until the return, in the pinned wording, counting up with the time
# since Cancel; a line at least once a second (the hook's timer is on the UI thread, so a longer gap is a
# UI thread that did not run); and no line once the scan has returned or as the app shuts down.
function CheckWaitLine($log) {
    $acted = @($log | Where-Object { $_.StartsWith('acted mode=stop ') })[0]
    $sinceArmed = [long](Field $acted 'sincearmed')
    Check ($sinceArmed -ge 3000 -and $sinceArmed -lt 4000) "Cancel was pressed 3 s after the stall armed ($sinceArmed ms)"

    $stopping = @(Read-Stopping $log)
    $returned = @($stopping | Where-Object Kind -eq 'returned')
    Check ($returned.Count -eq 1) "the hook saw the scan return once after Cancel ($($returned.Count) 'returned' lines)"
    if ($returned.Count -ne 1) { return }
    $back = $returned[0].Ms
    Write-Output ('        Cancel to scan return: {0:N1} s (measured, not asserted)' -f ($back / 1000))
    Check ($back -ge 3000) "VOID unless the scan outlasted the wait line's gate: it returned $back ms after Cancel"

    $ticks = @($stopping | Where-Object { $_.Kind -eq 'stopping' -and -not $_.Final -and $_.Ms -lt $back })
    $shown = @($ticks | Where-Object Detail -cne '-')
    Check ($shown.Count -gt 0) 'the wait line showed while the scan had not returned'
    if ($shown.Count -gt 0) {
        Check ($shown[0].Ms -ge 1900 -and $shown[0].Ms -le 3000) "it first showed $($shown[0].Ms) ms after Cancel (1900 to 3000)"
    }
    $early = @($ticks | Where-Object { $_.Ms -lt 1900 -and $_.Detail -cne '-' })
    Check ($early.Count -eq 0) "nothing showed before 1.9 s ($(Show (($early | ForEach-Object { "$($_.Ms) ms" }) -join ', ')))"
    $gaps = @($ticks | Where-Object { $_.Ms -ge 3000 -and $_.Detail -ceq '-' })
    Check ($gaps.Count -eq 0) "the line stayed from 3 s until the return ($(Show (($gaps | ForEach-Object { "$($_.Ms) ms" }) -join ', ')))"

    $previous = -1
    $wrong = [System.Collections.Generic.List[string]]::new()
    foreach ($tick in $shown) {
        if ($tick.Detail -cnotmatch '^Still waiting for the drive to answer \((\d+) s\)\.') {
            $wrong.Add("$($tick.Ms) ms: no count in $(Show $tick.Detail)")
            continue
        }
        $n = [long]$Matches[1]
        if ($tick.Detail -cne (Expected-Wait $n)) { $wrong.Add("$($tick.Ms) ms: $(Show $tick.Detail)") }
        if ($n -lt $previous) { $wrong.Add("$($tick.Ms) ms: $n s after $previous s") }
        # The app floors its own clock, started a moment after the hook's, and shows it at its next tick.
        if ($n * 1000 -gt $tick.Ms + 100 -or $tick.Ms - $n * 1000 -gt 1750) { $wrong.Add("$($tick.Ms) ms: shows $n s") }
        $previous = $n
    }
    Check ($wrong.Count -eq 0) "the wait line has the pinned wording and counts up with the time since Cancel ($(Show ($wrong -join ' | ')))"

    $times = @(0) + @($ticks | ForEach-Object Ms) + @($back)
    $worst = 0
    for ($i = 1; $i -lt $times.Count; $i++) { $worst = [Math]::Max($worst, $times[$i] - $times[$i - 1]) }
    Check ($worst -le 1000) "the UI thread logged at least once a second from Cancel to the return (longest gap $worst ms over $($ticks.Count) lines)"

    CheckEqual '-' $returned[0].Detail 'the wait line is gone when the scan returns'
    $final = @($stopping | Where-Object Final)
    Check ($final.Count -eq 1 -and $final[0].Detail -ceq '-') "the wait line is gone at shutdown ($(Show (($final | ForEach-Object Detail) -join ' | ')))"
}

# N-002's checks on the hook's log of an armed close, after CheckActed: whether Cancel came first (and the
# wait line was then on screen at the close), the window gone within 1 s of Close by the hook's own clock,
# and the process gone within 5 s by its exit time against the close's UTC stamp.
function CheckCloseEscape($log, [string] $exit, $exitUtc, [bool] $cancelledFirst) {
    $pressed = @($log | Where-Object { $_.StartsWith('acted mode=stop ') })
    $close = @($log | Where-Object { $_.StartsWith('acted mode=close ') })[0]
    if ($cancelledFirst) {
        Check ($pressed.Count -eq 1) 'Cancel was pressed before the close'
        Check ((Field $close 'detail') -cne '-') "the wait line was on screen when the window closed ($(Show ($close -creplace '^.* detail=(.*) ms=\d+ sincearmed=.*$', '$1')))"
    }
    else {
        Check ($pressed.Count -eq 0) "nothing pressed Cancel before the close ($($pressed.Count))"
    }
    $closed = @($log | Where-Object { $_.StartsWith('closed ') })
    Check ($closed.Count -eq 1) 'the window closed'
    if ($closed.Count -eq 1) {
        $windowMs = [long](Field $closed[0] 'ms') - [long](Field $close 'ms')
        Check ($windowMs -le 1000) "the window was gone $windowMs ms after Close (1000 at most)"
    }
    Check ($exit -ne 'TIMEOUT') 'the process ended'
    if ($exit -ne 'TIMEOUT') {
        $closeUtc = [DateTime]::Parse((Field $close 'utc'), [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
        $processMs = [long]($exitUtc - $closeUtc).TotalMilliseconds
        Check ($processMs -le 5000) "the process was gone $processMs ms after Close (5000 at most)"
        CheckEqual '0' $exit 'exit code'
    }
}

# ---- The stall rig (SL-038, -IncludeNetwork only) ----

# Starts tests/window/StallRig with its marker and tree in $folder and returns at once: the process, a
# task for the UNC root it prints once its mapping is made (after any other rig run has ended), and a task
# for its stderr. Wait-RigRoot waits for the root.
function Start-StallRig([string] $folder, [int] $waitSec = 150) {
    [void][System.IO.Directory]::CreateDirectory($folder)
    $marker = Join-Path $folder 'armed.txt'
    $start = [System.Diagnostics.ProcessStartInfo]::new($RigExe)
    foreach ($arg in '--marker', $marker, '--wait-sec', [string]$waitSec) { $start.ArgumentList.Add($arg) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)
    [pscustomobject]@{
        Process  = $process
        Marker   = $marker
        RootLine = $process.StandardOutput.ReadLineAsync()
        Errors   = $process.StandardError.ReadToEndAsync()
        Root     = $null
        Exit     = $null
        Killed   = $false
    }
}

# Sets the rig's Root once it has printed it within $timeoutSec, or fails a check in the rig's own words
# (another run held on, or the mapping failed). The caller reads $rig.Root.
function Wait-RigRoot($rig, [int] $timeoutSec = 200) {
    if ($rig.RootLine.Wait($timeoutSec * 1000) -and $rig.RootLine.Result) {
        $rig.Root = $rig.RootLine.Result
        Write-Output "        rig: serving $($rig.Root)"
        return
    }
    $said = if ($rig.Process.WaitForExit(5000)) { $rig.Errors.Result.Trim() } else { 'still running' }
    Check $false "the stall rig printed its share within $timeoutSec s ($(Show $said))"
}

# Closes the rig's stdin, so it answers again, removes its own mapping and exits. Sets $rig.Exit to its exit
# code (TIMEOUT after killing it, KILLED when a case had killed it) and shows its stderr.
function Stop-StallRig($rig, [int] $timeoutSec = 60) {
    if ($null -eq $rig -or $null -ne $rig.Exit) { return }
    $process = $rig.Process
    if ($process.HasExited -and $rig.Killed) {
        $rig.Exit = 'KILLED'
    }
    else {
        try { $process.StandardInput.Close() } catch { }
        if ($process.WaitForExit($timeoutSec * 1000)) {
            $rig.Exit = [string]$process.ExitCode
        }
        else {
            $process.Kill()
            $process.WaitForExit()
            $rig.Exit = 'TIMEOUT'
        }
    }
    if ($rig.Errors.Wait(5000)) {
        foreach ($line in ($rig.Errors.Result -split "`r?`n" | Where-Object { $_ })) { Write-Output "        rig: $line" }
    }
}

# Kills the rig as TerminateProcess does, with no cleanup: its mapping and tree stay behind.
function Kill-StallRig($rig) {
    $rig.Killed = $true
    $rig.Process.Kill()
    $rig.Process.WaitForExit()
}

# The machine's SMB mappings, read only. The rig is the one thing that changes them.
function Get-RigMappings {
    @(& powershell.exe -NoProfile -NonInteractive -Command 'Get-SmbMapping | ForEach-Object { $_.RemotePath }' |
            Where-Object { $_ -match '^\\\\127\.0\.0\.1\\fsa[0-9a-fA-F]{8}$' })
}

# Whether the rig's share answers: its root lists a, hung and z. Nothing here lists hung itself.
function Test-RigServes([string] $root) {
    $names = @(Get-ChildItem -LiteralPath $root -Name -ErrorAction SilentlyContinue | Sort-Object)
    ($names -join ',') -eq 'a,hung,z'
}

# ---- Cases ----

# C-001: a tree that finishes inside one timer tick.
function Test-C001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'small'
        $manifest = New-SmallTree $root
        CheckFinished (Read-Window $root) $root $root $manifest
    }
    finally { Remove-CaseFolder $case }
}

# C-002: a tree large enough for many timer ticks before the ending (New-LargeTree). Since SL-044 its ending
# shows the folder tree, whose root's children the hook reads where SL-039 read the results list.
function Test-C002 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        $manifest = New-LargeTree $root

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -ne $read.Report -and $null -ne $read.Report.core) {
            # Not an oracle for the window: only proof that this tree fed the shape (several ticks).
            $elapsed = $read.Report.core.elapsedMs
            Check ($elapsed -ge 750) "the scan ran for at least three 250 ms ticks ($elapsed ms)"
            Write-Output "        CountersText after the last tick: $(Show $read.Report.window.CountersText)"
        }
        # SL-044: at a completed ending the folder tree takes the results list's place, and the report holds no
        # rows of either (Q-016).
        if ($null -ne $read.Report) {
            CheckEqual $true $read.Report.window.FolderTree.visible 'FolderTree is on screen'
            CheckEqual $false $read.Report.window.TopLevelList.inView 'TopLevelList is not: the tree took its place'
            Check ($read.Text -notmatch '"topLevel"') 'no topLevel in the report: the results list holds nothing once the tree shows'
            CheckEqual "Largest at the top of $root, by space on disk" $read.Report.window.TopLevelHeader 'TopLevelHeader'
        }
        # The tree root's children, where SL-039 read the list: every top-level folder, each drawn with its size,
        # and they add up to the tree.
        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '1' }
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        if ($script:Failures.Count -gt $before) { return }
        $rows = @(Lines-Of $run 'tree-row' | Where-Object depth -eq 1)
        CheckEqual 150 $rows.Count 'the root row holds a row per top-level folder'
        $drawn = @($rows | Where-Object { $null -ne $_.rendered -and $_.rendered.onDisk -ceq (Format-Size $KiB64) -and $_.rendered.size -ceq (Format-Size $KiB64) })
        CheckEqual 150 $drawn.Count 'each is drawn with its 64 KiB, on disk and in bytes'
        CheckEqual $manifest.Bytes ($rows | Measure-Object -Property size -Sum).Sum 'they add up to the tree'
        $root_ = @(Lines-Of $run 'tree-row' | Where-Object depth -eq 0)
        CheckEqual (Format-Size $manifest.Bytes) $root_[0].rendered.size 'the root row draws the whole tree''s size'
    }
    finally { Remove-CaseFolder $case }
}

# SL-039: the report's topLevel counts against what the case expects, key by key.
function CheckTopLevel($topLevel, [hashtable] $expected, [string] $against) {
    Check ($null -ne $topLevel) 'the report has topLevel (the results list shows a snapshot)'
    if ($null -eq $topLevel) { return }
    foreach ($key in 'rows', 'reading', 'notRead', 'stoppedPartway', 'unreadable', 'bytes', 'onDisk', 'files', 'final') {
        if ($expected.ContainsKey($key)) {
            CheckEqual $expected[$key] $topLevel.$key "topLevel.$key $against"
        }
    }
}

# SL-039: the hook's watch line, as fields, or null when it never watched.
function Read-WatchLine($log) {
    $line = @($log | Where-Object { $_.StartsWith('watch rows=') })
    if ($line.Count -ne 1) { return $null }
    $fields = @{}
    foreach ($key in 'rows', 'folderBytes', 'notRead', 'containers', 'skipped', 'armed', 'running', 'listHeight') {
        $fields[$key] = Field $line[0] $key
    }
    [pscustomobject]$fields
}

# C-003: a junction and a hardlinked pair. The junction's target holds 1 MiB that must not be counted,
# and the pair's 128 KiB must be counted once.
function Test-C003 {
    $case = New-CaseFolder
    $junction = $null
    try {
        $outside = Join-Path $case 'outside'
        New-File (Join-Path $outside 'big.bin') 1MB
        $root = Join-Path $case 'links'
        $first = Join-Path $root 'one\a.bin'
        New-File $first (2 * $KiB64)
        New-File (Join-Path $root 'one\c.bin') $KiB64
        [void][System.IO.Directory]::CreateDirectory((Join-Path $root 'two'))
        New-Item -ItemType HardLink -Path (Join-Path $root 'two\b.bin') -Value $first | Out-Null
        $junction = Join-Path $root 'j'
        New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
        $manifest = [pscustomobject]@{ Files = 3; Folders = 2; Links = 1; Bytes = 3 * $KiB64 }

        CheckFinished (Read-Window $root) $root $root $manifest
    }
    finally {
        if ($junction -and (Test-Path -LiteralPath $junction)) {
            [System.IO.Directory]::Delete($junction) # the link only, never its target
        }
        Remove-CaseFolder $case
    }
}

# C-004: the root does not exist. The ending is in the file, not in the exit code.
function Test-C004 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'missing'
        $read = Read-Window $root
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        CheckEqual $root $read.Report.root 'root is the path passed'
        $title = $read.Report.window.OutcomeTitle
        Check (-not $title.StartsWith('Finished')) "OutcomeTitle is not Finished ($(Show $title))"
        CheckEqual "Could not read $root" $title 'OutcomeTitle'
        CheckEqual $true $read.Report.window.OutcomePanel.visible 'OutcomePanel is on screen'
        Check ($null -ne $read.Report.core) 'core holds the result'
        if ($null -ne $read.Report.core) {
            CheckEqual 'RootUnreadable' $read.Report.core.status 'core.status'
            Check ($null -ne $read.Report.core.rootError) "core.rootError is set ($($read.Report.core.rootError))"
        }
    }
    finally { Remove-CaseFolder $case }
}

# C-005: ShowProblem before any ScanResult. A relative root is refused by LongPath.NormalizeRoot before
# the scan makes a result, so the ending comes with no result and core is null.
function Test-C005 {
    $root = 'fsa-not-an-absolute-path'
    $read = Read-Window $root
    CheckEqual 'EXIT 0' $read.Exit 'exit code (70 would mean the null result crashed the report)'
    Check ($null -ne $read.Report) 'a report was written'
    if ($null -eq $read.Report) { return }
    CheckEqual $root $read.Report.root 'root is the argument passed'
    CheckEqual "Could not read $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
    Check ($read.Report.window.OutcomeDetail[0] -like '*is not an absolute path*') "OutcomeDetail says why ($(Show $read.Report.window.OutcomeDetail[0]))"
    Check ($read.Text -match '"core":\s*null') 'core is null'
}

# C-006: Stop pressed while the scan runs (criterion 6). The hook presses CancelButton as soon as a person
# could (enabled and on screen, which is only during a scan) and the ending must be the app's own
# Stopped. New-LargeTree's scan outlasts the hook's 25 ms poll many times over. First the hook's harness:
# pwsh 7, a .NET host that runs startup hooks, must be left alone by it, a case that throws with the
# variables set must leave none of them behind, and with no scan running the hook must press nothing.
function Test-C006 {
    # The control: pwsh ends when the hook it is told to load is missing, so it does load what is named.
    $missing = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-no-such-hook-{0}.dll" -f [guid]::NewGuid().ToString('N'))
    $control = Invoke-WithHook 'close' $missing { & pwsh -NoProfile -Command 'exit 0' 2>$null; $LASTEXITCODE }
    Check ($control.Result -ne 0) "control: pwsh fails when DOTNET_STARTUP_HOOKS names a missing dll (exit $(Show-Exit $control.Result)), so it runs startup hooks"
    $inert = Invoke-WithHook 'close' $HookDll { & pwsh -NoProfile -Command 'exit 0'; $LASTEXITCODE }
    CheckEqual '0' $inert.Result 'inert host: pwsh 7 exits 0 under the hook in close mode'
    Check ($inert.Log.Count -eq 0) "inert host: the hook wrote nothing ($(Show ($inert.Log -join ' | ')))"

    $message = 'a case failing after the hook variables were set'
    $threw = $null
    try { Invoke-WithHook 'stop' $HookDll { throw $message } | Out-Null }
    catch { $threw = $_.Exception.Message }
    CheckEqual $message $threw 'cleanup: the throw reached the case'
    foreach ($name in $HookVariables) {
        Check (-not (Test-Path -LiteralPath "Env:$name")) "cleanup: $name is removed after the throw"
    }

    # Opened with no root, the window shows CancelButton enabled (its XAML default) but hidden, and no scan
    # runs. The hook must not press it: it watches it for its 20 s and refuses. Report mode cannot show
    # this, because its scan starts as the window loads.
    $idle = Invoke-WithHook 'stop' $HookDll {
        $process = Start-Process -FilePath $Exe -WorkingDirectory ([System.IO.Path]::GetTempPath()) -PassThru
        try {
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            while (-not $process.HasExited -and $clock.Elapsed.TotalSeconds -lt 40) {
                $text = ''
                try { $text = [System.IO.File]::ReadAllText($env:FSA_HOOK_LOG) } catch { } # absent, or mid-append
                if ($text -match '(?m)^(acted|refused) ') { break }
                Start-Sleep -Milliseconds 250
            }
        }
        finally {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
    }
    foreach ($line in $idle.Log) { Write-Output "        hook: $line" }
    Check ($idle.Log -contains 'waiting found=True enabled=True visible=False') 'no scan: the hook saw CancelButton enabled but hidden'
    Check (@($idle.Log -match '^acted ').Count -eq 0) 'no scan: the hook did not press it'
    Check (@($idle.Log -match '^refused mode=stop ').Count -eq 1) 'no scan: the hook refused once its 20 s were up'

    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        $manifest = New-LargeTree $root
        $run = Invoke-HookedRun 'stop' $root (Join-Path $case 'report.json')
        $before = $script:Failures.Count
        CheckActed $run 'stop'
        if ($script:Failures.Count -gt $before) { return }
        CheckEqual '0' $run.Exit 'exit code'
        Check ($null -ne $run.Report) 'a report was written'
        if ($null -eq $run.Report) { return }
        CheckEqual 1 $run.Report.schema 'schema'
        CheckEqual $root $run.Report.root 'root is the path passed'
        CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
        $title = $run.Report.window.OutcomeTitle
        Check ("$title".StartsWith("Stopped: $root.")) "OutcomeTitle is the Stopped ending ($(Show $title))"
        Write-Output "        stopped after $($run.Report.core.folders) of $($manifest.Folders) folders"

        # SL-038: a local cancel returns inside the 2 s gate, so the Stopping state's wait line never shows.
        # This proves only that it does not appear; that it appears is N-001's (-IncludeNetwork). Checked
        # after the ending, so an ending that is already wrong (WindowMutants' CancelThrows) fails only on it.
        $stopping = @(Read-Stopping $run.Log)
        $returned = @($stopping | Where-Object Kind -eq 'returned')
        Check ($returned.Count -eq 1) "the hook saw the scan return once after Cancel ($($returned.Count) 'returned' lines)"
        if ($returned.Count -eq 1) {
            Check ($returned[0].Ms -lt 2000) "the scan returned within 2 s of Cancel ($($returned[0].Ms) ms)"
        }
        Check (@($stopping | Where-Object Final).Count -eq 1) 'the hook wrote its last stopping line as the app shut down'
        $shown = @($stopping | Where-Object Detail -cne '-')
        Check ($shown.Count -eq 0) "no stopping line shows the wait line ($(Show (($shown | ForEach-Object { "$($_.Ms) ms: $($_.Detail)" }) -join ' | ')))"
        $titles = @($stopping | Where-Object Title -cne 'Stopping...')
        Check ($titles.Count -eq 0) "every stopping line has the title Stopping... ($(Show (($titles | ForEach-Object Title) -join ' | ')))"
    }
    finally { Remove-CaseFolder $case }
}

# C-007: a root with a space, passed the way Start-Process passes an array: unquoted, so it splits.
# Report mode must refuse it (exit 2, no file); the reader must quote it and get the whole path back.
function Test-C007 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree with space'
        $manifest = New-SmallTree $root
        $report = Join-Path $case 'report.json'

        CheckEqual '2' (Invoke-App @($root, '--report', $report)) 'unquoted root: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'unquoted root: no report'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'unquoted root: no .tmp'

        CheckFinished (Read-Window $root) $root $root $manifest
    }
    finally { Remove-CaseFolder $case }
}

# C-008: a report from an earlier run sits at the target path.
function Test-C008 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        [void](New-SmallTree $root)
        $report = Join-Path $case 'report.json'
        $stale = '{"schema":1,"stale":true}'

        Set-Content -LiteralPath $report -Value $stale
        Set-Content -LiteralPath "$report.tmp" -Value $stale
        CheckEqual '2' (Invoke-App @('--report', $report)) 'no root: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'no root: the stale report is gone'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'no root: the stale .tmp is gone'

        Set-Content -LiteralPath $report -Value $stale
        CheckEqual '0' (Invoke-App @($root, '--report', $report)) 'with a root: exit code'
        $written = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { '' }
        Check ($written -notmatch 'stale') 'with a root: the stale report was replaced'
        CheckEqual $root ($written | ConvertFrom-Json).root 'with a root: the new report is this run''s'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'with a root: no .tmp left'
    }
    finally { Remove-CaseFolder $case }
}

# C-009: the report cannot be written. Exit 3 and nothing left behind, whether it is refused before any
# scan or the final write fails because the report's folder went away during the scan.
function Test-C009 {
    $case = New-CaseFolder
    $process = $null
    try {
        $root = Join-Path $case 'tree'
        [void](New-SmallTree $root)

        $report = Join-Path $case 'no-such-folder\report.json'
        CheckEqual '3' (Invoke-App @($root, '--report', $report)) 'missing folder: exit code'
        Check (-not (Test-Path -LiteralPath (Split-Path -Parent $report))) 'missing folder: nothing was created'

        $folder = Join-Path $case 'a-folder'
        [void][System.IO.Directory]::CreateDirectory($folder)
        CheckEqual '3' (Invoke-App @($root, '--report', $folder)) 'a folder as the report path: exit code'
        Check (Test-Path -LiteralPath $folder -PathType Container) 'a folder as the report path: the folder is untouched'
        Check (-not (Test-Path -LiteralPath "$folder.tmp")) 'a folder as the report path: no .tmp'

        # Past the startup check: the folder is deleted once the window is up, while a large tree is
        # still scanning, so only the final write can fail.
        $large = Join-Path $case 'large'
        [void](New-LargeTree $large)
        $out = Join-Path $case 'out'
        [void][System.IO.Directory]::CreateDirectory($out)
        $report = Join-Path $out 'r.json'
        $process = Start-App "`"$large`" --report `"$report`""
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and $clock.Elapsed.TotalSeconds -lt 30) {
            Start-Sleep -Milliseconds 10
            $process.Refresh()
        }
        Check ($process.MainWindowHandle -ne [IntPtr]::Zero -and -not $process.HasExited) "folder gone mid-scan: the window was up and the app still running after $($clock.ElapsedMilliseconds) ms"
        Remove-Item -LiteralPath $out -Recurse -Force
        CheckEqual '3' (Wait-App $process) 'folder gone mid-scan: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'folder gone mid-scan: no report'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'folder gone mid-scan: no .tmp'
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        Remove-CaseFolder $case
    }
}

# C-010: non-ASCII in the root, and a no-break space and a dash in the window text (the title shows the
# root). The file must be pure ASCII, read the same by Windows PowerShell 5.1 and pwsh.
function Test-C010 {
    $case = New-CaseFolder
    try {
        $name = "fsa $([char]0x00E9) $([char]0x65E5)$([char]0x672C)$([char]0x00A0)$([char]0x2013) end"
        $root = Join-Path $case $name
        $manifest = New-SmallTree $root

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        Check ($read.Text -cmatch '^[\x00-\x7F]*$') 'the report text is pure ASCII'
        foreach ($code in 0x00E9, 0x65E5, 0x672C, 0x00A0, 0x2013) {
            $escape = '{0}u{1:X4}' -f [char]92, $code # a backslash, u and four hex digits
            Check ($read.Text.Contains($escape, [StringComparison]::OrdinalIgnoreCase)) "the report holds $escape"
        }

        # The file itself, read by both shells.
        $report = Join-Path $case 'report.json'
        CheckEqual '0' (Invoke-App "`"$root`" --report `"$report`"") 'direct run: exit code'
        [byte[]] $bytes = [System.IO.File]::ReadAllBytes($report)
        CheckEqual '123' $bytes[0] 'the file starts with { (no byte-order mark)'
        Check (@($bytes | Where-Object { $_ -ge 0x80 }).Count -eq 0) 'every byte of the file is ASCII'

        $codes = { param($s) ([int[]][char[]]$s) -join ',' }
        $probe = Join-Path $case 'read51.ps1'
        Set-Content -LiteralPath $probe -Encoding ascii -Value @(
            'param([string] $Path)'
            '$j = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json'
            '([int[]][char[]]$j.root) -join '','''
            '([int[]][char[]]$j.window.OutcomeTitle) -join '','''
        )
        $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $read51 = @(& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $probe -Path $report)
        $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        CheckEqual (& $codes $root) $read51[0] 'Windows PowerShell 5.1 reads root as passed'
        CheckEqual (& $codes "Finished: $root") $read51[1] 'Windows PowerShell 5.1 reads OutcomeTitle'
        CheckEqual (& $codes $json.window.OutcomeTitle) $read51[1] 'pwsh and 5.1 read the same OutcomeTitle'
    }
    finally { Remove-CaseFolder $case }
}

# C-011: no --report. The window stays open after its scan has ended and nothing is written.
function Test-C011 {
    $case = New-CaseFolder
    $process = $null
    try {
        $root = Join-Path $case 'tree'
        $manifest = New-SmallTree $root
        $work = Join-Path $case 'work'
        [void][System.IO.Directory]::CreateDirectory($work)
        $before = @(Get-ChildItem -LiteralPath $case -Recurse -Force | ForEach-Object FullName)

        # How long the same tree takes to reach its ending, measured in report mode.
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        CheckFinished (Read-Window $root) $root $root $manifest
        $toEnding = $clock.Elapsed.TotalSeconds

        $process = Start-App @($root) $work
        $wait = [Math]::Max(5, 3 * $toEnding)
        Start-Sleep -Milliseconds ([int]($wait * 1000))
        Check (-not $process.HasExited) ("still open {0:N1} s after launch; its ending came within {1:N1} s in report mode" -f $wait, $toEnding)
        $after = @(Get-ChildItem -LiteralPath $case -Recurse -Force | ForEach-Object FullName)
        $new = @($after | Where-Object { $_ -notin $before })
        Check ($new.Count -eq 0 -and $after.Count -eq $before.Count) "no file was written in the tree or the working folder ($(Show ($new -join ', ')))"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        Remove-CaseFolder $case
    }
}

# C-012: an unhandled exception on the UI thread mid-scan (criterion 12). The app's own handler must end
# the run: exit 70 and nothing at the report path. A stale report is planted there first, so an absent
# file is the app's doing. Without the handler the runtime ends the process with 0xE0434352 instead.
function Test-C012 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)
        $report = Join-Path $case 'report.json'
        Set-Content -LiteralPath $report -Value '{"schema":1,"stale":true}'
        $run = Invoke-HookedRun 'throw' $root $report
        $before = $script:Failures.Count
        CheckActed $run 'throw'
        if ($script:Failures.Count -gt $before) { return }
        Check ($run.Exit -eq '70') "exit code is the app's 70 (actual $(Show-Exit $run.Exit))"
        Check (-not (Test-Path -LiteralPath $report)) 'the stale report is gone'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'no .tmp left'
    }
    finally { Remove-CaseFolder $case }
}

# C-013: a root ending in a backslash, which would escape the closing quote if the reader did not
# double it.
function Test-C013 {
    $case = New-CaseFolder
    try {
        $tree = Join-Path $case 'tree'
        $manifest = New-SmallTree $tree
        $root = "$tree\"
        CheckFinished (Read-Window $root) $root $tree $manifest
    }
    finally { Remove-CaseFolder $case }
}

# C-014: nothing to scan or nowhere to write. Exit 2 at once; a window waiting on a person would run
# into the timeout instead.
function Test-C014 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        [void](New-SmallTree $root)
        $report = Join-Path $case 'report.json'

        CheckEqual '2' (Invoke-App @('--report') 30) '--report alone: exit code'
        CheckEqual '2' (Invoke-App @($root, '--report') 30) 'a root and --report with no value: exit code'
        CheckEqual '2' (Invoke-App @('--report', $report) 30) 'a report path and no root: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'a report path and no root: no report'
    }
    finally { Remove-CaseFolder $case }
}

# O-016: the window closed while its scan runs, in report mode. WPF exits 0 when the last window closes;
# the app's Exit handler turns an exit 0 without a report into 70. The 'acted' line is what shows the
# scan was still running when the window closed: Stop was pressable. A stale report is planted as in C-012.
function Test-O016 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)
        $report = Join-Path $case 'report.json'
        Set-Content -LiteralPath $report -Value '{"schema":1,"stale":true}'
        $run = Invoke-HookedRun 'close' $root $report
        $before = $script:Failures.Count
        CheckActed $run 'close'
        if ($script:Failures.Count -gt $before) { return }
        Check ($run.Exit -eq '70') "exit code is 70, not the 0 a closed window gets by default (actual $(Show-Exit $run.Exit))"
        Check (-not (Test-Path -LiteralPath $report)) 'the stale report is gone'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'no .tmp left'
    }
    finally { Remove-CaseFolder $case }
}

# L-001 (SL-039): results fill in while the scan runs. The hook's watch mode waits until the list holds a
# folder row with bytes while another is still not read and Cancel is still pressable, logs what it saw and
# presses Cancel. After the stop the list must hold the final snapshot, whose rows add up to what was read
# (core: the only exact figure for a partial scan), with some rows short, and the report must carry no
# top-level name: counts only (Q-016).
function Test-L001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'named'
        $manifest = New-NamedTree $root
        $run = Invoke-HookedRun 'watch' $root (Join-Path $case 'report.json')
        $before = $script:Failures.Count
        CheckActed $run 'watch'
        if ($script:Failures.Count -gt $before) { return }
        $watch = Read-WatchLine $run.Log
        Check ($null -ne $watch) "the hook logged one 'watch rows=' line"
        if ($null -eq $watch) { return }
        CheckEqual 'True' $watch.running 'the scan was still running when the rows were read (Cancel pressable)'
        CheckEqual '20' $watch.rows 'the list held a row per top-level folder mid-scan'
        Check ([long]$watch.folderBytes -gt 0) "a folder row already showed bytes mid-scan ($($watch.folderBytes))"
        Check ([int]$watch.notRead -ge 1) "another was not read yet ($($watch.notRead))"

        CheckEqual '0' $run.Exit 'exit code'
        Check ($null -ne $run.Report) 'a report was written'
        if ($null -eq $run.Report) { return }
        CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
        $core = $run.Report.core
        CheckTopLevel $run.Report.topLevel @{ rows = 20; reading = 0; bytes = $core.bytes; onDisk = $core.bytesOnDisk; files = $core.files; final = $true } 'against core'
        if ($null -ne $run.Report.topLevel) {
            $short = [int]$run.Report.topLevel.notRead + [int]$run.Report.topLevel.stoppedPartway
            Check ($short -ge 1) "some rows are short after the stop (notRead + stoppedPartway = $short)"
        }
        CheckEqual 'What was read before the stop. Rows marked not read yet or stopped partway are short.' $run.Report.window.TopLevelHeader 'TopLevelHeader'
        $named = @($manifest.Names | Where-Object { $run.Text.Contains($_) })
        Check ($named.Count -eq 0 -and -not $run.Text.Contains('Recognizable-Top')) "no top-level name is in the report ($(Show ($named -join ', ')))"
    }
    finally { Remove-CaseFolder $case }
}

# L-002 (SL-039): thousands of rows, refreshed while the scan runs, on a UI thread slowed to 150 ms a refresh
# by the hook, so every refresh runs over the window's 100 ms and the tick after it skips. The hook presses
# Cancel only once a tick has skipped and the next skip is armed again, so the final snapshot arrives right
# after a skipped tick with the skip still armed: the ending must show it anyway. Mid-scan the list must
# realize only a screenful of its rows (virtualization on).
function Test-L002 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'wide'
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $manifest = New-WideTree $root 3000 20
        Write-Output ("        built {0:N0} folders in {1:N1} s" -f $manifest.Folders, $clock.Elapsed.TotalSeconds)
        $run = Invoke-HookedRun 'watch' $root (Join-Path $case 'report.json') @{ FSA_HOOK_SLOW_REFRESH_MS = '150' }
        $before = $script:Failures.Count
        CheckActed $run 'watch'
        if ($script:Failures.Count -gt $before) { return }
        $watch = Read-WatchLine $run.Log
        Check ($null -ne $watch) "the hook logged one 'watch rows=' line"
        if ($null -eq $watch) { return }
        CheckEqual 'True' $watch.running 'the scan was still running'
        CheckEqual '3000' $watch.rows 'the list held all 3,000 rows mid-scan'
        Check ([int]$watch.containers -gt 0 -and [int]$watch.containers -le 200) "only a screenful of rows was realized (at most $($watch.containers) containers for 3,000 rows)"
        Check ([int]$watch.skipped -ge 1) "a tick skipped its refresh after a slow one ($($watch.skipped) skipped)"
        CheckEqual 'True' $watch.armed 'the next skip was armed when Cancel was pressed'

        CheckEqual '0' $run.Exit 'exit code'
        Check ($null -ne $run.Report) 'a report was written'
        if ($null -eq $run.Report) { return }
        CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
        $core = $run.Report.core
        CheckTopLevel $run.Report.topLevel @{ rows = 3000; reading = 0; bytes = $core.bytes; files = $core.files; final = $true } 'against core (the ending showed the final snapshot)'
    }
    finally { Remove-CaseFolder $case }
}

# L-003 (SL-039): endings with nothing to show. An empty root shows the folder tree with its root row alone
# (SL-044) under its header; a root that is not there, and one refused before any scan, show no list and no tree
# at all, so no row from anything is left on screen. (A scan that faults after rows were shown ends the app in report mode, C-012: no report can
# show its list.)
function Test-L003 {
    $case = New-CaseFolder
    try {
        $empty = Join-Path $case 'empty'
        [void][System.IO.Directory]::CreateDirectory($empty)
        $read = Read-Window $empty
        CheckEqual 'EXIT 0' $read.Exit 'empty root: exit code'
        if ($null -ne $read.Report) {
            CheckEqual "Finished: $empty" $read.Report.window.OutcomeTitle 'empty root: OutcomeTitle'
            # SL-044: completed, so the tree, holding the root alone, where the empty list was.
            CheckEqual $true $read.Report.window.FolderTree.visible 'empty root: FolderTree is on screen'
            CheckEqual 1 $read.Report.window.FolderTree.realizedRows 'empty root: the tree has the root row alone'
            Check ($read.Text -notmatch '"topLevel"') 'empty root: no topLevel in the report (the list holds nothing)'
            CheckEqual "$empty is empty." $read.Report.window.TopLevelHeader 'empty root: TopLevelHeader'
        }

        foreach ($shape in @(
                [pscustomobject]@{ Name = 'missing root'; Root = (Join-Path $case 'missing') }
                [pscustomobject]@{ Name = 'relative root'; Root = 'fsa-not-an-absolute-path' })) {
            $read = Read-Window $shape.Root
            CheckEqual 'EXIT 0' $read.Exit "$($shape.Name): exit code"
            Check ($null -ne $read.Report) "$($shape.Name): a report was written"
            if ($null -eq $read.Report) { continue }
            Check ($read.Report.window.OutcomeTitle -like 'Could not read *') "$($shape.Name): the ending is Could not read ($(Show $read.Report.window.OutcomeTitle))"
            Check ($read.Text -notmatch '"topLevel"') "$($shape.Name): no topLevel in the report (the list shows nothing)"
            CheckEqual '' $read.Report.window.TopLevelHeader "$($shape.Name): TopLevelHeader"
            CheckEqual $false $read.Report.window.FolderTree.visible "$($shape.Name): no folder tree"
        }
    }
    finally { Remove-CaseFolder $case }
}

# M-001 (SL-039, named only): the results list at 100,000 rows. The hook's measure mode times every refresh
# (the window's own time, handler and layout) and an Input-priority probe every 50 ms while the scan runs.
# Checked against the limits spike live-refresh-expanded-100k-folder held under with a TreeView stand-in:
# refresh p95 under 100 ms, Input p95 under 200 ms, no refresh of 1 s or more. Timing on a shared machine:
# a failure here is a measurement to record, not proof of a defect by itself.
function Test-M001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'flat'
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt 100000; $i++) {
            [void][System.IO.Directory]::CreateDirectory((Join-Path $root ('f{0:D6}' -f $i)))
        }
        Write-Output ("        built 100,000 folders in {0:N1} s" -f $clock.Elapsed.TotalSeconds)
        $run = Invoke-HookedRun 'measure' $root (Join-Path $case 'report.json') @{} 300
        foreach ($line in $run.Log) { Write-Output "        hook: $line" }
        $measured = @($run.Log | Where-Object { $_.StartsWith('measured ') })
        Check ($measured.Count -eq 1) "the hook logged one 'measured' line"
        if ($measured.Count -ne 1) { return }
        $m = $measured[0]
        $refreshP95 = [double](Field $m 'refreshP95')
        $refreshMax = [double](Field $m 'refreshMax')
        $inputP95 = [double](Field $m 'inputP95')
        Check ([int](Field $m 'refreshes') -ge 3) "the list refreshed at least three times at scale ($(Field $m 'refreshes'))"
        CheckEqual '100000' (Field $m 'rows') 'the list held 100,000 rows when the scan returned'
        $containers = [int](Field $m 'containers')
        Check ($containers -gt 0 -and $containers -le 200) "only a screenful was realized (at most $containers containers after any refresh)"
        Check ($refreshP95 -lt 100) "refresh p95 $refreshP95 ms is under 100 ms"
        Check ($refreshMax -lt 1000) "no refresh took 1 s or more (max $refreshMax ms)"
        Check ($inputP95 -lt 200) "Input probe p95 $inputP95 ms is under 200 ms"
        CheckEqual '0' $run.Exit 'exit code'
        if ($null -ne $run.Report) {
            # SL-044: completed, so the folder tree has taken the list's place.
            CheckEqual $true $run.Report.window.FolderTree.visible 'after completion FolderTree is on screen'
            Check ($run.Text -notmatch '"topLevel"') 'and the results list holds nothing'
        }
    }
    finally { Remove-CaseFolder $case }
}

# N-001 (-IncludeNetwork, SL-038): Cancel on a share whose server stopped answering. The rig's server stops
# at the listing of hung; the hook presses Cancel 3 s after the rig's marker, and the scan returns only
# when the SMB client gives up (57 to 66 s on this rig). The wait line must first show 1.9 to 3 s after
# Cancel: the 2 s gate, plus up to one 250 ms progress tick (MainWindow's timer), one 250 ms hook sample
# and UI jitter (237 ms measured in spike 3), about 2.74 s at worst. Its seconds match the time since
# Cancel and never go down; the hook's own 250 ms UI-thread timer never goes a second without a line, so
# the UI stayed live; the line is gone when the scan returns and at shutdown; and the app ends as a
# cancel: exit 0, report written, Cancelled. Cancel-to-return is printed as measured and never asserted.
function Test-N001 {
    $case = New-CaseFolder
    $rig = $null
    try {
        $rig = Start-StallRig (Join-Path $case 'rig')
        Wait-RigRoot $rig
        if (-not $rig.Root) { return }
        $report = Join-Path $case 'report.json'
        $run = Invoke-WithHook 'stop' $HookDll { Wait-App (Start-App "`"$($rig.Root)`" --report `"$report`"") 150 } @{
            FSA_HOOK_ARMED           = $rig.Marker
            FSA_HOOK_CANCEL_DELAY_MS = '3000'
        }
        $run = [pscustomobject]@{
            Exit   = $run.Result
            Report = if (Test-Path -LiteralPath $report -PathType Leaf) { Get-Content -LiteralPath $report -Raw | ConvertFrom-Json } else { $null }
            Log    = $run.Log
        }
        $before = $script:Failures.Count
        CheckActed $run 'stop'
        if ($script:Failures.Count -gt $before) { return }
        CheckWaitLine $run.Log
        CheckEqual '0' $run.Exit 'exit code'
        Check ($null -ne $run.Report) 'a report was written'
        if ($null -ne $run.Report) {
            CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
            $title = "$($run.Report.window.OutcomeTitle)"
            Check ($title.StartsWith('Stopped: ')) "OutcomeTitle is the Stopped ending ($(Show $title))"
        }
    }
    finally {
        Stop-StallRig $rig
        if ($null -ne $rig -and $null -ne $rig.Exit) { CheckEqual '0' $rig.Exit 'the rig ended cleanly and removed its mapping' }
        Remove-CaseFolder $case
    }
}

# N-002 (-IncludeNetwork, SL-038): the wait line says "Closing the window quits at once", so closing the
# window during the stall must end the app: the window gone within 1 s and the process within 5 s. Two
# shapes, each on a fresh rig and in interactive mode as a person runs it: Cancel 3 s after the stall then
# Close 2.5 s later, with the wait line on screen; and Close 3 s after the stall with no Cancel first.
function Test-N002 {
    $shapes = @(
        [pscustomobject]@{ Name = 'Cancel then Close'; Cancel = '3000'; Close = '5500' }
        [pscustomobject]@{ Name = 'Close with no prior Cancel'; Cancel = $null; Close = '3000' }
    )
    foreach ($shape in $shapes) {
        Write-Output "  $($shape.Name)"
        $case = New-CaseFolder
        $rig = $null
        try {
            $rig = Start-StallRig (Join-Path $case 'rig')
            Wait-RigRoot $rig
            if (-not $rig.Root) { continue }
            $armed = @{ FSA_HOOK_ARMED = $rig.Marker; FSA_HOOK_CLOSE_DELAY_MS = $shape.Close }
            if ($shape.Cancel) { $armed.FSA_HOOK_CANCEL_DELAY_MS = $shape.Cancel }
            $run = Invoke-WithHook 'close' $HookDll {
                $process = Start-App "`"$($rig.Root)`""
                $exit = Wait-App $process 150
                [pscustomobject]@{ Exit = $exit; ExitUtc = if ($exit -ne 'TIMEOUT') { $process.ExitTime.ToUniversalTime() } }
            } $armed
            $before = $script:Failures.Count
            CheckActed $run 'close'
            if ($script:Failures.Count -gt $before) { continue }
            CheckCloseEscape $run.Log $run.Result.Exit $run.Result.ExitUtc ([bool]$shape.Cancel)
        }
        finally {
            Stop-StallRig $rig
            Remove-CaseFolder $case
        }
    }
}

# N-003 (-IncludeNetwork, SL-038, M1): two rig runs at once. The SMB client allows one port per server
# name, so rig runs are serialised by one lock per sign-in session (RigLock): a second run either fails at once,
# saying so (--wait-sec 2), or waits and starts when the first has ended. The first run's mapping survives
# both, and neither run's mapping outlives it.
function Test-N003 {
    $case = New-CaseFolder
    $first = $null
    $impatient = $null
    $patient = $null
    try {
        $first = Start-StallRig (Join-Path $case 'first')
        Wait-RigRoot $first
        if (-not $first.Root) { return }
        Check (Test-RigServes $first.Root) "the first run serves $($first.Root)"

        $impatient = Start-StallRig (Join-Path $case 'impatient') 2
        $ended = $impatient.Process.WaitForExit(30000)
        Check $ended 'a second run with --wait-sec 2 ended within 30 s'
        if ($ended) {
            CheckEqual '1' ([string]$impatient.Process.ExitCode) 'the second run exit code'
            $said = $impatient.Errors.Result
            Check ($said -match 'Another stall-rig run') "it said another run holds the rig ($(Show $said.Trim()))"
            Check (-not $impatient.RootLine.Result) 'it printed no share'
        }
        Check ($first.Root -in (Get-RigMappings)) "the first run's mapping survived the second run"
        Check (Test-RigServes $first.Root) 'the first run still serves its share'

        $patient = Start-StallRig (Join-Path $case 'patient')
        Check (-not $patient.RootLine.Wait(5000)) 'a third run waited while the first was alive (no share within 5 s)'
        Check ($first.Root -in (Get-RigMappings)) "the first run's mapping survived the waiting run"
        Stop-StallRig $first
        CheckEqual '0' $first.Exit 'the first run ended cleanly when its stdin closed'
        Wait-RigRoot $patient 60
        if ($patient.Root) {
            Check (Test-RigServes $patient.Root) "the waiting run serves $($patient.Root) once the first has ended"
            Check ($first.Root -notin (Get-RigMappings)) 'the first run removed its own mapping'
        }
        Stop-StallRig $patient
        CheckEqual '0' $patient.Exit 'the waiting run ended cleanly'
        $left = @(Get-RigMappings | Where-Object { $_ -in @($first.Root, $patient.Root) })
        Check ($left.Count -eq 0) "no mapping of these runs is left ($(Show ($left -join ', ')))"
    }
    finally {
        foreach ($rig in $first, $impatient, $patient) { Stop-StallRig $rig }
        Remove-CaseFolder $case
    }
}

# N-004 (-IncludeNetwork, SL-038, M1): a rig run killed with no cleanup leaves its mapping to 127.0.0.1 on a
# dead port, which the SMB client would hold against the next run's port. The next run must start anyway,
# remove that leftover under the lock and serve its own share.
function Test-N004 {
    $case = New-CaseFolder
    $killed = $null
    $next = $null
    try {
        $killed = Start-StallRig (Join-Path $case 'killed')
        Wait-RigRoot $killed
        if (-not $killed.Root) { return }
        Kill-StallRig $killed
        $left = @(Get-RigMappings)
        Check ($killed.Root -in $left) "VOID unless the killed run's mapping was left behind ($(Show ($left -join ', ')))"

        $next = Start-StallRig (Join-Path $case 'next')
        Wait-RigRoot $next 60
        if (-not $next.Root) { return }
        Check (Test-RigServes $next.Root) "the next run serves $($next.Root)"
        Check ($killed.Root -notin (Get-RigMappings)) "the next run removed the killed run's leftover mapping"
        Stop-StallRig $next
        CheckEqual '0' $next.Exit 'the next run ended cleanly'
        Check ($next.Errors.Result -match [regex]::Escape("left behind: $($killed.Root)")) 'the next run said which leftover it removed'
        Check ($next.Root -notin (Get-RigMappings)) 'the next run removed its own mapping'
    }
    finally {
        foreach ($rig in $killed, $next) { Stop-StallRig $rig }
        Remove-CaseFolder $case
    }
}

# ---- The largest files (SL-040) ----

$Planted = 'plantedname'

# Denies this user list-folder (RD, FILE_LIST_DIRECTORY) on $folder alone, so the scan lists it as unreadable
# with error 5, as Core.Tests' TempTree.Deny does. Undo-DenyListing takes the deny away again.
function Deny-Listing([string] $folder) {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $out = & icacls.exe $folder /deny "*${sid}:(RD)" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls /deny on $folder exited ${LASTEXITCODE}: $out" }
}

function Undo-DenyListing([string] $folder) {
    if (-not $folder -or -not [System.IO.Directory]::Exists($folder)) { return }
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $out = & icacls.exe $folder /remove:d "*$sid" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls /remove:d on $folder exited ${LASTEXITCODE}: $out" }
}

# What the report says of the largest-files section after a scan that returned: how many rows are bound,
# the first row's on-disk bytes, a measured ranking time, the section and its caption on screen, the list
# laid out inside the panel when it has rows, and no planted name anywhere in the report (Q-016).
function CheckLargestFiles($read, [int] $shown, $firstOnDisk, [string] $caption) {
    $report = $read.Report
    $window = $report.window
    CheckEqual $shown $report.largestFilesShown 'largestFilesShown (rows bound to LargestFilesList)'
    CheckEqual $firstOnDisk $report.largestFileOnDisk 'largestFileOnDisk (the first row, against the tree)'
    Check ($null -ne $report.largestFilesMs -and $report.largestFilesMs -ge 0) "largestFilesMs was measured ($(Show $report.largestFilesMs))"
    CheckEqual $true $window.LargestFilesPanel.visible 'LargestFilesPanel is on screen'
    Check ($window.LargestFilesPanel.actualHeight -gt 0) "LargestFilesPanel has been laid out (actualHeight $($window.LargestFilesPanel.actualHeight))"
    CheckEqual $caption $window.LargestFilesCaption 'LargestFilesCaption'
    CheckEqual ($shown -gt 0) $window.LargestFilesList.inView 'LargestFilesList is on screen inside the panel exactly when it has rows'
    Check (-not $read.Text.Contains($Planted)) "the report holds no planted file name ('$Planted')"
}

# B-001: the report's largest-files scalars for a finished scan of a planted tree, files at three depths and
# one empty; then an ending with no scan behind it (a missing root), where they are 0, null and null and the
# section is hidden.
function Test-B001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        New-File (Join-Path $root "$Planted-a.bin") (5 * $KiB64)
        New-File (Join-Path $root "sub\$Planted-b.bin") (3 * $KiB64)
        New-File (Join-Path $root "sub\deeper\$Planted-c.bin") $KiB64
        New-File (Join-Path $root "$Planted-empty.txt") 0
        $manifest = [pscustomobject]@{ Files = 4; Folders = 2; Links = 0; Bytes = 9 * $KiB64 }

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -eq $read.Report) { return }
        CheckLargestFiles $read 3 (5 * $KiB64) 'Top 3 of 4 files scanned.'
        CheckEqual 3 $read.Report.window.LargestFilesList.realizedRows 'LargestFilesList built all three rows'
        CheckEqual $false $read.Report.window.UnreadableExpander.visible 'UnreadableExpander is hidden'

        $missing = Join-Path $case 'missing'
        $read = Read-Window $missing
        CheckEqual 'EXIT 0' $read.Exit 'missing root: exit code'
        Check ($null -ne $read.Report) 'missing root: a report was written'
        if ($null -eq $read.Report) { return }
        CheckEqual "Could not read $missing" $read.Report.window.OutcomeTitle 'missing root: OutcomeTitle'
        CheckEqual 0 $read.Report.largestFilesShown 'missing root: largestFilesShown'
        CheckEqual $null $read.Report.largestFileOnDisk 'missing root: largestFileOnDisk'
        CheckEqual $null $read.Report.largestFilesMs 'missing root: largestFilesMs'
        CheckEqual $false $read.Report.window.LargestFilesPanel.visible 'missing root: LargestFilesPanel is hidden'
        CheckNoKinds $read.Report '' 'missing root (SL-046)'
    }
    finally { Remove-CaseFolder $case }
}

# B-002: the biggest file sits in a folder the scan cannot read. It is not listed, and the caption says the
# list leaves that folder out.
function Test-B002 {
    $case = New-CaseFolder
    $denied = $null
    try {
        $root = Join-Path $case 'tree'
        $denied = Join-Path $root 'denied'
        New-File (Join-Path $denied "$Planted-biggest.bin") (32 * $KiB64)
        New-File (Join-Path $root "$Planted-seen.bin") (2 * $KiB64)
        New-File (Join-Path $root "other\$Planted-seen-too.bin") $KiB64
        Deny-Listing $denied

        $read = Read-Window $root
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        CheckEqual "Finished: $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
        CheckLargestFiles $read 2 (2 * $KiB64) 'Top 2 of 2 files scanned. Files in 1 unreadable folder are not included.'
        CheckEqual $true $read.Report.window.UnreadableExpander.visible 'UnreadableExpander is on screen'
        CheckEqual $true $read.Report.window.UnreadableExpander.inView 'UnreadableExpander is inside the panel'
    }
    finally {
        Undo-DenyListing $denied
        Remove-CaseFolder $case
    }
}

# B-003: the list full at 100 rows with the unreadable-folders list also filled (12 folders), the first row a
# name with an unpaired UTF-16 surrogate. A throw while WPF lays out or draws that row ends the app with 70
# and no report. The list must stay virtualized (far fewer rows built than bound), and neither list may be
# pushed out of the panel at the window's default size: first with the unreadable folders closed, as the
# ending shows them, then opened as a person would open them (the hook's expand mode, O-024), when the two
# lists must share the panel and each keep at least one full row in view.
function Test-B003 {
    $case = New-CaseFolder
    $denied = @()
    $lone = $null
    try {
        $root = Join-Path $case 'tree'
        for ($d = 1; $d -le 4; $d++) {
            for ($f = 1; $f -le 26; $f++) {
                New-File (Join-Path $root "folder$d\$Planted-$f.bin") $KiB64
            }
        }
        $lone = "\\?\$root\a$([char]0xD800)b-$Planted.bin"
        [System.IO.File]::WriteAllBytes($lone, [byte[]]::new(16 * $KiB64))
        for ($k = 1; $k -le 12; $k++) {
            $folder = Join-Path $root "denied$k"
            New-File (Join-Path $folder "$Planted-hidden.bin") (32 * $KiB64)
            Deny-Listing $folder
            $denied += $folder
        }
        $caption = 'Top 100 of 105 files scanned. Files in 12 unreadable folders are not included.'

        Write-Output '  the unreadable folders closed, as the ending shows them'
        $read = Read-Window $root
        CheckEqual 'EXIT 0' $read.Exit 'exit code (70 would be a throw drawing the unpaired surrogate)'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -ne $read.Report) {
            CheckEqual "Finished: $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
            CheckLargestFiles $read 100 (16 * $KiB64) $caption
            $built = $read.Report.window.LargestFilesList.realizedRows
            Check ($built -gt 0 -and $built -lt 100) "LargestFilesList virtualizes: $built of 100 rows built"
            CheckEqual $false $read.Report.window.UnreadableExpander.expanded 'UnreadableExpander is closed'
            CheckEqual $true $read.Report.window.UnreadableExpander.visible 'UnreadableExpander is on screen'
            CheckEqual $true $read.Report.window.UnreadableExpander.inView 'UnreadableExpander is inside the panel, not pushed out by the list'
            CheckEqual $true $read.Report.window.FolderTree.inView "FolderTree, in TopLevelList's place (SL-044), is inside the window below the outcome ($($read.Report.window.FolderTree.actualHeight) px)"
        }

        Write-Output '  the unreadable folders opened'
        $run = Invoke-WithHook 'expand' $HookDll { Read-Window $root }
        $before = $script:Failures.Count
        CheckActed $run 'expand'
        if ($script:Failures.Count -gt $before) { return }
        $opened = @($run.Log | Where-Object { $_.StartsWith('expanded=') })
        Check ($opened.Count -eq 1 -and $opened[0].StartsWith('expanded=True ')) "the hook opened the list of unreadable folders ($(Show ($opened -join ' | ')))"
        $read = $run.Result
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual "Finished: $root" $window.OutcomeTitle 'OutcomeTitle'
        CheckLargestFiles $read 100 (16 * $KiB64) $caption
        CheckEqual $true $window.UnreadableExpander.expanded 'the report: UnreadableExpander is open'
        CheckEqual $true $window.UnreadableExpander.inView 'the report: UnreadableExpander is inside the panel'
        CheckEqual $true $window.UnreadableList.inView 'the report: UnreadableList is inside the panel'
        CheckEqual $true $window.FolderTree.inView "the report: FolderTree is inside the window below the outcome ($($window.FolderTree.actualHeight) px)"
        $built = $window.LargestFilesList.realizedRows
        Check ($built -gt 0 -and $built -lt 100) "LargestFilesList still virtualizes: $built of 100 rows built"
        Check ($window.LargestFilesList.fullRows -ge 1) "LargestFilesList keeps at least one full row in view ($($window.LargestFilesList.fullRows) in $($window.LargestFilesList.actualHeight) px)"
        Check ($window.UnreadableList.fullRows -ge 1) "UnreadableList keeps at least one full row in view ($($window.UnreadableList.fullRows) in $($window.UnreadableList.actualHeight) px)"
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        if ($lone -and [System.IO.File]::Exists($lone)) { [System.IO.File]::Delete($lone) }
        Remove-CaseFolder $case
    }
}

# B-004: a tree with nothing to rank (empty files and an empty folder). The section says so, with no rows,
# and the report reads 0 rows and no first row.
function Test-B004 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        for ($f = 1; $f -le 3; $f++) {
            New-File (Join-Path $root "$Planted-$f.txt") 0
        }
        [void][System.IO.Directory]::CreateDirectory((Join-Path $root 'nothing'))
        $manifest = [pscustomobject]@{ Files = 3; Folders = 1; Links = 0; Bytes = 0 }

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -eq $read.Report) { return }
        CheckLargestFiles $read 0 $null 'No non-empty files found.'
    }
    finally { Remove-CaseFolder $case }
}

# B-005: Stop pressed mid-scan (the hook, as C-006). The list comes from what was read, and its caption says
# so. Every file New-LargeTree writes is 64 KiB or empty, so a first row, if any, is 64 KiB. The caption's
# file count must be the one OutcomeDetail shows for the same stopped scan.
function Test-B005 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)
        $run = Invoke-HookedRun 'stop' $root (Join-Path $case 'report.json')
        $before = $script:Failures.Count
        CheckActed $run 'stop'
        if ($script:Failures.Count -gt $before) { return }
        CheckEqual '0' $run.Exit 'exit code'
        Check ($null -ne $run.Report) 'a report was written'
        if ($null -eq $run.Report) { return }
        CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
        $title = $run.Report.window.OutcomeTitle
        Check ("$title".StartsWith("Stopped: $root.")) "OutcomeTitle is the Stopped ending ($(Show $title))"

        $shown = [int]$run.Report.largestFilesShown
        $files = ("$($run.Report.window.OutcomeDetail[0])" -split ' and ')[0]
        Write-Output "        stopped with $shown rows listed, OutcomeDetail says $files"
        $caption = if ($shown -eq 0) { 'No non-empty files found in the part read before the scan stopped.' }
            else { "Top $shown of $files scanned in the part read before the scan stopped." }
        $read = [pscustomobject]@{ Report = $run.Report; Text = ($run.Report | ConvertTo-Json -Depth 8) }
        CheckLargestFiles $read $shown $(if ($shown -gt 0) { $KiB64 } else { $null }) $caption
    }
    finally { Remove-CaseFolder $case }
}

# B-006 (C-009): a second scan after the first has shown its list. The hook's rescan mode waits until the first
# ending shows the list with its rows laid out, then starts a second scan of the same root through the
# window's own Loaded handler, and the report, read through eng/read-window.ps1, is of the window at that
# second point, not at the first ending. Two shapes: the root moved aside first, so the second scan fails
# before any entry and the report reads its failure ending; and a large tree, whose second scan has begun and
# not finished when the report reads it. In both the old list must be gone: LargestFilesPanel hidden and no
# rows bound, from the moment the scan starts.
function Test-B006 {
    $shapes = @(
        [pscustomobject]@{ Name = 'the second scan fails before any entry (its root is gone)'; Missing = $true; Rows = 20 }
        [pscustomobject]@{ Name = 'the second scan has begun and not finished'; Missing = $false; Rows = 100 }
    )
    foreach ($shape in $shapes) {
        Write-Output "  $($shape.Name)"
        $case = New-CaseFolder
        try {
            if ($shape.Missing) {
                $root = Join-Path $case 'small'
                [void](New-SmallTree $root)
                $armed = @{ FSA_HOOK_MISSING = $root }
            }
            else {
                $root = Join-Path $case 'large'
                [void](New-LargeTree $root)
                $armed = @{}
            }
            $run = Invoke-WithHook 'rescan' $HookDll { Read-Window $root } $armed
            $before = $script:Failures.Count
            CheckActed $run 'rescan'
            if ($script:Failures.Count -gt $before) { continue }

            $listed = @($run.Log | Where-Object { $_.StartsWith('listed ') })[0]
            CheckEqual "$($shape.Rows)" (Field $listed 'rows') 'the first scan''s list was bound before the second scan (hook: listed rows)'
            Check ([int](Field $listed 'built') -gt 0) "and its rows were laid out, so the list had been shown ($(Show $listed))"
            CheckEqual 'True' (Field $listed 'largest') 'and LargestFilesPanel was set Visible'
            $started = @($run.Log | Where-Object { $_.StartsWith('started ') })
            Check ($started.Count -eq 1) "the hook saw the second scan start ($($started.Count) 'started' lines)"
            if ($started.Count -eq 1) {
                CheckEqual 'True' (Field $started[0] 'scanning') 'the second scan started (ScanningPanel set Visible)'
                CheckEqual 'False' (Field $started[0] 'largest') 'as it started, LargestFilesPanel was set Collapsed'
                CheckEqual '0' (Field $started[0] 'rows') 'as it started, LargestFilesList held no rows'
            }

            $read = $run.Result
            CheckEqual 'EXIT 0' $read.Exit 'exit code'
            Check ($null -ne $read.Report) 'a report was written'
            if ($null -eq $read.Report) { continue }
            $report = $read.Report
            CheckEqual 1 $report.schema 'schema'
            CheckEqual $root $report.root 'root is the path passed'
            CheckEqual $false $report.window.LargestFilesPanel.visible 'the report: LargestFilesPanel is not on screen'
            CheckEqual 0 $report.largestFilesShown 'the report: largestFilesShown (no rows bound)'
            CheckEqual $null $report.largestFileOnDisk 'the report: largestFileOnDisk'
            CheckEqual $null $report.largestFilesMs 'the report: largestFilesMs'
            CheckEqual $false $report.window.LargestFilesList.inView 'the report: LargestFilesList is not on screen'
            if ($shape.Missing) {
                Check (@($run.Log | Where-Object { $_.StartsWith('moved ') }).Count -eq 1) 'the hook moved the root aside before the second scan'
                $ended = @($run.Log | Where-Object { $_.StartsWith('ended ') })
                Check ($ended.Count -eq 1) "the hook held the report until the second scan's ending ($($ended.Count) 'ended' lines)"
                if ($ended.Count -eq 1) {
                    CheckEqual 'False' (Field $ended[0] 'largest') 'at the failure ending, LargestFilesPanel was set Collapsed'
                    CheckEqual '0' (Field $ended[0] 'rows') 'at the failure ending, LargestFilesList held no rows'
                }
                # Read at the second point: the first scan completed; this is the second one's failure.
                CheckEqual 'RootUnreadable' $report.core.status 'the report is of the second scan (core.status)'
                CheckEqual "Could not read $root" $report.window.OutcomeTitle 'the report: OutcomeTitle'
                CheckEqual $true $report.window.OutcomePanel.visible 'the report: the failure ending is on screen'
            }
            else {
                # Read at the second point: the scan start cleared the result, and nothing has ended since.
                Check ($read.Text -match '"core":\s*null') 'the report is of the running second scan (core is null)'
                CheckEqual $false $report.window.OutcomePanel.visible 'the report: no ending on screen'
            }
        }
        finally { Remove-CaseFolder $case }
    }
}

# After an ending, the outcome's lists and the results list below it (SL-039), or the folder tree in its place
# after a completed scan (SL-044), are on screen together, none pushed off the window by another or by any text
# (O-025), and each with at least one full row in view: a row wholly inside the list's viewport, readable
# without scrolling (the window-size rework). The outcome's texts are in view too, scrolling whatever does not fit.
function CheckBothLists($window, [bool] $opened, [bool] $completed) {
    $largest = $window.LargestFilesList
    CheckEqual $true $largest.inView "LargestFilesList is inside the window ($($largest.actualHeight) px)"
    Check ($largest.fullRows -ge 1) "LargestFilesList shows at least one full row ($($largest.fullRows) of $($largest.realizedRows) built)"
    CheckEqual $true $window.TopLevelPanel.visible 'TopLevelPanel is on screen'
    $results = if ($completed) { $window.FolderTree } else { $window.TopLevelList }
    $name = if ($completed) { 'FolderTree' } else { 'TopLevelList' }
    CheckEqual $true $results.inView "$name is inside the window ($($results.actualHeight) px)"
    Check ($results.fullRows -ge 1) "$name shows at least one full row ($($results.fullRows) of $($results.realizedRows) built)"
    CheckEqual (-not $completed) $window.TopLevelList.inView "TopLevelList is $(if ($completed) { 'not shown: the tree took its place' } else { 'shown' })"
    CheckEqual $opened $window.UnreadableExpander.expanded "UnreadableExpander is $(if ($opened) { 'open' } else { 'closed' })"
    CheckEqual $true $window.UnreadableExpander.inView 'UnreadableExpander is inside the window'
    if ($opened) {
        $folders = $window.UnreadableList
        CheckEqual $true $folders.inView "UnreadableList is inside the window ($($folders.actualHeight) px)"
        Check ($folders.fullRows -ge 1) "UnreadableList shows at least one full row ($($folders.fullRows))"
    }
    $texts = $window.OutcomeTexts
    CheckEqual $true $texts.inView "the outcome's texts are inside the window ($($texts.actualHeight) of $($texts.extentHeight) px shown, the rest a scroll away)"
}

# The hook set the window to its own MinWidth by MinHeight when it loaded, before the scan started.
function CheckSized($log) {
    $sized = @($log | Where-Object { $_.StartsWith('sized ') })
    $line = if ($sized.Count -eq 1) { $sized[0] } else { '' }
    Check ($sized.Count -eq 1 -and (Field $line 'width') -eq (Field $line 'minWidth') -and (Field $line 'height') -eq (Field $line 'minHeight')) "the hook set the window to its minimum size ($(Show ($sized -join ' | ')))"
}

# The hook held the report until the app's own drive buttons were in, then added $extra more like them
# (FSA_HOOK_DRIVES, O-028).
function CheckDrives($log, [int] $extra) {
    $drives = @($log | Where-Object { $_.StartsWith('drives in ') })
    $line = if ($drives.Count -eq 1) { $drives[0] } else { '' }
    Check ($drives.Count -eq 1 -and (Field $line 'added') -eq "$extra" -and [int](Field $line 'buttons') -ge $extra) "the report waited for the drive buttons and $extra more were added ($(Show ($drives -join ' | ')))"
}

# The hook put the reveal status line on screen as the drive buttons came in (FSA_HOOK_STATUS, SL-047 B3-4), ahead of
# the report: the line was visible, and one line tall, inside the 24 px with its 6 px margin that the window's minimum
# height budgets for it (MainWindow.xaml).
function CheckStatusShown($log) {
    $drives = @($log | Where-Object { $_.StartsWith('drives in ') })
    $line = if ($drives.Count -eq 1) { $drives[0] } else { '' }
    $height = 0.0
    $parsed = [double]::TryParse("$(Field $line 'statusHeight')", [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$height)
    Check ((Field $line 'status') -ceq 'True' -and $parsed -and $height -gt 0 -and $height + 6 -le 24) "the reveal status line was on screen, one line of $height px ($(Show ($drives -join ' | ')))"
}

# More drive buttons than Windows has drive letters, fed in beside the ready ones (O-028): the pickers must
# take the same height with them as without.
$ExtraDrives = 26

# B-007's and B-008's tree under $root, in listing order: 4 top-level folders of 26 non-empty files (104), 12
# folders the scan cannot read, and 20 folders of 1,500 empty folders each, which take the scan past a second.
# Returns the denied folders, for the caller to give back.
function New-TopsTree([string] $root) {
    for ($d = 1; $d -le 4; $d++) {
        for ($f = 1; $f -le 26; $f++) {
            New-File (Join-Path $root "a$d-$Planted\$Planted-$f.bin") $KiB64
        }
    }
    $denied = @()
    for ($k = 1; $k -le 12; $k++) {
        $folder = Join-Path $root ('b{0:D2}-{1}' -f $k, $Planted)
        [void][System.IO.Directory]::CreateDirectory($folder)
        Deny-Listing $folder
        $denied += $folder
    }
    for ($s = 1; $s -le 20; $s++) {
        $slow = Join-Path $root ('c{0:D2}-{1}' -f $s, $Planted)
        for ($j = 0; $j -lt 1500; $j++) {
            [void][System.IO.Directory]::CreateDirectory((Join-Path $slow "m$j"))
        }
    }
    , $denied
}

# The window's size when nothing has sized it: MainWindow.xaml's Width and Height, the height no more than the
# work area's (SL-044, review M2).
$DefaultWidth = 880
$DefaultHeight = 860

# New-TopsTree's completed scan as the folder tree draws the root's rows (the hook's tree mode, FSA_HOOK_WALK=1), at
# the window's default size (code review m1). Each of the 12 folders the scan could not open is a row with no
# expander, nothing on disk, a share of 0.0 % and the note 'could not be opened'; every other folder can be opened
# and has no note. The window is its default height, inside the work area, and the tree has a screenful of rows
# in view, not the two it had beside the outcome before (code review M2).
function CheckTopsTree($run, [string] $root) {
    $before = $script:Failures.Count
    CheckTreeRun $run $root
    if ($script:Failures.Count -gt $before) { return }
    $walked = @(Lines-Of $run 'walked')
    Check ($walked.Count -eq 1 -and $walked[0].rows -eq 37 -and $walked[0].unrendered -eq 0) "the hook read the root and its 36 rows, all drawn ($(Show ($walked | ConvertTo-Json -Compress)))"
    $rows = @(Lines-Of $run 'tree-row' | Where-Object depth -eq 1)
    $unreadable = @($rows | Where-Object { $_.name -match "^b\d\d-$Planted$" })
    CheckEqual 12 $unreadable.Count 'the root holds the 12 folders the scan could not open'
    foreach ($row in $unreadable) {
        $r = $row.rendered
        $bad = [System.Collections.Generic.List[string]]::new()
        if ($null -eq $r) { $bad.Add('no container drew it') }
        else {
            if ($r.name -cne $row.name) { $bad.Add("name drawn $(Show $r.name)") }
            if ($r.expander -ne $false) { $bad.Add('an expander is drawn') }
            if ($r.note -cne 'could not be opened') { $bad.Add("note $(Show $r.note)") }
            if ($r.onDisk -cne (Format-Size 0)) { $bad.Add("on disk drawn $(Show $r.onDisk)") }
            if ($r.share -cne (Format-Share 0)) { $bad.Add("share drawn $(Show $r.share)") }
        }
        if ([int]$row.childCount -ne 0 -or [long]$row.onDisk -ne 0) { $bad.Add("childCount $($row.childCount), onDisk $($row.onDisk)") }
        Check ($bad.Count -eq 0) "$($row.name) draws as a folder that could not be opened ($(Show ($bad -join '; ')))"
    }
    $others = @($rows | Where-Object { $_.name -notmatch "^b\d\d-$Planted$" })
    $wrong = @($others | Where-Object { $null -eq $_.rendered -or $_.rendered.note -cne '' -or $_.rendered.expander -ne $true })
    Check ($others.Count -eq 24 -and $wrong.Count -eq 0) "the other 24 folders can be opened and have no note ($(Show (($wrong | ForEach-Object { "$($_.name) note=$($_.rendered.note) expander=$($_.rendered.expander)" }) -join ', ')))"
    $rootRow = @(Lines-Of $run 'tree-row' | Where-Object depth -eq 0)
    Check ($rootRow.Count -eq 1 -and $null -ne $rootRow[0].rendered -and $rootRow[0].rendered.note -ceq '') 'the root row has no note'

    $ending = @(Lines-Of $run 'ending')[0]
    $height = [Math]::Min($DefaultHeight, [double]$ending.workAreaHeight)
    Write-Output ("        window {0:N0} px tall at {1:N0}, work area {2:N0} px from {3:N0}; the tree's viewport {4:N0} px" -f $ending.windowHeight, $ending.windowTop, $ending.workAreaHeight, $ending.workAreaTop, $ending.viewportHeight)
    Check ([Math]::Abs([double]$ending.windowHeight - $height) -le 1) "the window is $height px tall by default, $DefaultHeight or the work area's height ($($ending.windowHeight) px)"
    Check ([double]$ending.windowTop -ge [double]$ending.workAreaTop - 1 -and [double]$ending.windowTop + [double]$ending.windowHeight -le [double]$ending.workAreaTop + [double]$ending.workAreaHeight + 1) "the window lies inside the work area (top $($ending.windowTop), $($ending.windowHeight) px)"
    if ($height -ge $DefaultHeight) {
        Check ([double]$ending.viewportHeight -ge 12 * 20) "the tree shows at least 12 rows at the default size ($($ending.viewportHeight) px of 20 px rows)"
    }
    else {
        Write-Output "        the work area is under $DefaultHeight px: the tree's rows at the default size are not asserted"
    }
}

# One Cancelled ending of New-TopsTree's tree, pressed by the hook's watch mode once the files are read and the
# slow folders are not, with the unreadable folders closed or opened (FSA_HOOK_EXPAND), at the window's minimum
# size (FSA_HOOK_MINSIZE) or not, and with $extra drive buttons added (FSA_HOOK_DRIVES) or none. Its checks
# print as they go; the report it read is left in $script:StoppedReport, null when a check before it failed.
function Invoke-StoppedEnding([string] $case, [string] $root, [bool] $opened, [bool] $smallest, [int] $extra, [string] $statusMsg = $null) {
    $script:StoppedReport = $null
    $variables = @{}
    if ($smallest) { $variables.FSA_HOOK_MINSIZE = '1' }
    if ($extra -gt 0) { $variables.FSA_HOOK_DRIVES = "$extra" }
    if ($opened) { $variables.FSA_HOOK_EXPAND = '1' }
    if ($statusMsg) { $variables.FSA_HOOK_STATUS = $statusMsg }
    $run = Invoke-HookedRun 'watch' $root (Join-Path $case 'report.json') $variables
    $before = $script:Failures.Count
    CheckActed $run 'watch'
    if ($opened) { CheckActed $run 'expand' }
    if ($smallest) { CheckSized $run.Log }
    if ($smallest -or $extra -gt 0) { CheckDrives $run.Log $extra }
    if ($statusMsg) { CheckStatusShown $run.Log }
    if ($script:Failures.Count -gt $before) { return }
    $watch = Read-WatchLine $run.Log
    Check ($null -ne $watch) "the hook logged one 'watch rows=' line"
    CheckEqual '0' $run.Exit 'exit code'
    Check ($null -ne $run.Report) 'a report was written'
    if ($null -eq $watch -or $null -eq $run.Report) { return }
    CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
    $files = ("$($run.Report.window.OutcomeDetail[0])" -split ' and ')[0]
    $read = [pscustomobject]@{ Report = $run.Report; Text = ($run.Report | ConvertTo-Json -Depth 8) }
    CheckLargestFiles $read 100 $KiB64 "Top 100 of $files scanned in the part read before the scan stopped. Files in 12 unreadable folders are not included."
    CheckTopLevel $run.Report.topLevel @{ rows = 36; unreadable = 12; final = $true } 'against the tree'
    Check ([int]$run.Report.topLevel.notRead -ge 1) "some slow folders were not read before the stop ($($run.Report.topLevel.notRead))"
    CheckBothLists $run.Report.window $opened $false
    $after = $run.Report.window.TopLevelList.actualHeight
    Check ([double]$watch.listHeight -gt $after) "mid-scan, with no outcome on screen, TopLevelList had more height ($($watch.listHeight) px) than beside the outcome after the stop ($after px)"
    $script:StoppedReport = $run.Report
}

# O-028: the pickers took the same height in every run, whatever the window's size and however many drive
# buttons they held.
function CheckPickers($pickers) {
    $heights = @($pickers | ForEach-Object { $_.Height } | Sort-Object -Unique)
    Write-Output "        pickers: $(($pickers | ForEach-Object { "$($_.Run) $($_.Height) px with $($_.Buttons) drive buttons" }) -join '; ')"
    Check ($pickers.Count -ge 2 -and $heights.Count -eq 1) "PickerPanel took the same height in all $($pickers.Count) runs ($($heights -join ', ') px)"
    $counts = @($pickers | ForEach-Object { $_.Buttons } | Sort-Object -Unique)
    Check ($counts.Count -ge 2) "and the runs held different numbers of drive buttons ($($counts -join ', '))"
}

# B-007 (O-025 to O-028): the largest-files list full and the results list both on screen after an ending, at
# the window's default size and again at the smallest size the window allows (its MinWidth by MinHeight, set
# by the hook), each list with a full row in view. New-TopsTree's tree, four endings at each size: Completed,
# with the unreadable folders closed and then opened (the hook's expand mode); and Cancelled, closed and then
# opened. Every hooked run adds 26 drive buttons beside the ready ones (FSA_HOOK_DRIVES); the default-size
# Completed run with the list closed has only the ready ones, and the pickers must take the same height in all
# eight. Mid-scan, with no outcome on screen, the results list has more height than it keeps beside the outcome
# after the ending (watch's listHeight). First, the Completed ending read through the hook's tree mode: the 12
# unreadable folders as the folder tree draws them, and the tree's room at the default size (CheckTopsTree).
function Test-B007 {
    $case = New-CaseFolder
    $denied = @()
    $pickers = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-TopsTree $root
        $unread = ' Files in 12 unreadable folders are not included.'

        Write-Output '  Completed, the root''s rows as the folder tree draws them, at the default size'
        CheckTopsTree (Invoke-TreeRun $root @{ FSA_HOOK_WALK = '1' }) $root

        foreach ($smallest in $false, $true) {
            $at = if ($smallest) { "at the window's minimum size" } else { "at the default size ($DefaultWidth by $DefaultHeight, or the work area)" }
            # FSA_HOOK_MINSIZE sizes the window to its minimum ahead of watch and expand; size mode does only that.
            $sizing = if ($smallest) { @{ FSA_HOOK_MINSIZE = '1' } } else { @{} }

            foreach ($opened in $false, $true) {
                $label = "Completed, the unreadable folders $(if ($opened) { 'opened' } else { 'closed' }), $at"
                Write-Output "  $label"
                if ($opened -or $smallest) {
                    $mode = if ($opened) { 'expand' } else { 'size' }
                    $variables = @{ FSA_HOOK_DRIVES = "$ExtraDrives" }
                    if ($opened) { $variables += $sizing }
                    $run = Invoke-WithHook $mode $HookDll { Read-Window $root } $variables
                    $before = $script:Failures.Count
                    CheckActed $run $mode
                    if ($smallest) { CheckSized $run.Log }
                    CheckDrives $run.Log $ExtraDrives
                    if ($script:Failures.Count -gt $before) { continue }
                    $read = $run.Result
                }
                else {
                    $read = Read-Window $root
                }
                CheckEqual 'EXIT 0' $read.Exit 'exit code'
                Check ($null -ne $read.Report) 'a report was written'
                if ($null -eq $read.Report) { continue }
                CheckEqual "Finished: $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
                CheckLargestFiles $read 100 $KiB64 "Top 100 of 104 files scanned.$unread"
                Check ($read.Text -notmatch '"topLevel"') 'no topLevel in the report: the folder tree shows the completed scan (SL-044)'
                CheckBothLists $read.Report.window $opened $true
                $pickers += [pscustomobject]@{ Run = $label; Height = $read.Report.window.PickerPanel.actualHeight; Buttons = $read.Report.window.DrivePanel.buttons }
            }

            foreach ($opened in $false, $true) {
                $label = "Cancelled, the unreadable folders $(if ($opened) { 'opened' } else { 'closed' }), $at"
                Write-Output "  $label"
                Invoke-StoppedEnding $case $root $opened $smallest $ExtraDrives
                if ($null -ne $script:StoppedReport) {
                    $window = $script:StoppedReport.window
                    $pickers += [pscustomobject]@{ Run = $label; Height = $window.PickerPanel.actualHeight; Buttons = $window.DrivePanel.buttons }
                }
            }
        }
        $statusMsg = "'pagefile.sys' is hidden, so Explorer only highlights it if it is set to show hidden files (and protected operating system files, for system items)."
        # The reveal status line on screen at the minimum size (SL-047, B3-4), after the Completed ending with the
        # folder tree, where its height is taken from the tree's row, and after the Cancelled one.
        Write-Output "  Completed, the unreadable folders opened, at minimum size with reveal status visible"
        $variables = @{ FSA_HOOK_DRIVES = "$ExtraDrives"; FSA_HOOK_MINSIZE = '1'; FSA_HOOK_STATUS = $statusMsg }
        $run = Invoke-WithHook 'expand' $HookDll { Read-Window $root } $variables
        $before = $script:Failures.Count
        CheckActed $run 'expand'
        CheckSized $run.Log
        CheckDrives $run.Log $ExtraDrives
        CheckStatusShown $run.Log
        if ($script:Failures.Count -eq $before) {
            $read = $run.Result
            CheckEqual 'EXIT 0' $read.Exit 'exit code'
            Check ($null -ne $read.Report) 'a report was written'
            if ($null -ne $read.Report) {
                CheckEqual "Finished: $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
                CheckBothLists $read.Report.window $true $true
            }
        }
        Write-Output "  Cancelled, the unreadable folders opened, at minimum size with reveal status visible"
        Invoke-StoppedEnding $case $root $true $true $ExtraDrives $statusMsg
        CheckPickers $pickers
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# B-008 (the window-size rework, O-028): a stopped scan of a long path at the smallest size the window allows,
# with the unreadable folders closed and then opened, and 26 drive buttons added beside the ready ones.
# New-TopsTree's tree under a 140-character folder name, so the stopped title wraps over several lines and the
# outcome's texts run longer than a system drive's: title and detail over 400 characters, where a stopped C:\
# scan's came to 190 on this machine and a finished one's to about 320 with the volume line. Every list must
# keep a full row in view whatever the texts' length and however many drives are ready, and the texts must be
# in view, the rest a scroll away.
function Test-B008 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case ('long-path-' * 14)
        $denied = New-TopsTree $root
        foreach ($opened in $false, $true) {
            Write-Output "  Cancelled, the unreadable folders $(if ($opened) { 'opened' } else { 'closed' }), at the window's minimum size, a root of $($root.Length) characters, $ExtraDrives drive buttons added"
            Invoke-StoppedEnding $case $root $opened $true $ExtraDrives
            $report = $script:StoppedReport
            if ($null -eq $report) { continue }
            $title = "$($report.window.OutcomeTitle)"
            Check ($title.StartsWith("Stopped: $root.")) 'OutcomeTitle is the Stopped ending, with the whole root'
            $length = $title.Length + (@($report.window.OutcomeDetail) -join "`n").Length
            Check ($length -gt 400) "the outcome's title and detail run longer than a system drive's ($length characters)"
            $texts = $report.window.OutcomeTexts
            Check ($texts.extentHeight -gt $texts.actualHeight) "at this size the texts are taller than their row and scroll ($($texts.extentHeight) px in $($texts.actualHeight) px)"
        }
        $statusMsg = "'pagefile.sys' is hidden, so Explorer only highlights it if it is set to show hidden files (and protected operating system files, for system items)."
        Write-Output "  Cancelled, the unreadable folders opened, at minimum size with reveal status visible"
        Invoke-StoppedEnding $case $root $true $true $ExtraDrives $statusMsg
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# ---- The volume line and long paths (SL-043) ----

# The first letter from Z down that names no drive, for a SUBST that lasts one case.
function Get-FreeDriveLetter {
    $used = @([System.Environment]::GetLogicalDrives() | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() })
    foreach ($letter in 'ZYXWVUTSRQPONMLKJIHGFED'.ToCharArray()) {
        if ($used -notcontains [string]$letter) { return [string]$letter }
    }
    throw 'every drive letter from D to Z is in use, so no SUBST letter is free'
}

# V-001: the ending of a whole-drive scan carries the volume line, and a folder's does not. The window no
# longer asks the drive itself: the line comes from the figure the scan recorded, which the scan reads for a
# root that is exactly X:\. A SUBST letter is such a root, over a tree small enough to finish at once, and
# GetDiskFreeSpaceExW answers it with the volume under it (so the line reports that volume, m3). The line
# sits between the bytes line and the elapsed time.
function Test-V001 {
    $case = New-CaseFolder
    $letter = $null
    try {
        $root = Join-Path $case 'small'
        $manifest = New-SmallTree $root
        $letter = Get-FreeDriveLetter
        $output = & subst.exe "${letter}:" $root 2>&1
        if ($LASTEXITCODE -ne 0) { throw "subst ${letter}: $root failed with exit ${LASTEXITCODE}: $output" }
        $drive = "${letter}:\"

        Write-Output "  the tree as the whole drive $drive"
        $read = Read-Window $drive
        CheckFinished $read $drive $drive $manifest
        if ($null -ne $read.Report) {
            $lines = @($read.Report.window.OutcomeDetail)
            CheckEqual 4 $lines.Count "OutcomeDetail has four lines: counts, bytes, the volume line, the time ($(Show ($lines -join ' | ')))"
            Check ($lines.Count -eq 4 -and $lines[2].StartsWith('The volume reports ') -and $lines[2].Contains(' in use, so ')) "line 3 is the finished scan's volume line ($(Show $lines[2]))"
            Check ($lines.Count -eq 4 -and $lines[3].StartsWith('Read in ')) "line 4 is the time ($(Show $lines[3]))"
        }

        Write-Output "  the same tree by its folder path"
        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -ne $read.Report) {
            $lines = @($read.Report.window.OutcomeDetail)
            Check (@($lines | Where-Object { $_.StartsWith('The volume reports') }).Count -eq 0) "a folder's ending has no volume line ($(Show ($lines -join ' | ')))"
            CheckEqual 3 $lines.Count 'OutcomeDetail has three lines: counts, bytes, the time'
        }
    }
    finally {
        if ($letter) { & subst.exe "${letter}:" /D 2>&1 | Out-Null }
        Remove-CaseFolder $case
    }
}

# P-001: a tree whose paths run past MAX_PATH, read by the published exe. The testhost that runs the Core tests
# is longPathAware and the exe is not (risk spike: a plain long path opens in one and fails with error 3 in the
# other), so only the exe can show that nothing the App or Core opens drops the \\?\ prefix. Nine nested
# folders of 41 characters, two files and a side folder with one file at each level: folders past 260
# characters and files past 400, every file 64 KiB so the size on disk is the size.
function Test-P001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'deep'
        $extended = '\\?\' + $root
        [void][System.IO.Directory]::CreateDirectory($extended)
        $path = ''
        $files = 0
        $folders = 0
        $longestFolder = 0
        $longestFile = 0
        for ($level = 1; $level -le 9; $level++) {
            $path += "\level$level-" + ('d' * 33)
            [void][System.IO.Directory]::CreateDirectory($extended + $path)
            $folders++
            $longestFolder = [Math]::Max($longestFolder, ($root + $path).Length)
            for ($k = 1; $k -le 2; $k++) {
                $name = "$path\file$k-" + ('f' * 30) + '.bin'
                [System.IO.File]::WriteAllBytes($extended + $name, [byte[]]::new($KiB64))
                $files++
                $longestFile = [Math]::Max($longestFile, ($root + $name).Length)
            }
            [void][System.IO.Directory]::CreateDirectory("$extended$path\side-$level")
            $folders++
            [System.IO.File]::WriteAllBytes("$extended$path\side-$level\note.bin", [byte[]]::new($KiB64))
            $files++
        }
        $manifest = [pscustomobject]@{ Files = $files; Folders = $folders; Links = 0; Bytes = $files * $KiB64 }
        Check ($longestFolder -gt 260) "a folder path runs past 260 characters ($longestFolder)"
        Check ($longestFile -gt 400) "a file path runs past 400 characters ($longestFile)"

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -ne $read.Report) {
            CheckEqual $false $read.Report.window.UnreadableExpander.visible 'no folder was unreadable (UnreadableExpander is not shown)'
            CheckEqual $manifest.Files $read.Report.largestFilesShown 'the largest-files list holds every file of the tree'
        }
    }
    finally {
        if (Test-Path -LiteralPath $case) { [System.IO.Directory]::Delete('\\?\' + $case, $true) }
    }
}

# ---- The folder tree (SL-044) ----

# Files here are whole multiples of 4 KiB, at least 4 KiB, or empty: on this machine's NTFS (4 KiB clusters) the
# space on disk is then the size. Every row's own on-disk figure is checked against that too, so a volume where
# it does not hold fails by name rather than on a share.
$KiB4 = 4KB
$ShareTrack = 60 # FolderTreeRowText.BarTrack, the width of a share bar's track

# The hook's JSON lines, as objects. Other lines ("loaded ...", "refused ...") are left out.
function Read-HookJson($log) {
    foreach ($line in @($log)) {
        if ($line -and $line.StartsWith('{')) { $line | ConvertFrom-Json }
    }
}

# One launch of the app on $root without --report, under the hook's tree mode with $variables: the exit code (or
# TIMEOUT) and the hook's lines, raw and as objects.
function Invoke-TreeRun([string] $root, [hashtable] $variables = @{}, [int] $timeoutSec = 120) {
    $run = Invoke-WithHook 'tree' $HookDll { Wait-App (Start-App "`"$root`"") $timeoutSec } $variables
    [pscustomobject]@{
        Exit  = $run.Result
        Log   = $run.Log
        Lines = @(Read-HookJson $run.Log)
    }
}

function Lines-Of($run, [string] $kind) { @($run.Lines | Where-Object kind -ceq $kind) }

# What every tree run must show before its own checks: the hook ran, found the tree at a Finished ending in the
# results list's place, logged no error, closed the window, and the app exited as a closed window does.
function CheckTreeRun($run, [string] $root) {
    foreach ($line in $run.Log) {
        if ($line -notmatch '^\{"kind":"(tree-row|recycle-row)"') { Write-Output "        hook: $line" }
    }
    $errors = @(Lines-Of $run 'error')
    Check ($errors.Count -eq 0) "the hook reported no error ($(Show (($errors | ForEach-Object reason) -join ' | ')))"
    CheckTreeEnding $run $root 'ending'
    Check (@(Lines-Of $run 'closed').Count -eq 1) 'the hook closed the window'
    CheckEqual '0' $run.Exit 'exit code (a closed window)'
}

# The ending the hook logged as $kind ('ending', or 'second-ending' after a scan it started): Finished, with the
# tree of $root alone shown where the results list was.
function CheckTreeEnding($run, [string] $root, [string] $kind) {
    $ending = @(Lines-Of $run $kind)
    Check ($ending.Count -eq 1) "the hook saw the ending ('$kind')"
    if ($ending.Count -ne 1) { return }
    CheckEqual "Finished: $root" $ending[0].title "$($kind): Finished"
    CheckEqual $true $ending[0].tree "$($kind): FolderTree is shown"
    CheckEqual 1 $ending[0].treeItems "$($kind): the tree holds one row at its top"
    CheckEqual $root $ending[0].root "$($kind): that row is the root, by its path"
    CheckEqual $true $ending[0].headings "$($kind): the tree's headings are shown"
    CheckEqual $false $ending[0].list "$($kind): TopLevelList is not shown, the tree took its place"
}

# A share as the window writes it: one decimal place, a midpoint away from zero (review m2), in this culture.
function Format-Share([double] $share) {
    '{0:F1} %' -f [math]::Round($share * 100, 1, [MidpointRounding]::AwayFromZero)
}

function Parent-Of([string] $path) {
    $i = $path.LastIndexOf('\')
    if ($i -lt 0) { '' } else { $path.Substring(0, $i) }
}

function Leaf-Of([string] $path) { $path.Substring($path.LastIndexOf('\') + 1) }

# A sparse file of $length bytes with $dataBytes of data at $dataOffset (both 64 KiB-aligned), and the space NTFS
# allocated to it as it reports it (fsutil sparse queryrange, which an ordinary user may run): the case's oracle
# for its on-disk size, which bytes written do not give (review m3). The data goes in before the file is
# extended to its length: written the other way round, the file's directory entry kept an allocation of 0 while
# 64 KiB were allocated (measured at SL-044's build, also after a rename and after a reopen), and the scan
# charges a sparse file what its directory entry says. That is a question for the scanner, not the tree.
function New-SparseFile([string] $path, [long] $length, [long] $dataOffset, [long] $dataBytes) {
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllBytes($path, [byte[]]::new(0))
    $out = & fsutil.exe sparse setflag $path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "fsutil sparse setflag $path exited ${LASTEXITCODE}: $out" }
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
    try {
        $stream.Position = $dataOffset
        $data = [byte[]]::new($dataBytes)
        [Array]::Fill($data, [byte]1)
        $stream.Write($data, 0, $data.Length)
        $stream.SetLength($length)
    }
    finally { $stream.Dispose() }
    $ranges = @(& fsutil.exe sparse queryrange $path 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "fsutil sparse queryrange $path exited ${LASTEXITCODE}: $ranges" }
    $allocated = 0
    foreach ($line in $ranges) {
        if ("$line" -match 'Length:\s*0x([0-9A-Fa-f]+)') { $allocated += [Convert]::ToInt64($Matches[1], 16) }
    }
    $allocated
}

# The rows a tree should show, from what the case built: its files and links with their bytes and space on disk,
# and its folders with the sums of every file below them (links and empty folders add 0). Keyed by path from
# the root ('' is the root); each row has its Name, Kind, Bytes, OnDisk, Parent, Children and its Share of its
# parent's space on disk.
function Get-TreeOracle([string] $root, $entries) {
    $rows = [ordered]@{}
    $rows[''] = [pscustomobject]@{ Path = ''; Name = $root; Kind = 'Folder'; Bytes = [long]0; OnDisk = [long]0; Parent = $null; Children = [System.Collections.Generic.List[string]]::new(); Share = $null }
    foreach ($e in $entries) {
        $rows[$e.Path] = [pscustomobject]@{ Path = $e.Path; Name = (Leaf-Of $e.Path); Kind = $e.Kind; Bytes = [long]$e.Bytes; OnDisk = [long]$e.OnDisk
            Parent = (Parent-Of $e.Path); Children = [System.Collections.Generic.List[string]]::new(); Share = $null }
    }
    foreach ($row in @($rows.Values)) {
        if ($null -ne $row.Parent) { $rows[$row.Parent].Children.Add($row.Path) }
    }
    foreach ($row in @($rows.Values)) {
        if ($row.Kind -ne 'File') { continue }
        for ($p = $row.Parent; $null -ne $p; $p = $(if ($p -eq '') { $null } else { Parent-Of $p })) {
            $rows[$p].Bytes += $row.Bytes
            $rows[$p].OnDisk += $row.OnDisk
        }
    }
    foreach ($row in @($rows.Values)) {
        if ($null -eq $row.Parent) { continue }
        $parent = $rows[$row.Parent]
        $row.Share = if ($parent.OnDisk -eq 0) { [double]0 } else { [double]$row.OnDisk / $parent.OnDisk }
    }
    $rows
}

# Every row of the oracle against the row the hook logged at the same path: what its container draws (name,
# note, on disk, size, share, the bar's laid-out width, the expander) and the raw values behind it; then each
# folder's children in the order drawn, largest on disk first.
function CheckTreeRows($oracle, $logged) {
    $byPath = @{}
    foreach ($row in $logged) { $byPath[$row.path] = $row }
    CheckEqual $oracle.Count $logged.Count "the hook read one row per row of the tree ($($oracle.Count))"
    foreach ($want in $oracle.Values) {
        $label = if ($want.Path -eq '') { 'the root row' } else { "row $($want.Path)" }
        $row = $byPath[$want.Path]
        if ($null -eq $row) {
            Check $false "$label was read"
            continue
        }
        if ($null -eq $row.rendered) {
            Check $false "$label has a container that draws it (rendered)"
            continue
        }
        $r = $row.rendered
        $bad = [System.Collections.Generic.List[string]]::new()
        if ($r.name -cne $want.Name) { $bad.Add("name $(Show $r.name)") }
        if ([long]$row.onDisk -ne $want.OnDisk) { $bad.Add("onDisk $($row.onDisk), want $($want.OnDisk)") }
        if ([long]$row.size -ne $want.Bytes) { $bad.Add("size $($row.size), want $($want.Bytes)") }
        if ($r.onDisk -cne (Format-Size $want.OnDisk)) { $bad.Add("on disk drawn $(Show $r.onDisk), want $(Show (Format-Size $want.OnDisk))") }
        if ($r.size -cne (Format-Size $want.Bytes)) { $bad.Add("size drawn $(Show $r.size), want $(Show (Format-Size $want.Bytes))") }
        $note = if ($want.Kind -eq 'Link') { 'link, not followed' } else { '' }
        if ($r.note -cne $note) { $bad.Add("note $(Show $r.note), want $(Show $note)") }
        $expander = $want.Kind -eq 'Folder' -and $want.Children.Count -gt 0
        if ([bool]$r.expander -ne $expander) { $bad.Add("expander $($r.expander), want $expander") }
        if ($want.Path -eq '') {
            if ($r.share -cne '') { $bad.Add("share drawn $(Show $r.share), want none") }
            if ($r.barShown -ne $false) { $bad.Add('a bar is shown') }
        }
        else {
            if ($r.share -cne (Format-Share $want.Share)) { $bad.Add("share drawn $(Show $r.share), want $(Show (Format-Share $want.Share))") }
            if ($r.barShown -ne $true -or [double]$r.barTrackWidth -ne $ShareTrack) { $bad.Add("bar track shown=$($r.barShown) width=$($r.barTrackWidth)") }
            $bar = [double]$r.barActualWidth
            if ($want.Share -eq 0 -and $bar -ne 0) { $bad.Add("bar $bar px for a share of 0") }
            if ([Math]::Abs($bar - $want.Share * $ShareTrack) -gt 1) { $bad.Add("bar $bar px, want $($want.Share * $ShareTrack) within 1 px") }
        }
        if ([double]$r.rowHeight -ne 20) { $bad.Add("row height $($r.rowHeight)") }
        Check ($bad.Count -eq 0) "$label draws as the tree says ($(Show ($bad -join '; ')))"
    }
    foreach ($want in $oracle.Values) {
        if ($want.Children.Count -eq 0) { continue }
        $drawn = @($logged | Where-Object { $_.depth -gt 0 -and (Parent-Of $_.path) -eq $want.Path } | Sort-Object order)
        $sizes = @($drawn | ForEach-Object { [long]$_.onDisk })
        $descending = $true
        for ($i = 1; $i -lt $sizes.Count; $i++) { if ($sizes[$i] -gt $sizes[$i - 1]) { $descending = $false } }
        $names = @($drawn | ForEach-Object { $_.rendered.name } | Sort-Object)
        $expected = @($want.Children | ForEach-Object { $oracle[$_].Name } | Sort-Object)
        $label = if ($want.Path -eq '') { 'the root' } else { $want.Path }
        Check ($descending -and ($names -join '|') -ceq ($expected -join '|')) "under $label, the rows are its children, largest on disk first ($(Show (($drawn | ForEach-Object { "$($_.rendered.name)=$($_.onDisk)" }) -join ', ')))"
    }
}

# Case A's tree (T-001), under $root, and a folder beside it that its links point into. Returns the entries the
# oracle is built from and the links, for the caller to remove before the tree.
function New-TreeCaseA([string] $root, [string] $outside) {
    $entries = [System.Collections.Generic.List[object]]::new()
    function Add([string] $path, [string] $kind, [long] $bytes, [long] $onDisk) {
        $entries.Add([pscustomobject]@{ Path = $path; Kind = $kind; Bytes = $bytes; OnDisk = $onDisk })
    }
    function Folder([string] $path) {
        [void][System.IO.Directory]::CreateDirectory((Join-Path $root $path))
        Add $path 'Folder' 0 0
    }
    function Plain([string] $path, [long] $bytes) {
        New-File (Join-Path $root $path) $bytes
        Add $path 'File' $bytes $bytes
    }

    New-File (Join-Path $outside 'o.bin') 1MB # outside the scanned tree; the links point here

    # A folder three levels deep, and one that holds only a chain of folders and no file of its own.
    Folder 'deep'; Folder 'deep\mid'; Folder 'deep\mid\low'
    Plain 'deep\mid\low\leaf.bin' (5 * $KiB4)
    Plain 'deep\mid\mid.bin' (2 * $KiB4)
    Folder 'chain'; Folder 'chain\only'; Folder 'chain\only\end'
    Plain 'chain\only\end\e.bin' (9 * $KiB4)
    # Bytes and space on disk rank these differently: sparse.bin is the largest in bytes, dense.bin on disk.
    Folder 'size_&_order'
    $sparse = Join-Path $root 'size_&_order\sparse.bin'
    $allocated = New-SparseFile $sparse 1MB 256KB 64KB
    Add 'size_&_order\sparse.bin' 'File' 1MB $allocated
    Plain 'size_&_order\dense.bin' (32 * $KiB4)
    Plain 'size_&_order\my_file&co.bin' (3 * $KiB4)
    # Nothing in it.
    Folder 'empty'
    # A parent that takes nothing on disk, with children: every share 0.
    Folder 'zeros'
    foreach ($z in 'z1.txt', 'z2.txt', 'z3.txt') { Plain "zeros\$z" 0 }
    # Shares with no terminating decimal, exact midpoints and one too small to show at one decimal place.
    Folder 'thirds'
    foreach ($t in 't1.bin', 't2.bin', 't3.bin') { Plain "thirds\$t" $KiB4 }
    Folder 'twothirds'
    Plain 'twothirds\a.bin' (4 * $KiB4)
    Plain 'twothirds\b.bin' (8 * $KiB4)
    Folder 'sixteenths'
    Plain 'sixteenths\one.bin' $KiB4
    Plain 'sixteenths\fifteen.bin' (15 * $KiB4)
    Folder 'tiny'
    Plain 'tiny\big.bin' 8MB
    Plain 'tiny\small.bin' $KiB4
    # A name far too long for its column.
    Plain ('long-' + ('n' * 140) + '.bin') (6 * $KiB4)
    # Links: never followed, charged 0, never opened (Q-001: junctions and both kinds of symbolic link).
    $links = @()
    $junction = Join-Path $root 'j'
    New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
    $links += $junction
    Add 'j' 'Link' 0 0
    $folderLink = Join-Path $root 'sd'
    New-Item -ItemType SymbolicLink -Path $folderLink -Value $outside | Out-Null
    $links += $folderLink
    Add 'sd' 'Link' 0 0
    $fileLink = Join-Path $root 'sf'
    New-Item -ItemType SymbolicLink -Path $fileLink -Value (Join-Path $outside 'o.bin') | Out-Null
    $links += $fileLink
    Add 'sf' 'Link' 0 0
    [pscustomobject]@{ Entries = $entries; Links = $links; SparseOnDisk = $allocated }
}

# Links go first, one by one, so no removal can reach through one into its target.
function Remove-Links($links) {
    foreach ($link in @($links)) {
        if (-not $link) { continue }
        if ([System.IO.Directory]::Exists($link)) { [System.IO.Directory]::Delete($link) }
        elseif ([System.IO.File]::Exists($link)) { [System.IO.File]::Delete($link) }
    }
}

# T-001 (rows 1 to 4): every row of case A's tree, walked to depth 4 with each folder opened, against the tree the
# case built: each row as its container draws it, the order under every folder, totals rolled up through folders
# with no files of their own, shares to one decimal place and their bars, links and an empty folder with no
# expander, and names with '_', '&' and far too long for their column drawn as written.
function Test-T001 {
    $case = New-CaseFolder
    $built = $null
    try {
        $root = Join-Path $case 'tree'
        $built = New-TreeCaseA $root (Join-Path $case 'outside')
        $oracle = Get-TreeOracle $root $built.Entries
        Write-Output ("        sparse.bin: 1 MB in bytes, {0:N0} bytes allocated (fsutil sparse queryrange)" -f $built.SparseOnDisk)
        Check ($built.SparseOnDisk -gt 0 -and $built.SparseOnDisk -lt 32 * $KiB4) "VOID unless sparse.bin takes less on disk than dense.bin while larger in bytes ($($built.SparseOnDisk) allocated)"

        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '4' }
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        if ($script:Failures.Count -gt $before) { return }
        $walked = @(Lines-Of $run 'walked')
        Check ($walked.Count -eq 1 -and $walked[0].unrendered -eq 0) "every row read had a container that drew it ($(Show ($walked | ConvertTo-Json -Compress)))"
        $rows = @(Lines-Of $run 'tree-row')
        CheckTreeRows $oracle $rows

        # The shapes by name, so a failure says which one.
        $byPath = @{}
        foreach ($row in $rows) { $byPath[$row.path] = $row }
        $order = @($rows | Where-Object { (Parent-Of $_.path) -eq 'size_&_order' -and $_.depth -eq 2 } | Sort-Object order | ForEach-Object { $_.rendered.name })
        CheckEqual 'dense.bin|sparse.bin|my_file&co.bin' ($order -join '|') 'size_&_order: dense.bin, smaller in bytes, ranks above sparse.bin, larger in bytes (row 2)'
        $sum = ($rows | Where-Object depth -eq 1 | Measure-Object -Property size -Sum).Sum
        CheckEqual $oracle[''].Bytes $byPath[''].size 'the root row''s size is everything below it (row 4)'
        CheckEqual $sum $byPath[''].size 'the root row''s size is the sum of its rows (it holds no file of its own)'
        CheckEqual (Format-Size (5 * $KiB4)) $byPath['deep\mid\low'].rendered.size 'deep\mid\low, three levels deep, draws its file''s size'
        CheckEqual (Format-Size (9 * $KiB4)) $byPath['chain'].rendered.size 'chain, which holds only a chain of folders, draws the file at its end'
        foreach ($z in 'z1.txt', 'z2.txt', 'z3.txt') {
            $r = $byPath["zeros\$z"].rendered
            Check ($r.share -ceq (Format-Share 0) -and [double]$r.barActualWidth -eq 0) "zeros\$z, under a parent that takes nothing, draws $(Format-Share 0) and no bar ($(Show $r.share), $($r.barActualWidth) px)"
        }
        CheckEqual (Format-Share (1 / 3)) $byPath['thirds\t1.bin'].rendered.share 'a third'
        CheckEqual (Format-Share (2 / 3)) $byPath['twothirds\b.bin'].rendered.share 'two thirds'
        CheckEqual (Format-Share (1 / 16)) $byPath['sixteenths\one.bin'].rendered.share 'a sixteenth, an exact midpoint (6.25), rounded away from zero'
        CheckEqual (Format-Share (15 / 16)) $byPath['sixteenths\fifteen.bin'].rendered.share 'fifteen sixteenths (93.75)'
        $small = $byPath['tiny\small.bin'].rendered
        Check ($small.share -ceq (Format-Share 0) -and [double]$small.barActualWidth -gt 0) "tiny\small.bin, a share too small for one decimal place, draws $(Format-Share 0) and a bar that is not empty ($(Show $small.share), $($small.barActualWidth) px)"
        foreach ($name in 'size_&_order', 'size_&_order\my_file&co.bin', ('long-' + ('n' * 140) + '.bin')) {
            CheckEqual (Leaf-Of $name) $byPath[$name].rendered.name "$(if ($name.Length -gt 40) { 'the long name' } else { $name }) is drawn as written"
        }
        foreach ($link in 'j', 'sd', 'sf') {
            $r = $byPath[$link].rendered
            Check (-not $r.expander -and $r.onDisk -ceq '0 bytes' -and $r.note -ceq 'link, not followed') "link $link has no expander, 0 bytes and says it is not followed"
        }
        Check (-not $byPath['empty'].rendered.expander) 'the empty folder has no expander'
    }
    finally {
        if ($null -ne $built) { Remove-Links $built.Links }
        Remove-CaseFolder $case
    }
}

# T-002 (row 2): expand state through recycling. A folder of 60 folders, far more rows than the tree's height;
# the hook opens it, opens its third child from the end, scrolls the tree to its end and back twice, closes and
# reopens the folder, and reads every child as drawn: only that child open, on the row it was opened on, and the
# order and figures as before.
function Test-T002 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        for ($i = 0; $i -lt 60; $i++) {
            New-File (Join-Path $root ('many\f{0:D2}\x.bin' -f $i)) (($i + 1) * $KiB4)
        }
        New-File (Join-Path $root 'other.bin') $KiB4
        $expected = @(for ($i = 59; $i -ge 0; $i--) { 'f{0:D2}' -f $i })

        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '0'; FSA_HOOK_RECYCLE = 'many' }
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        if ($script:Failures.Count -gt $before) { return }
        $recycle = @(Lines-Of $run 'recycle')
        Check ($recycle.Count -eq 1) "the hook logged one 'recycle' line"
        if ($recycle.Count -ne 1) { return }
        $r = $recycle[0]
        Check ([double]$r.extentHeight -gt 2 * [double]$r.viewportHeight) "the rows run far past what the tree shows ($($r.extentHeight) px of rows in $($r.viewportHeight) px)"
        CheckEqual 'f02' $r.child 'the child opened is the third from the end, f02'
        CheckEqual ($expected -join '|') (@($r.before) -join '|') 'before: the children largest on disk first'
        CheckEqual (@($r.before) -join '|') (@($r.after) -join '|') 'after scrolling away and back and reopening the folder: the same order'
        CheckEqual 'f02' (@($r.expandedNodes) -join '|') 'only f02 is open, by the rows'' own state'
        CheckEqual 'f02' (@($r.expandedContainers) -join '|') 'only f02 is drawn open'
        CheckEqual 'x.bin' (@($r.childRowsBefore) -join '|') 'f02 showed x.bin when opened'
        CheckEqual 'x.bin' (@($r.childRowsAfter) -join '|') 'and still draws it after'
        CheckEqual 0 $r.unrendered 'every child had a container that drew it'
        $wrong = @(Lines-Of $run 'recycle-row' | Where-Object {
                $null -eq $_.rendered -or $_.rendered.name -cne $_.name -or $_.rendered.onDisk -cne (Format-Size $_.onDisk) -or $_.rendered.expanded -ne $_.nodeExpanded })
        Check ($wrong.Count -eq 0) "every child draws its own name, size and open state ($(Show (($wrong | ForEach-Object name) -join ', ')))"
        $sizes = @(Lines-Of $run 'recycle-row' | ForEach-Object { [long]$_.onDisk })
        CheckEqual (@($r.beforeOnDisk) -join '|') ($sizes -join '|') 'every child''s size is what it was before'
    }
    finally { Remove-CaseFolder $case }
}

# A folder of 100,000 files directly under $root\big (never the root itself, review m7): 50 of them distinct 4 KiB
# multiples up to 200 KiB, the rest empty, a tie block at 0. Beside it a folder chain and a file. About 30 s.
function New-HundredThousand([string] $root) {
    $big = Join-Path $root 'big'
    [void][System.IO.Directory]::CreateDirectory($big)
    $empty = [byte[]]::new(0)
    for ($i = 0; $i -lt 100000; $i++) {
        # Assigned in each branch: an if's output would unroll the array.
        if ($i % 2000 -eq 0) { $bytes = [byte[]]::new(($i / 2000 + 1) * $KiB4) } else { $bytes = $empty }
        [System.IO.File]::WriteAllBytes([System.IO.Path]::Combine($big, ('e{0:D6}.bin' -f $i)), $bytes)
    }
    New-File (Join-Path $root 'deep\a\b.bin') $KiB4
    New-File (Join-Path $root 'top.bin') (2 * $KiB4)
}

# T-003 (row 5, review M1): opening a folder of 100,000 files, cold and then four more times after closing it,
# each timed by the hook from setting IsExpanded on its row to the first ContextIdle pass with its first child's
# row built: every open under 0.5 s, only a screenful of rows built (virtualization on), the largest file first.
# Then every folder closed, the root row selected and the numpad * key sent to it: no folder may open.
function Test-T003 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        New-HundredThousand $root
        Write-Output ("        built 100,000 files in {0:N1} s" -f $clock.Elapsed.TotalSeconds)

        Write-Output '  at the default window size, then the * key'
        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '0'; FSA_HOOK_EXPAND = 'big'; FSA_HOOK_MULTIPLY = '1' } 300
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        if ($script:Failures.Count -gt $before) { return }
        CheckOpens $run
        $defaultViewport = [double]@(Lines-Of $run 'expand')[0].viewportHeight
        $multiply = @(Lines-Of $run 'multiply')
        Check ($multiply.Count -eq 1) "the hook sent * ('multiply' line)"
        if ($multiply.Count -eq 1) {
            CheckEqual $true $multiply[0].selected 'the root row was selected'
            CheckEqual $true $multiply[0].previewHandled 'the tree took the * key'
            CheckEqual 0 $multiply[0].expandedBeyondRoot "no folder below the root opened ($(Show (@($multiply[0].expanded) -join ', ')))"
        }

        # At the default size the tree has three quarters of the height below the pickers beside the outcome (review
        # M2), a screenful of rows; a person looking for space may make the window as tall as the screen allows, and
        # every row then on screen is built as the folder opens.
        Write-Output '  in a window as tall as the work area'
        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '0'; FSA_HOOK_EXPAND = 'big'; FSA_HOOK_HEIGHT = '4000' } 300
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        if ($script:Failures.Count -gt $before) { return }
        $first = @(Lines-Of $run 'expand')[0]
        Check ([double]$first.viewportHeight -gt $defaultViewport) "VOID unless the taller window gave the tree more room than the default ($($first.viewportHeight) px against $defaultViewport px)"
        CheckOpens $run
    }
    finally { Remove-CaseFolder $case }
}

# T-003's checks on the five opens of big: each under 0.5 s to its first rows, only the rows on screen built, the
# largest file first; open 1 cold, with its sort and row build logged apart, and 2 to 5 warm.
function CheckOpens($run) {
    $opens = @(Lines-Of $run 'expand')
    CheckEqual 5 $opens.Count 'the hook opened big five times'
    foreach ($open in $opens) {
        $split = if ($open.cold) { (' (sort {0:N1} ms, row build {1:N1} ms, the rest {2:N1} ms)' -f $open.sortMs, $open.buildMs, $open.layoutMs) } else { '' }
        Write-Output ("        open {0}: {1:N1} ms{2}, {3} rows built, a viewport of {4:N0} px" -f $open.open, $open.ms, $split, $open.realized, $open.viewportHeight)
        CheckEqual ($open.open -eq 1) $open.cold "open $($open.open) is $(if ($open.open -eq 1) { 'cold, building its rows' } else { 'warm, reusing them' })"
        Check ($open.firstChild -and [double]$open.ms -lt 500) ("open {0} drew its first rows in {1:N1} ms (under 500)" -f $open.open, $open.ms)
        Check ($open.realized -gt 0 -and $open.realized -le 200) "open $($open.open) built only a screenful of rows ($($open.realized))"
        CheckEqual 100000 $open.children "open $($open.open): big holds 100,000 rows"
        CheckEqual (50 * $KiB4) $open.firstOnDisk "open $($open.open): the first row is the largest file"
    }
    $cold = @($opens | Where-Object cold)
    Check ($cold.Count -eq 1 -and $null -ne $cold[0].sortMs -and $null -ne $cold[0].buildMs) 'the cold open logged its sort and row build times'
}

# T-004 (rows 1 and 6, review M2): a second scan started while a tree is shown with a large folder open and a row
# three levels deep selected, as a person picking a new folder would leave it. The first scan's store must be
# collectable while the second scan runs (New-LargeTree's scan takes a second or more, so it is still running when
# the hook checks), and again after it ends, when the tree shows only the new root.
function Test-T004 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'first'
        $big = Join-Path $root 'big'
        [void][System.IO.Directory]::CreateDirectory($big)
        for ($i = 0; $i -lt 3000; $i++) {
            New-File (Join-Path $big ('e{0:D4}.bin' -f $i)) $(if ($i % 150 -eq 0) { ($i / 150 + 1) * $KiB4 } else { 0 })
        }
        New-File (Join-Path $root 'a\b\c.bin') $KiB4
        $second = Join-Path $case 'second'
        [void](New-LargeTree $second)

        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '0'; FSA_HOOK_EXPAND = 'big'; FSA_HOOK_SELECT = 'a\b\c.bin'; FSA_HOOK_RELEASE = $second }
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        CheckTreeEnding $run $second 'second-ending'
        if ($script:Failures.Count -gt $before) { return }
        $opens = @(Lines-Of $run 'expand')
        Check ($opens.Count -eq 5 -and $opens[-1].firstChild) 'the large folder was open when the second scan started'
        $selected = @(Lines-Of $run 'selected')
        Check ($selected.Count -eq 1 -and $selected[0].selected -and $selected[0].treeSelected -and $selected[0].depth -eq 3) "a row three levels deep was selected ($(Show ($selected | ConvertTo-Json -Compress)))"
        $released = @(Lines-Of $run 'store-released')
        CheckEqual 2 $released.Count "the hook checked the first store twice"
        if ($released.Count -eq 2) {
            Check ($released[0].scanning -and -not $released[0].tree) "VOID unless the first check came while the second scan ran, with no tree shown (scanning=$($released[0].scanning) tree=$($released[0].tree))"
            CheckEqual $true $released[0].released 'the first scan''s store was collectable while the second scan ran'
            CheckEqual $true $released[1].released 'and after the second scan ended'
        }
    }
    finally { Remove-CaseFolder $case }
}

# T-005 (row 6, review m7): the UI thread sampled by the hook every 10 ms (an Input-priority probe) through a scan
# while the window is moved and resized, the hand-off to the tree, one open of a folder of 100,000 files, and a
# second scan started over that open tree, to its hand-off. In every phase but the open, whose own bar is T-003's,
# no probe may wait a second, and where a phase has at least 20 probes, 95% of them must get through within
# 200 ms. With fewer, the 95th percentile is the longest wait itself, and one stall already there before SL-044
# (the first resize after a scan starts, 0.4 to 0.8 s, the code review's M1) would fail the phase on the second
# bar rather than the first; so a phase that short is held to the second alone, and says so.
$GapP95Floor = 20
function Test-T005 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        New-HundredThousand $root
        # Folders are what take a scan time (New-LargeTree), so these keep it running about a second.
        for ($s = 0; $s -lt 20; $s++) {
            for ($j = 0; $j -lt 1000; $j++) { [void][System.IO.Directory]::CreateDirectory((Join-Path $root "slow\s$s\m$j")) }
        }
        Write-Output ("        built 100,000 files and 20,000 folders in {0:N1} s" -f $clock.Elapsed.TotalSeconds)

        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '0'; FSA_HOOK_GAP = 'big' } 300
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        CheckTreeEnding $run $root 'second-ending'
        if ($script:Failures.Count -gt $before) { return }
        $first = @(Lines-Of $run 'ending')[0]
        $again = @(Lines-Of $run 'second-ending')[0]
        Check ([int]$first.moves -ge 5) "the window was moved and resized while the first scan ran ($($first.moves) times)"
        Write-Output ("        moved {0} times during the first scan (the longest move held the UI thread {1:N0} ms), {2} during the second ({3:N0} ms)" -f $first.moves, $first.longestMoveMs, $again.moves, $again.longestMoveMs)
        $started = @(Lines-Of $run 'rescan-started')
        if ($started.Count -eq 1) { Write-Output ("        the second scan's start, the open tree's release included, held the UI thread {0:N1} ms" -f $started[0].startScanMs) }
        $phases = @(Lines-Of $run 'gap')
        CheckEqual 5 $phases.Count 'the hook sampled five phases'
        foreach ($phase in $phases) {
            Write-Output ("        {0}: {1} probes, p95 {2} ms, max {3} ms (posted at {4} ms)" -f $phase.phase, $phase.probes, $phase.p95, $phase.max, $phase.maxAt)
            if ($phase.phase -eq 'open') { continue }
            Check ([int]$phase.probes -ge 1) "$($phase.phase): the UI thread was sampled"
            if ([int]$phase.probes -eq 0) { continue }
            Check ([double]$phase.max -lt 1000) "$($phase.phase): no probe waited a second ($($phase.max) ms)"
            if ([int]$phase.probes -ge $GapP95Floor) {
                Check ([double]$phase.p95 -lt 200) "$($phase.phase): 95% of $($phase.probes) probes got through within 200 ms ($($phase.p95) ms)"
            }
            else {
                Write-Output "        $($phase.phase): p95 not asserted, $($phase.probes) probes (fewer than $GapP95Floor)"
            }
        }
    }
    finally { Remove-CaseFolder $case }
}

# T-006 (row 1): the endings that show no tree, because their shares would be of an incomplete whole or of
# nothing. Stop pressed mid-scan: the results list stays with the final snapshot and FolderTree is not shown. A
# fault on the UI thread mid-scan: the app ends 70, and as it shuts down the tree is not shown. A root that is not
# there: Could not read, no tree.
function Test-T006 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)

        Write-Output '  Stop pressed mid-scan'
        $run = Invoke-HookedRun 'stop' $root (Join-Path $case 'report.json')
        $before = $script:Failures.Count
        CheckActed $run 'stop'
        if ($script:Failures.Count -eq $before) {
            CheckEqual '0' $run.Exit 'exit code'
            Check ($null -ne $run.Report) 'a report was written'
            if ($null -ne $run.Report) {
                CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
                CheckEqual $false $run.Report.window.FolderTree.visible 'FolderTree is not shown after a stop'
                CheckEqual $true $run.Report.window.TopLevelPanel.visible 'the results panel is'
                CheckEqual $true $run.Report.window.TopLevelList.inView 'with the results list in view'
                Check ($null -ne $run.Report.topLevel -and $run.Report.topLevel.final) 'holding the final snapshot'
            }
        }

        Write-Output '  a fault on the UI thread mid-scan'
        $report = Join-Path $case 'fault.json'
        Set-Content -LiteralPath $report -Value '{"schema":1,"stale":true}'
        $run = Invoke-HookedRun 'throw' $root $report
        $before = $script:Failures.Count
        CheckActed $run 'throw'
        if ($script:Failures.Count -eq $before) {
            Check ($run.Exit -eq '70') "exit code is the app's 70 (actual $(Show-Exit $run.Exit))"
            $shutdown = @($run.Log | Where-Object { $_.StartsWith('shutdown ') })
            Check ($shutdown.Count -eq 1 -and (Field $shutdown[0] 'tree') -eq 'False' -and (Field $shutdown[0] 'treeItems') -eq '0') "as the app shut down, the tree was found, not shown and empty ($(Show ($shutdown -join ' | ')))"
        }

        Write-Output '  a root that is not there'
        $read = Read-Window (Join-Path $case 'missing')
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        if ($null -ne $read.Report) {
            Check ("$($read.Report.window.OutcomeTitle)".StartsWith('Could not read ')) 'the ending is Could not read'
            CheckEqual $false $read.Report.window.FolderTree.visible 'FolderTree is not shown'
        }
    }
    finally { Remove-CaseFolder $case }
}

# ---- The files by kind (SL-046) ----

# Whether the report carries "kinds" at all: absent and an empty array are two different endings.
function Has-Kinds($report) { $report.PSObject.Properties.Name -contains 'kinds' }

# The report's "kinds" against the case's own oracle, row by row in the list's order, and added up against the root's
# totals as the window's core section has them (the only exact figure for a stopped scan). KindsList holds a row for
# each, whichever view the results cell shows, and nothing in the report names an extension the tree planted (Q-016).
function CheckKinds($read, $expected, [string] $caption) {
    $report = $read.Report
    Check (Has-Kinds $report) 'the report carries kinds'
    if (-not (Has-Kinds $report)) { return }
    $kinds = @($report.kinds)
    $shown = ($kinds | ForEach-Object { "$($_.id) $($_.files) $($_.size) $($_.onDisk)" }) -join '; '
    if ($null -ne $expected) {
        $wanted = ($expected | ForEach-Object { "$($_.Id) $($_.Files) $($_.Size) $($_.OnDisk)" }) -join '; '
        CheckEqual $wanted $shown 'kinds against the tree, in order (id files size onDisk)'
    }
    else {
        Write-Output "        kinds: $shown"
    }
    $files = ($kinds | Measure-Object -Property files -Sum).Sum
    $size = ($kinds | Measure-Object -Property size -Sum).Sum
    $onDisk = ($kinds | Measure-Object -Property onDisk -Sum).Sum
    CheckEqual "$($report.core.files) $($report.core.bytes) $($report.core.bytesOnDisk)" "$([long]$files) $([long]$size) $([long]$onDisk)" 'the kinds add up to the root (core files, bytes, bytesOnDisk)'
    Check (@($kinds | Where-Object { -not $_.id -or -not $_.label }).Count -eq 0) 'every kind has an id and a label'
    CheckEqual $kinds.Count $report.window.KindsList.rows 'KindsList holds a row for each kind'
    CheckEqual $caption $report.window.KindsCaption 'KindsCaption'
    CheckEqual $true $report.window.ShowKindsButton.visible 'the choice of the files by kind is on screen'
    Check (-not $read.Text.Contains($Planted)) "the report holds no planted name or extension ('$Planted')"
}

# The results cell as the kinds view shows it, chosen by the hook's kinds mode: the view on screen in the folders' place,
# with all $full rows built and wholly inside the list's viewport or, for 0, at least one row there (the list
# virtualizes, as a ListBox does, so at the minimum size it builds only what is near the viewport).
function CheckKindsShown($report, [int] $full) {
    $window = $report.window
    $rows = [int]$window.KindsList.rows
    CheckEqual $true $window.ShowKindsButton.isChecked 'the files by kind are chosen'
    CheckEqual $true $window.KindsView.visible 'KindsView is on screen'
    CheckEqual $false $window.FolderTree.visible 'in the folders'' place: FolderTree is not on screen'
    CheckEqual $true $window.KindsList.inView "KindsList is inside the window ($($window.KindsList.actualHeight) px)"
    if ($full -gt 0) {
        CheckEqual $rows $window.KindsList.realizedRows 'every row of KindsList was built'
        CheckEqual $full $window.KindsList.fullRows "all $full rows are in view without scrolling"
    }
    else {
        Check ($window.KindsList.fullRows -ge 1) "KindsList shows at least one full row ($($window.KindsList.fullRows) of $rows in $($window.KindsList.actualHeight) px)"
    }
}

# What the report says when an ending has no kinds to show, or none yet.
function CheckNoKinds($report, [string] $caption, [string] $when) {
    Check (-not (Has-Kinds $report)) "$($when): no kinds in the report"
    CheckEqual 0 $report.window.KindsList.rows "$($when): KindsList holds no rows"
    CheckEqual $false $report.window.KindsView.visible "$($when): KindsView is not on screen"
    CheckEqual $caption $report.window.KindsCaption "$($when): KindsCaption"
}

# K-001 (SL-046): a completed scan of a planted tree, one file or more of each of seven kinds, every size a multiple of
# 64 KiB or empty, so the space on disk is the size. The report's kinds must be the tree's, in the list's order (by space
# on disk, then size, then kind), and add up to the root. Rows that tie keep the kind order: Videos before Programs,
# Images before No extension. The extension no table names (.plantedname-ext) is Other's, and never in the report.
# Read three ways: as the ending shows it, the folder tree in the results cell and the choice of the kinds beside its
# header; with the kinds chosen (the hook's kinds mode), every row in view at the default size; and so again at the
# window's minimum size with 26 drive buttons added, at least one full row in view. Then a tree with folders and no
# file: an empty array, and the caption says so.
function Test-K001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        New-File (Join-Path $root "clips\$Planted-a.mp4") (3 * $KiB64)
        New-File (Join-Path $root "clips\$Planted-b.MKV") (2 * $KiB64)
        New-File (Join-Path $root "$Planted-photo.jpg") $KiB64
        New-File (Join-Path $root "pkg\$Planted.msi") (4 * $KiB64)
        New-File (Join-Path $root "pkg\$Planted-setup.exe") (5 * $KiB64)
        New-File (Join-Path $root "$Planted-noext") $KiB64
        New-File (Join-Path $root "deep\er\$Planted.$Planted-ext") (6 * $KiB64)
        New-File (Join-Path $root "$Planted-empty.txt") 0
        $manifest = [pscustomobject]@{ Files = 8; Folders = 4; Links = 0; Bytes = 22 * $KiB64 }
        $expected = @(
            [pscustomobject]@{ Id = 'other'; Files = 1; Size = 6 * $KiB64; OnDisk = 6 * $KiB64 }
            [pscustomobject]@{ Id = 'videos'; Files = 2; Size = 5 * $KiB64; OnDisk = 5 * $KiB64 }
            [pscustomobject]@{ Id = 'programs'; Files = 1; Size = 5 * $KiB64; OnDisk = 5 * $KiB64 }
            [pscustomobject]@{ Id = 'installers'; Files = 1; Size = 4 * $KiB64; OnDisk = 4 * $KiB64 }
            [pscustomobject]@{ Id = 'images'; Files = 1; Size = $KiB64; OnDisk = $KiB64 }
            [pscustomobject]@{ Id = 'no-extension'; Files = 1; Size = $KiB64; OnDisk = $KiB64 }
            [pscustomobject]@{ Id = 'documents'; Files = 1; Size = 0; OnDisk = 0 }
        )

        $caption = '8 files scanned, sorted by extension.'

        Write-Output '  Completed, seven kinds, as the ending shows them'
        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -ne $read.Report) {
            CheckKinds $read $expected $caption
            $labels = @($read.Report.kinds | ForEach-Object label) -join ', '
            CheckEqual 'Other, Videos, Programs & system files, Installers, Images, No extension, Documents' $labels 'the kinds'' labels, as the list shows them'
            CheckEqual $false $read.Report.window.ShowKindsButton.isChecked 'the ending shows the folders: the kinds are not chosen'
            CheckEqual $false $read.Report.window.KindsView.visible 'KindsView is not on screen'
            CheckEqual $true $read.Report.window.FolderTree.inView 'FolderTree is'
        }

        foreach ($smallest in $false, $true) {
            Write-Output "  Completed, seven kinds, the kinds chosen, $(if ($smallest) { "at the window's minimum size with $ExtraDrives drive buttons added" } else { 'at the default size' })"
            $variables = if ($smallest) { @{ FSA_HOOK_MINSIZE = '1'; FSA_HOOK_DRIVES = "$ExtraDrives" } } else { @{} }
            $run = Invoke-WithHook 'kinds' $HookDll { Read-Window $root } $variables
            $before = $script:Failures.Count
            CheckActed $run 'kinds'
            if ($smallest) {
                CheckSized $run.Log
                CheckDrives $run.Log $ExtraDrives
            }
            if ($script:Failures.Count -gt $before) { continue }
            $read = $run.Result
            CheckEqual 'EXIT 0' $read.Exit 'exit code'
            Check ($null -ne $read.Report) 'a report was written'
            if ($null -eq $read.Report) { continue }
            CheckKinds $read $expected $caption
            CheckKindsShown $read.Report $(if ($smallest) { 0 } else { $expected.Count })
        }

        Write-Output '  Completed, folders and no file'
        $empty = Join-Path $case 'folders'
        [void][System.IO.Directory]::CreateDirectory((Join-Path $empty 'a\b'))
        [void][System.IO.Directory]::CreateDirectory((Join-Path $empty 'c'))
        $read = Read-Window $empty
        CheckFinished $read $empty $empty ([pscustomobject]@{ Files = 0; Folders = 3; Links = 0; Bytes = 0 })
        if ($null -ne $read.Report) {
            CheckKinds $read @() 'No files.'
            CheckEqual 0 @($read.Report.kinds).Count 'kinds is an empty array'
        }
    }
    finally { Remove-CaseFolder $case }
}

# K-002 (SL-046, review m2): Stop pressed mid-scan (the hook, as C-006). The kinds of a stopped scan are computed like a
# completed one's, from what was read, and add up to what the ending says was read (core, the only exact figure for a
# partial scan); the caption says they are of the part read. New-LargeTree's files are data.bin (64 KiB) and empty
# .txt files, so the kinds are Other and Documents, whichever were reached.
function Test-K002 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)
        $run = Invoke-HookedRun 'stop' $root (Join-Path $case 'report.json')
        $before = $script:Failures.Count
        CheckActed $run 'stop'
        if ($script:Failures.Count -gt $before) { return }
        CheckEqual '0' $run.Exit 'exit code'
        Check ($null -ne $run.Report) 'a report was written'
        if ($null -eq $run.Report) { return }
        CheckEqual 'Cancelled' $run.Report.core.status 'core.status'
        $files = [long]$run.Report.core.files
        Write-Output "        stopped with $files files read"
        $caption = if ($files -eq 0) { 'No files in the part read before the scan stopped.' }
            else { "$(Format-Count $files 'file') scanned in the part read before the scan stopped, sorted by extension." }
        $read = [pscustomobject]@{ Report = $run.Report; Text = $run.Text }
        CheckKinds $read $null $caption
        $ids = @($run.Report.kinds | ForEach-Object id)
        Check (@($ids | Where-Object { $_ -notin 'other', 'documents' }).Count -eq 0) "only Other and Documents: the tree holds .bin and .txt files ($(Show ($ids -join ', ')))"
    }
    finally { Remove-CaseFolder $case }
}

# K-003 (SL-046): a second scan after the first has shown its ending (the hook's rescan mode, as B-006). The report is of
# the window at the second scan's start, or at its failure ending when the root was moved aside first: in neither may the
# first scan's kinds show, in the list or in the report. At the start the section says when they come.
function Test-K003 {
    $shapes = @(
        [pscustomobject]@{ Name = 'the second scan has begun and not finished'; Missing = $false }
        [pscustomobject]@{ Name = 'the second scan fails before any entry (its root is gone)'; Missing = $true }
    )
    foreach ($shape in $shapes) {
        Write-Output "  $($shape.Name)"
        $case = New-CaseFolder
        try {
            if ($shape.Missing) {
                $root = Join-Path $case 'small'
                [void](New-SmallTree $root)
                $armed = @{ FSA_HOOK_MISSING = $root }
            }
            else {
                $root = Join-Path $case 'large'
                [void](New-LargeTree $root)
                $armed = @{}
            }
            $run = Invoke-WithHook 'rescan' $HookDll { Read-Window $root } $armed
            $before = $script:Failures.Count
            CheckActed $run 'rescan'
            if ($script:Failures.Count -gt $before) { continue }
            $listed = @($run.Log | Where-Object { $_.StartsWith('listed ') })[0]
            CheckEqual 'True' (Field $listed 'largest') 'the first ending was on screen before the second scan (hook: listed largest=True)'

            $read = $run.Result
            CheckEqual 'EXIT 0' $read.Exit 'exit code'
            Check ($null -ne $read.Report) 'a report was written'
            if ($null -eq $read.Report) { continue }
            if ($shape.Missing) {
                CheckEqual 'RootUnreadable' $read.Report.core.status 'the report is of the second scan''s failure (core.status)'
                CheckNoKinds $read.Report '' 'at the Could not read ending'
            }
            else {
                Check ($read.Text -match '"core":\s*null') 'the report is of the running second scan (core is null)'
                CheckNoKinds $read.Report 'Shown when the scan finishes.' 'as the second scan runs'
            }
        }
        finally { Remove-CaseFolder $case }
    }
}

function Invoke-RevealRun([string] $root, [string] $revealTargets, [int] $timeoutSec = 60) {
    $variables = @{ FSA_HOOK_REVEAL = $revealTargets }
    $run = Invoke-WithHook 'reveal' $HookDll { Wait-App (Start-App "`"$root`"") $timeoutSec } $variables
    $lines = @(Read-HookJson $run.Log)
    # Every menu the hook opened acted on the row the app's own Opened handler captured: the hook reads the menu's
    # Tag and never writes it (B3-3). Printed to the host: this function's output is the run.
    foreach ($line in @($lines | Where-Object via -eq 'menu')) {
        CheckEqual $true $line.captured "$($line.surface):$($line.name) the app's Opened handler captured the row the menu was opened on" | Out-Host
    }
    [pscustomobject]@{
        Exit  = $run.Result
        Log   = $run.Log
        Lines = $lines
    }
}

function Wait-ExplorerSelection($expectedFolder, $expectedItem, [int] $timeoutSec = 5) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $shell = New-Object -ComObject Shell.Application
    $normalizedExpectedFolder = ($expectedFolder -replace '[/\\]+$', '').ToLowerInvariant()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        foreach ($w in $shell.Windows()) {
            try {
                $doc = $w.Document
                if ($null -eq $doc) { continue }
                $f = $doc.Folder.Self.Path
                $normalizedActual = ($f -replace '[/\\]+$', '').ToLowerInvariant()
                if ($normalizedActual -ne $normalizedExpectedFolder) {
                    try {
                        $expanded = ([System.IO.DirectoryInfo]::new($f)).FullName
                        $normalizedActual = ($expanded -replace '[/\\]+$', '').ToLowerInvariant()
                    } catch { }
                }
                if ($normalizedActual -eq $normalizedExpectedFolder) {
                    $selected = @($doc.SelectedItems() | ForEach-Object {
                        $_.Name
                        try { Split-Path -Leaf $_.Path } catch { }
                    })
                    if ($expectedItem -in $selected) {
                        return [pscustomobject]@{ Matched = $true; Window = $w; Folder = $f; Selected = $selected }
                    }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    return [pscustomobject]@{ Matched = $false }
}

# R-001 (SL-047): reveals items from all results surfaces in Windows Explorer, asserting selection,
# ancestor fallback for unaddressable names, hidden file note, and proper command binding.
function Test-R001 {
    $shell = New-Object -ComObject Shell.Application
    $baselineHwnds = @(try { $shell.Windows() | ForEach-Object { $_.HWND } } catch { @() })

    $case = New-CaseFolder
    $denied = $null
    $dupDot = $null
    $links = @()
    try {
        $root = Join-Path $case 'reveal-root'
        [void][System.IO.Directory]::CreateDirectory($root)

        $sub = Join-Path $root 'subfolder'
        [void][System.IO.Directory]::CreateDirectory($sub)
        New-File (Join-Path $sub 'inner.bin') $KiB64

        $largest = Join-Path $root 'largest_file.bin'
        New-File $largest ($KiB64 * 32)

        $denied = Join-Path $root 'denied'
        [void][System.IO.Directory]::CreateDirectory($denied)
        Deny-Listing $denied

        $dup = Join-Path $root 'dup'
        New-File $dup $KiB64
        $dupDot = "\\?\" + (Join-Path $root 'dup.')
        [System.IO.File]::WriteAllBytes($dupDot, [byte[]]@(1, 2, 3))

        $lead = Join-Path $root ' lead'
        New-File $lead $KiB64
        $comma = Join-Path $root 'a,b;c.txt'
        New-File $comma $KiB64
        $emojiName = "$([char]::ConvertFromUtf32(0x1F600)).txt"
        $emoji = Join-Path $root $emojiName
        New-File $emoji $KiB64
        $pct = Join-Path $root '%TEMP%.txt'
        New-File $pct $KiB64

        $hs = Join-Path $root 'hs_file.sys'
        New-File $hs $KiB64
        [System.IO.File]::SetAttributes($hs, [System.IO.FileAttributes]'Hidden, System')

        $outside = Join-Path $case 'outside'
        [void][System.IO.Directory]::CreateDirectory($outside)
        New-File (Join-Path $outside 'target.bin') $KiB64

        $junction = Join-Path $root 'junc'
        New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
        $links += $junction

        $symlink = Join-Path $root 'sym_file.bin'
        New-Item -ItemType SymbolicLink -Path $symlink -Value (Join-Path $outside 'target.bin') | Out-Null
        $links += $symlink

        $symDir = Join-Path $root 'sym_dir'
        New-Item -ItemType SymbolicLink -Path $symDir -Value $outside | Out-Null
        $links += $symDir

        $longDir = Join-Path $root ('p' * 70 + '\' + 'q' * 70 + '\' + 'r' * 70)
        [void][System.IO.Directory]::CreateDirectory($longDir)
        $longFile = Join-Path $longDir 'long_file.bin'
        New-File $longFile $KiB64
        Check ($longFile.Length -gt 260) "long file path length is > 260 ($($longFile.Length))"

        # Before every step (B3-2): close the Explorer windows this case opened, never one that was open at its baseline,
        # and wait until they are gone, so a step's selection can only be matched by the window that step opened.
        $closeNewWindows = {
            $sh = New-Object -ComObject Shell.Application
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            do {
                $left = 0
                foreach ($w in @($sh.Windows())) {
                    try {
                        if ($w.HWND -notin $baselineHwnds) {
                            $left++
                            $w.Quit()
                        }
                    } catch { }
                }
                if ($left -gt 0) { Start-Sleep -Milliseconds 100 }
            } while ($left -gt 0 -and $sw.Elapsed.TotalSeconds -lt 5)
            Check ($left -eq 0) "the Explorer windows the case opened are closed before the next step ($left left)"
        }

        # 1. largest: largest_file.bin (C-001)
        & $closeNewWindows
        $runL = Invoke-RevealRun $root 'largest:largest_file.bin'
        CheckEqual '0' $runL.Exit 'largest run exit code'
        $lRow = $runL.Lines | Where-Object name -eq 'largest_file.bin'
        Check ($null -ne $lRow) 'largest_file.bin was logged'
        CheckEqual 'menu' $lRow.via 'largest_file.bin executed via menu'
        CheckEqual $largest $lRow.path 'largest_file.bin resolved path'
        CheckEqual 'Exact' $lRow.kind 'largest_file.bin kind'
        $lSel = Wait-ExplorerSelection $root 'largest_file.bin'
        Check $lSel.Matched 'largest_file.bin selected in Explorer'

        # 2. tree: subfolder (C-002)
        & $closeNewWindows
        $runSub = Invoke-RevealRun $root 'tree:subfolder'
        CheckEqual '0' $runSub.Exit 'subfolder run exit code'
        $treeRow = $runSub.Lines | Where-Object name -eq 'subfolder'
        Check ($null -ne $treeRow) 'subfolder was logged'
        CheckEqual 'menu' $treeRow.via 'subfolder executed via menu'
        CheckEqual $sub $treeRow.path 'subfolder resolved path'
        CheckEqual 'Exact' $treeRow.kind 'subfolder kind'
        $tSel = Wait-ExplorerSelection $root 'subfolder'
        Check $tSel.Matched 'subfolder selected in Explorer'

        # 3. tree: dup. (C-005 / B3-2: mis-parsed name, run before root so no leftover window matches)
        & $closeNewWindows
        $runDup = Invoke-RevealRun $root 'tree:dup.'
        CheckEqual '0' $runDup.Exit 'dup. run exit code'
        $dupRow = $runDup.Lines | Where-Object name -eq 'dup.'
        Check ($null -ne $dupRow) 'dup. was logged'
        CheckEqual 'Ancestor' $dupRow.kind 'dup. kind is Ancestor'
        CheckEqual 'NameUnaddressable' $dupRow.reason 'dup. reason is NameUnaddressable'
        $dupAncestorSel = Wait-ExplorerSelection $case (Split-Path -Leaf $root)
        Check $dupAncestorSel.Matched 'ancestor reveal-root selected in Explorer for dup.'
        # Poll for 2 s asserting decoy 'dup' is never selected anywhere in root
        $pollSw = [System.Diagnostics.Stopwatch]::StartNew()
        $decoyFound = $false
        while ($pollSw.ElapsedMilliseconds -lt 2000) {
            $sh = New-Object -ComObject Shell.Application
            foreach ($w in $sh.Windows()) {
                try {
                    $doc = $w.Document
                    if ($null -ne $doc -and ($doc.Folder.Self.Path -replace '[/\\]+$', '').ToLowerInvariant() -eq ($root -replace '[/\\]+$', '').ToLowerInvariant()) {
                        $sel = @($doc.SelectedItems() | ForEach-Object {
                            $_.Name
                            try { Split-Path -Leaf $_.Path } catch { }
                        })
                        if ('dup' -in $sel) {
                            $decoyFound = $true
                            break
                        }
                    }
                } catch { }
            }
            if ($decoyFound) { break }
            Start-Sleep -Milliseconds 100
        }
        Check (-not $decoyFound) "decoy 'dup' is never selected"

        # 4. tree: root (C-003 / O-017)
        & $closeNewWindows
        $runRoot = Invoke-RevealRun $root 'tree:root'
        CheckEqual '0' $runRoot.Exit 'root run exit code'
        $rRow = $runRoot.Lines | Where-Object name -eq 'root'
        Check ($null -ne $rRow) 'root row was logged'
        CheckEqual 'Exact' $rRow.kind 'root row kind'
        CheckEqual $root $rRow.path 'root row path'
        $rSel = Wait-ExplorerSelection (Split-Path -Parent $root) (Split-Path -Leaf $root)
        Check $rSel.Matched 'root selected in Explorer'

        # 5. tree: hs_file.sys (C-008: hidden+system)
        & $closeNewWindows
        $runHs = Invoke-RevealRun $root 'tree:hs_file.sys'
        CheckEqual '0' $runHs.Exit 'hs_file.sys run exit code'
        $hsRow = $runHs.Lines | Where-Object name -eq 'hs_file.sys'
        Check ($null -ne $hsRow) 'hs_file.sys was logged'
        CheckEqual 'Exact' $hsRow.kind 'hs_file.sys kind'
        Check ($hsRow.status -match "is hidden, so Explorer only highlights it") "hs_file.sys status note shown ($($hsRow.status))"

        # 6. tree: junc (C-009: junction)
        & $closeNewWindows
        $runJ = Invoke-RevealRun $root 'tree:junc'
        CheckEqual '0' $runJ.Exit 'junc run exit code'
        $juncRow = $runJ.Lines | Where-Object name -eq 'junc'
        Check ($null -ne $juncRow) 'junc was logged'
        CheckEqual 'Exact' $juncRow.kind 'junc kind'
        $jSel = Wait-ExplorerSelection $root 'junc'
        Check $jSel.Matched 'junc selected in Explorer'

        # 7. tree: sym_file.bin (C-009: symlink to file)
        & $closeNewWindows
        $runSym = Invoke-RevealRun $root 'tree:sym_file.bin'
        CheckEqual '0' $runSym.Exit 'sym_file.bin run exit code'
        $symRow = $runSym.Lines | Where-Object name -eq 'sym_file.bin'
        Check ($null -ne $symRow) 'sym_file.bin was logged'
        CheckEqual 'Exact' $symRow.kind 'sym_file.bin kind'
        $sSel = Wait-ExplorerSelection $root 'sym_file.bin'
        Check $sSel.Matched 'sym_file.bin selected in Explorer'

        # 8. tree: sym_dir (C-009 / O-017: symlink to folder)
        & $closeNewWindows
        $runSymDir = Invoke-RevealRun $root 'tree:sym_dir'
        CheckEqual '0' $runSymDir.Exit 'sym_dir run exit code'
        $symDirRow = $runSymDir.Lines | Where-Object name -eq 'sym_dir'
        Check ($null -ne $symDirRow) 'sym_dir was logged'
        CheckEqual 'Exact' $symDirRow.kind 'sym_dir kind'
        $sdSel = Wait-ExplorerSelection $root 'sym_dir'
        Check $sdSel.Matched 'sym_dir selected in Explorer'

        # 9. unusual addressable names (C-006 / O-017)
        & $closeNewWindows
        $runC = Invoke-RevealRun $root 'tree:a,b;c.txt'
        CheckEqual '0' $runC.Exit 'a,b;c.txt run exit code'
        $cRow = $runC.Lines | Where-Object name -eq 'a,b;c.txt'
        Check ($null -ne $cRow) 'a,b;c.txt was logged'
        CheckEqual 'Exact' $cRow.kind 'a,b;c.txt kind'
        $cSel = Wait-ExplorerSelection $root 'a,b;c.txt'
        Check $cSel.Matched 'a,b;c.txt selected in Explorer'

        & $closeNewWindows
        $runLead = Invoke-RevealRun $root 'tree: lead'
        CheckEqual '0' $runLead.Exit ' lead run exit code'
        $leadRow = $runLead.Lines | Where-Object name -eq ' lead'
        Check ($null -ne $leadRow) ' lead was logged'
        CheckEqual 'Exact' $leadRow.kind ' lead kind'
        $leadSel = Wait-ExplorerSelection $root ' lead'
        Check $leadSel.Matched ' lead selected in Explorer'

        & $closeNewWindows
        $runPct = Invoke-RevealRun $root 'tree:%TEMP%.txt'
        CheckEqual '0' $runPct.Exit '%TEMP%.txt run exit code'
        $pctRow = $runPct.Lines | Where-Object name -eq '%TEMP%.txt'
        Check ($null -ne $pctRow) '%TEMP%.txt was logged'
        CheckEqual 'Exact' $pctRow.kind '%TEMP%.txt kind'
        $pctSel = Wait-ExplorerSelection $root '%TEMP%.txt'
        Check $pctSel.Matched '%TEMP%.txt selected in Explorer'

        & $closeNewWindows
        $runDupPlain = Invoke-RevealRun $root 'tree:dup'
        CheckEqual '0' $runDupPlain.Exit 'dup run exit code'
        $dupPlainRow = $runDupPlain.Lines | Where-Object name -eq 'dup'
        Check ($null -ne $dupPlainRow) 'dup was logged'
        CheckEqual 'Exact' $dupPlainRow.kind 'dup kind'
        $dpSel = Wait-ExplorerSelection $root 'dup'
        Check $dpSel.Matched 'dup selected in Explorer'

        & $closeNewWindows
        $runEmoji = Invoke-RevealRun $root "tree:$emojiName"
        CheckEqual '0' $runEmoji.Exit 'emoji run exit code'
        $emojiRow = $runEmoji.Lines | Where-Object name -eq $emojiName
        Check ($null -ne $emojiRow) 'emoji file was logged'
        CheckEqual 'Exact' $emojiRow.kind 'emoji kind'
        $eSel = Wait-ExplorerSelection $root $emojiName
        Check $eSel.Matched 'emoji selected in Explorer'

        # 10. long path >260 chars through App (C-004 / O-017)
        & $closeNewWindows
        $runLong = Invoke-RevealRun $root 'tree:long_file.bin'
        CheckEqual '0' $runLong.Exit 'long file run exit code'
        $longRow = $runLong.Lines | Where-Object name -eq 'long_file.bin'
        Check ($null -ne $longRow) 'long file was logged'
        CheckEqual 'Exact' $longRow.kind 'long file kind'
        CheckEqual $longFile $longRow.path 'long file resolved path'
        $longSel = Wait-ExplorerSelection $longDir 'long_file.bin'
        Check $longSel.Matched 'long file selected in Explorer'

        # 11. nested tree row with competing selection (C-011 / O-017)
        & $closeNewWindows
        $runComp = Invoke-RevealRun $root 'tree-competing:inner.bin'
        CheckEqual '0' $runComp.Exit 'competing selection run exit code'
        $compRow = $runComp.Lines | Where-Object name -eq 'inner.bin'
        Check ($null -ne $compRow) 'inner.bin was logged'
        CheckEqual 'Exact' $compRow.kind 'competing selection row kind'
        CheckEqual (Join-Path $sub 'inner.bin') $compRow.path 'competing selection path'
        $compSel = Wait-ExplorerSelection $sub 'inner.bin'
        Check $compSel.Matched 'inner.bin selected in Explorer'

        # 12. gesture reveal Ctrl+Enter against Window target (M1 / O-014 / O-019)
        & $closeNewWindows
        $runG = Invoke-RevealRun $root 'gesture:largest_file.bin'
        CheckEqual '0' $runG.Exit 'gesture run exit code'
        $gRow = $runG.Lines | Where-Object name -eq 'largest_file.bin'
        Check ($null -ne $gRow) 'gesture row was logged'
        CheckEqual 'gesture' $gRow.via 'command executed via gesture against Window target'
        CheckEqual 'Exact' $gRow.kind 'gesture kind'
        $gSel = Wait-ExplorerSelection $root 'largest_file.bin'
        Check $gSel.Matched 'gesture selected in Explorer'

        # 13. placeholder row disabled (m3 / O-018 / O-026)
        & $closeNewWindows
        $runP = Invoke-RevealRun $root 'placeholder:dummy'
        CheckEqual '0' $runP.Exit 'placeholder run exit code'
        $pRow = $runP.Lines | Where-Object surface -eq 'placeholder'
        Check ($null -ne $pRow) 'placeholder row logged'
        CheckEqual $true $pRow.found "a placeholder row's own container was drawn and its RowContextMenu opened"
        CheckEqual $true $pRow.captured "the app's Opened handler captured the placeholder row"
        CheckEqual $true $pRow.disabled "the placeholder row's 'Open in Explorer' is disabled"

        # 14. unreadable: denied (C-012)
        & $closeNewWindows
        $runD = Invoke-RevealRun $root 'unreadable:denied'
        CheckEqual '0' $runD.Exit 'denied run exit code'
        $unrRow = $runD.Lines | Where-Object name -eq 'denied'
        Check ($null -ne $unrRow) 'denied was logged'
        CheckEqual $denied $unrRow.path 'denied clean path (not display string with Win32 message)'
        CheckEqual 'Exact' $unrRow.kind 'denied kind'
        $dSel = Wait-ExplorerSelection $root 'denied'
        Check $dSel.Matched 'denied selected in Explorer'

        # 15. top surface mid-scan live refresh (O-024) and stopped scan (C-012 / O-022 / O-028)
        # Sized to outlast the hook's stop via New-TopsTree
        $topCase = New-CaseFolder
        $topDenied = @()
        try {
            $topRoot = Join-Path $topCase 'top-root'
            $topDenied = New-TopsTree $topRoot
            $chosenTop = "a1-$Planted"
            $targetTop = Join-Path $topRoot $chosenTop

            # Mid-scan (B3-1 / O-024): the menu opened on a results-list row stays enabled after a live refresh has
            # detached that row's container, and its click reveals the row it was opened on, while the scan runs.
            & $closeNewWindows
            $runLive = Invoke-RevealRun $topRoot "top-live:$chosenTop"
            CheckEqual '0' $runLive.Exit 'top-live run exit code'
            $liveRow = $runLive.Lines | Where-Object surface -eq 'top-live'
            Check ($null -ne $liveRow) "top-live row was logged ($(Show ($runLive.Lines | ConvertTo-Json -Compress)))"
            CheckEqual $true $liveRow.detached 'a live refresh detached the container the menu was opened on'
            CheckEqual $true $liveRow.scanning 'the scan was still running when the container was detached'
            CheckEqual $true $liveRow.open 'the menu was still open after the refresh'
            CheckEqual $true $liveRow.enabled "'Open in Explorer' stayed enabled after the refresh"
            CheckEqual $targetTop $liveRow.path 'the click revealed the row the menu was opened on'
            CheckEqual 'Exact' $liveRow.kind 'top-live kind'
            $liveSel = Wait-ExplorerSelection $topRoot $chosenTop
            Check $liveSel.Matched 'top-live folder selected in Explorer'

            # Stopped scan reveal (O-022 / O-028)
            & $closeNewWindows
            $run2 = Invoke-RevealRun $topRoot "top:$chosenTop"
            CheckEqual '0' $run2.Exit 'run 2 exit code'
            $topRow = $run2.Lines | Where-Object surface -eq 'top'
            Check ($null -ne $topRow) 'top surface row was logged'
            CheckEqual $targetTop $topRow.path 'top folder resolved path'
            CheckEqual 'Exact' $topRow.kind 'top folder kind'
            $topSel = Wait-ExplorerSelection $topRoot $chosenTop
            Check $topSel.Matched 'top folder selected in Explorer'
        }
        finally {
            foreach ($f in $topDenied) { Undo-DenyListing $f }
            Remove-CaseFolder $topCase
        }
    }
    finally {
        # Close only hwnds that were not in baseline
        $sh = New-Object -ComObject Shell.Application
        foreach ($w in $sh.Windows()) {
            try {
                if ($w.HWND -notin $baselineHwnds) {
                    $w.Quit()
                }
            } catch { }
        }
        if ($denied) { Undo-DenyListing $denied }
        if ($dupDot -and [System.IO.File]::Exists($dupDot)) { [System.IO.File]::Delete($dupDot) }
        Remove-Links $links
        Remove-CaseFolder $case
    }
}

# ---- Run ----

$cases = if ($Case -eq 'All') { @('C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-012', 'C-013', 'C-014', 'O-016', 'L-001', 'L-002', 'L-003', 'B-001', 'B-002', 'B-003', 'B-004', 'B-005', 'B-006', 'B-007', 'B-008', 'V-001', 'P-001', 'T-001', 'T-002', 'T-003', 'T-004', 'T-005', 'T-006', 'K-001', 'K-002', 'K-003', 'R-001') + $NetworkCases } else { @($Case) }
$notRun = @()
if (-not $IncludeNetwork) {
    $notRun = @($cases | Where-Object { $_ -in $NetworkCases })
    $cases = @($cases | Where-Object { $_ -notin $NetworkCases })
}
if (@($cases | Where-Object { $_ -in $HookedCases }).Count -gt 0) {
    Update-Hook
}
if (@($cases | Where-Object { $_ -in $NetworkCases }).Count -gt 0) {
    Update-StallRig
}
if (@($cases | Where-Object { $_ -notin $RigOnlyCases }).Count -gt 0) {
    Update-App
}
$failed = @()
foreach ($key in $cases) {
    Write-Output $key
    $before = $script:Failures.Count
    try {
        & "Test-$($key -replace '-', '')"
    }
    catch {
        Write-Output "  FAIL  $($_.Exception.Message)"
        $script:Failures.Add("$key threw: $($_.Exception.Message)")
    }
    if ($script:Failures.Count -gt $before) {
        $failed += $key
    }
}

if ($notRun.Count -gt 0) {
    Write-Output "NOT RUN: $($notRun -join ', ') (they change this machine's SMB mappings; pass -IncludeNetwork to run them)"
}
if ($failed.Count -gt 0) {
    Write-Output "FAILED: $($failed -join ', ')"
    exit 1
}
if ($cases.Count -eq 0) {
    exit 2 # a network case named without -IncludeNetwork: nothing ran, so nothing passed
}
Write-Output "PASSED: $($cases -join ', ')"
exit 0
