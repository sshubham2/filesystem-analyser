# T-005 as a batch of runs (SL-080): does the 860 px default window height make the stall at the first move after a
# scan starts worse, and what is the UI thread blocked under while it lasts. Each run is tests/window/WindowCases.ps1
# -Case T-005, unmodified, with TEMP and TMP on the drive given (T-005's tree then lives there: spike
# stall-rides-competing-cpu-load, 18 of 18 runs over 250 ms on D:), read back through its FSA_T005_SUMMARY line.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/T005Batch.ps1 -Plan Height|Trace|Render [-Drive D:] [-Out <folder>]
#        [-HighHeight 860] [-LowHeight 644] [-SampleMs <n>] [-Reread]
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/T005Batch.ps1 -SelfTest [-Only <fixture>]
#
# Everything goes to -Out (default artifacts/sl080/<plan>-<time>, gitignored): runs.jsonl, run-<n>.log, the traces and
# stacks-<n>.json, and verdict.json. Exit 0 with a verdict (inconclusive and no-stall-this-batch are verdicts), 1 when
# the batch stopped, 2 when the hook, the app or dotnet-trace could not be built or installed. T-005's own failures
# (its 1 s bar) are data, not errors. -Reread runs no T-005: it reads the runs.jsonl and traces already in -Out again
# under the same rules, for a batch whose reading failed after its runs. Either way every scheduled run must have
# exactly one summary line that stands in its pair, or the batch stops; a live batch refuses an -Out that already holds
# a runs.jsonl. -Out is made absolute. A run's process gets none of this shell's FSA_T005_*, FSA_HOOK_*,
# DOTNET_EventPipe*, COMPlus_EventPipe* (with DOTNET_ and COMPlus_EnableEventPipe) or DOTNET_STARTUP_HOOKS
# variables, only its arm's.
#
# The schedules. Run 0 is a warm-up, run and discarded. Then blocks of four, in adjacent pairs, the arms swapped in
# the second pair of each block (ABBA), so a stall that drifts over minutes lands on both arms alike:
#   Height  4 blocks of HighHeight, LowHeight, LowHeight, HighHeight, untraced: 8 pairs.
#   Trace   2 blocks of traced, untraced, untraced, traced, at HighHeight: 4 pairs.
#   Render  2 blocks of software, hardware, hardware, software, at HighHeight, untraced: 4 pairs.
# LowHeight is 644, not the 620 of the pre-SL-044 default: SL-047 raised the window's MinHeight to 644, so a 620
# window cannot be had, and the hook refuses it rather than run it at 644 (FSA_HOOK_HEIGHT, "height not applied").
#
# The batch stops, exit 1, rather than leave a hole in a pair, when a run writes no summary line; ends any way but the
# hook closing the window (a crash, a timeout, a hook error, a refused height); ran at another height or render mode
# than its arm, or was traced (wrote a trace) when its arm is not, or not when it is; or never reached its scan or
# rescan phase (a null phase, never averaged as 0). The drive must be a fixed NTFS volume.
#
# The rules, fixed before any run. A run's figure is primaryMs: the larger of its scan's and its rescan's longest
# wait (the probe's, every 10 ms). No other phase is decided on. A run stalls at 250 ms or more.
#   Height (8 pairs, ratio = the high arm's primaryMs over the low arm's in each pair)
#     no-stall-this-batch       fewer than 4 of the 16 runs stall: run it again, never read as no difference
#     <high>-worsens            high higher in at least 7 of 8 pairs and a median ratio of at least 1.2
#     <high>-improves           high higher in at most 1 of 8 pairs and a median ratio of at most 0.83
#     no-measurable-difference  high higher in 3 to 5 of 8 pairs and a median ratio in [0.83, 1.2]
#     inconclusive              anything else
#     orderEffect (a flag)      the first run of a pair higher in at least 7, or at most 1, of 8 pairs, whatever the arm
#   With it: the spread (standard deviation) of the pairs' log ratios, and detectableRatio, the smallest ratio the
#   batch could have told from 1: exp((t(0.975, n-1) + t(0.80, n-1)) * sd / sqrt(n)), a paired test at 5% two-sided
#   with 80% power (review m6). A no-measurable-difference is quoted with it, never as "does not worsen it".
#   Trace (4 pairs; the traced runs read by StallStacks.ps1)
#     tracing-masks             untraced runs stall in at least 3 of 4 while traced ones stall in at most 1: the
#                               sampler hides the stall. Next: this plan once more with -SampleMs 10 (a sample every
#                               10 ms instead of 1, review M5); if it masks again, the in-process fallback. The interval
#                               is the one the traced runs' own traces record (StallStacks' samplingNs), whatever
#                               -SampleMs this reading was given; -SampleMs only when no trace could be read, and traced
#                               runs sampled at different intervals stop the reading, exit 1 (review R2-m7).
#     no-stall-this-batch       both at most 1 of 4
#     inconclusive              fewer than 3 traced runs stalled
#     cannot-read               2 or more of the stalling traced runs CANNOT-READ. Next: the in-process fallback
#                               (FSA_HOOK_MSGLOG, whose contract is in docs/design/SL-080-move-stall.md).
#     render-path               3 or more stalling traced runs RENDER-PATH. Next: -Plan Render.
#     NAMED                     3 or more stalling traced runs NAMED with one blockedUnder
#     SPLIT                     anything else: the traced runs disagree
#   Render (4 pairs, ratio = software's primaryMs over hardware's). WPF in software still makes the UI thread wait in
#   SyncFlush while the render thread draws, now on the CPU; so the readings, stated before any run:
#     no-stall-this-batch       hardware runs stall in fewer than 2 of 4
#     gpu-present-side          software lower in 4 of 4 pairs and a median ratio of at most 0.6: the wait is on the
#                               GPU or its present, which software takes away
#     render-thread-or-dwm-not-gpu  a median ratio in [0.83, 1.2]: the wait is the render thread's or the desktop
#                               compositor's work on a resize, and not the GPU's, since taking the GPU away changed nothing
#     software-slower           software higher in 4 of 4 pairs and a median ratio of at least 1.2: the wait is the
#                               render thread drawing, which the CPU does slower; not the GPU either
#     inconclusive              anything else

