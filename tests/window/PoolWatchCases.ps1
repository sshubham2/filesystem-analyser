# PoolWatch.ps1's own cases (SL-095, stack decision: a case script in the WindowCases idiom, no Pester). C-053 to C-056,
# C-027 and P-001 to P-003 run -Report over hand-built JSONL run logs in a scratch folder (the temp folder, or
# LocalAppData\Temp when TEMP is under the Windows folder: Get-ScratchParent): no scan, no restart, nothing read from
# the machine. Each is the clean, complete run with one thing changed, and says what -Report must list. P-004 (SL-099)
# checks how tags are recorded and keyed: a fixture whose marks list Ntff before NtfF, Get-Net loaded from PoolWatch.ps1
# with both in its tag list, and one live reading of this PC's pool tag table (about a second, a few KB). Across every
# fixture no output line may carry a judgement word: the narrowed slice judges no tolerance, and SC-128 does (fitness
# rule 'PoolWatch reports completeness, never a verdict'). C-011 asks -Protocol to start on this boot, which is too old
# for it, and checks that it refuses having written nothing. C-052 runs -Protocol -Rehearse end to end over a generated
# %TEMP% tree: about five minutes, the published app run ten times, and the scratch deleted afterwards. Give the command
# that runs C-052 (or All) a timeout of at least 10 minutes: a caller that kills it mid-run leaves the rehearsal's scratch
# behind, which the next rehearsal sweeps only once it is an hour old (review C2).
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/PoolWatchCases.ps1 -Case C-053
#
# Exit 0 when every case named passed, 1 otherwise.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-011', 'C-027', 'C-052', 'C-053', 'C-054', 'C-055', 'C-056', 'P-001', 'P-002', 'P-003', 'P-004')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'
$PoolWatch = Join-Path $PSScriptRoot 'PoolWatch.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$Judgement = '\b(PASS|FAIL|VOID|OUTCOME)\b'

