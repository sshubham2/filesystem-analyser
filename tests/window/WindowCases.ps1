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
# NTFS volume with clusters of 64 KiB or less, without asking the volume, in a folder that is not
# NTFS-compressed: New-CaseFolder refuses one that is, by name (HF-027).
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
# second scan over an open large folder; its bars are timings on a shared machine, so like M-001 it runs only
# when named, never in All (HF-010). T-006 is the endings that show no tree. C-002, L-003, B-003, B-007 and
# M-001 read the tree where they read the results list after a completed scan. T-007 and T-008 (SL-082) are the row of
# a folder whose listing failed partway: the hook arms Core's listing failpoint before the app's Main
# (FSA_HOOK_FAIL_LISTING), which fails one named folder's listing at a page of its own, and T-007 reads that row
# beside a folder read in full and one denied outright; T-008 is a value the hook cannot arm, which ends the app
# before its Main.
#
# K-001 to K-003 (SL-046) are the files by kind, a view of the results cell chosen beside its header: the report's
# "kinds" (per kind its id, label and totals, never an extension or a name) against a planted tree of seven kinds, and
# the view on screen once the hook's kinds mode has chosen it, at the default and the minimum size, and against a tree
# with no file (K-001); added up against the root after Stop (K-002, the hook's stop mode); and gone from the report and
# the list as a second scan starts and at its Could not read ending (K-003, the hook's rescan mode, as B-006). B-001
# checks a missing root's Could not read ending carries no kinds. The Scan failed ending (Scanner.Scan throws) takes the
# same path in the window, ShowProblem, and no case drives it: no hook mode makes the scan fail.
#
# S-001 to S-009 (SL-065) are the outcome's sections, one tab each of OutcomeTabs: the largest files, and the unreadable
# folders only when there are any. S-001 is the default state at the window's minimum size (Largest files selected, the
# fit with its 10 px to spare); S-002 selects the unreadable folders and goes back (the hook's expand mode, with
# FSA_HOOK_BACK); S-003 starts a second scan with the unreadable folders selected, whose result has none; S-004 hides the
# largest files while the unreadable folders are shown (the hook's hide mode); S-005 is an ending with no section and a
# long text; S-006 a long text above the sections; S-007 a five-digit count of unreadable folders in the header at the
# minimum width; S-008 the lists at the minimum size and maximized; S-009 a second scan that refills a list whose tab is
# not selected. The expand mode of B-003, B-007 and B-008 selects the unreadable folders' tab where it opened their
# expander before SL-065, and the report's UnreadableExpander, kept, is read from that tab.
#
# R-001 (SL-047) and R-002 (SL-088) are 'Open in Explorer', through the hook's reveal mode, checked in Explorer's own
# windows through Shell.Application: a landing is the item's parent with the item as the only selection, both sides
# resolved to long names (U-114). They open real Explorer windows, and close only the ones they opened on their own
# trees. R-002 is the reveal at 161, 300 and 1000 characters, into folders this user cannot open, timed from the click,
# and refused with nothing opened when the item's folder was deleted after the scan; -Step a,b,... runs some of its
# steps only (see Test-R002).

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-012', 'C-013', 'C-014', 'O-016',
        'L-001', 'L-002', 'L-003', 'M-001', 'B-001', 'B-002', 'B-003', 'B-004', 'B-005', 'B-006', 'B-007', 'B-008', 'V-001', 'P-001', 'N-001', 'N-002', 'N-003', 'N-004',
        'T-001', 'T-002', 'T-003', 'T-004', 'T-005', 'T-006', 'T-007', 'T-008', 'K-001', 'K-002', 'K-003', 'R-001', 'R-002',
        'S-001', 'S-002', 'S-003', 'S-004', 'S-005', 'S-006', 'S-007', 'S-008', 'S-009', 'X-001', 'X-002', 'X-003', 'X-004')]
    [string] $Case,
    [switch] $IncludeNetwork,
    # R-002's steps to run, of a to e, comma-separated (all of them when left out).
    [ValidatePattern('^[a-e](,[a-e])*$')]
    [string] $Step
)

$ErrorActionPreference = 'Stop'
if ($Step -and $Case -ne 'R-002') { throw '-Step names steps of R-002 and is for -Case R-002 only' }
$R002Steps = if ($Step) { $Step -split ',' } else { @() }

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe'
$Reader = Join-Path $Repo 'eng\read-window.ps1'
$HookProject = Join-Path $Repo 'tests\window\FsaWindowHook\FsaWindowHook.csproj'
$HookDll = Join-Path $Repo 'artifacts\window-hook\FsaWindowHook.dll'
$RigProject = Join-Path $Repo 'tests\window\StallRig\StallRig.csproj'
$RigExe = Join-Path $Repo 'artifacts\stall-rig\StallRig.exe'
$HookVariables = 'DOTNET_STARTUP_HOOKS', 'FSA_HOOK_MODE', 'FSA_HOOK_LOG', 'FSA_HOOK_ARMED', 'FSA_HOOK_CANCEL_DELAY_MS', 'FSA_HOOK_CLOSE_DELAY_MS',
    'FSA_HOOK_SLOW_REFRESH_MS', 'FSA_HOOK_MISSING', 'FSA_HOOK_EXPAND', 'FSA_HOOK_MINSIZE', 'FSA_HOOK_DRIVES',
    'FSA_HOOK_WALK', 'FSA_HOOK_RECYCLE', 'FSA_HOOK_MULTIPLY', 'FSA_HOOK_SELECT', 'FSA_HOOK_RELEASE', 'FSA_HOOK_GAP', 'FSA_HOOK_HEIGHT', 'FSA_HOOK_REVEAL', 'FSA_HOOK_STATUS',
    'FSA_HOOK_MAXIMIZE', 'FSA_HOOK_BACK', 'FSA_HOOK_READABLE', 'FSA_HOOK_BEFORE', 'FSA_HOOK_AFTER', 'FSA_HOOK_RENAMED', 'FSA_HOOK_RENDER', 'FSA_HOOK_FAIL_LISTING', 'FSA_HOOK_REVEAL_GATE',
    'FSA_HOOK_AT_FOLDERS', 'FSA_HOOK_PAST', 'FSA_HOOK_PAST_FOLDERS', 'FSA_HOOK_AT_SIZED', 'FSA_HOOK_TRIGGER_DEADLINE_S'
$HookedCases = 'C-002', 'C-006', 'C-012', 'O-016', 'L-001', 'L-002', 'M-001', 'B-003', 'B-005', 'B-006', 'B-007', 'B-008', 'N-001', 'N-002',
    'T-001', 'T-002', 'T-003', 'T-004', 'T-005', 'T-006', 'T-007', 'T-008', 'K-001', 'K-002', 'K-003', 'R-001', 'R-002',
    'S-001', 'S-002', 'S-003', 'S-004', 'S-005', 'S-006', 'S-007', 'S-008', 'S-009', 'X-003'
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