param(
    [ValidateSet('Height', 'Trace', 'Render')]
    [string] $Plan,
    [string] $Drive = 'D:',
    [string] $Out,
    [int] $HighHeight = 860,
    [int] $LowHeight = 644,
    [int] $SampleMs = 0,
    [switch] $Reread,
    [switch] $SelfTest,
    [string] $Only
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Cases = Join-Path $PSScriptRoot 'WindowCases.ps1'
$StackReader = Join-Path $PSScriptRoot 'StallStacks.ps1'
$FixtureDir = Join-Path $PSScriptRoot 'fixtures\T005Batch'
$Stall = 250.0
$RunTimeoutSec = 900

# t quantiles by degrees of freedom (1 to 15): 0.975 and 0.80.
$T975 = @(0, 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131)
$T80 = @(0, 1.376, 1.061, 0.978, 0.941, 0.920, 0.906, 0.896, 0.889, 0.883, 0.879, 0.876, 0.873, 0.870, 0.868, 0.866)

function Say([string] $text) { Write-Host $text }

# ---- The schedule ----

function New-Arm([int] $height, [bool] $trace, [string] $render) { [ordered]@{ height = $height; trace = $trace; render = $render } }

function New-Schedule([string] $plan, [int] $high, [int] $low) {
    $warm = New-Arm $high $false 'hardware'
    switch ($plan) {
        'Height' { $block = @((New-Arm $high $false 'hardware'), (New-Arm $low $false 'hardware'), (New-Arm $low $false 'hardware'), (New-Arm $high $false 'hardware')); $blocks = 4 }
        'Trace' { $block = @((New-Arm $high $true 'hardware'), (New-Arm $high $false 'hardware'), (New-Arm $high $false 'hardware'), (New-Arm $high $true 'hardware')); $blocks = 2 }
        'Render' { $block = @((New-Arm $high $false 'software'), (New-Arm $high $false 'hardware'), (New-Arm $high $false 'hardware'), (New-Arm $high $false 'software')); $blocks = 2 }
    }
    [pscustomobject]@{ run = 0; pair = 0; position = 0; warmup = $true; arm = $warm }
    for ($b = 0; $b -lt $blocks; $b++) {
        for ($k = 0; $k -lt 4; $k++) {
            [pscustomobject]@{ run = 1 + 4 * $b + $k; pair = 1 + 2 * $b + [int][Math]::Floor($k / 2); position = 1 + $k % 2; warmup = $false; arm = $block[$k] }
        }
    }
}

# ---- Running it ----

function Read-Runs([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return @() }
    @(Get-Content -LiteralPath $path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -Depth 16 -DateKind String })
}

# Why a run's line cannot stand in its pair, or $null.
function Test-RunLine($line, $item) {
    $scan = @($line.phases | Where-Object name -eq 'scan') | Select-Object -First 1
    $rescan = @($line.phases | Where-Object name -eq 'rescan') | Select-Object -First 1
    $software = $line.renderMode -eq 'SoftwareOnly'
    if ($line.run -ne $item.run) { "the newest summary line is run $($line.run), not run $($item.run)" }
    elseif ($line.exitReason -ne 'closed') { "run $($item.run) did not end with the hook closing the window: $($line.exitReason) (exit $($line.exitCode))" }
    elseif ($null -eq $line.actualHeight -or [Math]::Abs([double]$line.actualHeight - $item.arm.height) -gt 1) { "run $($item.run) ran at $(if ($null -eq $line.actualHeight) { 'no reported height' } else { "$($line.actualHeight) px" }), not its arm's $($item.arm.height) px" }
    elseif ($software -ne ($item.arm.render -eq 'software')) { "run $($item.run) rendered $(if ($software) { 'in software' } else { "in $($line.renderMode)" }), not its arm's $($item.arm.render)" }
    elseif (($null -ne $line.traceFile) -ne [bool]$item.arm.trace) { "run $($item.run) was $(if ($null -ne $line.traceFile) { 'traced' } else { 'not traced (no trace written)' }), not its arm's $(if ($item.arm.trace) { 'traced' } else { 'untraced' })" }
    elseif (-not $scan.reached) { "run $($item.run): the hook never reached its scan phase (a null phase would leave a hole in its pair)" }
    elseif (-not $rescan.reached) { "run $($item.run): the hook never reached its rescan phase (a null phase would leave a hole in its pair)" }
    else { $null }
}