# The temp folder, unless that is at or under the Windows folder (the engine's C:\WINDOWS\TEMP, which a user may create in
# but not list or delete from, so a fixture's cleanup there is a terminating error): then LocalAppData\Temp, as
# tests/rules/RuleCases.ps1 does. C-052 does not use it: it plants beside the scratch -Protocol -Rehearse sweeps, which is
# under GetTempPath() (SL-099 review N8).
function Get-ScratchParent {
    $temp = [System.IO.Path]::GetTempPath()
    $windows = [Environment]::GetFolderPath('Windows').TrimEnd('\')
    $under = ($temp.TrimEnd('\') + '\').StartsWith($windows + '\', [StringComparison]::OrdinalIgnoreCase)
    if (-not $under) { return $temp }
    [System.IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')
}

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# What -Report printed: its exit code and lines, each echoed, and the check that none carries a judgement word.
function Invoke-Report([string] $dir) {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $PoolWatch -Report -RunDir $dir 2>&1 | ForEach-Object { "$_" })
    $exit = $LASTEXITCODE
    foreach ($l in $lines) { Write-Host "        | $l" }
    $judged = @($lines | Where-Object { $_ -cmatch $Judgement })
    Check ($judged.Count -eq 0) "no line carries a judgement word ($($judged -join ' | '))"
    [pscustomobject]@{ Exit = $exit; Lines = $lines }
}

# What a fixture is and what -Report printed for it go to the host, not down the pipeline: a function's Write-Output is
# part of its return value, and Invoke-Report's and Invoke-Fixture's results are the report the checks read (SL-099: the
# text is what tests/bugs/SL-099-ReportTextUnchanged.Tests.ps1 compares across commits).

function Check-Incomplete($report, [string[]] $expected) {
    Check ($report.Exit -eq 2) "-Report exits 2 for an incomplete run (exit $($report.Exit))"
    Check (@($report.Lines | Where-Object { $_ -ceq 'RUN INCOMPLETE' }).Count -eq 1) 'it says RUN INCOMPLETE'
    Check (@($report.Lines | Where-Object { $_ -ceq 'RUN COMPLETE' }).Count -eq 0) 'it never says RUN COMPLETE'
    foreach ($e in $expected) {
        Check (@($report.Lines | Where-Object { $_ -like "  MISSING $e*" }).Count -ge 1) "it lists: $e"
    }
}

# ---- The clean run, as -Protocol writes it ----

$Start = [DateTime]::new(2026, 10, 5, 0, 0, 0, [DateTimeKind]::Utc)

function New-Run {
    [pscustomobject]@{ T = 0.0; Paged = 5000MB; Nonpaged = 3000MB; Marks = [System.Collections.Generic.List[object]]::new(); Arms = [System.Collections.Generic.List[object]]::new()
        FilterPaste = 'not supplied (fixture)' }
}

function Add-Mark($run, [string] $label) {
    $run.Marks.Add([ordered]@{
            kind = 'mark'; label = $label; utc = $Start.AddSeconds($run.T).ToString('o'); uptimeSec = 600 + [long]$run.T
            pagedBytes = $run.Paged; nonpagedBytes = $run.Nonpaged; availableMB = 12000; systemHandles = 9000; system120089 = 1600
            tags = @([ordered]@{ tag = 'WCsc'; pa = 0; pf = 0; pagedBytes = 0; na = 1600; nf = 0; nonpagedBytes = 0 }, [ordered]@{ tag = 'FMfn'; pa = 900000; pf = 0; pagedBytes = 0; na = 0; nf = 0; nonpagedBytes = 0 })
            rehearsal = $false
        })
}

function Add-Arm($run, [string] $name, [string] $type) {
    foreach ($label in "$name/before", "$name/exit", "$name/settled") { Add-Mark $run $label; $run.T += 300; $run.Paged += 1MB }
    $arm = [ordered]@{ kind = 'arm'; arm = $name; type = $type; exitCode = 0; report = [ordered]@{ status = 'Completed'; notScanned = 1; skippedStores = 1; opensUnderExcludedStore = 0 };
        hookLog = @(); acted = $false; foldersAtCancel = $null; ranFull = $false; guardStop = $null; guardAtStart = $false; cascadeFrom = $null; rehearsal = $false }
    if ($type -ne 'exe-full') {
        $mode = if ($type -eq 'exe-close') { 'close' } else { 'stop' }
        $arm.hookLog = @("acted mode=$mode past=C:\Users pastfolders=1000 folders=41000 sized=- current=C:\Windows\WinSxS ms=120000")
        $arm.acted = $true
        $arm.foldersAtCancel = 41000
        if ($type -eq 'exe-close') { $arm.exitCode = 70; $arm.report = $null } else { $arm.report.status = 'Cancelled' }
    }
    $run.Arms.Add($arm)
    $arm
}

function Build-Clean {
    $run = New-Run
    Add-Mark $run 'R0'
    foreach ($label in 'I0/start', 'I0/end') { Add-Mark $run $label; $run.T += 1200 }
    foreach ($n in 1..5) { $null = Add-Arm $run "F$n" 'exe-full' }
    foreach ($n in 1..4) { $null = Add-Arm $run "X$n" 'exe-cancel' }
    $null = Add-Arm $run 'X5' 'exe-close'
    foreach ($label in 'I1/start', 'I1/end') { Add-Mark $run $label; $run.T += 1200 }
    $run
}

# Writes the run's logs, with the plan line first in arms.jsonl and an environment record, to a fresh %TEMP% folder.
function Write-Run($run) {
    $dir = Join-Path (Get-ScratchParent) ('fsa-poolwatch\case-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'prepare') | Out-Null
    $plan = [ordered]@{ kind = 'plan'; utc = $Start.ToString('o'); root = 'C:\'; windows = @('I0', 'I1'); arms = @('F1', 'F2', 'F3', 'F4', 'F5', 'X1', 'X2', 'X3', 'X4', 'X5'); rehearsal = $false }
    @($plan) + @($run.Arms) | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 6 } | Set-Content -LiteralPath (Join-Path $dir 'arms.jsonl') -Encoding UTF8
    $run.Marks | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 6 } | Set-Content -LiteralPath (Join-Path $dir 'marks.jsonl') -Encoding UTF8
    [ordered]@{ kind = 'prepare'; environment = [ordered]@{ os = 'Windows 11 (fixture)'; wcifsVersion = '10.0.26100.9278'; drivers = @(); antivirus = @(); oneDriveRunning = $true; fltmcFilters = $run.FilterPaste } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dir 'prepare\prepare.json') -Encoding UTF8
    $dir
}

# The clean run with $change made to it, and then $files made to the folder it was written to (a paste, a torn line).
function Invoke-Fixture([string] $what, [scriptblock] $change, [scriptblock] $files) {
    Write-Host "    $what"
    $run = Build-Clean
    if ($change) { & $change $run }
    $dir = Write-Run $run
    $script:FixtureDir = $dir
    try {
        if ($files) { & $files $dir }
        Invoke-Report $dir
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

function Check-Complete($report) {
    Check ($report.Exit -eq 0) "-Report exits 0 for a complete run (exit $($report.Exit))"
    Check (@($report.Lines | Where-Object { $_ -ceq 'RUN COMPLETE' }).Count -eq 1) 'it says RUN COMPLETE'
}

function Lines-Like($report, [string] $pattern) { , @($report.Lines | Where-Object { $_ -like $pattern }) }

# What `fltmc filters` prints elevated (its list), and unelevated (its refusal).
$FltmcList = @('', 'Filter Name                     Num Instances    Altitude    Frame', '------------------------------  -------------  ------------  -----',
    'bindflt                                 1       409800         0', 'wcifs                                   1       189900         0')
$FltmcRefusal = @('Filter listing failed with error: 0x80070005', 'Access is denied.')

function Mark($run, [string] $label) { @($run.Marks | Where-Object { $_.label -eq $label })[0] }

function Arm($run, [string] $name) { @($run.Arms | Where-Object { $_.arm -eq $name })[0] }

# ---- Cases ----

# C-053 (review M1): the clean run reads RUN COMPLETE and exits 0; with F3 exited 70 and no report, the logs M1 measured
# reading a pass, -Report names F3 as incomplete.
function Test-C053 {
    $clean = Invoke-Fixture 'the clean run' $null
    Check ($clean.Exit -eq 0) "-Report exits 0 for the complete run (exit $($clean.Exit))"
    Check (@($clean.Lines | Where-Object { $_ -ceq 'RUN COMPLETE' }).Count -eq 1) 'it says RUN COMPLETE'
    Check (@($clean.Lines | Where-Object { $_ -match '^(F[1-5]|X[1-5]|I[01])\s' }).Count -eq 12) 'it prints a raw net row for each of the 10 arms and 2 idle windows'
    $crashed = Invoke-Fixture 'F3 exited 70 with no report' { param($run) $a = Arm $run 'F3'; $a.exitCode = 70; $a.report = $null }
    Check-Incomplete $crashed @('arm F3: a full scan that exited 70, not 0', 'arm F3: a full scan that wrote no report')
}

# C-054 (review M1): an arm the plan names that is not in arms.jsonl, and a run with no exe arm at all. An arm that never
# started has no marks either (one with a /before mark and no line is P-003's).
function Remove-ArmMarks($run, [string] $name) {
    foreach ($m in @($run.Marks | Where-Object { $_.label -like "$name/*" })) { $run.Marks.Remove($m) | Out-Null }
}

function Test-C054 {
    $one = Invoke-Fixture 'X2 never started' { param($run) $run.Arms.Remove((Arm $run 'X2')) | Out-Null; Remove-ArmMarks $run 'X2' }
    Check-Incomplete $one @('arm X2: in the plan but not in arms.jsonl, so it never started', 'the run holds 5 full and 4 cancelled or closed exe runs, not 5 and 5')
    $none = Invoke-Fixture 'no exe arm at all' { param($run) foreach ($a in @($run.Arms)) { Remove-ArmMarks $run $a.arm }; $run.Arms.Clear() }
    Check-Incomplete $none @('arm F1: in the plan but not in arms.jsonl', 'arm X5: in the plan but not in arms.jsonl', 'the run holds 0 full and 0 cancelled or closed exe runs, not 5 and 5')
}

# C-055 (review R2-R3-3): stop runs whose hook refused and never pressed anything: the app shut down before X4's close
# trigger (a crash), and X2's trigger deadline passed while its scan ran to the end (status Completed). And a cancel
# that was pressed but whose report says Completed.
function Test-C055 {
    $refused = Invoke-Fixture 'X4 shut down before its trigger; X2 passed its trigger deadline' {
        param($run)
        $x4 = Arm $run 'X4'; $x4.acted = $false; $x4.foldersAtCancel = $null; $x4.exitCode = 70; $x4.report = $null
        $x4.hookLog = @('refused mode=stop reason=the app shut down before the trigger')
        $x2 = Arm $run 'X2'; $x2.acted = $false; $x2.foldersAtCancel = $null; $x2.report.status = 'Completed'
        $x2.hookLog = @('refused mode=stop reason=the trigger past=C:\Users pastfolders=1000 was not met within 3600 s')
    }
    Check-Incomplete $refused @(
        "arm X4: a cancelled run whose hook never acted: the hook logged 'refused mode=stop reason=the app shut down before the trigger'",
        "arm X2: a cancelled run whose hook never acted: the hook logged 'refused mode=stop reason=the trigger past=C:\Users pastfolders=1000 was not met within 3600 s'")
    $closed = Invoke-Fixture 'X5 closed nothing: the app shut down first' {
        param($run) $x5 = Arm $run 'X5'; $x5.acted = $false; $x5.foldersAtCancel = $null; $x5.hookLog = @('refused mode=close reason=the app shut down before the trigger')
    }
    Check-Incomplete $closed @("arm X5: a closed run whose hook never acted: the hook logged 'refused mode=close reason=the app shut down before the trigger'")
    $uncancelled = Invoke-Fixture 'X1 pressed Cancel but its report says Completed' { param($run) (Arm $run 'X1').report.status = 'Completed' }
    Check-Incomplete $uncancelled @('arm X1: a cancelled run whose report says Completed, not Cancelled')
}

# C-056 (review M1 items 3 and 4, R4-2): F4's settled mark missing; and X1 stopped by the machine guard while X2, the next
# arm, would have started already over it, so it was not started and is named as the cascade, not as its own result.
function Test-C056 {
    $settled = Invoke-Fixture "F4's settled mark missing" { param($run) $run.Marks.Remove(@($run.Marks | Where-Object { $_.label -eq 'F4/settled' })[0]) | Out-Null }
    Check-Incomplete $settled @('arm F4: mark F4/settled is missing')
    $cascade = Invoke-Fixture 'X1 stopped by the guard, X2 started over it' {
        param($run)
        $x1 = Arm $run 'X1'; $x1.guardStop = 'available 2900 MB, pool +310 MiB over X1/before'; $x1.exitCode = -1; $x1.report = $null
        $x2 = Arm $run 'X2'; $x2.guardAtStart = $true; $x2.guardStop = 'available 2800 MB at start'; $x2.cascadeFrom = 'X1'; $x2.exitCode = $null; $x2.report = $null
        foreach ($label in 'X2/exit', 'X2/settled') { $run.Marks.Remove(@($run.Marks | Where-Object { $_.label -eq $label })[0]) | Out-Null }
    }
    Check-Incomplete $cascade @('arm X1: stopped by the machine guard (available 2900 MB, pool +310 MiB over X1/before): paged +1 MiB',
        'arm X2: not measured: guard already exceeded at start (cascade from X1)')
    Check (@($cascade.Lines | Where-Object { $_ -like '  MISSING arm X2: mark*' }).Count -eq 0) 'the cascaded arm is named as the cascade, not by its missing marks'
}

# C-027 (O-041, as the slice was narrowed): one mark missing, a mark with a null field (the class-64 System handle
# reading), no fltmc paste at all, and an idle gap that is a burst far over the others. With no verdict left to print,
# -Report must say RUN INCOMPLETE and name each missing thing, never RUN COMPLETE or a judgement word, and FILTERS
# UNDECIDED; and since it takes no drift out of anything, the burst is printed raw in I0's row, all of it.
function Test-C027 {
    $r = Invoke-Fixture 'F2/exit missing, F3/settled without its 0x120089 count, no fltmc paste, a burst in I0' {
        param($run)
        $run.Marks.Remove((Mark $run 'F2/exit')) | Out-Null
        (Mark $run 'F3/settled').system120089 = $null
        $run.FilterPaste = $null
        # I0 in six gaps of 1 MiB but one of 2 GiB, a burst over 3x the median gap.
        $start = Mark $run 'I0/start'; $end = Mark $run 'I0/end'
        $at = $run.Marks.IndexOf($end)
        $paged = [long]$start.pagedBytes
        foreach ($k in 1..5) {
            $paged += $(if ($k -eq 3) { 2GB } else { 1MB })
            $m = [ordered]@{}; foreach ($key in $start.Keys) { $m[$key] = $start[$key] }
            $m.label = 'I0/t{0:D3}' -f $k; $m.pagedBytes = $paged
            $run.Marks.Insert($at, $m); $at++
        }
        $end.pagedBytes = $paged + 1MB
    }
    Check-Incomplete $r @('arm F2: mark F2/exit is missing', 'arm F3: mark F3/settled has no system120089')
    Check ((Lines-Like $r 'FILTERS UNDECIDED: *').Count -eq 1 -and (Lines-Like $r 'FILTERS recorded*').Count -eq 0) 'no fltmc paste reads FILTERS UNDECIDED, never recorded'
    $i0 = Lines-Like $r 'I0 *'
    Check ($i0.Count -eq 1 -and $i0[0] -match '\b2,053\.0\b') "I0's row prints the burst raw, 2 GiB and 5 MiB with nothing taken out ($($i0 -join ' | '))"
    Check ((Lines-Like $r 'Raw nets, *no drift taken out and no tolerance applied*').Count -eq 1) 'the raw nets say no drift was taken out'
}

# C-011 (O-014, as the slice was narrowed): the protocol on a boot that has already walked the layer, which this boot
# has (it is far over 30 minutes old). -Protocol must refuse before it takes a mark: no plan line, no marks, no arm, so
# no run can pass vacuously on a touched boot. A boot young enough to start the real session is not one this case may
# run on: it says so and fails rather than start a 4-hour protocol of whole-C: scans.
function Test-C011 {
    $uptime = [long]((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds
    if ($uptime -le 1800 + 120) {
        Check $false "this boot is $uptime s old: -Protocol would start the real session here, so the case does not run it"
        return
    }
    $dir = Join-Path (Get-ScratchParent) ('fsa-poolwatch\case-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'prepare') | Out-Null
    try {
        [ordered]@{ kind = 'prepare'; environment = [ordered]@{ os = 'fixture' } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'prepare\prepare.json') -Encoding UTF8
        $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $PoolWatch -Protocol -RunDir $dir 2>&1 | ForEach-Object { "$_" })
        $exit = $LASTEXITCODE
        foreach ($l in $lines) { Write-Output "        | $l" }
        Check ($exit -ne 0) "-Protocol exits non-zero on a boot $uptime s old (exit $exit)"
        Check (@($lines | Where-Object { $_ -match 'this boot is \d+ s old, over 1800 s' }).Count -ge 1) 'it says the boot is too old, and by how much'
        Check (-not (Test-Path -LiteralPath (Join-Path $dir 'marks.jsonl'))) 'it took no mark'
        Check (-not (Test-Path -LiteralPath (Join-Path $dir 'arms.jsonl'))) 'it wrote no plan line and started no arm'
        # Only an app this -Protocol call could have started: its --report file would sit under $dir. Other sessions'
        # window cases and rehearsals, and the person's own copy, are not this case's (review R2-D1).
        $ours = @(Get-CimInstance Win32_Process -Filter "Name='FsAnalyser.App.exe'" | Where-Object { $_.CommandLine -like "*$dir*" })
        Check ($ours.Count -eq 0) "it started no FsAnalyser.App ($($ours.Count) running with a report under $dir)"
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

# P-001 (reviews m1, m2, R2-N3): the fltmc paste in each place a person may put it, and what -Report says of it. The
# environment record holding fltmc's list, and holding its unelevated refusal; then, with nothing in the record, a
# list pasted into the run folder after -Prepare, one pasted into the prepare folder, and a refusal pasted into the run
# folder. Only a list is recorded, and -Report names the file it read.
function Test-P001 {
    $listed = Invoke-Fixture 'the environment record holds the filter list' { param($run) $run.FilterPaste = $FltmcList }
    Check-Complete $listed
    Check ((Lines-Like $listed 'FILTERS recorded: *prepare\prepare.json').Count -eq 1) 'FILTERS recorded, from prepare.json'

    $refused = Invoke-Fixture 'the environment record holds the unelevated refusal' { param($run) $run.FilterPaste = $FltmcRefusal }
    Check-Complete $refused
    Check ((Lines-Like $refused 'FILTERS UNDECIDED: Filter listing failed with error: 0x80070005*').Count -eq 1) 'the refusal reads FILTERS UNDECIDED, quoting it'
    Check ((Lines-Like $refused 'FILTERS recorded*').Count -eq 0) 'the refusal is never recorded'

    $pasted = Invoke-Fixture 'a list pasted into the run folder after -Prepare' $null { param($dir) $FltmcList | Set-Content -LiteralPath (Join-Path $dir 'fltmc.txt') -Encoding UTF8 }
    Check-Complete $pasted
    Check ((Lines-Like $pasted "FILTERS recorded: $(Join-Path $script:FixtureDir 'fltmc.txt')").Count -eq 1) 'FILTERS recorded, naming the run folder''s fltmc.txt'

    $inPrepare = Invoke-Fixture 'a list pasted into the prepare folder after -Prepare' $null { param($dir) $FltmcList | Set-Content -LiteralPath (Join-Path $dir 'prepare\fltmc.txt') -Encoding UTF8 }
    Check ((Lines-Like $inPrepare "FILTERS recorded: $(Join-Path $script:FixtureDir 'prepare\fltmc.txt')").Count -eq 1) 'FILTERS recorded, naming the prepare folder''s fltmc.txt'

    $pastedRefusal = Invoke-Fixture 'a refusal pasted into the run folder' $null { param($dir) $FltmcRefusal | Set-Content -LiteralPath (Join-Path $dir 'fltmc.txt') -Encoding UTF8 }
    Check ((Lines-Like $pastedRefusal "FILTERS UNDECIDED: $(Join-Path $script:FixtureDir 'fltmc.txt') holds no filter list (Filter listing failed*").Count -eq 1) 'a pasted refusal reads FILTERS UNDECIDED, naming the file'
    Check ((Lines-Like $pastedRefusal 'FILTERS recorded*').Count -eq 0) 'a pasted refusal is never recorded'
}

# P-002 (review R2-n1): the endings a kernel-pool session is likely to have. A torn last line in marks.jsonl (a kill
# mid-write), a NUL-filled tail in arms.jsonl (a bugcheck before the data was flushed), and a torn prepare.json. -Report
# must not stop on them: RUN INCOMPLETE naming each line, exit 2, and every raw net it can still print.
function Test-P002 {
    $r = Invoke-Fixture 'a torn marks.jsonl line, a NUL tail on arms.jsonl, a torn prepare.json' $null {
        param($dir)
        [IO.File]::AppendAllText((Join-Path $dir 'marks.jsonl'), '{"kind":"mark","label":"I1/t001","pagedBytes":1,"nonpa')
        $stream = [IO.File]::Open((Join-Path $dir 'arms.jsonl'), 'Append')
        try { $stream.Write((New-Object byte[] 64), 0, 64) } finally { $stream.Dispose() }
        [IO.File]::WriteAllText((Join-Path $dir 'prepare\prepare.json'), '{"kind":"prepare","environment":{"os":"Win')
    }
    Check-Incomplete $r @('marks.jsonl line 36 is not JSON (a torn or unflushed write)', 'arms.jsonl line 12 is not JSON (a torn or unflushed write)')
    Check ((Lines-Like $r '  MISSING arm *').Count -eq 0) 'nothing else is missing: the lines before the tear were all read'
    Check (@($r.Lines | Where-Object { $_ -match '^(F[1-5]|X[1-5]|I[01])\s' -and $_ -notmatch 'no net' }).Count -eq 12) 'it still prints a raw net row for each of the 10 arms and 2 idle windows'
    Check ((Lines-Like $r 'ENVIRONMENT not readable: *').Count -eq 1) 'a torn prepare.json is named, and the report goes on'
}

# P-003 (reviews R2-n2, C1): what -Report says of an arm -Protocol was stopped during. X3's /before mark is in
# marks.jsonl but its arm line was never written (the old ending, before the arm's finally). F2's line was written by
# that finally while FsAnalyser.exe still ran: marked interrupted and killed, with no exit and no later marks. F4's was
# written by it during the settle, the likeliest moment to stop a run: the exe had exited 0, its /exit mark and report
# are there, only /settled is missing, and nothing was killed.
function Test-P003 {
    $r = Invoke-Fixture 'X3 started but has no arm line; F2 interrupted and killed; F4 interrupted after its exe exited' {
        param($run)
        $run.Arms.Remove((Arm $run 'X3')) | Out-Null
        $f2 = Arm $run 'F2'; $f2.interrupted = $true; $f2.killed = $true; $f2.exitCode = $null; $f2.report = $null
        foreach ($label in 'F2/exit', 'F2/settled') { $run.Marks.Remove((Mark $run $label)) | Out-Null }
        $f4 = Arm $run 'F4'; $f4.interrupted = $true; $f4.killed = $false
        $run.Marks.Remove((Mark $run 'F4/settled')) | Out-Null
    }
    Check-Incomplete $r @('arm X3: started (its /before mark is in marks.jsonl) but its arm line was never written',
        'arm F2: interrupted while FsAnalyser.exe ran; it was killed',
        'arm F4: interrupted after FsAnalyser.exe exited (exit 0), before its /settled mark')
    Check ((Lines-Like $r '  MISSING arm X3: in the plan but not in arms.jsonl*').Count -eq 0) 'X3 is not said to have never started'
    Check ((Lines-Like $r '  MISSING arm F2: mark*').Count -eq 0) 'the interrupted arm is named as interrupted, not by its missing marks'
    Check ((Lines-Like $r '  MISSING arm F4:*killed*').Count -eq 0) 'F4, whose exe had exited, is never said to have been killed'
    Check ((Lines-Like $r '  MISSING arm F4: mark*').Count -eq 0) 'F4 is named as interrupted, not by its missing /settled mark'

    # SL-099 (SC-130): the same endings on an arm the machine guard had already stopped. Invoke-ExeArm's finally writes
    # interrupted on the arm line the guard stop is on, and -Report must keep the guard's reason and the net at the stop.
    $guard = 'available 2900 MB, pool +310 MiB over {0}/before'
    $stopped = {
        param($run, [string] $name)
        $a = Arm $run $name
        $a.guardStop = $guard -f $name; $a.exitCode = -1; $a.report = $null
        $exit = Mark $run "$name/exit"
        if ($exit) { $exit.pagedBytes = [long](Mark $run "$name/before").pagedBytes + 310MB }
        $a
    }
    $g = Invoke-Fixture 'guard-stopped, then interrupted: X1 in its settle wait, X2 while the exe was being killed, F5 after /settled, F1 with a null reading' {
        param($run)
        # (b) the exe exited -1 (killed by the guard), the interrupt landed in the settle wait: /before and /exit, no /settled.
        $x1 = & $stopped $run 'X1'; $x1.interrupted = $true; $x1.killed = $false
        $run.Marks.Remove((Mark $run 'X1/settled')) | Out-Null
        # (c) the interrupt landed before the kill returned: killed, no exit code, neither /exit nor /settled.
        $x2 = & $stopped $run 'X2'; $x2.interrupted = $true; $x2.killed = $true; $x2.exitCode = $null
        foreach ($label in 'X2/exit', 'X2/settled') { $run.Marks.Remove((Mark $run $label)) | Out-Null }
        # (e) a throw in the report read after /settled: every mark is there, the arm line says interrupted.
        $f5 = & $stopped $run 'F5'; $f5.interrupted = $true; $f5.killed = $false
        # (h) interrupted before /settled, and /exit's 0x120089 reading is null: named on its own line as well.
        $f1 = & $stopped $run 'F1'; $f1.interrupted = $true
        $run.Marks.Remove((Mark $run 'F1/settled')) | Out-Null
        (Mark $run 'F1/exit').system120089 = $null
    }
    Check-Incomplete $g @(
        'arm X1: stopped by the machine guard (available 2900 MB, pool +310 MiB over X1/before): paged +310 MiB, nonpaged 0 MiB from /before to /exit; then interrupted before its /settled mark',
        'arm X2: stopped by the machine guard (available 2900 MB, pool +310 MiB over X2/before): its net at the stop is unread (no X2/exit mark); then interrupted before its /exit mark and killed',
        'arm F5: stopped by the machine guard (available 2900 MB, pool +310 MiB over F5/before): paged +310 MiB, nonpaged 0 MiB from /before to /exit; then interrupted after its /settled mark',
        'arm F1: mark F1/exit has no system120089',
        'arm F1: stopped by the machine guard (available 2900 MB, pool +310 MiB over F1/before): paged +310 MiB, nonpaged 0 MiB from /before to /exit; then interrupted before its /settled mark')
    foreach ($name in 'X1', 'X2', 'F5') {
        Check ((Lines-Like $g "  MISSING arm ${name}:*").Count -eq 1) "$name has exactly one line: the guard stop, with the interrupt as its suffix"
    }
    Check ((Lines-Like $g '  MISSING arm X2:*from /before to /exit*').Count -eq 0) 'X2, whose /exit mark was never written, prints no net'
    Check ((Lines-Like $g '  MISSING arm X1: interrupted*').Count -eq 0 -and (Lines-Like $g '  MISSING arm *interrupted after FsAnalyser.exe exited*').Count -eq 0) 'no guard-stopped arm is reported only as an exe that exited'
    Check ((Lines-Like $g '  MISSING arm *').Count -eq 5) "nothing else is missing ($((Lines-Like $g '  MISSING arm *').Count) lines for 4 arms, F1 with two)"

    # The guard stop with no interrupt: the net, and the /settled mark's own reading checked as at HEAD (a missing mark, a null field).
    $n = Invoke-Fixture 'guard-stopped, not interrupted: X4 with a null /settled pagedBytes, F3 with no /settled mark, F2 with a null /exit tags reading' {
        param($run)
        $x4 = & $stopped $run 'X4'
        (Mark $run 'X4/settled').pagedBytes = $null
        $f3 = & $stopped $run 'F3'
        $run.Marks.Remove((Mark $run 'F3/settled')) | Out-Null
        $f2 = & $stopped $run 'F2'
        (Mark $run 'F2/exit').tags = $null
    }
    Check-Incomplete $n @(
        'arm X4: mark X4/settled has no pagedBytes',
        'arm X4: stopped by the machine guard (available 2900 MB, pool +310 MiB over X4/before): paged +310 MiB, nonpaged 0 MiB from /before to /exit',
        'arm F3: mark F3/settled is missing',
        'arm F3: stopped by the machine guard (available 2900 MB, pool +310 MiB over F3/before): paged +310 MiB, nonpaged 0 MiB from /before to /exit',
        'arm F2: mark F2/exit has no tags',
        'arm F2: stopped by the machine guard (available 2900 MB, pool +310 MiB over F2/before): paged +310 MiB, nonpaged 0 MiB from /before to /exit')
    Check ((Lines-Like $n '  MISSING arm *interrupted*').Count -eq 0) 'an arm that was not interrupted is never said to have been'
    Check ((Lines-Like $n '  MISSING arm *').Count -eq 6) "nothing else is missing ($((Lines-Like $n '  MISSING arm *').Count) lines for 3 arms, two each)"
}

# P-004 (SL-099 reviews M2, M3, M4): how tags are recorded and keyed. NtfF and Ntff, NtxI and NtxF, differ only by case and
# move in opposite directions (spike 107: 917 against 15,895 in one mark), so a lookup or a table that folds case puts one's
# count in the other's column. PoolWatch.ps1 is read through the parser here, never copied: its Tag, Live and Get-Net
# functions, its $WatchedTags list, its native pool-tag reader and Read-Mark.
function Get-PoolWatchAst {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($PoolWatch, [ref]$tokens, [ref]$errors)
    if ($errors -and @($errors).Count) { throw "PoolWatch.ps1 does not parse: $(@($errors)[0].Message)" }
    $ast
}

function Get-PoolWatchFunctions($ast, [string[]] $names) {
    $found = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $names -ccontains $n.Name }, $true))
    foreach ($name in $names) { if (@($found | Where-Object { $_.Name -ceq $name }).Count -ne 1) { throw "PoolWatch.ps1 does not define exactly one function $name" } }
    ($found | ForEach-Object { $_.Extent.Text }) -join "`n"
}

function Get-PoolWatchAssignment($ast, [string] $variable) {
    $found = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.Left.VariablePath.UserPath -ceq $variable }, $true))
    if ($found.Count -ne 1) { throw "PoolWatch.ps1 does not assign `$$variable exactly once ($($found.Count) times)" }
    $found[0].Extent.Text
}

function New-TagEntry([string] $tag, [long] $live) { [ordered]@{ tag = $tag; pa = $live; pf = 0; pagedBytes = 0; na = 0; nf = 0; nonpagedBytes = 0 } }

# The tag array of a mark that lists the lower-case variant first, as the committed build-2026-10-05 marks do, and
# different live counts for each name.
function New-VariantTags([long] $perFile, [long] $perFolder, [long] $fcbFile, [long] $fcbIndex) {
    @((New-TagEntry 'Ntff' $perFile), (New-TagEntry 'NtfF' $perFolder), (New-TagEntry 'NtxF' $fcbFile), (New-TagEntry 'NtxI' $fcbIndex),
        (New-TagEntry 'WCsc' 1600), (New-TagEntry 'FMfn' 900000))
}

function Test-P004 {
    $ast = Get-PoolWatchAst

    # Fixture half, through -Report: F1's NtfF column is NtfF's delta (+7), not Ntff's (+900), with Ntff listed first.
    $r = Invoke-Fixture 'F1 marks list Ntff before NtfF and NtxF before NtxI, with different counts (NtfF +7, Ntff +900)' {
        param($run)
        (Mark $run 'F1/before').tags = New-VariantTags 1000 100 50 10
        (Mark $run 'F1/exit').tags = New-VariantTags 1500 103 60 10
        (Mark $run 'F1/settled').tags = New-VariantTags 1900 107 80 11
    }
    Check-Complete $r
    $f1 = @($r.Lines | Where-Object { $_ -match '^F1\s' })
    $cells = if ($f1.Count -eq 1) { @($f1[0].Trim() -split '\s+') } else { @() }
    Check ($cells.Count -ge 5 -and $cells[-4] -ceq '7') "F1's NtfF column is NtfF's delta, 7, not Ntff's, 900 ($($f1 -join ' | '))"

    # Fixture half, through Get-Net itself, with both names in the tag list: two separate values, and a table that keeps the case.
    $defs = Get-PoolWatchFunctions $ast @('Tag', 'Live', 'Get-Net')
    $program = $defs + "`n" + '$NetTags = @(''NtfF'', ''Ntff'', ''NtxI'', ''NtxF'')' + "`n" + 'Get-Net $args[0] ''A/before'' ''A/after'''
    $before = [pscustomobject]@{ pagedBytes = 1000; nonpagedBytes = 500; systemHandles = 10; system120089 = 5; tags = @(New-VariantTags 1000 100 50 10) }
    $after = [pscustomobject]@{ pagedBytes = 4000; nonpagedBytes = 700; systemHandles = 12; system120089 = 6; tags = @(New-VariantTags 1900 107 80 11) }
    $net = & ([scriptblock]::Create($program)) @{ 'A/before' = $before; 'A/after' = $after }
    Check ($null -ne $net -and $net['NtfF'] -eq 7 -and $net['Ntff'] -eq 900) "Get-Net keeps NtfF and Ntff apart (NtfF $($net['NtfF']), Ntff $($net['Ntff']))"
    Check ($null -ne $net -and $net['NtxI'] -eq 1 -and $net['NtxF'] -eq 30) "Get-Net keeps NtxI and NtxF apart (NtxI $($net['NtxI']), NtxF $($net['NtxF']))"
    Check ($null -ne $net -and $net['paged'] -eq 3000 -and $net['nonpaged'] -eq 200 -and $net['handles'] -eq 2 -and $net['h120089'] -eq 1) 'Get-Net still has its paged, nonpaged, handles and h120089 keys'
    Check ($null -ne $net -and $net.Contains('Ntff') -and -not $net.Contains('ntff') -and -not $net.Contains('NTFF')) 'its table is keyed case-exactly'

    # Live half: the 21 names the reading rule needs, spelled as a literal oracle (never PoolWatch's own list, in which a
    # typo would be a name nobody looks for), against the parsed $WatchedTags, then against one reading of this PC.
    $oracle = @('WCsc', 'WCse', 'WCss', 'WCfc', 'WCri', 'WCfn', 'FMwi',
        'MmSt', 'MmCa', 'FMsl', 'FIcs', 'NtxI', 'Ntfo', 'Ntff', 'NtxF', 'NtFs', 'FSim', 'MmSm', 'Ntf0', 'MmRe', 'Ntce')
    $watched = @(Invoke-Expression ((Get-PoolWatchAssignment $ast 'WatchedTags') -replace '^\$WatchedTags\s*=\s*', ''))
    $absent = @($oracle | Where-Object { $watched -cnotcontains $_ })
    Check ($oracle.Count -eq 21 -and $absent.Count -eq 0) "PoolWatch records all 21 names case-exactly (missing from `$WatchedTags: $($absent -join ', '))"

    # An NTFS boot has these live; the table is read first, because tags appear in it and do not leave it.
    $alwaysLive = @('NtfF', 'Ntff', 'NtxI', 'NtxF', 'Ntfo', 'MmSt', 'MmCa', 'FMsl', 'FIcs', 'File')
    $native = $null
    foreach ($c in @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Add-Type' }, $true))) {
        $here = @($c.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $_.StringConstantType -eq 'SingleQuotedHereString' })
        if ($here.Count -eq 1) { $native = $here[0].Value }
    }
    if ($null -eq $native) { throw 'PoolWatch.ps1 has no Add-Type here-string for PoolWatchNative' }
    $reading = (Get-PoolWatchFunctions $ast @('Get-Boot', 'Get-Pool', 'Read-Mark')) + "`n" + (Get-PoolWatchAssignment $ast 'WatchedTags') + "`n" + '$script:Rehearsing = $false' + "`n" +
    'if (-not (''PoolWatchNative'' -as [type])) { Add-Type -TypeDefinition $args[0] }' + "`n" +
    '$table = @([PoolWatchNative]::PoolTags() | ForEach-Object { $_[0] })' + "`n" +
    '$mark = Read-Mark ''P-004''' + "`n" +
    '[pscustomobject]@{ Table = $table; Tags = @($mark.tags | ForEach-Object { $_.tag }) }'
    $live = & ([scriptblock]::Create($reading)) $native
    $dead = @($alwaysLive | Where-Object { $live.Table -cnotcontains $_ })
    Check ($dead.Count -eq 0) "this PC's pool tag table holds the names an NTFS boot always has live (not in the table: $($dead -join ', '))"
    $skipped = @($oracle | Where-Object { $live.Table -ccontains $_ -and $live.Tags -cnotcontains $_ })
    Check ($skipped.Count -eq 0) "every one of the 21 names that is live in the table is in the mark Read-Mark wrote (live and not recorded: $($skipped -join ', '))"
    Check (@($live.Tags | Where-Object { $_ -ceq 'NtfF' }).Count -eq 1 -and @($live.Tags | Where-Object { $_ -ceq 'Ntff' }).Count -eq 1) 'the mark holds NtfF and Ntff as two entries'
}

# C-052 (review F5): -Protocol -Rehearse end to end over a generated %TEMP% tree. -Report must read RUN COMPLETE, which
# needs the plan line, R0, both idle windows' marks, and /before, /exit and /settled on all ten exe arms, each cancel
# and close acted by the hook mid-scan; and the scratch must be gone afterwards. When it is not complete, the hook's
# refused lines are in the MISSING lines echoed above, so a trigger that lost the race tells itself apart from a harness
# defect. It takes about five minutes: the command running it needs a timeout of at least 10 minutes, or a kill leaves
# the scratch behind (review C2). Rehearsal scratch over an hour old that an earlier kill left is swept as it starts.
function Test-C052 {
    # Two planted leftovers of the shape a killed rehearsal leaves: one last written two hours ago, which the rehearsal
    # must sweep, and one written now, as a rehearsal still running elsewhere would be, which it must leave alone.
    $parent = Join-Path ([IO.Path]::GetTempPath()) 'fsa-poolwatch'
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $stale = Join-Path $parent "rehearse-stale$id"
    $live = Join-Path $parent "rehearse-live$id"
    try {
        foreach ($d in $stale, $live) {
            New-Item -ItemType Directory -Force -Path (Join-Path $d 'run') | Out-Null
            Set-Content -LiteralPath (Join-Path $d 'run\marks.jsonl') -Value '{"kind":"mark"}' -Encoding UTF8
        }
        $old = (Get-Date).AddHours(-2)
        foreach ($p in (Join-Path $stale 'run\marks.jsonl'), (Join-Path $stale 'run'), $stale) { (Get-Item -LiteralPath $p -Force).LastWriteTime = $old }
        Invoke-C052Rehearsal
        Check (-not (Test-Path -LiteralPath $stale)) 'a rehearse-* folder last written two hours ago (a killed rehearsal''s) is swept'
        Check (Test-Path -LiteralPath (Join-Path $live 'run\marks.jsonl')) 'a rehearse-* folder written in the last hour is left alone'
    }
    finally {
        foreach ($d in $stale, $live) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-C052Rehearsal {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $PoolWatch -Protocol -Rehearse 2>&1 | ForEach-Object { "$_" })
    $exit = $LASTEXITCODE
    $shown = @($lines | Where-Object { $_ -match '^(REHEARSAL|RUN |  MISSING|Raw nets|arm |F[1-5] |X[1-5] |I[01] |ENVIRONMENT|FILTERS)' })
    foreach ($l in $shown) { Write-Output "        | $l" }
    $judged = @($shown | Where-Object { $_ -cmatch $Judgement })
    Check ($judged.Count -eq 0) "no report line carries a judgement word ($($judged -join ' | '))"
    Check ($exit -eq 0) "-Protocol -Rehearse exits 0 (exit $exit)"
    Check (@($lines | Where-Object { $_ -ceq 'RUN COMPLETE' }).Count -eq 1) 'its report says RUN COMPLETE'
    Check (@($lines | Where-Object { $_ -like 'REHEARSAL: *' }).Count -eq 1) 'its report is headed REHEARSAL'
    Check (@($lines | Where-Object { $_ -match '^(F[1-5]|X[1-5]|I[01])\s' -and $_ -notmatch 'no net' }).Count -eq 12) 'a raw net row for each of the 10 arms and 2 idle windows'
    $scratch = @($lines | Where-Object { $_ -like 'REHEARSAL scratch: *' }) | Select-Object -First 1
    $path = if ($scratch) { $scratch.Substring('REHEARSAL scratch: '.Length) } else { $null }
    Check ($path -and -not (Test-Path -LiteralPath $path)) "the scratch is deleted ($path)"
    Check (@($lines | Where-Object { $_ -ceq 'REHEARSAL scratch deleted: True' }).Count -eq 1) 'PoolWatch says it deleted it'
}

# ---- Run ----

$cases = if ($Case -eq 'All') { @('C-053', 'C-054', 'C-055', 'C-056', 'C-027', 'P-001', 'P-002', 'P-003', 'P-004', 'C-011', 'C-052') } else { @($Case) }
$failed = @()
foreach ($key in $cases) {
    Write-Output $key
    # Where a case's fixtures go, and the temp folder it was given (C-052 roots its own scratch under GetTempPath()).
    if ($key -ne 'C-052') { Write-Output "  scratch: $(Get-ScratchParent) (TEMP $([IO.Path]::GetTempPath()))" }
    $before = $script:Failures.Count
    try { & "Test-$($key -replace '-', '')" }
    catch {
        Write-Output "  FAIL  $($_.Exception.Message)"
        $script:Failures.Add("$key threw: $($_.Exception.Message)")
    }
    if ($script:Failures.Count -gt $before) { $failed += $key }
}
if ($failed.Count -gt 0) {
    Write-Output "FAILED: $($failed -join ', ')"
    exit 1
}
Write-Output "PASSED: $($cases -join ', ')"
exit 0
