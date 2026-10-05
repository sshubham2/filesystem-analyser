# PoolWatch.ps1's own cases (SL-095, stack decision: a case script in the WindowCases idiom, no Pester). C-053 to C-056,
# C-027 and P-001 to P-003 run -Report over hand-built JSONL run logs in %TEMP%: no scan, no restart, nothing read from
# the machine. Each is the clean, complete run with one thing changed, and says what -Report must list. Across every
# fixture no output line may carry a judgement word: the narrowed slice judges no tolerance, and SC-128 does (fitness
# rule 'PoolWatch reports completeness, never a verdict'). C-011 asks -Protocol to start on this boot, which is too old
# for it, and checks that it refuses having written nothing. C-052 runs -Protocol -Rehearse end to end over a generated
# %TEMP% tree: about five minutes, the published app run ten times, and the scratch deleted afterwards.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/PoolWatchCases.ps1 -Case C-053
#
# Exit 0 when every case named passed, 1 otherwise.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-011', 'C-027', 'C-052', 'C-053', 'C-054', 'C-055', 'C-056', 'P-001', 'P-002', 'P-003')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'
$PoolWatch = Join-Path $PSScriptRoot 'PoolWatch.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$Judgement = '\b(PASS|FAIL|VOID|OUTCOME)\b'

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
    foreach ($l in $lines) { Write-Output "        | $l" }
    $judged = @($lines | Where-Object { $_ -cmatch $Judgement })
    Check ($judged.Count -eq 0) "no line carries a judgement word ($($judged -join ' | '))"
    [pscustomobject]@{ Exit = $exit; Lines = $lines }
}

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
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('fsa-poolwatch\case-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
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
    Write-Output "    $what"
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
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('fsa-poolwatch\case-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
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
        Check (@(Get-Process FsAnalyser.App -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-2) }).Count -eq 0) 'it started no FsAnalyser.App'
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

# P-003 (review R2-n2): what -Report says of an arm -Protocol was stopped during. X3's /before mark is in marks.jsonl but
# its arm line was never written (the old ending, before the arm's finally); F2's line was written by that finally,
# marked interrupted, with no exit and no later marks.
function Test-P003 {
    $r = Invoke-Fixture 'X3 started but has no arm line; F2 interrupted' {
        param($run)
        $run.Arms.Remove((Arm $run 'X3')) | Out-Null
        $f2 = Arm $run 'F2'; $f2.interrupted = $true; $f2.exitCode = $null; $f2.report = $null
        foreach ($label in 'F2/exit', 'F2/settled') { $run.Marks.Remove((Mark $run $label)) | Out-Null }
    }
    Check-Incomplete $r @('arm X3: started (its /before mark is in marks.jsonl) but its arm line was never written',
        'arm F2: interrupted: -Protocol stopped while it ran, and FsAnalyser.exe was killed')
    Check ((Lines-Like $r '  MISSING arm X3: in the plan but not in arms.jsonl*').Count -eq 0) 'X3 is not said to have never started'
    Check ((Lines-Like $r '  MISSING arm F2: mark*').Count -eq 0) 'the interrupted arm is named as interrupted, not by its missing marks'
}

# C-052 (review F5): -Protocol -Rehearse end to end over a generated %TEMP% tree. -Report must read RUN COMPLETE, which
# needs the plan line, R0, both idle windows' marks, and /before, /exit and /settled on all ten exe arms, each cancel
# and close acted by the hook mid-scan; and the scratch must be gone afterwards. When it is not complete, the hook's
# refused lines are in the MISSING lines echoed above, so a trigger that lost the race tells itself apart from a harness
# defect.
function Test-C052 {
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

$cases = if ($Case -eq 'All') { @('C-053', 'C-054', 'C-055', 'C-056', 'C-027', 'P-001', 'P-002', 'P-003', 'C-011', 'C-052') } else { @($Case) }
$failed = @()
foreach ($key in $cases) {
    Write-Output $key
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