# Runs the schedule, one run at a time, and stops at the first run that cannot stand in its pair.
function Invoke-Batch($schedule, [scriptblock] $runner, [string] $summary) {
    $lastEnd = $null
    foreach ($item in $schedule) {
        $before = @(Read-Runs $summary).Count
        $gap = if ($null -ne $lastEnd) { [Math]::Round(([DateTime]::UtcNow - $lastEnd).TotalSeconds, 1) } else { $null }
        Say ("run {0}{1}: {2} px, {3}, {4}" -f $item.run, $(if ($item.warmup) { ' (warm-up)' } else { " (pair $($item.pair), position $($item.position))" }), $item.arm.height, $(if ($item.arm.trace) { 'traced' } else { 'untraced' }), $item.arm.render)
        & $runner $item $gap
        $lastEnd = [DateTime]::UtcNow
        $lines = @(Read-Runs $summary)
        $problem = if ($lines.Count -ne $before + 1) { "run $($item.run) wrote no summary line" } else { Test-RunLine $lines[-1] $item }
        if ($problem) {
            Say "  STOP  $problem"
            return [pscustomobject]@{ Stopped = $true; Reason = $problem; Run = $item.run }
        }
        Say ("  primary {0:N1} ms ({1}); scan {2:N1} ms, rescan {3:N1} ms" -f $lines[-1].primaryMs, $lines[-1].primaryPhase,
            (@($lines[-1].phases | Where-Object name -eq 'scan'))[0].longestWaitMs, (@($lines[-1].phases | Where-Object name -eq 'rescan'))[0].longestWaitMs)
    }
    [pscustomobject]@{ Stopped = $false; Reason = $null; Run = $null }
}

# Every scheduled run resolved to exactly one summary line that can stand in its pair, whether the batch just ran or
# is read again (-Reread); a missing, doubled, stray or invalid line stops it (SL-080 code review m1).
function Resolve-Lines($schedule, $lines) {
    $stop = { param($run, $why) [pscustomobject]@{ Stopped = $true; Reason = $why; Run = $run; Lines = $null } }
    foreach ($line in $lines) {
        if (@($schedule | Where-Object run -eq $line.run).Count -eq 0) { return & $stop $line.run "a summary line for run $($line.run), which this plan's schedule does not have" }
    }
    foreach ($item in $schedule) {
        $mine = @($lines | Where-Object run -eq $item.run)
        if ($mine.Count -eq 0) { return & $stop $item.run "run $($item.run) has no summary line" }
        if ($mine.Count -gt 1) { return & $stop $item.run "run $($item.run) has $($mine.Count) summary lines" }
        if (Test-RunLine $mine[0] $item) { return & $stop $item.run (Test-RunLine $mine[0] $item) }
    }
    [pscustomobject]@{ Stopped = $false; Reason = $null; Run = $null; Lines = @($lines) }
}

# What a run's process is given of this shell's environment: none of the variables that choose an arm or trace it,
# so a shell left with one set cannot trace an untraced run or change its rate or render mode (SL-080 review R2-m4).
# The runtime reads COMPlus_EnableEventPipe and COMPlus_EventPipe* as it reads the DOTNET_ forms (review m6). -match
# ignores case, as Windows does in a variable's name.
$ArmVariables = '^(FSA_T005_|FSA_HOOK_|(DOTNET|COMPlus)_(EventPipe|EnableEventPipe$)|DOTNET_STARTUP_HOOKS$)'

# One real run: WindowCases.ps1 -Case T-005 as its own process, with its variables given to it alone.
function Invoke-T005Run($item, $gap, [string] $out, [string] $temp, [int] $sampleMs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach ($a in '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Cases, '-Case', 'T-005') { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($key in @($psi.Environment.Keys | Where-Object { $_ -match $ArmVariables })) { [void]$psi.Environment.Remove($key) }
    $psi.Environment['TEMP'] = $temp
    $psi.Environment['TMP'] = $temp
    $psi.Environment['FSA_T005_SUMMARY'] = Join-Path $out 'runs.jsonl'
    $psi.Environment['FSA_T005_HEIGHT'] = [string]$item.arm.height
    $psi.Environment['FSA_T005_TAG'] = [ordered]@{ run = $item.run; pair = $item.pair; position = $item.position; warmup = $item.warmup; arm = $item.arm; idleGapS = $gap } | ConvertTo-Json -Compress
    if ($item.arm.trace) {
        $psi.Environment['FSA_T005_TRACE'] = Join-Path $out 'traces'
        if ($sampleMs -gt 0) { $psi.Environment['FSA_T005_SAMPLE_MS'] = [string]$sampleMs }
    }
    if ($item.arm.render -eq 'software') { $psi.Environment['FSA_T005_RENDER'] = 'software' }
    $process = [System.Diagnostics.Process]::Start($psi)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEndAsync()
    if (-not $process.WaitForExit($RunTimeoutSec * 1000)) { $process.Kill($true); $process.WaitForExit() }
    Set-Content -LiteralPath (Join-Path $out "run-$($item.run).log") -Value ($output.Result + $errors.Result + "EXIT $($process.ExitCode)") -Encoding utf8NoBOM
}

# One traced run's stacks through StallStacks.ps1 ($script). Its stacks-<n>.json from an earlier reading is deleted
# first, and a reader that fails is CANNOT-READ with its own words, never the file an earlier reading left (SL-080
# code review R2-m5).
function Invoke-StackReader([string] $script, $line, [string] $out) {
    $file = Join-Path $out "stacks-$($line.run).json"
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    $output = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $script -Trace "$($line.traceFile)" -Summary (Join-Path $out 'runs.jsonl') -Run $line.run -Out $file 2>&1)
    $exit = $LASTEXITCODE
    if ($exit -ne 0 -or -not (Test-Path -LiteralPath $file)) {
        $why = (@($output | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) | Select-Object -Last 3) -join ' | '
        return [pscustomobject]@{ run = $line.run; verdict = 'CANNOT-READ'; blockedUnder = $null; reason = "StallStacks.ps1 exited $exit and wrote no stacks: $why" }
    }
    Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -Depth 16
}