# A folder made under an NTFS-compressed one is compressed too, and there a file of zeros takes nothing on disk: every
# on-disk check would fail as if the app had miscounted (HF-027). Such a case folder is refused by name, and removed.
# The refusal names the folder it was made in, which carries the attribute (fsa-window-cases, made compressed under a
# compressed %TEMP% and left so), and the one remedy measured to clear it and all it holds: compact /u /s:"folder".
# Plain compact /u, or /u /s "folder", clears that folder's own attribute only (SL-073 review, R2-m2).
function New-CaseFolder {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-window-cases\{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($folder)
    if ([System.IO.File]::GetAttributes($folder).HasFlag([System.IO.FileAttributes]::Compressed)) {
        [System.IO.Directory]::Delete($folder)
        $parent = Split-Path -Parent $folder
        throw "$folder came out NTFS-compressed and was removed: $parent is NTFS-compressed, so every case folder made in it is too, and the window cases take a file's size on disk to be its size. Uncompress it and all it holds with compact /u /s:`"$parent`", or point TEMP and TMP at a folder that is not compressed."
    }
    $folder
}

# Bytes that do not compress (TempTree's pattern), so the file takes its own size on disk in a compressed folder too.
function New-File([string] $path, [long] $bytes) {
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    $content = [byte[]]::new($bytes)
    [System.Random]::new(20).NextBytes($content)
    [System.IO.File]::WriteAllBytes($path, $content)
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
# spaces and quotes nothing. $environment (SL-080) is given to this one process alone (-Environment, pwsh 7.4+),
# never set in this script's own $env:, so no build or other process it starts ever sees it.
function Start-App($arguments, [string] $workingDirectory = [System.IO.Path]::GetTempPath(), [hashtable] $environment = @{}) {
    $more = if ($environment.Count -gt 0) { @{ Environment = $environment } } else { @{} }
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WorkingDirectory $workingDirectory -PassThru @more
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

# C-001: a tree that finishes inside one timer tick. It has no unreadable folder, so the outcome's sections are the
# largest files alone, selected; the unreadable folders' tab, and the expander key the report keeps for it, are not
# on screen (SL-065).
function Test-C001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'small'
        $manifest = New-SmallTree $root
        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual $true $window.OutcomeTabs.visible 'OutcomeTabs is on screen'
        CheckEqual 'LargestFilesTab' $window.OutcomeTabs.selected 'OutcomeTabs.selected'
        CheckEqual $true $window.LargestFilesTab.selected 'LargestFilesTab is selected'
        CheckEqual $false $window.UnreadableTab.visible 'UnreadableTab is not shown: no folder was unreadable'
        CheckEqual $false $window.UnreadableExpander.visible 'UnreadableExpander (the key kept for that tab) reads not shown'
        CheckEqual $false $window.UnreadableExpander.expanded 'UnreadableExpander reads not expanded'
        CheckEqual $false $window.UnreadableList.inView 'UnreadableList is not on screen'
        CheckEqual 1 $window.OutcomeTabs.stripRows 'OutcomeTabs.stripRows'
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

# C-008: a report from an earlier run sits at the target path: writable, read-only, or held open.
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

        # Read-only, both of them (SL-070, HF-018): the delete-first step still deletes them, so this exit 2 leaves no
        # file at the path either.
        Set-Content -LiteralPath $report -Value $stale
        Set-Content -LiteralPath "$report.tmp" -Value $stale
        foreach ($file in $report, "$report.tmp") { (Get-Item -LiteralPath $file).Attributes = 'ReadOnly' }
        CheckEqual '2' (Invoke-App @('--report', $report)) 'no root, read-only: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'no root, read-only: the stale report is gone'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'no root, read-only: the stale .tmp is gone'

        # Held open by another program that does not share delete: nothing can delete it, so the run exits 3 and the
        # stale report stays, the one file a non-zero exit may leave (WindowReport.cs).
        Set-Content -LiteralPath $report -Value $stale
        $held = [System.IO.File]::Open($report, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        try {
            CheckEqual '3' (Invoke-App @('--report', $report)) 'no root, held open: exit code'
        }
        finally { $held.Dispose() }
        Check (Test-Path -LiteralPath $report) 'no root, held open: the stale report is still there'

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
# laid out inside the panel when it has rows, and no planted name anywhere in the report (Q-016). With
# $selected false another section's tab is selected (SL-065): the section is then off screen, caption and list.
function CheckLargestFiles($read, [int] $shown, $firstOnDisk, [string] $caption, [bool] $selected = $true) {
    $report = $read.Report
    $window = $report.window
    CheckEqual $shown $report.largestFilesShown 'largestFilesShown (rows bound to LargestFilesList)'
    CheckEqual $firstOnDisk $report.largestFileOnDisk 'largestFileOnDisk (the first row, against the tree)'
    Check ($null -ne $report.largestFilesMs -and $report.largestFilesMs -ge 0) "largestFilesMs was measured ($(Show $report.largestFilesMs))"
    CheckEqual $true $window.LargestFilesTab.visible 'LargestFilesTab is on screen'
    CheckEqual $selected $window.LargestFilesTab.selected "LargestFilesTab is $(if ($selected) { '' } else { 'not ' })selected"
    CheckEqual $selected $window.LargestFilesPanel.visible "LargestFilesPanel is $(if ($selected) { 'on screen' } else { 'off screen: its tab is not selected' })"
    Check ($window.LargestFilesPanel.actualHeight -gt 0) "LargestFilesPanel has been laid out (actualHeight $($window.LargestFilesPanel.actualHeight))"
    CheckEqual $caption $window.LargestFilesCaption 'LargestFilesCaption'
    CheckEqual ($selected -and $shown -gt 0) $window.LargestFilesList.inView "LargestFilesList is on screen inside the panel exactly when it has rows$(if (-not $selected) { ' and its tab is selected' })"
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
        CheckLargestFiles $read 2 (2 * $KiB64) 'Top 2 of 2 files scanned. Files in 1 folder that could not be read (in full) may be missing.'
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
        $caption = 'Top 100 of 105 files scanned. Files in 12 folders that could not be read (in full) may be missing.'

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
        # Since SL-065 the two lists are two tabs, one on screen at a time: the unreadable folders' now.
        CheckLargestFiles $read 100 (16 * $KiB64) $caption $false
        CheckEqual $true $window.UnreadableExpander.expanded 'the report: UnreadableExpander is open (their tab is selected)'
        CheckEqual $true $window.UnreadableExpander.inView 'the report: UnreadableExpander is inside the panel'
        CheckEqual $true $window.UnreadableList.inView 'the report: UnreadableList is inside the panel'
        CheckEqual $true $window.FolderTree.inView "the report: FolderTree is inside the window below the outcome ($($window.FolderTree.actualHeight) px)"
        $built = $window.LargestFilesList.realizedRows
        Check ($built -gt 0 -and $built -lt 100) "LargestFilesList built only a screenful before its tab was left: $built of 100 rows"
        CheckEqual 0 $window.LargestFilesList.fullRows 'LargestFilesList shows no row: its tab is not selected'
        Check ($window.UnreadableList.fullRows -ge 1) "UnreadableList keeps at least one full row in view ($($window.UnreadableList.fullRows) in $($window.UnreadableList.actualHeight) px)"
        CheckTabsKeptHeight $run.Log $window
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

# After an ending, the outcome's selected section and the results list below it (SL-039), or the folder tree in its
# place after a completed scan (SL-044), are on screen together, none pushed off the window by another or by any text
# (O-025), and each with at least one full row in view: a row wholly inside the list's viewport, readable
# without scrolling (the window-size rework). The outcome's texts are in view too, scrolling whatever does not fit.
# Since SL-065 the outcome's two lists are two tabs: $opened is the unreadable folders' selected (their expander open,
# before), and then the largest files' list is off screen, with no full row, whatever height it last had. Either list
# is inside the tabs, and the headers take one row.
function CheckBothLists($window, [bool] $opened, [bool] $completed) {
    $tabs = $window.OutcomeTabs
    CheckEqual $true $tabs.visible 'OutcomeTabs is on screen'
    CheckEqual $(if ($opened) { 'UnreadableTab' } else { 'LargestFilesTab' }) $tabs.selected 'OutcomeTabs.selected'
    CheckEqual 1 $tabs.stripRows 'the tab headers take one row (OutcomeTabs.stripRows)'
    $largest = $window.LargestFilesList
    $shown = if ($opened) { $window.UnreadableList } else { $largest }
    $hidden = if ($opened) { $largest } else { $window.UnreadableList }
    $shownName = if ($opened) { 'UnreadableList' } else { 'LargestFilesList' }
    $hiddenName = if ($opened) { 'LargestFilesList' } else { 'UnreadableList' }
    CheckEqual $true $shown.inView "$shownName is inside the window ($($shown.actualHeight) px)"
    Check ($shown.fullRows -ge 1) "$shownName shows at least one full row ($($shown.fullRows))"
    Check ($shown.actualHeight -le $tabs.actualHeight) "$shownName is no taller than the tabs ($($shown.actualHeight) of $($tabs.actualHeight) px): its height is bounded"
    CheckEqual $false $hidden.inView "$hiddenName is off screen: its tab is not selected"
    CheckEqual 0 $hidden.fullRows "$hiddenName shows no full row (last laid out at $($hidden.actualHeight) px)"
    if (-not $opened) {
        Check ($largest.realizedRows -lt 100) "LargestFilesList virtualizes ($($largest.realizedRows) rows built)"
    }
    CheckEqual $true $window.TopLevelPanel.visible 'TopLevelPanel is on screen'
    $results = if ($completed) { $window.FolderTree } else { $window.TopLevelList }
    $name = if ($completed) { 'FolderTree' } else { 'TopLevelList' }
    CheckEqual $true $results.inView "$name is inside the window ($($results.actualHeight) px)"
    Check ($results.fullRows -ge 1) "$name shows at least one full row ($($results.fullRows) of $($results.realizedRows) built)"
    CheckEqual (-not $completed) $window.TopLevelList.inView "TopLevelList is $(if ($completed) { 'not shown: the tree took its place' } else { 'shown' })"
    CheckEqual $opened $window.UnreadableExpander.expanded "UnreadableExpander is $(if ($opened) { 'open' } else { 'closed' })"
    CheckEqual $true $window.UnreadableExpander.inView 'UnreadableExpander is inside the window'
    CheckEqual $true $window.UnreadableTab.inView 'UnreadableTab, its header, is inside the window'
    $texts = $window.OutcomeTexts
    CheckEqual $true $texts.inView "the outcome's texts are inside the window ($($texts.actualHeight) of $($texts.extentHeight) px shown, the rest a scroll away)"
}

# SL-065: the hook's expand mode logged the sections' height as it selected the unreadable folders' tab, and the report
# reads it after: only the selected section is laid out, so the other one adds nothing and the height does not move.
# Not compared when the hook put the reveal status line on screen (FSA_HOOK_STATUS): that comes after the click, as the
# drive buttons come in, and takes its own share of the window's height between the two readings (16.5 px of the
# outcome's after a stop, measured). Every case that shows the line also runs the same ending without it.
function CheckTabsKeptHeight($log, $window) {
    $drives = @($log | Where-Object { $_.StartsWith('drives in ') })
    if ($drives.Count -eq 1 -and (Field $drives[0] 'status') -ceq 'True') {
        Write-Output "        OutcomeTabs' height across the click is not compared here: the reveal status line came on screen between the two readings"
        return
    }
    $acted = @($log | Where-Object { $_.StartsWith('acted mode=expand ') })
    $before = if ($acted.Count -eq 1) { Field $acted[0] 'tabsHeight' } else { $null }
    $after = $window.OutcomeTabs.actualHeight
    Check ($null -ne $before -and [Math]::Abs([double]$before - [double]$after) -lt 0.01) "OutcomeTabs kept its height when the unreadable folders' tab was selected ($before px before, $after px after)"
}

# SL-065: the hook maximized the window when it loaded (FSA_HOOK_MAXIMIZE), and the ending was laid out maximized,
# larger than the window's minimum both ways.
function CheckMaximized($log) {
    $drives = @($log | Where-Object { $_.StartsWith('drives in ') })
    $line = if ($drives.Count -eq 1) { $drives[0] } else { '' }
    Check (@($log | Where-Object { $_.StartsWith('maximized ') }).Count -eq 1 -and (Field $line 'state') -ceq 'Maximized' -and
        [int](Field $line 'windowWidth') -gt 560 -and [int](Field $line 'windowHeight') -gt 644) "the hook maximized the window, and the ending was laid out so ($(Show $line))"
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
function Invoke-StoppedEnding([string] $case, [string] $root, [bool] $opened, [bool] $smallest, [int] $extra, [string] $statusMsg = $null, [bool] $maximized = $false) {
    $script:StoppedReport = $null
    $variables = @{}
    if ($smallest) { $variables.FSA_HOOK_MINSIZE = '1' }
    if ($maximized) { $variables.FSA_HOOK_MAXIMIZE = '1' }
    if ($extra -gt 0) { $variables.FSA_HOOK_DRIVES = "$extra" }
    if ($opened) { $variables.FSA_HOOK_EXPAND = '1' }
    if ($statusMsg) { $variables.FSA_HOOK_STATUS = $statusMsg }
    $run = Invoke-HookedRun 'watch' $root (Join-Path $case 'report.json') $variables
    $before = $script:Failures.Count
    CheckActed $run 'watch'
    if ($opened) { CheckActed $run 'expand' }
    if ($smallest) { CheckSized $run.Log }
    if ($maximized) { CheckMaximized $run.Log }
    if ($smallest -or $maximized -or $extra -gt 0) { CheckDrives $run.Log $extra }
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
    CheckLargestFiles $read 100 $KiB64 "Top 100 of $files scanned in the part read before the scan stopped. Files in 12 folders that could not be read (in full) may be missing." (-not $opened)
    CheckTopLevel $run.Report.topLevel @{ rows = 36; unreadable = 12; final = $true } 'against the tree'
    Check ([int]$run.Report.topLevel.notRead -ge 1) "some slow folders were not read before the stop ($($run.Report.topLevel.notRead))"
    CheckBothLists $run.Report.window $opened $false
    if ($opened) { CheckTabsKeptHeight $run.Log $run.Report.window }
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
        $unread = ' Files in 12 folders that could not be read (in full) may be missing.'

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
                CheckLargestFiles $read 100 $KiB64 "Top 100 of 104 files scanned.$unread" (-not $opened)
                Check ($read.Text -notmatch '"topLevel"') 'no topLevel in the report: the folder tree shows the completed scan (SL-044)'
                CheckBothLists $read.Report.window $opened $true
                if ($opened) { CheckTabsKeptHeight $run.Log $read.Report.window }
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
                CheckTabsKeptHeight $run.Log $read.Report.window
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
# TIMEOUT) and the hook's lines, raw and as objects. $environment goes to the app's process alone (Start-App).
function Invoke-TreeRun([string] $root, [hashtable] $variables = @{}, [int] $timeoutSec = 120, [hashtable] $environment = @{}) {
    $run = Invoke-WithHook 'tree' $HookDll { Wait-App (Start-App "`"$root`"" ([System.IO.Path]::GetTempPath()) $environment) $timeoutSec } $variables
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

# T-005 (row 6, review m7; named only, HF-010): the UI thread sampled by the hook every 10 ms (an Input-priority probe) through a scan
# while the window is moved and resized, the hand-off to the tree, one open of a folder of 100,000 files, and a
# second scan started over that open tree, to its hand-off. In every phase but the open, whose own bar is T-003's,
# no probe may wait a second, and where a phase has at least 20 probes, 95% of them must get through within
# 200 ms. With fewer, the 95th percentile is the longest wait itself, and one stall already there before SL-044
# (the first resize after a scan starts, 0.4 to 0.8 s, the code review's M1) would fail the phase on the second
# bar rather than the first; so a phase that short is held to the second alone, and says so. Timing on a shared
# machine, as M-001's: a failure here is a measurement to record, not proof of a defect by itself.
$GapP95Floor = 20

# SL-080: T-005 as a measurement of its own, for tests/window/T005Batch.ps1. Each variable is off unless set, and
# none changes what T-005 checks.
#   FSA_T005_HEIGHT=<px>     the window made that tall as it is made, before it has a window handle, so it is never
#                            resized before the hook's first move at any height: the hook's FSA_HOOK_HEIGHT, which
#                            refuses a height the window does not take
#   FSA_T005_TRACE=<folder>  the app traced by EventPipe from its start (CPU samples, with rundown) into that folder as
#                            t005-<run>-<pid>.nettrace; the variables go to the app's process alone (Start-App)
#   FSA_T005_SAMPLE_MS=<n>   with a trace, a sample every n ms, not every 1 ms: DOTNET_EventPipeThreadSamplingRate, which
#                            runtime 10.0.12 reads as milliseconds in hexadecimal (measured at SL-080's build: A gave
#                            10.1 ms between samples, 5 gave 5.1 ms, 2710 held the app's exit 7 s)
#   FSA_T005_RENDER=software WPF draws the app in software: the hook's FSA_HOOK_RENDER, for T005Batch.ps1 -Plan Render
#   FSA_T005_TAG=<json>      {run, pair, position, warmup, arm, idleGapS} from the batch, copied into the summary line
#   FSA_T005_SUMMARY=<file>  one JSON line appended for the run (ConvertTo-T005Summary), before T-005's own checks, so a
#                            run that fails a bar still gives its numbers
$T005TraceProviders = 'Microsoft-DotNETCore-SampleProfiler:0xF00000000000:5,Microsoft-Windows-DotNETRuntime:0x00000014C14FCCBD:4'

function New-T005Trace($tag) {
    if (-not $env:FSA_T005_TRACE) { return [pscustomobject]@{ Environment = @{}; Folder = $null; Prefix = $null } }
    # Absolute: the app runs in %TEMP%, where a relative folder would be another one, and no trace would be found
    # (SL-080 code review R2-M2).
    $folder = [System.IO.Path]::GetFullPath($env:FSA_T005_TRACE, (Get-Location).ProviderPath)
    [void][System.IO.Directory]::CreateDirectory($folder)
    $name = if ($null -ne $tag -and $null -ne $tag.run) { $tag.run } else { [guid]::NewGuid().ToString('N').Substring(0, 8) }
    $prefix = "t005-$name-"
    $environment = @{
        DOTNET_EnableEventPipe          = '1'
        DOTNET_EventPipeOutputPath      = Join-Path $folder "$prefix{pid}.nettrace" # the runtime puts its pid in
        DOTNET_EventPipeConfig          = $T005TraceProviders
        DOTNET_EventPipeOutputStreaming = '1'
        DOTNET_EventPipeRundown         = '1'
    }
    if ($env:FSA_T005_SAMPLE_MS) { $environment.DOTNET_EventPipeThreadSamplingRate = '{0:X}' -f [int]$env:FSA_T005_SAMPLE_MS }
    [pscustomobject]@{ Environment = $environment; Folder = $folder; Prefix = $prefix }
}

# The run's own trace, by the pid the hook logged; null when there is none or it is empty.
function Find-T005Trace($trace, $run) {
    if ($null -eq $trace.Folder) { return $null }
    $loaded = @($run.Log | Where-Object { $_ -cmatch '^loaded mode=tree pid=(\d+)' })
    if ($loaded.Count -eq 0 -or $loaded[0] -notmatch 'pid=(\d+)') { return $null }
    $path = Join-Path $trace.Folder "$($trace.Prefix)$($Matches[1]).nettrace"
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -gt 0) { $path } else { $null }
}

function Value-Of($object, [string] $name) { if ($null -eq $object) { $null } else { $object.$name } }

# One T-005 run as the batch reads it. A phase the hook never sampled (no gap line, or no probe in it) is written
# with reached false and null figures, never 0. primaryMs, the one figure a batch decides on, is the larger of the
# scan's and the rescan's longest wait, and null when either is. exitReason is 'closed' only when the hook closed
# the window, logged no error, and the app exited 0.
function ConvertTo-T005Summary($run, $tag, [string] $startedUtc, $traceFile) {
    $lines = @($run.Lines)
    $first = { param($kind) @($lines | Where-Object { $_.kind -ceq $kind }) | Select-Object -First 1 }
    $facts = & $first 'facts'
    $sized = & $first 'sized'
    $errors = @($lines | Where-Object { $_.kind -ceq 'error' })
    $exitReason = if ($run.Exit -eq 'TIMEOUT') { 'timeout' }
    elseif ($errors.Count -gt 0) { 'hook-error: ' + (($errors | ForEach-Object reason) -join ' | ') }
    elseif (@($lines | Where-Object { $_.kind -ceq 'closed' }).Count -eq 0) { 'no-close' }
    elseif ([string]$run.Exit -ne '0') { "exit $($run.Exit)" }
    else { 'closed' }
    $endings = @{ scan = (& $first 'ending'); rescan = (& $first 'second-ending') }
    $phases = foreach ($name in 'scan', 'handoff', 'open', 'rescan', 'rescan-handoff') {
        $gap = @($lines | Where-Object { $_.kind -ceq 'gap' -and $_.phase -ceq $name }) | Select-Object -First 1
        $reached = $null -ne $gap -and $null -ne $gap.max -and [int]$gap.probes -gt 0
        $ending = $endings[$name]
        [ordered]@{
            name          = $name
            reached       = $reached
            longestWaitMs = if ($reached) { [double]$gap.max } else { $null }
            maxAt         = if ($reached) { [double]$gap.maxAt } else { $null }
            maxEndAt      = if ($reached -and $null -ne $gap.maxEndAt) { [double]$gap.maxEndAt } else { $null }
            p95           = if ($reached) { [double]$gap.p95 } else { $null }
            probes        = if ($null -ne $gap) { [int]$gap.probes } else { $null }
            longestMoveMs = if ($null -ne $ending) { [double]$ending.longestMoveMs } else { $null }
            moves         = if ($null -ne $ending) { [int]$ending.moves } else { $null }
            firstMoveAt   = if ($null -ne $ending) { $ending.firstMoveAt } else { $null }
        }
    }
    $scan = @($phases | Where-Object { $_.name -eq 'scan' })[0].longestWaitMs
    $rescan = @($phases | Where-Object { $_.name -eq 'rescan' })[0].longestWaitMs
    [ordered]@{
        run                            = Value-Of $tag 'run'
        pair                           = Value-Of $tag 'pair'
        position                       = Value-Of $tag 'position'
        warmup                         = Value-Of $tag 'warmup'
        arm                            = Value-Of $tag 'arm'
        idleGapS                       = Value-Of $tag 'idleGapS'
        startedUtc                     = $startedUtc
        exitCode                       = [string]$run.Exit
        exitReason                     = $exitReason
        askedHeight                    = Value-Of $sized 'asked'
        actualHeight                   = Value-Of $sized 'actual'
        uiThread                       = Value-Of $facts 'uiThread'
        priorityClass                  = Value-Of $facts 'priorityClass'
        runtime                        = Value-Of $facts 'runtime'
        renderMode                     = Value-Of $facts 'renderMode'
        renderTier                     = Value-Of $facts 'renderTier'
        pid                            = Value-Of $facts 'pid'
        clockOriginUtc                 = Value-Of $facts 'clockOriginUtc'
        clockOriginSinceProcessStartMs = Value-Of $facts 'clockOriginSinceProcessStartMs'
        traceFile                      = $traceFile
        primaryMs                      = if ($null -ne $scan -and $null -ne $rescan) { [Math]::Max($scan, $rescan) } else { $null }
        primaryPhase                   = if ($null -ne $scan -and $null -ne $rescan) { if ($rescan -gt $scan) { 'rescan' } else { 'scan' } } else { $null }
        phases                         = @($phases)
    }
}

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

        $variables = @{ FSA_HOOK_WALK = '0'; FSA_HOOK_GAP = 'big' }
        if ($env:FSA_T005_HEIGHT) { $variables.FSA_HOOK_HEIGHT = $env:FSA_T005_HEIGHT }
        if ($env:FSA_T005_RENDER) { $variables.FSA_HOOK_RENDER = $env:FSA_T005_RENDER }
        $tag = if ($env:FSA_T005_TAG) { $env:FSA_T005_TAG | ConvertFrom-Json } else { $null }
        $trace = New-T005Trace $tag
        $startedUtc = [DateTime]::UtcNow.ToString('o')
        $run = Invoke-TreeRun $root $variables 300 $trace.Environment
        if ($env:FSA_T005_SUMMARY) {
            $summary = ConvertTo-T005Summary $run $tag $startedUtc (Find-T005Trace $trace $run)
            [System.IO.File]::AppendAllText([System.IO.Path]::GetFullPath($env:FSA_T005_SUMMARY, (Get-Location).ProviderPath), ($summary | ConvertTo-Json -Compress -Depth 6) + "`n")
            Write-Output ("        summary: {0}, primary {1} ms ({2}), trace {3}" -f $summary.exitReason, $summary.primaryMs, $summary.primaryPhase, (Show $summary.traceFile))
        }
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

# ---- The folder whose listing failed partway (SL-082) ----

# New-PartwayTree's two listed folders each hold this many files of this size. At 46 characters a name, a listing
# record (FILE_ID_EXTD_DIR_INFO, 88 bytes and the name, to 8) takes 184 bytes, so one 64 KiB page lists about 355 of
# them and 2,000 take six pages: a fault at the second page keeps the first page's, under the 1,000 children WALK
# opens (review m4).
$PartwayFiles = 2000
$PartwayFileBytes = 4KB

# T-007's tree under $root: Q (a-<tag>) and P (p-<tag>), $PartwayFiles files each, and R (r-<tag>), empty and denied
# list-folder as Deny-Listing does. Q sorts first, and the scan lists folders in listing order, so Q is listed before
# P (review M1). Returns the three names, the tag the files' names carry, and R's path for the caller to give back.
function New-PartwayTree([string] $root) {
    $tag = [guid]::NewGuid().ToString('N')
    $content = [byte[]]::new($PartwayFileBytes)
    [System.Random]::new(20).NextBytes($content)
    foreach ($name in "a-$tag", "p-$tag") {
        $folder = Join-Path $root $name
        [void][System.IO.Directory]::CreateDirectory($folder)
        for ($i = 0; $i -lt $PartwayFiles; $i++) {
            [System.IO.File]::WriteAllBytes((Join-Path $folder ('file-{0:D4}-{1}.bin' -f $i, $tag)), $content)
        }
    }
    $denied = Join-Path $root "r-$tag"
    [void][System.IO.Directory]::CreateDirectory($denied)
    Deny-Listing $denied
    [pscustomobject]@{ Q = "a-$tag"; P = "p-$tag"; R = "r-$tag"; Tag = $tag; Denied = $denied }
}

# T-007 (SL-082): the row of a folder whose listing failed partway, which no ordinary user can make happen. The hook
# arms Core's listing failpoint before the app's Main (FSA_HOOK_FAIL_LISTING, see StartupHook.cs) so that P's listing
# fails at its second page with error 995, and its tree mode walks the tree to depth 2. The fault fired once, on P,
# during the scan. P keeps its first page: its row reads 'could not be read in full', has an expander, is open, and
# opens onto exactly the children it charges, each drawn by a container of its own, more of them than the viewport
# holds (review M2): its size is that many files, and it ranks by it. Q, as large, listed before P and with no fault
# armed against it, is read in full, so a failpoint that ignored the name or counted pages across folders fails it
# (review M1). R, denied outright, reads 'could not be opened' with no expander. The root is P and Q together.
function Test-T007 {
    $case = New-CaseFolder
    $built = $null
    try {
        $root = Join-Path $case 'tree'
        $built = New-PartwayTree $root
        $n = $PartwayFiles
        $run = Invoke-TreeRun $root @{ FSA_HOOK_WALK = '2'; FSA_HOOK_FAIL_LISTING = "$($built.P)|2|995" }
        $log = @($run.Log)
        $armed = @($log | Where-Object { $_.StartsWith('fail-listing ') -and -not $_.StartsWith('fail-listing fired ') })
        Check ($armed.Count -eq 1 -and $armed[0] -ceq "fail-listing armed $($built.P) page 2 error 995" -and $log[0] -ceq $armed[0]) "the hook armed the listing failpoint for P before the app's Main, its first line ($(Show ($armed -join ' | ')))"
        if ($armed.Count -ne 1 -or $armed[0] -notlike 'fail-listing armed *') {
            foreach ($line in $log) { Write-Output "        hook: $line" }
            Write-Output "        exit $(Show-Exit $run.Exit)"
            return
        }
        $before = $script:Failures.Count
        CheckTreeRun $run $root
        if ($script:Failures.Count -gt $before) { return }

        $fired = @($log | Where-Object { $_.StartsWith('fail-listing fired ') })
        Check ($fired.Count -gt 0) 'the failpoint fired: P''s listing reached a second page (if not, P fits one page and nothing below is a folder read partway)'
        $endingAt = [array]::IndexOf($log, @($log | Where-Object { $_.StartsWith('{"kind":"ending"') })[0])
        Check ($fired.Count -eq 1 -and $fired[0] -ceq "fail-listing fired $($built.P) page 2" -and [array]::IndexOf($log, $fired[0]) -lt $endingAt) "the failpoint fired once, on P at its second page, during the scan ($(Show ($fired -join ' | ')))"

        $rows = @(Lines-Of $run 'tree-row')
        $top = @($rows | Where-Object depth -eq 1 | Sort-Object order)
        CheckEqual "$($built.Q)|$($built.P)|$($built.R)" (($top | ForEach-Object name) -join '|') 'the root''s rows are Q, P, R: largest on disk first, P charged only what was listed'
        $q = @($top | Where-Object name -ceq $built.Q)[0]
        $p = @($top | Where-Object name -ceq $built.P)[0]
        $r = @($top | Where-Object name -ceq $built.R)[0]
        if ($null -eq $q -or $null -eq $p -or $null -eq $r) {
            Check $false "the hook read the rows of Q, P and R ($(Show (($top | ForEach-Object name) -join ', ')))"
            return
        }

        $bad = [System.Collections.Generic.List[string]]::new()
        if ($null -eq $q.rendered) { $bad.Add('no container drew it') }
        elseif ($q.rendered.note -cne '' -or $q.rendered.expander -ne $true) { $bad.Add("note $(Show $q.rendered.note), expander $($q.rendered.expander)") }
        if ([int]$q.childCount -ne $n -or [long]$q.size -ne $n * $PartwayFileBytes -or [long]$q.onDisk -ne $n * $PartwayFileBytes) { $bad.Add("childCount $($q.childCount), size $($q.size), onDisk $($q.onDisk)") }
        Check ($bad.Count -eq 0) "Q, listed before P with no fault armed against it, was read in full: no note, an expander, $n children of $(Format-Size $PartwayFileBytes) ($(Show ($bad -join '; ')))"

        $k = [int]$p.childCount
        Write-Output ("        P kept {0:N0} of its {1:N0} files; the tree's viewport is {2:N0} px" -f $k, $n, @(Lines-Of $run 'ending')[0].viewportHeight)
        $bad = [System.Collections.Generic.List[string]]::new()
        if ($null -eq $p.rendered) { $bad.Add('no container drew it') }
        else {
            if ($p.rendered.note -cne 'could not be read in full') { $bad.Add("note $(Show $p.rendered.note)") }
            if ($p.rendered.expander -ne $true) { $bad.Add('no expander is drawn') }
            if ($p.rendered.expanded -ne $true -or $p.nodeExpanded -ne $true) { $bad.Add("expanded $($p.rendered.expanded), node $($p.nodeExpanded)") }
        }
        if ($k -le 0 -or $k -ge $n) { $bad.Add("childCount $k") }
        if ([long]$p.size -ne $k * $PartwayFileBytes -or [long]$p.onDisk -ne $k * $PartwayFileBytes) { $bad.Add("size $($p.size), onDisk $($p.onDisk), for $k files") }
        Check ($bad.Count -eq 0) "P, whose listing failed at its second page, reads 'could not be read in full', has an expander, is open, and charges the $k of $n files it kept ($(Show ($bad -join '; ')))"

        Check ($k -le 1000) "P's $k children are few enough for the walk to open it (1,000 at most)"
        $children = @($rows | Where-Object { $_.depth -eq 2 -and (Parent-Of $_.path) -ceq $built.P })
        $drawn = @($children | Where-Object { $null -ne $_.rendered -and $_.rendered.name -cmatch "^file-\d{4}-$($built.Tag)\.bin$" -and $_.rendered.name -ceq $_.name })
        $files = @($children | Where-Object { $_.entryKind -ceq 'File' -and [long]$_.size -eq $PartwayFileBytes })
        $distinct = @($children | ForEach-Object name | Sort-Object -Unique -CaseSensitive).Count
        Check ($children.Count -eq $k -and $drawn.Count -eq $k -and $files.Count -eq $k -and $distinct -eq $k) "P's expander opens onto exactly its $k children, each one of its files of $(Format-Size $PartwayFileBytes), drawn by a container of its own ($($children.Count) read, $($drawn.Count) drawn, $($files.Count) such files, $distinct names)"
        $viewport = [double]@(Lines-Of $run 'ending')[0].viewportHeight
        Check ($k * 20 -gt $viewport) "more of them than the viewport holds ($k rows of 20 px against $viewport px): the count is P's, not the screen's"

        $bad = [System.Collections.Generic.List[string]]::new()
        if ($null -eq $r.rendered) { $bad.Add('no container drew it') }
        elseif ($r.rendered.note -cne 'could not be opened' -or $r.rendered.expander -ne $false) { $bad.Add("note $(Show $r.rendered.note), expander $($r.rendered.expander)") }
        if ([int]$r.childCount -ne 0 -or [long]$r.onDisk -ne 0) { $bad.Add("childCount $($r.childCount), onDisk $($r.onDisk)") }
        Check ($bad.Count -eq 0) "R, denied outright, reads 'could not be opened' and has no expander, told apart from P ($(Show ($bad -join '; ')))"

        $rootRow = @($rows | Where-Object depth -eq 0)[0]
        Check ($null -ne $rootRow -and [long]$rootRow.size -eq [long]$p.size + [long]$q.size -and $null -ne $rootRow.rendered -and $rootRow.rendered.note -ceq '') "the root is P and Q together, R adding nothing, and has no note ($(Show $rootRow.size) against $([long]$p.size + [long]$q.size))"
    }
    finally {
        if ($null -ne $built) { Undo-DenyListing $built.Denied }
        Remove-CaseFolder $case
    }
}

# T-008 (SL-082): an FSA_HOOK_FAIL_LISTING the hook cannot arm ends the app before its Main, so no case reads an
# unfaulted scan as a faulted one (review m7). Each value in a launch of its own, in report mode under the hook's
# size mode, where an armed hook would let the app size its window, scan and write its report with exit 0: two parts
# instead of three, a page or an error that is not a number, and the values Core refuses: page 1, error 0, error 18
# (ERROR_NO_MORE_FILES, which would end the listing at the page with no error, so the folder would read complete), an
# empty name and a name holding a path separator (review m1, O-017). Each must end within the timeout on the
# exception thrown out of the hook's Initialize (0xE0434352, the runtime's unhandled exception, measured at SL-082's
# build), with no report written and the hook's refusal, naming its own reason, its only line. A Core member the
# hook cannot find is WindowMutants.ps1's NoFailListingMember.
function Test-T008 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'small'
        [void](New-SmallTree $root)
        $bad = @(
            @{ Value = 'folder1|2'; Reason = '^fail-listing refused reason=FormatException: ''folder1\|2'' is not ' }
            @{ Value = 'folder1|two|995'; Reason = '^fail-listing refused reason=FormatException: the page ''two'' is not a whole number$' }
            @{ Value = 'folder1|1|995'; Reason = '^fail-listing refused reason=ArgumentOutOfRangeException: .*\(Parameter ''page''\)' }
            @{ Value = 'folder1|2|0'; Reason = '^fail-listing refused reason=ArgumentOutOfRangeException: .*\(Parameter ''win32Error''\)' }
            @{ Value = 'folder1|2|x'; Reason = '^fail-listing refused reason=FormatException: the error ''x'' is not a whole number$' }
            @{ Value = 'folder1|2|18'; Reason = '^fail-listing refused reason=ArgumentOutOfRangeException: .*\(Parameter ''win32Error''\) Actual value was 18\.$' }
            @{ Value = '|2|995'; Reason = '^fail-listing refused reason=ArgumentException: '''' is not one folder''s name \(Parameter ''folderName''\)$' }
            @{ Value = 'a\b|2|995'; Reason = '^fail-listing refused reason=ArgumentException: ''a\\b'' is not one folder''s name \(Parameter ''folderName''\)$' }
        )
        for ($i = 0; $i -lt $bad.Count; $i++) {
            $value = $bad[$i].Value
            Write-Output "  FSA_HOOK_FAIL_LISTING=$value"
            $report = Join-Path $case "bad-$i.json"
            $run = Invoke-HookedRun 'size' $root $report @{ FSA_HOOK_FAIL_LISTING = $value } 30
            foreach ($line in $run.Log) { Write-Output "        hook: $line" }
            $log = @($run.Log)
            Check ($run.Exit -eq '-532462766') "the app ended within 30 s on the exception the hook threw out of Initialize: the runtime's 0xE0434352 (exit $(Show-Exit $run.Exit))"
            Check ($null -eq $run.Text) 'no report was written: the app never reached an ending'
            Check ($log.Count -eq 1 -and $log[0] -match $bad[$i].Reason) "the hook's refusal is its only line (/$($bad[$i].Reason)/): nothing ran after it"
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

# What the reveal cases read of Explorer and of the paths it reports (SL-088). Nothing here acts on a window: it lists
# top-level windows with their class, process and visibility, and expands 8.3 names. Reveal is the one exception, R-002
# step (d)'s setup: Explorer showing a folder with one item selected, made as a reveal of that item makes it. Loaded by
# the reveal cases only, so no other case pays for the compile.
function Initialize-RevealWin32 {
    if ('FsaWindowCases.Win32' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FsaWindowCases
{
    public sealed class TopWindow
    {
        public long Hwnd;
        public string Class;
        public uint Pid;
        public bool Visible;
    }

    public static class Win32
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr items, uint flags);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        // The path with every 8.3 alias expanded, asked in \\?\ form with a buffer of 32767 so a path past MAX_PATH is
        // neither refused nor cut. The path as given when it cannot be resolved (a shell namespace path, a gone item).
        public static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("::", StringComparison.Ordinal))
                return path ?? string.Empty;
            string p = path.Length > 3 ? path.TrimEnd('\\') : path;
            string extended = p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p
                : p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p.Substring(2)
                : @"\\?\" + p;
            var buffer = new StringBuilder(32767);
            uint n = GetLongPathNameW(extended, buffer, (uint)buffer.Capacity);
            if (n == 0 || n >= buffer.Capacity)
                return p;
            string r = buffer.ToString();
            if (r.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + r.Substring(8);
            return r.StartsWith(@"\\?\", StringComparison.Ordinal) ? r.Substring(4) : r;
        }

        public static List<TopWindow> TopWindows()
        {
            var found = new List<TopWindow>();
            EnumProc each = (hwnd, _) =>
            {
                var name = new StringBuilder(256);
                GetClassNameW(hwnd, name, name.Capacity);
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                found.Add(new TopWindow { Hwnd = hwnd.ToInt64(), Class = name.ToString(), Pid = pid, Visible = IsWindowVisible(hwnd) });
                return true;
            };
            EnumWindows(each, IntPtr.Zero);
            GC.KeepAlive(each);
            return found;
        }

        public static int Reveal(string path)
        {
            IntPtr pidl;
            uint attributes;
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes);
            if (hr != 0)
                return hr;
            try
            {
                return SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
            }
            finally
            {
                ILFree(pidl);
            }
        }
    }
}
'@
}

function Resolve-LongPath([string] $path) { [FsaWindowCases.Win32]::LongPath($path) }

# Every window Shell.Application lists, read once: its HWND, its folder resolved to long names, and each selected item
# as its resolved path and its name. A window that goes away or stops answering mid-read is left out of that read.
function Get-ExplorerWindows($shell = (New-Object -ComObject Shell.Application)) {
    foreach ($w in @($shell.Windows())) {
        try {
            $doc = $w.Document
            if ($null -eq $doc) { continue }
            $folder = Resolve-LongPath ([string]$doc.Folder.Self.Path)
            $selected = @(foreach ($item in @($doc.SelectedItems())) {
                    [pscustomobject]@{ Path = (Resolve-LongPath ([string]$item.Path)); Name = [string]$item.Name }
                })
            [pscustomobject]@{ Hwnd = [long]$w.HWND; Folder = $folder; Selected = $selected; Window = $w }
        }
        catch { }
    }
}

# U-114 (SL-088): $target has landed when some window's folder is its parent and its selection is $target alone. Both
# sides are resolved to long names first, because past MAX_PATH Explorer reports 8.3 aliases, and every window is
# looked at, because Explorer reuses one already on the parent. An item whose path would not resolve (a folder this user
# cannot open, say) matches by its resolved parent and its name instead. Returns the window, or null.
function Find-Landing($windows, [string] $target) {
    $parent = (Resolve-LongPath ([System.IO.Path]::GetDirectoryName($target))).TrimEnd('\')
    $leaf = Resolve-LongPath $target
    $name = [System.IO.Path]::GetFileName($target)
    foreach ($w in @($windows)) {
        if (-not [string]::Equals($w.Folder.TrimEnd('\'), $parent, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (@($w.Selected).Count -ne 1) { continue }
        $item = @($w.Selected)[0]
        $itemParent = (Resolve-LongPath ([System.IO.Path]::GetDirectoryName($item.Path))).TrimEnd('\')
        if ([string]::Equals($item.Path, $leaf, [System.StringComparison]::OrdinalIgnoreCase) -or
            ([string]::Equals($itemParent, $parent, [System.StringComparison]::OrdinalIgnoreCase) -and $item.Name -ceq $name)) {
            return $w
        }
    }
    $null
}

# $expectedItem in $expectedFolder has landed within $timeoutSec (Find-Landing), polled every 25 ms.
function Wait-ExplorerSelection($expectedFolder, $expectedItem, [int] $timeoutSec = 5) {
    $target = Join-Path $expectedFolder $expectedItem
    $shell = New-Object -ComObject Shell.Application
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $landed = Find-Landing @(Get-ExplorerWindows $shell) $target
        if ($landed) {
            return [pscustomobject]@{ Matched = $true; Window = $landed.Window; Folder = $landed.Folder; Selected = @($landed.Selected | ForEach-Object Name) }
        }
        Start-Sleep -Milliseconds 25
    }
    return [pscustomobject]@{ Matched = $false }
}

function Test-Under([string] $folder, [string[]] $roots) {
    foreach ($root in $roots) {
        $r = (Resolve-LongPath $root).TrimEnd('\')
        if ($folder.Equals($r, [System.StringComparison]::OrdinalIgnoreCase) -or $folder.StartsWith($r + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    $false
}

# How many entries Shell.Application lists under each HWND, reading each entry's HWND alone, so an entry
# Get-ExplorerWindows leaves out because its Document cannot be read at this moment (a tab mid-navigation, or one
# refusing calls while busy) is still counted (review m4). An entry whose HWND cannot be read either is counted under 0:
# it might share any window.
function Get-ExplorerHwndCounts($shell) {
    $counts = @{}
    foreach ($w in @($shell.Windows())) {
        $hwnd = 0L
        try { $hwnd = [long]$w.HWND } catch { $hwnd = 0L }
        $counts[$hwnd] = 1 + [int]$counts[$hwnd]
    }
    $counts
}

# Closes the Explorer windows a reveal case opened on its own tree (SL-088). The roots are fresh GUID folders under
# %TEMP% that nothing else has open, so an entry whose folder is one of them or under one is the case's, wherever
# Explorer put it, a new tab in a window open before the case included (review m1). Windows 11 lists each tab of a
# tabbed window as its own entry with the window's HWND, and whether Quit() on a tab's entry closes the tab or the whole
# window was not measured, so an entry is quit only when Shell.Application's raw count of entries under its HWND equals
# the number of the case's own entries there: a window of the case's own tabs alone. The raw count is read apart from
# Get-ExplorerWindows, which leaves out entries it cannot read, so a tab of the person's that is busy at that moment still
# counts (review m4); while any entry's HWND cannot be read, nothing is quit on that look, and the count is taken again
# just before each Quit, so a tab the person opens meanwhile is seen. A case tab beside anything else is never quit; it
# is reported, by folder. Waits up to 5 s and returns the folders of the case's entries still there on the last look.
function Close-CaseWindows([string[]] $roots) {
    $shell = New-Object -ComObject Shell.Application
    $alone = {
        param($counts, [long] $hwnd, [int] $mine)
        -not $counts.ContainsKey(0L) -and [int]$counts[$hwnd] -eq $mine
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        $left = @(Get-ExplorerWindows $shell | Where-Object { Test-Under $_.Folder $roots })
        $counts = Get-ExplorerHwndCounts $shell
        foreach ($w in $left) {
            $mine = @($left | Where-Object { $_.Hwnd -eq $w.Hwnd }).Count
            if (-not (& $alone $counts $w.Hwnd $mine)) { continue }
            if (-not (& $alone (Get-ExplorerHwndCounts $shell) $w.Hwnd $mine)) { continue }
            try { $w.Window.Quit() } catch { }
        }
        if ($left.Count -gt 0) { Start-Sleep -Milliseconds 100 }
    } while ($left.Count -gt 0 -and $sw.Elapsed.TotalSeconds -lt 5)
    $left | ForEach-Object Folder
}

# What Explorer shows: each window's HWND, folder and selection, and the visible top-level CabinetWClass windows, which
# include one that is on screen before Shell.Application lists it.
function Get-ExplorerState {
    [pscustomobject]@{
        Windows  = @(Get-ExplorerWindows | ForEach-Object {
                [pscustomobject]@{ Hwnd = $_.Hwnd; Folder = $_.Folder; Selected = (@($_.Selected | ForEach-Object Path) -join '|') }
            })
        Cabinets = @([FsaWindowCases.Win32]::TopWindows() | Where-Object { $_.Class -eq 'CabinetWClass' -and $_.Visible } | ForEach-Object Hwnd)
    }
}

# Every way $after differs from $before that a reveal would cause, each naming the folder, so a failure says where
# Explorer went. Each entry is its HWND, folder and selection together, and every entry of $after that $before does not
# hold as often is reported: a new window or tab, one moved to another folder, one whose selection changed. An HWND alone
# is no key, since Windows 11 lists every tab of a tabbed window under the window's one HWND (review m1). A newly visible
# CabinetWClass is reported too.
function Compare-ExplorerState($before, $after) {
    $held = [System.Collections.Generic.List[string]]::new()
    foreach ($w in $before.Windows) { $held.Add("$($w.Hwnd)|$($w.Folder)|$($w.Selected)") }
    foreach ($w in $after.Windows) {
        if (-not $held.Remove("$($w.Hwnd)|$($w.Folder)|$($w.Selected)")) {
            'an Explorer window or tab on {0} selecting {1} that was not there before (HWND 0x{2:X})' -f (Show $w.Folder), (Show $w.Selected), $w.Hwnd
        }
    }
    foreach ($h in $after.Cabinets) {
        if ($h -notin $before.Cabinets) { 'a newly visible Explorer window (CabinetWClass 0x{0:X})' -f $h }
    }
}

# Explorer watched for $ms from now against $before: every change seen, each once.
function Watch-ExplorerUnchanged($before, [int] $ms) {
    $seen = [System.Collections.Generic.List[string]]::new()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        foreach ($change in @(Compare-ExplorerState $before (Get-ExplorerState))) {
            if (-not $seen.Contains($change)) { $seen.Add($change) }
        }
        Start-Sleep -Milliseconds 25
    }
    $seen.ToArray()
}

# R-001 (SL-047): reveals items from all results surfaces in Windows Explorer, asserting selection, the refusal of
# an unaddressable name (SL-088: it opened its folder instead until then), hidden file note, and proper command binding.
# Every landing is Find-Landing's: resolved long paths, and the target as the only selected item (SL-088).
function Test-R001 {
    Initialize-RevealWin32

    $case = New-CaseFolder
    $caseRoots = [System.Collections.Generic.List[string]]::new()
    $caseRoots.Add($case)
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

        # Padded so the file's path is exactly 300 characters whatever %TEMP% is (SL-088, criterion 5).
        $pad = 300 - ($root.Length + ('\' + 'p' * 70 + '\' + 'q' * 70 + '\').Length + '\long_file.bin'.Length)
        if ($pad -lt 1) { throw "case setup: $root is too long for a 300-character path under it" }
        $longDir = Join-Path $root ('p' * 70 + '\' + 'q' * 70 + '\' + 'r' * $pad)
        [void][System.IO.Directory]::CreateDirectory($longDir)
        $longFile = Join-Path $longDir 'long_file.bin'
        New-File $longFile $KiB64
        CheckEqual 300 $longFile.Length 'long file path length'

        # Before every step (B3-2): close the Explorer windows this case opened, only ever on the case's own folders
        # (SL-088, Close-CaseWindows), and wait until they are gone, so a step's selection can only be matched by the
        # window that step opened.
        $closeNewWindows = {
            $left = @(Close-CaseWindows $caseRoots.ToArray())
            Check ($left.Count -eq 0) "the Explorer windows the case opened are closed before the next step ($($left.Count) left: $($left -join '; '))"
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

        # 3. tree: dup. (C-005 / B3-2: mis-parsed name). SL-088: refused, and nothing is opened in its place; SL-047
        # opened its folder instead. No window opening or changing in the 2 s after also means the decoy 'dup' is never
        # selected.
        & $closeNewWindows
        $beforeDup = Get-ExplorerState
        $runDup = Invoke-RevealRun $root 'tree:dup.'
        CheckEqual '0' $runDup.Exit 'dup. run exit code'
        $dupRow = $runDup.Lines | Where-Object name -eq 'dup.'
        Check ($null -ne $dupRow) 'dup. was logged'
        CheckEqual 'Failed' $dupRow.kind 'dup. kind is Failed'
        CheckEqual 'NameUnaddressable' $dupRow.reason 'dup. reason is NameUnaddressable'
        CheckEqual "Couldn't show 'dup.' in Explorer (Explorer can't select 'dup.' by name: it ends in a dot or space, or is a reserved device name). Nothing was opened." $dupRow.status 'dup. status says nothing was opened'
        $dupChanges = @(Watch-ExplorerUnchanged $beforeDup 2000)
        Check ($dupChanges.Count -eq 0) "no Explorer window opened or changed for dup., decoy 'dup' included ($($dupChanges -join '; '))"

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

        # 10. long path of exactly 300 chars through App (C-004 / O-017, SL-088)
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
        $caseRoots.Add($topCase)
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
        # Close only the windows this case opened on its own folders (SL-088)
        [void](Close-CaseWindows $caseRoots.ToArray())
        if ($denied) { Undo-DenyListing $denied }
        if ($dupDot -and [System.IO.File]::Exists($dupDot)) { [System.IO.File]::Delete($dupDot) }
        Remove-Links $links
        Remove-CaseFolder $case
    }
}

# ---- A reveal lands or is refused (SL-088) ----

# A deny ACE for FullControl for this user on $folder, not only list-folder as Deny-Listing's, through the .NET ACL API
# on the \\?\ form, which takes a path past MAX_PATH. The owner keeps READ_CONTROL and WRITE_DAC whatever the DACL says,
# so Undo-DenyAll can take it away again.
function Deny-All([string] $folder) {
    $info = [System.IO.DirectoryInfo]::new('\\?\' + $folder)
    $acl = [System.IO.FileSystemAclExtensions]::GetAccessControl($info)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'None', 'None', 'Deny'))
    [System.IO.FileSystemAclExtensions]::SetAccessControl($info, $acl)
}

function Undo-DenyAll([string] $folder) {
    if (-not $folder -or -not [System.IO.Directory]::Exists('\\?\' + $folder)) { return }
    $info = [System.IO.DirectoryInfo]::new('\\?\' + $folder)
    $acl = [System.IO.FileSystemAclExtensions]::GetAccessControl($info)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.RemoveAccessRuleAll([System.Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'None', 'None', 'Deny'))
    [System.IO.FileSystemAclExtensions]::SetAccessControl($info, $acl)
}

# Whether this user can list $folder: the check that Deny-All took, made before the scan, so a step cannot pass on an
# ordinary folder.
function Test-Listable([string] $folder) {
    try {
        [void][System.IO.Directory]::GetFileSystemEntries('\\?\' + $folder)
        $true
    }
    catch [System.UnauthorizedAccessException] {
        $false
    }
}

# R-002's fixtures carry this file, one line per folder denied in them, written before the deny, so a run that ended
# before its cleanup leaves what the next run needs to take the denies away before it deletes the tree.
$R002Marker = 'R-002.fixture'

function Remove-R002Fixture([string] $case) {
    $marker = Join-Path $case $R002Marker
    if ([System.IO.File]::Exists($marker)) {
        foreach ($folder in [System.IO.File]::ReadAllLines($marker)) {
            if ($folder) { Undo-DenyAll $folder }
        }
    }
    if ([System.IO.Directory]::Exists('\\?\' + $case)) { [System.IO.Directory]::Delete('\\?\' + $case, $true) }
}

function Clear-StaleR002 {
    $cases = Join-Path ([System.IO.Path]::GetTempPath()) 'fsa-window-cases'
    if (-not [System.IO.Directory]::Exists($cases)) { return }
    foreach ($case in [System.IO.Directory]::GetDirectories($cases)) {
        if ([System.IO.File]::Exists((Join-Path $case $R002Marker))) {
            Write-Output "  removing a stale R-002 fixture: $case"
            Remove-R002Fixture $case
        }
    }
}

# A path under $base exactly $total characters long: folders of 60 characters while more than two parts are left, then
# one or two parts sharing the rest, the last a leaf that starts with $leaf and is padded to fit. Created by nobody.
function Get-ExactPath([string] $base, [int] $total, [string] $leaf) {
    $path = $base.TrimEnd('\')
    $n = 0
    while ($total - $path.Length -gt 1 + 69 + 1 + 50) {
        $path += '\' + ('d{0:D2}' -f $n) + ('_' * 57)
        $n++
    }
    $rest = $total - $path.Length - 1
    if ($rest -gt 69) {
        $dir = [int][Math]::Floor(($rest - 1) / 2)
        $path += '\' + ('d{0:D2}' -f $n) + ('_' * ($dir - 3))
        $rest = $total - $path.Length - 1
    }
    if ($rest -lt $leaf.Length + 1) { throw "case setup: $base is too long for a $total-character path under it" }
    $path += '\' + $leaf + ('x' * ($rest - $leaf.Length))
    if ($path.Length -ne $total) { throw "case setup: built a $($path.Length)-character path for $total" }
    $path
}

# Top-level #32770 dialogs on screen, owned by explorer.exe or by the process $appId.
function Get-Dialogs([int] $appId) {
    $owners = @(@(Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object Id) + $appId)
    @([FsaWindowCases.Win32]::TopWindows() | Where-Object { $_.Class -eq '#32770' -and $_.Visible -and [int]$_.Pid -in $owners } | ForEach-Object Hwnd)
}

# One reveal of $revealTarget (a hook target, surface:name) with Explorer watched while the app runs. Shell.Application's
# windows are read every 25 ms from the launch, and the first read in which $target has landed (Find-Landing) is kept
# with the QPC timestamp taken right after it, so the landing is timed from the hook's revealStartQpc on the one
# system-wide clock. Reads go on for up to 3 s after the app exits if nothing has landed by then. With -WatchDialogs,
# new #32770 dialogs owned by explorer.exe or the app, and any consent.exe, are looked for from the launch until 500 ms
# after the landing. $between runs once the scan has ended and before the click (FSA_HOOK_REVEAL_GATE).
function Invoke-WatchedReveal([string] $root, [string] $revealTarget, [string] $target, [switch] $WatchDialogs, [int] $timeoutSec = 120) {
    $variables = @{ FSA_HOOK_REVEAL = $revealTarget }
    $consentBefore = @(Get-Process -Name consent -ErrorAction SilentlyContinue | ForEach-Object Id)
    $run = Invoke-WithHook 'reveal' $HookDll {
        $process = Start-App "`"$root`""
        $dialogsBefore = @(Get-Dialogs $process.Id)
        $shell = New-Object -ComObject Shell.Application
        $landed = $null
        $landedQpc = 0
        $dialogs = [System.Collections.Generic.List[long]]::new()
        $consent = [System.Collections.Generic.List[int]]::new()
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $exitedAt = $null
        while ($clock.Elapsed.TotalSeconds -lt $timeoutSec) {
            if ($null -eq $exitedAt -and $process.HasExited) { $exitedAt = $clock.Elapsed.TotalSeconds }
            if ($null -eq $landed) {
                $windows = @(Get-ExplorerWindows $shell)
                $qpc = [System.Diagnostics.Stopwatch]::GetTimestamp()
                $landed = Find-Landing $windows $target
                if ($landed) { $landedQpc = $qpc }
            }
            if ($WatchDialogs) {
                foreach ($h in @(Get-Dialogs $process.Id)) { if ($h -notin $dialogsBefore -and -not $dialogs.Contains($h)) { $dialogs.Add($h) } }
                foreach ($id in @(Get-Process -Name consent -ErrorAction SilentlyContinue | ForEach-Object Id)) { if ($id -notin $consentBefore -and -not $consent.Contains($id)) { $consent.Add($id) } }
            }
            $watchDone = -not $WatchDialogs -or ($landed -and ([System.Diagnostics.Stopwatch]::GetTimestamp() - $landedQpc) * 1000 / [System.Diagnostics.Stopwatch]::Frequency -ge 500)
            if ($null -ne $exitedAt -and (($landed -and $watchDone) -or $clock.Elapsed.TotalSeconds - $exitedAt -ge 3)) { break }
            Start-Sleep -Milliseconds 25
        }
        [pscustomobject]@{
            Exit      = Wait-App $process 60
            Landed    = $landed
            LandedQpc = $landedQpc
            Dialogs   = $dialogs.ToArray()
            Consent   = $consent.ToArray()
        }
    } $variables
    $lines = @(Read-HookJson $run.Log)
    [pscustomobject]@{ Watch = $run.Result; Lines = $lines; Row = @($lines | Where-Object { $_.surface -and $_.name })[0] }
}

# The checks every landing step makes: the run ended well, the hook logged Exact for this very path and saw the command
# start, the window's folder and single selection are the target's (Find-Landing), after the click and, when $timed,
# within 1000 ms of it. Its output is those checks' lines and the milliseconds from the click to the landing, so call it
# plainly: the runner prints nothing else about a step (review m3).
function Check-Landing($reveal, [string] $target, [string] $what, [bool] $timed) {
    CheckEqual '0' $reveal.Watch.Exit "$what run exit code"
    $row = $reveal.Row
    Check ($null -ne $row) "$what was logged ($(Show ($reveal.Lines | ConvertTo-Json -Compress)))"
    if ($null -eq $row) { return }
    CheckEqual $target $row.path "$what resolved path"
    CheckEqual 'Exact' $row.kind "$what kind"
    Check ([long]$row.revealStartQpc -gt 0) "the hook saw the Open in Explorer command start for $what"
    $landed = $reveal.Watch.Landed
    Check ($null -ne $landed) "$what landed: its parent's window with it as the only selected item"
    if ($null -eq $landed -or [long]$row.revealStartQpc -le 0) { return }
    $ms = [Math]::Round(([long]$reveal.Watch.LandedQpc - [long]$row.revealStartQpc) * 1000.0 / [long]$row.qpcFrequency)
    Write-Output "        $what landed $ms ms after the click, in the window on $(Show $landed.Folder) selecting $(Show (@($landed.Selected | ForEach-Object Name) -join ', '))"
    Check ($ms -gt 0) "$what landed after the click, not in a window left from before it ($ms ms)"
    if ($timed) { Check ($ms -le 1000) "$what landed within 1000 ms of the click ($ms ms)" }
}

# One refused reveal: $between runs once the scan has ended and before the click, through FSA_HOOK_REVEAL_GATE, and
# Explorer is compared with how it was before the launch (Compare-ExplorerState) from the launch until 2 s after the
# app has exited, which it does only after the hook has logged the outcome.
function Invoke-RefusedReveal([string] $root, [string] $revealTarget, [scriptblock] $between, [int] $timeoutSec = 120) {
    $gate = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-window-hook\gate-{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $gate))
    $variables = @{ FSA_HOOK_REVEAL = $revealTarget; FSA_HOOK_REVEAL_GATE = $gate }
    $before = Get-ExplorerState
    try {
        $run = Invoke-WithHook 'reveal' $HookDll {
            $process = Start-App "`"$root`""
            $changes = [System.Collections.Generic.List[string]]::new()
            $opened = $false
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            $exitedAt = $null
            while ($clock.Elapsed.TotalSeconds -lt $timeoutSec) {
                if (-not $opened -and [System.IO.File]::Exists("$gate.ready")) {
                    & $between
                    [System.IO.File]::WriteAllText($gate, '')
                    $opened = $true
                }
                if ($null -eq $exitedAt -and $process.HasExited) { $exitedAt = $clock.Elapsed.TotalSeconds }
                foreach ($change in @(Compare-ExplorerState $before (Get-ExplorerState))) {
                    if (-not $changes.Contains($change)) { $changes.Add($change) }
                }
                if ($null -ne $exitedAt -and $clock.Elapsed.TotalSeconds - $exitedAt -ge 2) { break }
                Start-Sleep -Milliseconds 25
            }
            [pscustomobject]@{ Exit = Wait-App $process 60; Gated = $opened; Changes = $changes.ToArray() }
        } $variables
    }
    finally {
        foreach ($file in $gate, "$gate.ready") { if ([System.IO.File]::Exists($file)) { [System.IO.File]::Delete($file) } }
    }
    $lines = @(Read-HookJson $run.Log)
    [pscustomobject]@{ Watch = $run.Result; Lines = $lines; Row = @($lines | Where-Object { $_.surface -and $_.name })[0] }
}

# R-002 (SL-088): a reveal lands on its item at any path length, folders this user cannot open included, or is refused
# and opens nothing. Its own tree: a short folder with a deny-all ACE beside a file; files of exactly 161, 300 and 1000
# characters, 3, 4 and 5 MiB, each beside a sibling; a deny-all folder of exactly 300 characters beside a sibling; and
# gone\victim.bin. Every deny is checked to have taken before the scan. Before each step the case closes the windows it
# opened on its own tree and asserts none is left, and every step reveals a target no other step reveals, so no step can
# match a window an earlier one left. The steps run (b), (a), (c), (d), (e): the 161-character reveal is the run's first,
# with no warm-up before it, so its 1000 ms bound is met by Explorer's cold reveal window or not at all.
#   (b) the 161- and then the 300-character file from their biggest-files rows, each within 1000 ms of the click;
#   (a) the short deny-all folder from its tree row: selected by name, and no #32770 dialog of explorer.exe or the app
#       and no consent.exe from the launch until 500 ms after the landing;
#   (c) the 300-character deny-all folder from its tree row, within 1000 ms, with (a)'s watch;
#   (d) the 1000-character file from its biggest-files row, within 1000 ms, into an Explorer already on its parent with
#       the sibling selected: the landing must have the file as the only selected item;
#   (e) gone\ deleted after the scan, then victim.bin revealed from its tree row: Failed, NoLongerExists, the refusal on
#       the status line, and no Explorer window new, moved, re-selected or newly visible from the launch until 2 s after.
# -Step runs some of them only, in that order. Cleanup closes only this case's own windows, takes the denies away and
# deletes the tree; a fixture an earlier run left behind is cleared the same way first.
function Test-R002 {
    Initialize-RevealWin32
    Clear-StaleR002
    $steps = @('b', 'a', 'c', 'd', 'e' | Where-Object { $R002Steps.Count -eq 0 -or $_ -in $R002Steps })
    $case = New-CaseFolder
    try {
        $marker = Join-Path $case $R002Marker
        [System.IO.File]::WriteAllText($marker, '')
        $root = Join-Path $case 'r2'

        $shortDeny = Join-Path $root 'short\denyall_short'
        [void][System.IO.Directory]::CreateDirectory($shortDeny)
        New-File (Join-Path $root 'short\sibling.bin') $KiB64

        $files = [ordered]@{}
        foreach ($spec in @(@(161, 3), @(300, 4), @(1000, 5))) {
            $file = Get-ExactPath (Join-Path $root "c$($spec[0])") $spec[0] "f$($spec[0])_"
            New-File $file ($spec[1] * 1MB)
            New-File (Join-Path ([System.IO.Path]::GetDirectoryName($file)) 'sibling.bin') $KiB64
            CheckEqual $spec[0] $file.Length "the $($spec[0])-character file's path length"
            $files[[string]$spec[0]] = $file
        }

        $deny300 = Get-ExactPath (Join-Path $root 'cden300') 300 'denyall300_'
        [void][System.IO.Directory]::CreateDirectory($deny300)
        New-File (Join-Path ([System.IO.Path]::GetDirectoryName($deny300)) 'sibling.bin') $KiB64
        CheckEqual 300 $deny300.Length "the 300-character deny-all folder's path length"

        $victim = Join-Path $root 'gone\victim.bin'
        New-File $victim (2 * 1MB)

        foreach ($folder in $shortDeny, $deny300) {
            [System.IO.File]::AppendAllLines($marker, [string[]]@($folder))
            Deny-All $folder
            Check (-not (Test-Listable $folder)) "the deny-all ACE took before the scan: listing $(Show $folder) is refused"
        }

        $isolate = {
            $left = @(Close-CaseWindows @($case))
            Check ($left.Count -eq 0) "no window on the case's own tree is open before the step ($($left.Count) left: $($left -join '; '))"
        }

        foreach ($s in $steps) {
            switch ($s) {
                'b' {
                    foreach ($length in '161', '300') {
                        & $isolate
                        $file = $files[$length]
                        $leaf = [System.IO.Path]::GetFileName($file)
                        Write-Output "  (b) the $length-character file from its biggest-files row$(if ($length -eq '161') { ', the run''s first reveal' })"
                        $r = Invoke-WatchedReveal $root "largest:$leaf" $file
                        Check-Landing $r $file "the $length-character file" $true
                    }
                }
                'a' {
                    & $isolate
                    Write-Output '  (a) the short deny-all folder from its tree row'
                    $r = Invoke-WatchedReveal $root 'tree:denyall_short' $shortDeny -WatchDialogs
                    Check-Landing $r $shortDeny 'the short deny-all folder' $false
                    if ($r.Watch.Landed) { CheckEqual 'denyall_short' @($r.Watch.Landed.Selected)[0].Name 'the selected item is the deny-all folder, by name' }
                    Check ($r.Watch.Dialogs.Count -eq 0) "no dialog of explorer.exe or the app for the short deny-all folder ($($r.Watch.Dialogs.Count))"
                    Check ($r.Watch.Consent.Count -eq 0) "no elevation prompt (consent.exe) for the short deny-all folder ($($r.Watch.Consent.Count))"
                }
                'c' {
                    & $isolate
                    $leaf = [System.IO.Path]::GetFileName($deny300)
                    Write-Output '  (c) the 300-character deny-all folder from its tree row'
                    $r = Invoke-WatchedReveal $root "tree:$leaf" $deny300 -WatchDialogs
                    Check-Landing $r $deny300 'the 300-character deny-all folder' $true
                    if ($r.Watch.Landed) { CheckEqual $leaf @($r.Watch.Landed.Selected)[0].Name 'the selected item is the 300-character deny-all folder, by name' }
                    Check ($r.Watch.Dialogs.Count -eq 0) "no dialog of explorer.exe or the app for the 300-character deny-all folder ($($r.Watch.Dialogs.Count))"
                    Check ($r.Watch.Consent.Count -eq 0) "no elevation prompt (consent.exe) for the 300-character deny-all folder ($($r.Watch.Consent.Count))"
                }
                'd' {
                    & $isolate
                    $file = $files['1000']
                    $sibling = Join-Path ([System.IO.Path]::GetDirectoryName($file)) 'sibling.bin'
                    Write-Output '  (d) the 1000-character file, with Explorer already on its parent and the sibling selected'
                    $hr = [FsaWindowCases.Win32]::Reveal($sibling)
                    CheckEqual 0 $hr 'the setup reveal of the sibling'
                    $pre = Wait-ExplorerSelection ([System.IO.Path]::GetDirectoryName($sibling)) 'sibling.bin'
                    Check $pre.Matched "Explorer is on the 1000-character file's parent with only the sibling selected before the click"
                    $preHwnd = if ($pre.Matched) { [long]$pre.Window.HWND } else { 0 }
                    $r = Invoke-WatchedReveal $root "largest:$([System.IO.Path]::GetFileName($file))" $file
                    Check-Landing $r $file 'the 1000-character file' $true
                    if ($r.Watch.Landed) {
                        Write-Output "        landed in $(if ($r.Watch.Landed.Hwnd -eq $preHwnd) { 'the window already on the parent' } else { 'a new window' })"
                    }
                }
                'e' {
                    & $isolate
                    Write-Output '  (e) victim.bin, its parent deleted after the scan'
                    $gone = Join-Path $root 'gone'
                    $r = Invoke-RefusedReveal $root 'tree:victim.bin' { [System.IO.Directory]::Delete($gone, $true) }
                    CheckEqual '0' $r.Watch.Exit 'the deleted-parent run exit code'
                    Check $r.Watch.Gated 'gone\ was deleted after the scan and before the click'
                    Check (-not [System.IO.Directory]::Exists($gone)) 'gone\ is not there when the reveal runs'
                    $row = $r.Row
                    Check ($null -ne $row) "victim.bin was logged ($(Show ($r.Lines | ConvertTo-Json -Compress)))"
                    if ($null -ne $row) {
                        CheckEqual $victim $row.path 'victim.bin resolved path'
                        CheckEqual 'Failed' $row.kind 'victim.bin kind'
                        CheckEqual 'NoLongerExists' $row.reason 'victim.bin reason'
                        CheckEqual "'victim.bin' no longer exists where the scan found it, so Explorer was not opened. Rescan to refresh." $row.status 'the refusal on the status line'
                    }
                    Check ($r.Watch.Changes.Count -eq 0) "no Explorer window opened, moved, changed its selection or showed for victim.bin ($($r.Watch.Changes -join '; '))"
                }
            }
        }
    }
    finally {
        [void](Close-CaseWindows @($case))
        Remove-R002Fixture $case
    }
}

# ---- The outcome's sections (SL-065) ----

# MainWindow.xaml's fit at the window's minimum size: the outcome's texts capped at 76 px while a section is shown, and
# the tabs at least their 154 px minimum, with 10 px to spare. Written here as numbers, as MainWindow.xaml writes them,
# so that a change to either shows up as a failing case and not as a silently different layout.
$TextsCap = 76
$TabsMinHeight = 154
$FitSpare = 10
$RevealStatusText = "'pagefile.sys' is hidden, so Explorer only highlights it if it is set to show hidden files (and protected operating system files, for system items)."

# 104 non-empty files, 26 in each of 4 folders, and $denied folders the scan cannot open: the largest files list full at
# 100 rows beside the unreadable folders. Returns the denied folders, for the caller to give back.
function New-SectionsTree([string] $root, [int] $denied) {
    for ($d = 1; $d -le 4; $d++) {
        for ($f = 1; $f -le 26; $f++) {
            New-File (Join-Path $root "a$d-$Planted\$Planted-$f.bin") $KiB64
        }
    }
    $folders = @()
    for ($k = 1; $k -le $denied; $k++) {
        $folder = Join-Path $root ('b{0:D2}-{1}' -f $k, $Planted)
        [void][System.IO.Directory]::CreateDirectory($folder)
        Deny-Listing $folder
        $folders += $folder
    }
    , $folders
}

function Format-Unreadable([int] $n) { "Files in $(Format-Count $n 'folder') that could not be read (in full) may be missing." }

# The fit of MainWindow.xaml once a section is shown: the texts within their cap, the tabs at their minimum and the
# 10 px to spare (the outcome's row is never under its 292 px minimum), and texts and tabs inside the panel with its
# border and padding (26) and the tabs' margin (10), with nothing left between them and the panel's foot.
function CheckFit($window) {
    $texts = $window.OutcomeTexts
    $tabs = $window.OutcomeTabs
    $panel = $window.OutcomePanel
    Check ($texts.actualHeight -le $TextsCap + 0.5) "the outcome's texts are within their $TextsCap px cap ($($texts.actualHeight) px shown of $($texts.extentHeight))"
    CheckEqual $true $texts.inView "the outcome's texts are in view"
    Check ($tabs.actualHeight -ge $TabsMinHeight + $FitSpare - 0.5) "the tabs have their $TabsMinHeight px minimum and the $FitSpare px to spare ($($tabs.actualHeight) px)"
    $rest = $panel.actualHeight - 26 - $texts.actualHeight - 10 - $tabs.actualHeight
    Check ([Math]::Abs($rest) -le 1) "texts, margin and tabs fill the panel, with no band left under the tabs ($([Math]::Round($rest, 2)) px left of $($panel.actualHeight))"
}

# A size-mode run (the window at its minimum, or maximized with FSA_HOOK_MAXIMIZE) through eng/read-window.ps1, with 26
# drive buttons added; the report, or null when a check on the hook failed.
function Invoke-Sized([string] $root, [hashtable] $variables = @{}) {
    $all = @{ FSA_HOOK_DRIVES = "$ExtraDrives" } + $variables
    $run = Invoke-WithHook 'size' $HookDll { Read-Window $root } $all
    $before = $script:Failures.Count
    CheckActed $run 'size'
    if ($variables.ContainsKey('FSA_HOOK_MAXIMIZE')) { CheckMaximized $run.Log } else { CheckSized $run.Log }
    CheckDrives $run.Log $ExtraDrives
    if ($variables.ContainsKey('FSA_HOOK_STATUS')) { CheckStatusShown $run.Log }
    if ($script:Failures.Count -gt $before) { return $null }
    CheckEqual 'EXIT 0' $run.Result.Exit 'exit code'
    Check ($null -ne $run.Result.Report) 'a report was written'
    $run.Result
}

# S-001 (SL-065, design C-002): the outcome's default state with an unreadable folder, at the window's minimum size
# with the reveal status line shown and 26 drive buttons added, the least room the window has. The largest files are
# selected, with a full row of their list; the unreadable folders' tab is beside them with its count, not selected,
# and their list is off screen; the headers take one row; and the fit holds with its 10 px to spare.
function Test-S001 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-SectionsTree $root 1
        $read = Invoke-Sized $root @{ FSA_HOOK_STATUS = $RevealStatusText }
        if ($null -eq $read -or $null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual "Finished: $root" $window.OutcomeTitle 'OutcomeTitle'
        CheckLargestFiles $read 100 $KiB64 "Top 100 of 104 files scanned. $(Format-Unreadable 1)"
        CheckEqual $true $window.UnreadableTab.visible 'UnreadableTab is shown: one folder was unreadable'
        CheckEqual 'Unreadable folders (1)' $window.UnreadableTab.header 'UnreadableTab.header'
        CheckEqual $false $window.UnreadableTab.selected 'UnreadableTab is not selected: the window never selects it itself'
        CheckEqual $true $window.UnreadableExpander.visible 'UnreadableExpander (the key kept for that tab) reads shown'
        CheckBothLists $window $false $true
        CheckFit $window
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# S-002 (SL-065, design C-003): the unreadable folders' tab selected at the minimum size (the hook's expand mode), and
# then selected and left again for the largest files (FSA_HOOK_BACK). The list of the tab that is not selected reads off
# screen with no full row, though it keeps the height it was last laid out at, and the tabs keep their height whichever
# is selected: only the selected section is laid out.
function Test-S002 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-SectionsTree $root 3
        $caption = "Top 100 of 104 files scanned. $(Format-Unreadable 3)"

        Write-Output '  the unreadable folders selected'
        $run = Invoke-WithHook 'expand' $HookDll { Read-Window $root } @{ FSA_HOOK_MINSIZE = '1'; FSA_HOOK_DRIVES = "$ExtraDrives" }
        $before = $script:Failures.Count
        CheckActed $run 'expand'
        CheckSized $run.Log
        CheckDrives $run.Log $ExtraDrives
        if ($script:Failures.Count -eq $before -and $null -ne $run.Result.Report) {
            $window = $run.Result.Report.window
            CheckLargestFiles $run.Result 100 $KiB64 $caption $false
            Check ($window.LargestFilesList.actualHeight -gt 0) "LargestFilesList keeps the height it was laid out at before its tab was left ($($window.LargestFilesList.actualHeight) px)"
            CheckBothLists $window $true $true
            CheckTabsKeptHeight $run.Log $window
            CheckFit $window
        }

        Write-Output '  the unreadable folders selected, laid out, and left for the largest files'
        $run = Invoke-WithHook 'expand' $HookDll { Read-Window $root } @{ FSA_HOOK_MINSIZE = '1'; FSA_HOOK_DRIVES = "$ExtraDrives"; FSA_HOOK_BACK = '1' }
        $before = $script:Failures.Count
        CheckActed $run 'expand'
        CheckSized $run.Log
        if ($script:Failures.Count -gt $before -or $null -eq $run.Result.Report) { return }
        $shown = @($run.Log | Where-Object { $_.StartsWith('shown ') })
        $height = if ($shown.Count -eq 1) { [double](Field $shown[0] 'unreadableHeight') } else { 0 }
        Check ($height -gt 0 -and [int](Field $shown[0] 'unreadableRows') -ge 1) "the unreadable folders' list was on screen before the tab was left ($(Show ($shown -join ' | ')))"
        Check (@($run.Log | Where-Object { $_.StartsWith('back selected=LargestFilesTab ') }).Count -eq 1) 'the hook selected the largest files again'
        $window = $run.Result.Report.window
        CheckLargestFiles $run.Result 100 $KiB64 $caption
        CheckBothLists $window $false $true
        Check ([Math]::Abs($window.UnreadableList.actualHeight - $height) -lt 0.01) "UnreadableList keeps the $height px it was laid out at, and still reads off screen ($($window.UnreadableList.actualHeight) px, inView $($window.UnreadableList.inView))"
        CheckTabsKeptHeight $run.Log $window
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# S-003 (SL-065, design C-004): a second scan started with the unreadable folders' tab selected (the hook's rescan mode,
# FSA_HOOK_BEFORE), whose result has no unreadable folder, because the one the first scan met is gone
# (FSA_HOOK_READABLE). The selection must leave that tab before it is hidden: the second ending shows the largest files
# selected, their list refilled while its tab was not selected, and the unreadable folders' list is off screen.
function Test-S003 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-SectionsTree $root 1
        $run = Invoke-WithHook 'rescan' $HookDll { Read-Window $root } @{ FSA_HOOK_BEFORE = 'UnreadableTab'; FSA_HOOK_READABLE = $denied[0] }
        $before = $script:Failures.Count
        CheckActed $run 'rescan'
        if ($script:Failures.Count -gt $before) { return }
        $selected = @($run.Log | Where-Object { $_.StartsWith('before selected=') })
        Check ($selected.Count -eq 1 -and (Field $selected[0] 'selected') -ceq 'UnreadableTab' -and [double](Field $selected[0] 'unreadableHeight') -gt 0) "the unreadable folders were selected and on screen as the second scan started ($(Show ($selected -join ' | ')))"
        Check (@($run.Log | Where-Object { $_.StartsWith('readable deleted=') }).Count -eq 1) 'the hook deleted the unreadable folder before the second scan'
        Check (@($run.Log | Where-Object { $_.StartsWith('ended ') }).Count -eq 1) "the hook held the report until the second scan's ending"
        $read = $run.Result
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual 'Completed' $read.Report.core.status 'the report is of the second scan (core.status)'
        CheckEqual "Finished: $root" $window.OutcomeTitle 'OutcomeTitle'
        CheckLargestFiles $read 100 $KiB64 'Top 100 of 104 files scanned.'
        CheckEqual $false $window.UnreadableTab.visible 'UnreadableTab is not shown: the second scan met no unreadable folder'
        CheckEqual $false $window.UnreadableExpander.visible 'UnreadableExpander reads not shown'
        CheckEqual $false $window.UnreadableExpander.expanded 'UnreadableExpander reads not expanded'
        CheckEqual 'LargestFilesTab' $window.OutcomeTabs.selected 'OutcomeTabs.selected: the largest files, not the hidden tab'
        CheckEqual $false $window.UnreadableList.inView 'UnreadableList is not on screen under the largest files'
        CheckEqual 0 $window.UnreadableList.fullRows 'UnreadableList shows no row'
        Check ($window.LargestFilesList.fullRows -ge 1) "LargestFilesList shows its new rows ($($window.LargestFilesList.fullRows) full, $($window.LargestFilesList.realizedRows) built)"
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# S-004 (SL-065, design C-005): the largest files' tab hidden while the unreadable folders' is shown. No ending of the
# app does that (both come with every scan that returned), so the hook's hide mode collapses it and calls the window's
# EnsureSelectedTabIsVisible: it must select the unreadable folders, the first section still shown, and not the
# collapsed tab a guard naming the largest files would.
function Test-S004 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-SectionsTree $root 3
        $run = Invoke-WithHook 'hide' $HookDll { Read-Window $root }
        $before = $script:Failures.Count
        CheckActed $run 'hide'
        if ($script:Failures.Count -gt $before) { return }
        $hidden = @($run.Log | Where-Object { $_.StartsWith('hidden ') })
        Check ($hidden.Count -eq 1 -and (Field $hidden[0] 'selected') -ceq 'UnreadableTab' -and (Field $hidden[0] 'tabs') -ceq 'Visible') "the guard selected the unreadable folders and kept the tabs ($(Show ($hidden -join ' | ')))"
        $read = $run.Result
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual $true $window.OutcomeTabs.visible 'OutcomeTabs is on screen'
        CheckEqual 'UnreadableTab' $window.OutcomeTabs.selected 'OutcomeTabs.selected'
        CheckEqual $false $window.LargestFilesTab.visible 'LargestFilesTab is hidden'
        CheckEqual $false $window.LargestFilesTab.selected 'LargestFilesTab is not selected'
        CheckEqual $true $window.UnreadableTab.selected 'UnreadableTab is selected'
        CheckEqual $true $window.UnreadableExpander.expanded 'UnreadableExpander reads expanded'
        CheckEqual $true $window.UnreadableList.inView 'UnreadableList is on screen'
        Check ($window.UnreadableList.fullRows -ge 1) "UnreadableList shows a full row ($($window.UnreadableList.fullRows))"
        CheckEqual $false $window.LargestFilesList.inView 'LargestFilesList is off screen with its tab'
        CheckEqual 0 $window.LargestFilesList.fullRows 'LargestFilesList shows no row'
        CheckEqual 1 $window.OutcomeTabs.stripRows 'OutcomeTabs.stripRows'
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# S-005 (SL-065, design C-006): an ending with no section, Could not read, at the window's minimum size, with a missing
# root of over 1,400 characters, so the title runs to about 27 lines. With no section the texts are not capped: they
# take the whole panel and scroll inside it (before SL-065 they were laid out whole and cut off, 575 px in 378). A
# short missing root's ending is as tall as its texts, with no blank band under them.
function Test-S005 {
    $case = New-CaseFolder
    try {
        $root = $case
        foreach ($i in 1..8) { $root = Join-Path $root (('missing-{0}-' -f $i) + ('x' * 160)) }
        Write-Output "  a missing root of $($root.Length) characters, at the window's minimum size"
        $read = Invoke-Sized $root
        if ($null -ne $read -and $null -ne $read.Report) {
            $window = $read.Report.window
            CheckEqual "Could not read $root" $window.OutcomeTitle 'OutcomeTitle'
            CheckEqual $false $window.OutcomeTabs.visible 'OutcomeTabs is not on screen: no section'
            CheckEqual $null $window.OutcomeTabs.selected 'OutcomeTabs.selected'
            CheckEqual 0 $window.OutcomeTabs.stripRows 'OutcomeTabs.stripRows'
            CheckEqual $false $window.LargestFilesTab.visible 'LargestFilesTab is hidden'
            CheckEqual $false $window.UnreadableTab.visible 'UnreadableTab is hidden'
            $texts = $window.OutcomeTexts
            CheckEqual $true $texts.inView "the texts are in view ($($texts.actualHeight) px)"
            Check ($texts.extentHeight -gt $texts.actualHeight) "the texts are taller than the panel and scroll inside it ($($texts.extentHeight) px in $($texts.actualHeight))"
            Check ($texts.actualHeight -gt $TextsCap) "with no section the texts are not held to the $TextsCap px cap ($($texts.actualHeight) px)"
            $rest = $window.OutcomePanel.actualHeight - 26 - $texts.actualHeight
            Check ([Math]::Abs($rest) -le 1) "the texts take the whole panel ($($texts.actualHeight) of $($window.OutcomePanel.actualHeight) px)"
        }

        $short = Join-Path $case 'missing'
        Write-Output '  a short missing root, at the default size'
        $read = Read-Window $short
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        if ($null -eq $read.Report) { Check $false 'a report was written'; return }
        $window = $read.Report.window
        CheckEqual "Could not read $short" $window.OutcomeTitle 'OutcomeTitle'
        CheckEqual $false $window.OutcomeTabs.visible 'OutcomeTabs is not on screen'
        $texts = $window.OutcomeTexts
        Check ([Math]::Abs($texts.extentHeight - $texts.actualHeight) -lt 0.5) "the texts are shown whole ($($texts.actualHeight) of $($texts.extentHeight) px)"
        $rest = $window.OutcomePanel.actualHeight - 26 - $texts.actualHeight
        Check ([Math]::Abs($rest) -le 1) "the panel is as tall as its texts, with no blank band under them ($($window.OutcomePanel.actualHeight) px for $($texts.actualHeight))"
    }
    finally { Remove-CaseFolder $case }
}

# S-006 (SL-065, design C-007): Validate #3's shape: texts far longer than the room above the sections, at the window's
# minimum size with the reveal status line shown. A root of 140 characters makes the finished title wrap over several
# lines: the texts scroll inside their cap, and never take the sections under their minimum.
function Test-S006 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case ('long-root-' * 14)
        $denied = New-SectionsTree $root 1
        $read = Invoke-Sized $root @{ FSA_HOOK_STATUS = $RevealStatusText }
        if ($null -eq $read -or $null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual "Finished: $root" $window.OutcomeTitle 'OutcomeTitle'
        $texts = $window.OutcomeTexts
        Check ($texts.extentHeight -gt $TextsCap + 40) "the texts run well past their cap ($($texts.extentHeight) px)"
        Check ($texts.extentHeight -gt $texts.actualHeight) "they scroll inside it ($($texts.actualHeight) px shown)"
        CheckLargestFiles $read 100 $KiB64 "Top 100 of 104 files scanned. $(Format-Unreadable 1)"
        CheckBothLists $window $false $true
        CheckFit $window
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# S-007 (SL-065, design C-008 and C-009): 12,345 folders the scan cannot open, so the unreadable folders' header carries
# a five-digit count, 'Unreadable folders (12,345)', at the window's minimum width: the headers must still take one row,
# or every section loses a row's height. Then their tab selected: a list of 12,345 rows bounded by the tabs. The folders
# are denied and given back with one icacls call each way.
function Test-S007 {
    $case = New-CaseFolder
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $root = Join-Path $case 'tree'
    $denied = $false
    try {
        [void](New-SectionsTree $root 0)
        for ($k = 1; $k -le 12345; $k++) { [void][System.IO.Directory]::CreateDirectory((Join-Path $root ('u{0:D5}' -f $k))) }
        $out = & icacls.exe (Join-Path $root 'u*') /deny "*${sid}:(RD)" 2>&1
        $denied = $true
        if ($LASTEXITCODE -ne 0) { throw "icacls /deny on $root\u* exited ${LASTEXITCODE}: $out" }
        $caption = "Top 100 of 104 files scanned. $(Format-Unreadable 12345)"

        Write-Output "  the default state, at the window's minimum size"
        $read = Invoke-Sized $root
        if ($null -ne $read -and $null -ne $read.Report) {
            $window = $read.Report.window
            CheckEqual 'Unreadable folders (12,345)' $window.UnreadableTab.header 'UnreadableTab.header'
            CheckLargestFiles $read 100 $KiB64 $caption
            CheckEqual 1 $window.OutcomeTabs.stripRows 'the headers take one row at the minimum width (OutcomeTabs.stripRows)'
            CheckEqual $true $window.UnreadableTab.inView "UnreadableTab's header is inside the window"
            CheckBothLists $window $false $true
            CheckFit $window
        }

        Write-Output "  the unreadable folders selected, at the window's minimum size"
        $run = Invoke-WithHook 'expand' $HookDll { Read-Window $root } @{ FSA_HOOK_MINSIZE = '1'; FSA_HOOK_DRIVES = "$ExtraDrives" }
        $before = $script:Failures.Count
        CheckActed $run 'expand'
        CheckSized $run.Log
        if ($script:Failures.Count -gt $before -or $null -eq $run.Result.Report) { return }
        $window = $run.Result.Report.window
        CheckLargestFiles $run.Result 100 $KiB64 $caption $false
        CheckEqual 1 $window.OutcomeTabs.stripRows 'the headers still take one row with the unreadable folders selected'
        CheckBothLists $window $true $true
        CheckTabsKeptHeight $run.Log $window
    }
    finally {
        if ($denied) { & icacls.exe (Join-Path $root 'u*') /remove:d "*$sid" 2>&1 | Out-Null }
        Remove-CaseFolder $case
    }
}

# S-008 (SL-065, design C-009 and C-010): the lists at the window's minimum size and maximized. B-007's tree, whose
# largest files are 100 rows, far more than the tabs hold: each list stays within the tabs' height and builds only a
# screenful of rows. Stopped, the outcome has 3 of every 4 px left below the pickers, so maximized the tabs get the
# extra height, with no band left under them by the texts' cap; completed, the folder tree has most of it and the
# outcome keeps its minimum, maximized too.
function Test-S008 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-TopsTree $root

        Write-Output "  Cancelled, at the window's minimum size"
        Invoke-StoppedEnding $case $root $false $true $ExtraDrives
        $floor = $script:StoppedReport
        if ($null -ne $floor) { CheckFit $floor.window }

        Write-Output '  Cancelled, maximized'
        Invoke-StoppedEnding $case $root $false $false $ExtraDrives $null $true
        $max = $script:StoppedReport
        if ($null -ne $max) {
            CheckFit $max.window
            if ($null -ne $floor) {
                Check ($max.window.OutcomeTabs.actualHeight -gt $floor.window.OutcomeTabs.actualHeight + 100) "maximized, the tabs get the extra height ($($max.window.OutcomeTabs.actualHeight) px, against $($floor.window.OutcomeTabs.actualHeight) at the minimum size)"
                Check ($max.window.LargestFilesList.fullRows -gt $floor.window.LargestFilesList.fullRows) "and the largest files show more full rows ($($max.window.LargestFilesList.fullRows), against $($floor.window.LargestFilesList.fullRows))"
            }
        }

        Write-Output '  Cancelled, the unreadable folders selected, maximized'
        Invoke-StoppedEnding $case $root $true $false $ExtraDrives $null $true
        if ($null -ne $script:StoppedReport) { CheckFit $script:StoppedReport.window }

        Write-Output '  Completed, maximized'
        $read = Invoke-Sized $root @{ FSA_HOOK_MAXIMIZE = '1' }
        if ($null -ne $read -and $null -ne $read.Report) {
            CheckEqual "Finished: $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
            CheckLargestFiles $read 100 $KiB64 "Top 100 of 104 files scanned. $(Format-Unreadable 12)"
            CheckBothLists $read.Report.window $false $true
            CheckFit $read.Report.window
        }
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# S-009 (SL-065, design C-011): a second scan that refills the unreadable folders' list while their tab is not selected,
# and was never selected: the list is out of the visual tree and has never been laid out. The first scan meets two
# unreadable folders; one is deleted before the second (FSA_HOOK_READABLE), and the tab is selected only once the second
# ending is on screen (FSA_HOOK_AFTER). The report reads the refilled lists without failing, and the tab shows the
# second scan's one folder, not the first scan's two.
function Test-S009 {
    $case = New-CaseFolder
    $denied = @()
    try {
        $root = Join-Path $case 'tree'
        $denied = New-SectionsTree $root 2
        $run = Invoke-WithHook 'rescan' $HookDll { Read-Window $root } @{ FSA_HOOK_READABLE = $denied[1]; FSA_HOOK_AFTER = 'UnreadableTab' }
        $before = $script:Failures.Count
        CheckActed $run 'rescan'
        if ($script:Failures.Count -gt $before) { return }
        Check (@($run.Log | Where-Object { $_.StartsWith('before ') }).Count -eq 0) 'the unreadable folders were not selected before the second scan'
        $after = @($run.Log | Where-Object { $_.StartsWith('after selected=') })
        Check ($after.Count -eq 1 -and (Field $after[0] 'selected') -ceq 'UnreadableTab' -and (Field $after[0] 'unreadableRows') -eq '1') "after the second ending the tab shows its one row ($(Show ($after -join ' | ')))"
        $read = $run.Result
        CheckEqual 'EXIT 0' $read.Exit 'exit code (70 would be a throw reading a list out of the visual tree)'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        $window = $read.Report.window
        CheckEqual 'Completed' $read.Report.core.status 'the report is of the second scan (core.status)'
        CheckLargestFiles $read 100 $KiB64 "Top 100 of 104 files scanned. $(Format-Unreadable 1)" $false
        CheckEqual 'Unreadable folders (1)' $window.UnreadableTab.header 'UnreadableTab.header: the second scan''s count'
        CheckEqual 'UnreadableTab' $window.OutcomeTabs.selected 'OutcomeTabs.selected'
        CheckEqual $true $window.UnreadableList.inView 'UnreadableList is on screen'
        CheckEqual 1 $window.UnreadableList.fullRows 'UnreadableList shows the one row of the second scan, not the two of the first'
        CheckEqual 0 $window.LargestFilesList.fullRows 'LargestFilesList, refilled and now off screen, shows no row'
    }
    finally {
        foreach ($folder in $denied) { Undo-DenyListing $folder }
        Remove-CaseFolder $case
    }
}

# ---- The Windows container layer store (SL-095) ----
#
# Both read this PC's real store, %ProgramData%\Microsoft\Windows\Containers\Layers, through the published exe and
# eng/read-window.ps1, and neither opens anything in it: X-001's root is refused by the string check alone, and X-002's
# scan stops at the store when it pops it. On a PC with no store they fail saying SKIPPED, never pass: a green run
# there would hide that the guard was never exercised (C-024). Not in All, like T-005: they need this PC's store.

$StoreRoot = Join-Path $env:ProgramData 'Microsoft\Windows\Containers\Layers'

function Test-StorePresent {
    if (Test-Path -LiteralPath $StoreRoot -PathType Container) { return $true }
    Check $false "SKIPPED: this PC has no Windows container layer store at $StoreRoot, so the guard was not exercised"
    $false
}

# X-001 (C-039): a root at the store, in three spellings, is refused: the report says outcome refused and refusal
# container-layer-store, exits 0, nothing was counted, and the window says why and how to turn the precaution off.
function Test-X001 {
    if (-not (Test-StorePresent)) { return }
    foreach ($root in @($StoreRoot, ($StoreRoot + '\'), $StoreRoot.ToLowerInvariant())) {
        $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec 120 -Outcome)
        CheckEqual 'EXIT 0' $lines[0] "exit code for $root"
        $outcome = @($lines | Where-Object { $_ -like 'OUTCOME *' })
        CheckEqual 1 $outcome.Count "read-window -Outcome printed its line for $root"
        if ($outcome.Count -ne 1) { continue }
        Check ($outcome[0] -like 'OUTCOME outcome=refused refusal=container-layer-store skippedStores=0 opensUnderExcludedStore=0 *') "the outcome line for $root ($(Show $outcome[0]))"
        $report = ($lines | Select-Object -Skip 1 | Where-Object { $_ -notlike 'OUTCOME *' }) -join "`n" | ConvertFrom-Json
        CheckEqual 'Refused' $report.core.status "core.status for $root"
        CheckEqual 0 $report.core.files "core.files for ${root}: nothing was read"
        CheckEqual 0 $report.core.folders "core.folders for $root"
        Check ($report.window.OutcomeTitle -like 'Not scanned: *') "OutcomeTitle says not scanned ($(Show $report.window.OutcomeTitle))"
        Check (($report.window.OutcomeDetail -join ' ') -like "*can't be scanned safely*Precautions*") 'OutcomeDetail says why and where the precaution is turned off'
        CheckEqual $true @($report.precautions | Where-Object { $_.id -eq 'wcifs-container-layers' })[0].on 'the precaution was on'
    }
}

# X-002 (C-010, C-025, review M5, X1): the store's parent, ...\Containers, scanned to the end. The store is one folder
# left out on purpose: skippedStores 1, never in the unreadable count; the backstop refused nothing; the window says
# so beside the totals and in its own row, under a tab whose header counts it apart from unreadable folders.
function Test-X002 {
    if (-not (Test-StorePresent)) { return }
    $root = Split-Path -Parent $StoreRoot
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec 120 -Outcome)
    CheckEqual 'EXIT 0' $lines[0] 'exit code'
    $outcome = @($lines | Where-Object { $_ -like 'OUTCOME *' })
    CheckEqual 1 $outcome.Count 'read-window -Outcome printed its line'
    if ($outcome.Count -ne 1) { return }
    Check ($outcome[0] -like 'OUTCOME outcome=completed refusal=- skippedStores=1 opensUnderExcludedStore=0 unreadable=*') "the outcome line ($(Show $outcome[0]))"
    $report = ($lines | Select-Object -Skip 1 | Where-Object { $_ -notlike 'OUTCOME *' }) -join "`n" | ConvertFrom-Json
    CheckEqual 'Completed' $report.core.status 'core.status'
    CheckEqual 1 $report.core.skippedStores 'core.skippedStores: the store, once'
    CheckEqual 0 $report.core.opensUnderExcludedStore 'core.opensUnderExcludedStore'
    $unreadable = [int]$report.core.unreadable
    $expectedHeader = if ($unreadable -eq 0) { 'Not scanned (1)' } else { "Unreadable folders ($unreadable), not scanned (1)" }
    CheckEqual $expectedHeader $report.window.UnreadableTab.header 'UnreadableTab.header counts the store apart from the unreadable folders'
    Check (@($report.window.OutcomeNotes.lines | Where-Object { $_ -like "Not scanned, size not counted: $StoreRoot (Windows container layers)*" }).Count -eq 1) "OutcomeNotes names the store and why ($(Show ($report.window.OutcomeNotes.lines -join ' | ')))"
    Check ($report.window.OutcomeNotes.visible) 'OutcomeNotes is on screen'
}

# X-004 (C-025, C-010, reviews M5, X1): a report that holds the store left out and unreadable folders as well, from
# the store's grandparent, ...\Microsoft\Windows, where this user is refused some folders. The two are counted apart:
# core.unreadable counts only the failed folders, core.skippedStores the store, and the tab header names both. Not in
# All, like X-001 and X-002: it needs this PC's store.
function Test-X004 {
    if (-not (Test-StorePresent)) { return }
    $root = Split-Path -Parent (Split-Path -Parent $StoreRoot)
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec 300 -Outcome)
    CheckEqual 'EXIT 0' $lines[0] 'exit code'
    $outcome = @($lines | Where-Object { $_ -like 'OUTCOME *' })
    CheckEqual 1 $outcome.Count 'read-window -Outcome printed its line'
    if ($outcome.Count -ne 1) { return }
    Write-Output "        $($outcome[0])"
    $report = ($lines | Select-Object -Skip 1 | Where-Object { $_ -notlike 'OUTCOME *' }) -join "`n" | ConvertFrom-Json
    CheckEqual 'Completed' $report.core.status 'core.status'
    CheckEqual 1 $report.core.skippedStores 'core.skippedStores: the store, once'
    CheckEqual 0 $report.core.opensUnderExcludedStore 'core.opensUnderExcludedStore'
    $unreadable = [int]$report.core.unreadable
    Check ($unreadable -gt 0) "the scan met unreadable folders as well, the shape this case is about ($unreadable)"
    CheckEqual "Unreadable folders ($unreadable), not scanned (1)" $report.window.UnreadableTab.header 'UnreadableTab.header counts the two apart'
    Check (@($report.window.OutcomeNotes.lines | Where-Object { $_ -like "Not scanned, size not counted: $StoreRoot (Windows container layers)*" }).Count -eq 1) 'OutcomeNotes names the store beside the totals'
}

# X-003 (C-026, reviews m6, N5): the hook's progress triggers, which PoolWatch -Protocol's cancelled and closed runs
# depend on, fire on the published exe, on a %TEMP% tree that outlasts many 250 ms ticks (New-LargeTree, 30,150
# folders). A dead trigger found on the fresh boot would spend the only cold state there is, so each is proved here:
# stop at a folder count presses Cancel at or past it; stop past a folder logs where the walk passed it and presses
# Cancel the set number of folders after; close at a folder count closes the window mid-scan, which writes no report
# (exit 70, WindowReport), so its hook log is the run's record. Touches no store: nothing here is near one.
function Test-X003 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        $null = New-LargeTree $root
        $report = Join-Path $case 'report.json'

        $stop = Invoke-HookedRun 'stop' $root $report @{ FSA_HOOK_AT_FOLDERS = '3000' }
        CheckActed $stop 'stop'
        CheckEqual '0' $stop.Exit 'stop at 3,000 folders: exit code'
        $acted = @($stop.Log | Where-Object { $_.StartsWith('acted mode=stop ') })
        if ($acted.Count -eq 1) {
            Check ([long](Field $acted[0] 'folders') -ge 3000) "Cancel was pressed at or past 3,000 folders ($(Field $acted[0] 'folders'))"
        }
        CheckEqual 'cancelled' $stop.Report.outcome 'stop at 3,000 folders: the report says cancelled'
        Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue

        $past = Join-Path $root 'n1'
        $passed = Invoke-HookedRun 'stop' $root $report @{ FSA_HOOK_PAST = $past; FSA_HOOK_PAST_FOLDERS = '500' }
        CheckActed $passed 'stop'
        $passedLine = @($passed.Log | Where-Object { $_.StartsWith('passed folders=') })
        CheckEqual 1 $passedLine.Count "the hook logged once where the walk passed $past"
        $acted = @($passed.Log | Where-Object { $_.StartsWith('acted mode=stop ') })
        if ($passedLine.Count -eq 1 -and $acted.Count -eq 1) {
            $at = [long](Field $passedLine[0] 'folders')
            Check ([long](Field $acted[0] 'folders') -ge $at + 500) "Cancel came 500 folders or more after the walk passed n1 (passed at $at, pressed at $(Field $acted[0] 'folders'))"
            Check ((Field $passedLine[0] 'current') -notlike "$past\*" -and (Field $passedLine[0] 'current') -ne $past) "the folder being listed when it passed was not n1 or under it ($(Field $passedLine[0] 'current'))"
        }
        CheckEqual 'cancelled' $passed.Report.outcome 'stop past n1: the report says cancelled'
        Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue

        $close = Invoke-HookedRun 'close' $root $report @{ FSA_HOOK_AT_FOLDERS = '3000' }
        CheckActed $close 'close'
        CheckEqual '70' $close.Exit 'close at 3,000 folders: exit 70, a window closed before its ending'
        Check ($null -eq $close.Report) 'close at 3,000 folders: no report, so the hook log is the record'
        Check (@($close.Log | Where-Object { $_.StartsWith('closed ') }).Count -eq 1) 'the hook saw the window close'
    }
    finally { Remove-CaseFolder $case }
}
# ---- Run ----

$cases = if ($Case -eq 'All') { @('C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-012', 'C-013', 'C-014', 'O-016', 'L-001', 'L-002', 'L-003', 'B-001', 'B-002', 'B-003', 'B-004', 'B-005', 'B-006', 'B-007', 'B-008', 'V-001', 'P-001', 'T-001', 'T-002', 'T-003', 'T-004', 'T-006', 'T-007', 'T-008', 'K-001', 'K-002', 'K-003', 'R-001', 'R-002', 'S-001', 'S-002', 'S-003', 'S-004', 'S-005', 'S-006', 'S-007', 'S-008', 'S-009', 'X-003') + $NetworkCases } else { @($Case) }
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