# ---- The verdicts ----

function Get-Median([double[]] $values) {
    $s = @($values | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    if ($s.Count % 2) { $s[($s.Count - 1) / 2] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
}

# The pairs as the schedule made them, each run's line found by its run number; A is the arm the ratio is of.
function Get-Pairs($schedule, $lines, [scriptblock] $isA) {
    foreach ($group in @($schedule | Where-Object { -not $_.warmup } | Group-Object pair | Sort-Object { [int]$_.Name })) {
        $items = @($group.Group | Sort-Object position)
        $first = @($lines | Where-Object run -eq $items[0].run)[0]
        $second = @($lines | Where-Object run -eq $items[1].run)[0]
        $aFirst = & $isA $items[0]
        $a, $b = if ($aFirst) { $first, $second } else { $second, $first }
        [pscustomobject]@{
            pair        = [int]$group.Name
            runs        = @($items[0].run, $items[1].run)
            aFirst      = $aFirst
            aMs         = [double]$a.primaryMs
            bMs         = [double]$b.primaryMs
            ratio       = [double]$a.primaryMs / [Math]::Max(0.001, [double]$b.primaryMs)
            firstHigher = [double]$first.primaryMs -gt [double]$second.primaryMs
        }
    }
}

function Get-Spread($pairs) {
    $n = @($pairs).Count
    $logs = @($pairs | ForEach-Object { [Math]::Log($_.ratio) })
    $mean = ($logs | Measure-Object -Average).Average
    $sd = if ($n -gt 1) { [Math]::Sqrt((($logs | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Sum).Sum / ($n - 1)) } else { $null }
    $df = [Math]::Min($n - 1, 15)
    [ordered]@{
        logRatioSd      = if ($null -ne $sd) { [Math]::Round($sd, 3) } else { $null }
        detectableRatio = if ($null -ne $sd -and $df -ge 1) { [Math]::Round([Math]::Exp(($T975[$df] + $T80[$df]) * $sd / [Math]::Sqrt($n)), 2) } else { $null }
    }
}

function Get-HeightVerdict($pairs, $lines, [int] $high) {
    $n = @($pairs).Count
    $stalls = @($lines | Where-Object { [double]$_.primaryMs -ge $Stall }).Count
    $higher = @($pairs | Where-Object { $_.ratio -gt 1 }).Count
    $firstHigher = @($pairs | Where-Object firstHigher).Count
    $median = Get-Median @($pairs | ForEach-Object ratio)
    $verdict = if ($stalls -lt 4) { 'no-stall-this-batch' }
    elseif ($higher -ge [Math]::Ceiling(7 * $n / 8) -and $median -ge 1.2) { "$high-worsens" }
    elseif ($higher -le [Math]::Floor($n / 8) -and $median -le 0.83) { "$high-improves" }
    elseif ($higher -ge [Math]::Ceiling(3 * $n / 8) -and $higher -le [Math]::Floor(5 * $n / 8) -and $median -ge 0.83 -and $median -le 1.2) { 'no-measurable-difference' }
    else { 'inconclusive' }
    $result = [ordered]@{
        verdict     = $verdict
        stalls      = "$stalls of $(@($lines).Count) runs at $Stall ms or more"
        higher      = "$high px higher in $higher of $n pairs"
        medianRatio = [Math]::Round($median, 3)
        orderEffect = $firstHigher -ge [Math]::Ceiling(7 * $n / 8) -or $firstHigher -le [Math]::Floor($n / 8)
        firstHigher = "the first run of its pair higher in $firstHigher of $n"
    }
    foreach ($p in (Get-Spread $pairs).GetEnumerator()) { $result[$p.Key] = $p.Value }
    $result.next = if ($verdict -eq 'no-stall-this-batch') { 'run -Plan Height again: a quiet batch answers nothing' } else { $null }
    $result
}

# The interval the traced runs were sampled at, in ms: from their own traces' headers (samplingNs in each run's stacks),
# so a batch read again without its original -SampleMs is still routed on the rate it ran at; this reading's -SampleMs
# only when no stacks have one. Traced runs that disagree are a problem, not a rate (SL-080 code review R2-m7).
function Get-SampleRate($stacks, [int] $sampleMs) {
    $rates = @($stacks.Values | Where-Object { $null -ne $_ -and $null -ne $_.samplingNs } | ForEach-Object { [double]$_.samplingNs / 1e6 } | Sort-Object -Unique)
    if ($rates.Count -gt 1) { return [pscustomobject]@{ Ms = $null; Source = $null; Problem = "the traced runs were sampled at different intervals ($($rates -join ', ') ms), so no one rule applies to them" } }
    if ($rates.Count -eq 1) { return [pscustomobject]@{ Ms = $rates[0]; Source = 'the traces'; Problem = $null } }
    [pscustomobject]@{ Ms = [double]$(if ($sampleMs -gt 0) { $sampleMs } else { 1 }); Source = '-SampleMs'; Problem = $null }
}

function Get-TraceVerdict($lines, $stacks, $rate) {
    $traced = @($lines | Where-Object { $_.arm.trace })
    $untraced = @($lines | Where-Object { -not $_.arm.trace })
    $tStall = @($traced | Where-Object { [double]$_.primaryMs -ge $Stall })
    $uStalls = @($untraced | Where-Object { [double]$_.primaryMs -ge $Stall }).Count
    $read = @(foreach ($line in $tStall) { $stacks["$($line.run)"] })
    $count = { param($v) @($read | Where-Object { $_.verdict -eq $v }).Count }
    $named = @($read | Where-Object { $_.verdict -eq 'NAMED' } | Group-Object blockedUnder | Sort-Object Count -Descending)
    $fallback = 'the in-process fallback: FSA_HOOK_MSGLOG, whose contract is in docs/design/SL-080-move-stall.md (not built unless this is reached)'
    $verdict, $next = if ($uStalls -ge 3 -and $tStall.Count -le 1) {
        'tracing-masks', $(if ($rate.Ms -lt 10) { 'run -Plan Trace once more with -SampleMs 10' } else { $fallback })
    }
    elseif ($uStalls -le 1 -and $tStall.Count -le 1) { 'no-stall-this-batch', 'run -Plan Trace again: a quiet batch answers nothing' }
    elseif ($tStall.Count -lt 3) { 'inconclusive', 'run -Plan Trace again' }
    elseif ((& $count 'CANNOT-READ') -ge 2) { 'cannot-read', $fallback }
    elseif ((& $count 'RENDER-PATH') -ge 3) { 'render-path', 'run -Plan Render' }
    elseif ($named.Count -gt 0 -and $named[0].Count -ge 3) { 'NAMED', 'a fix slice under the named frame' }
    else { 'SPLIT', 'the traced runs disagree: run -Plan Trace again before reading any one of them' }
    [ordered]@{
        verdict          = $verdict
        blockedUnder     = if ($verdict -eq 'NAMED') { $named[0].Name } else { $null }
        tracedStalls     = "$($tStall.Count) of $($traced.Count)"
        untracedStalls   = "$uStalls of $($untraced.Count)"
        tracedMedianMs   = Get-Median @($traced | ForEach-Object { [double]$_.primaryMs })
        untracedMedianMs = Get-Median @($untraced | ForEach-Object { [double]$_.primaryMs })
        sampleMs         = $rate.Ms
        sampleMsFrom     = $rate.Source
        stacks           = @($read | ForEach-Object { [ordered]@{ run = $_.run; verdict = $_.verdict; blockedUnder = $_.blockedUnder; reason = $_.reason } })
        next             = $next
    }
}

function Get-RenderVerdict($pairs, $lines) {
    $n = @($pairs).Count
    $hardware = @($lines | Where-Object { $_.arm.render -ne 'software' })
    $hwStalls = @($hardware | Where-Object { [double]$_.primaryMs -ge $Stall }).Count
    $lower = @($pairs | Where-Object { $_.ratio -lt 1 }).Count
    $median = Get-Median @($pairs | ForEach-Object ratio)
    $verdict = if ($hwStalls -lt 2) { 'no-stall-this-batch' }
    elseif ($lower -eq $n -and $median -le 0.6) { 'gpu-present-side' }
    elseif ($median -ge 0.83 -and $median -le 1.2) { 'render-thread-or-dwm-not-gpu' }
    elseif ($lower -eq 0 -and $median -ge 1.2) { 'software-slower' }
    else { 'inconclusive' }
    $result = [ordered]@{
        verdict        = $verdict
        hardwareStalls = "$hwStalls of $($hardware.Count)"
        softwareLower  = "software lower in $lower of $n pairs"
        medianRatio    = [Math]::Round($median, 3)
    }
    foreach ($p in (Get-Spread $pairs).GetEnumerator()) { $result[$p.Key] = $p.Value }
    $result
}

# The batch, then its verdict; $runner runs one item, $reader reads one traced run's stacks. Returns the exit code.
function Invoke-Plan([string] $plan, $schedule, [scriptblock] $runner, [scriptblock] $reader, [string] $out, [int] $sampleMs, [int] $high, [bool] $reread = $false) {
    $summary = Join-Path $out 'runs.jsonl'
    $verdictFile = Join-Path $out 'verdict.json'
    if (-not $reread -and (Test-Path -LiteralPath $summary)) {
        Say "FAILED: $summary already holds a batch, whose lines this one's would be paired with; give a new -Out, or -Reread to read that one again"
        return 1
    }
    $batch = if ($reread) { [pscustomobject]@{ Stopped = $false } } else { Invoke-Batch $schedule $runner $summary }
    if (-not $batch.Stopped) { $batch = Resolve-Lines $schedule @(Read-Runs $summary) }
    $lines = if (-not $batch.Stopped) { @($batch.Lines | Where-Object { -not $_.warmup }) }
    $stacks = @{}
    if (-not $batch.Stopped -and $plan -eq 'Trace') {
        foreach ($line in @($lines | Where-Object { $_.arm.trace })) {
            Say "reading the stacks of run $($line.run)"
            $stacks["$($line.run)"] = & $reader $line $out
        }
        $rate = Get-SampleRate $stacks $sampleMs
        if ($rate.Problem) { $batch = [pscustomobject]@{ Stopped = $true; Reason = $rate.Problem; Run = $null } }
    }
    if ($batch.Stopped) {
        [ordered]@{ plan = $plan; verdict = 'stopped'; stoppedAt = $batch.Run; reason = $batch.Reason } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verdictFile -Encoding utf8NoBOM
        Say "STOPPED$(if ($null -ne $batch.Run) { " at run $($batch.Run)" }): $($batch.Reason)"
        return 1
    }
    $result = switch ($plan) {
        'Height' { Get-HeightVerdict @(Get-Pairs $schedule $lines { param($i) $i.arm.height -eq $high }) $lines $high }
        'Render' { Get-RenderVerdict @(Get-Pairs $schedule $lines { param($i) $i.arm.render -eq 'software' }) $lines }
        'Trace' { Get-TraceVerdict $lines $stacks $rate }
    }
    $record = [ordered]@{ plan = $plan }
    foreach ($p in $result.GetEnumerator()) { $record[$p.Key] = $p.Value }
    if ($plan -ne 'Trace') {
        $isA = if ($plan -eq 'Height') { { param($i) $i.arm.height -eq $high } } else { { param($i) $i.arm.render -eq 'software' } }
        $record.pairs = @(Get-Pairs $schedule $lines $isA | ForEach-Object { [ordered]@{ pair = $_.pair; runs = $_.runs; aFirst = $_.aFirst; aMs = [Math]::Round($_.aMs, 1); bMs = [Math]::Round($_.bMs, 1); ratio = [Math]::Round($_.ratio, 3) } })
    }
    $record.runs = @($lines | ForEach-Object {
            [ordered]@{ run = $_.run; pair = $_.pair; position = $_.position; arm = $_.arm; primaryMs = $_.primaryMs; primaryPhase = $_.primaryPhase; idleGapS = $_.idleGapS
                longestMoveMs = @($_.phases | Where-Object { $_.name -in 'scan', 'rescan' } | ForEach-Object { $_.longestMoveMs }) }
        })
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verdictFile -Encoding utf8NoBOM
    Say "VERDICT ($plan): $($result.verdict)$(if ($result.next) { "; next: $($result.next)" })"
    0
}

# ---- Self-test ----

# A fixture is a batch that already happened: the plan, the summary line each run writes (null: it writes none), what
# StallStacks.ps1 read in each traced run, and what must come of it. The same Invoke-Plan runs it, with a runner that
# appends the fixture's line instead of running T-005 and a reader that returns the fixture's stacks.
# ConvertTo-T005Summary and Value-Of, taken from WindowCases.ps1 by its parser, which runs a case when it is run.
function Import-Summary {
    if (Get-Command ConvertTo-T005Summary -ErrorAction SilentlyContinue) { return }
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Cases, [ref]$null, [ref]$null)
    foreach ($name in 'Value-Of', 'ConvertTo-T005Summary') {
        $definition = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name }, $false)
        if ($null -eq $definition) { throw "WindowCases.ps1 has no function $name" }
        . ([scriptblock]::Create($definition.Extent.Text.Replace("function $name", "function script:$name")))
    }
}

function Invoke-SelfTest([string] $only) {
    $files = @(Get-ChildItem -LiteralPath $FixtureDir -Filter '*.json' | Sort-Object Name)
    if ($only) { $files = @($files | Where-Object { $_.BaseName -eq $only }) }
    $variables = -not $only -or $only -eq 'arm-variables'
    if ($files.Count -eq 0 -and -not $variables) { Write-Output "FAILED: no fixture$(if ($only) { " named $only" }) in $FixtureDir"; $script:SelfTestFailed = $true; return }
    $failed = 0
    # Not a batch: the names Invoke-T005Run takes out of a run's environment, in both of the runtime's prefixes and any
    # case, and names it leaves (SL-080 code review m6).
    if ($variables) {
        $stripped = 'FSA_T005_TRACE', 'FSA_T005_SAMPLE_MS', 'FSA_HOOK_HEIGHT', 'DOTNET_STARTUP_HOOKS', 'DOTNET_EnableEventPipe', 'DOTNET_EventPipeOutputPath',
        'DOTNET_EventPipeThreadSamplingRate', 'COMPlus_EnableEventPipe', 'COMPlus_EventPipeConfig', 'COMPlus_EventPipeOutputPath', 'complus_eventpipeoutputstreaming', 'Dotnet_EnableEventPipe'
        $kept = 'TEMP', 'TMP', 'PATH', 'DOTNET_ROOT', 'DOTNET_gcServer', 'COMPlus_gcServer', 'DOTNET_EnableEventPipeX', 'COMPlus_STARTUP_HOOKSX'
        $wrong = @(@($stripped | Where-Object { $_ -notmatch $ArmVariables } | ForEach-Object { "$_ is given to the run" }) + @($kept | Where-Object { $_ -match $ArmVariables } | ForEach-Object { "$_ is taken from the run" }))
        if ($wrong.Count -eq 0) { Write-Output "  ok    arm-variables: $($stripped.Count) taken out, $($kept.Count) left" }
        else { $failed++; Write-Output "  FAIL  arm-variables: $($wrong -join '; ')" }
    }
    foreach ($file in $files) {
        $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 32 -DateKind String
        $out = Join-Path ([System.IO.Path]::GetTempPath()) ("sl080-batch-selftest\{0}" -f [guid]::NewGuid().ToString('N'))
        [void][System.IO.Directory]::CreateDirectory($out)
        $ran = [System.Collections.Generic.List[int]]::new()
        $lineOf = @{}
        foreach ($p in $fixture.lines.PSObject.Properties) { $lineOf[[int]$p.Name] = $p.Value }
        # A run given as the hook's own log is summarised by WindowCases.ps1's ConvertTo-T005Summary, as T-005 does.
        $low = if ($fixture.low) { [int]$fixture.low } else { 644 }
        $schedule = @(New-Schedule $fixture.plan 860 $low)
        foreach ($p in @($fixture.hookLogs.PSObject.Properties)) {
            if ($null -eq $p) { continue }
            Import-Summary
            $item = @($schedule | Where-Object run -eq ([int]$p.Name))[0]
            $tag = [ordered]@{ run = $item.run; pair = $item.pair; position = $item.position; warmup = $item.warmup; arm = $item.arm; idleGapS = 5.0 } | ConvertTo-Json -Compress | ConvertFrom-Json
            $log = @($p.Value.log)
            $run = [pscustomobject]@{ Exit = [string]$p.Value.exit; Log = $log; Lines = @($log | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json }) }
            $lineOf[[int]$p.Name] = ConvertTo-T005Summary $run $tag '2026-09-29T00:00:00.0000000Z' $null | ConvertTo-Json -Depth 16 | ConvertFrom-Json -Depth 16 -DateKind String
        }
        $stackOf = $fixture.stacks
        # A batch already in -Out: its lines (and any extra ones) for -Reread, or a stray one a live batch must refuse.
        $summaryPath = Join-Path $out 'runs.jsonl'
        if ($fixture.reread) {
            foreach ($k in @($lineOf.Keys | Sort-Object)) { if ($null -ne $lineOf[$k]) { Add-Content -LiteralPath $summaryPath -Value ($lineOf[$k] | ConvertTo-Json -Compress -Depth 16) -Encoding utf8NoBOM } }
            foreach ($x in @($fixture.extraLines)) { if ($null -ne $x) { Add-Content -LiteralPath $summaryPath -Value ($x | ConvertTo-Json -Compress -Depth 16) -Encoding utf8NoBOM } }
        }
        if ($fixture.existing) { Add-Content -LiteralPath $summaryPath -Value '{"run":0}' -Encoding utf8NoBOM }
        $runner = {
            param($item, $gap)
            $ran.Add($item.run)
            if ($null -ne $lineOf[$item.run]) { Add-Content -LiteralPath (Join-Path $out 'runs.jsonl') -Value ($lineOf[$item.run] | ConvertTo-Json -Compress -Depth 16) -Encoding utf8NoBOM }
        }.GetNewClosure()
        $reader = { param($line, $o) $stackOf."$($line.run)" }.GetNewClosure()
        if ($fixture.crashReader) {
            # The real reader, over a StallStacks.ps1 that throws, with an earlier reading's stacks left in -Out.
            $crash = Join-Path $out 'crash.ps1'
            Set-Content -LiteralPath $crash -Value "throw 'the reader broke'" -Encoding utf8NoBOM
            foreach ($k in $lineOf.Keys) { Set-Content -LiteralPath (Join-Path $out "stacks-$k.json") -Value "{`"run`":$k,`"verdict`":`"NAMED`",`"blockedUnder`":`"an earlier reading`"}" -Encoding utf8NoBOM }
            $reader = { param($line, $o) Invoke-StackReader $crash $line $o }
        }
        try { $code = Invoke-Plan $fixture.plan $schedule $runner $reader $out ([int]$fixture.sampleMs) 860 ([bool]$fixture.reread) 6>$null }
        catch { $code = "threw: $($_.Exception.Message)" }
        $verdict = if (Test-Path -LiteralPath (Join-Path $out 'verdict.json')) { Get-Content -LiteralPath (Join-Path $out 'verdict.json') -Raw | ConvertFrom-Json -Depth 16 } else { $null }
        $e = $fixture.expect
        $problems = @(
            if ([string]$code -ne [string]$e.exit) { "exit $code, expected $($e.exit)" }
            if ($e.noVerdict) { if ($null -ne $verdict) { "a verdict.json was written ($($verdict.verdict))" } }
            elseif ($null -eq $verdict) { 'no verdict.json' }
            elseif ($verdict.verdict -cne $e.verdict) { "verdict $($verdict.verdict), expected $($e.verdict)" }
            if ($null -ne $e.ran -and $ran.Count -ne [int]$e.ran) { "ran $($ran.Count) runs, expected $($e.ran)" }
            if ($e.stackReason -and @($verdict.stacks | Where-Object { "$($_.reason)" -notmatch $e.stackReason }).Count -gt 0) { "a run's stacks do not match '$($e.stackReason)': $(@($verdict.stacks | ForEach-Object reason) -join ' / ')" }
            if ($null -ne $e.orderEffect -and $verdict.orderEffect -ne $e.orderEffect) { "orderEffect $($verdict.orderEffect), expected $($e.orderEffect)" }
            if ($e.stoppedAt -ne $null -and $verdict.stoppedAt -ne $e.stoppedAt) { "stopped at $($verdict.stoppedAt), expected $($e.stoppedAt)" }
            if ($e.stoppedAt -ne $null -and -not $fixture.reread -and ($ran.Count -eq 0 -or $ran[-1] -ne $e.stoppedAt)) { "ran up to run $($ran[-1]), expected to stop at $($e.stoppedAt)" }
            if ($e.reason -and "$($verdict.reason)" -notmatch $e.reason) { "reason '$($verdict.reason)' does not match '$($e.reason)'" }
            if ($e.next -and "$($verdict.next)" -notmatch $e.next) { "next '$($verdict.next)' does not match '$($e.next)'" }
            if ($e.blockedUnder -and $verdict.blockedUnder -cne $e.blockedUnder) { "blockedUnder $($verdict.blockedUnder), expected $($e.blockedUnder)" }
            if ($null -ne $e.sampleMs -and $verdict.sampleMs -ne $e.sampleMs) { "sampleMs $($verdict.sampleMs) (from $($verdict.sampleMsFrom)), expected $($e.sampleMs)" }
            if ($e.detectable -and -not ($verdict.detectableRatio -gt 1)) { "no detectableRatio above 1 ($($verdict.detectableRatio))" }
            foreach ($n in @($e.nullPhases)) {
                if ($null -eq $n) { continue }
                $line = $lineOf[[int]$e.stoppedAt]
                $phase = @($line.phases | Where-Object name -eq $n)[0]
                if ($null -eq $phase -or $phase.reached -or $null -ne $phase.longestWaitMs -or $null -ne $line.primaryMs) { "the summary wrote $n as reached=$($phase.reached) longestWaitMs=$($phase.longestWaitMs) primaryMs=$($line.primaryMs), not unreached and null" }
            }
        )
        Remove-Item -LiteralPath $out -Recurse -Force
        if ($problems.Count -eq 0) {
            Write-Output ("  ok    {0}: exit {1}, {2}{3}" -f $file.BaseName, $code, $verdict.verdict, $(if ($verdict.reason) { " ($($verdict.reason))" } elseif ($verdict.next) { " (next: $($verdict.next))" } else { '' }))
        }
        else {
            $failed++
            Write-Output "  FAIL  $($file.BaseName): $($problems -join '; ')"
            if ($null -ne $verdict) { Write-Output ($verdict | ConvertTo-Json -Depth 8) }
        }
    }
    $total = $files.Count + [int]$variables
    if ($failed -gt 0) { Write-Output "FAILED: $failed of $total fixtures"; $script:SelfTestFailed = $true; return }
    Write-Output "PASSED: $total fixtures"
}

$script:SelfTestFailed = $false
if ($SelfTest) {
    Invoke-SelfTest $Only
    exit $(if ($script:SelfTestFailed) { 1 } else { 0 })
}
if (-not $Plan) { throw 'T005Batch.ps1 -Plan Height|Trace|Render, or -SelfTest' }

# ---- A real batch ----

$letter = $Drive.TrimEnd(':', '\')
$volume = Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue
if ($null -eq $volume -or $volume.FileSystem -ne 'NTFS' -or $volume.DriveType -ne 'Fixed') {
    Write-Output "FAILED: $Drive is not a fixed NTFS volume ($(if ($volume) { "$($volume.DriveType) $($volume.FileSystem)" } else { 'no such volume' })), so no run started"
    exit 1
}
if (-not $Out) { $Out = Join-Path $Repo ("artifacts\sl080\{0}-{1:yyyyMMdd-HHmmss}" -f $Plan.ToLowerInvariant(), (Get-Date)) }
# Absolute: each run's app works in %TEMP%, where a relative folder is another one and its traces are lost (SL-080 code
# review R2-M2).
$Out = [System.IO.Path]::GetFullPath($Out, (Get-Location).ProviderPath)
[void][System.IO.Directory]::CreateDirectory($Out)
$temp = "${letter}:\fsa-sl080-temp"
[void][System.IO.Directory]::CreateDirectory($temp)

# Built once here, so a failure is exit 2 and not a missing summary line; each run's own Update-Hook and Update-App
# then find nothing to do.
$dotnet = Join-Path $Repo 'eng\dotnet.ps1'
$built = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $dotnet build (Join-Path $PSScriptRoot 'FsaWindowHook\FsaWindowHook.csproj') -c Release -o (Join-Path $Repo 'artifacts\window-hook') 2>&1)
if ($LASTEXITCODE -ne 0) { $built | ForEach-Object { "  $_" }; Write-Output 'FAILED: the hook did not build'; exit 2 }
$exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe'
$newest = (@(Get-ChildItem -LiteralPath (Join-Path $Repo 'src') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
    @(Get-Item -LiteralPath (Join-Path $Repo 'Directory.Build.props'), (Join-Path $Repo 'global.json')) | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
if (-not (Test-Path -LiteralPath $exe) -or (Get-Item -LiteralPath $exe).LastWriteTimeUtc -lt $newest) {
    $published = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $dotnet publish (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Join-Path $Repo 'artifacts\publish') 2>&1)
    if ($LASTEXITCODE -ne 0) { $published | ForEach-Object { "  $_" }; Write-Output 'FAILED: the app did not publish'; exit 2 }
}
if ($Plan -eq 'Trace') {
    $tool = Join-Path $Repo 'artifacts\tools\dotnet-trace\dotnet-trace.exe'
    if (-not (Test-Path -LiteralPath $tool)) {
        $installed = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $dotnet tool install dotnet-trace --tool-path (Split-Path -Parent $tool) --version 10.0.745401 2>&1)
        if ($LASTEXITCODE -ne 0) { $installed | ForEach-Object { "  $_" }; Write-Output 'FAILED: dotnet-trace could not be installed'; exit 2 }
    }
}

$schedule = @(New-Schedule $Plan $HighHeight $LowHeight)
Say "T-005 batch: -Plan $Plan on $Drive ($temp), $($schedule.Count) runs, out $Out"
$runner = { param($item, $gap) Invoke-T005Run $item $gap $Out $temp $SampleMs }
$reader = { param($line, $o) Invoke-StackReader $StackReader $line $o }
exit (Invoke-Plan $Plan $schedule $runner $reader $Out $SampleMs $HighHeight $Reread.IsPresent)
