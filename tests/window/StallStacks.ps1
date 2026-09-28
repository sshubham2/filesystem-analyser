# What the UI thread was doing while T-005's probe waited (SL-080). Reads one traced T-005 run: the app's EventPipe
# trace (tests/window/WindowCases.ps1 with FSA_T005_TRACE) and that run's summary line (FSA_T005_SUMMARY), and names
# the call the UI thread was in or blocked under for the longest wait of the scan and of the rescan.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/StallStacks.ps1 -Trace <run.nettrace> -Summary <runs.jsonl> -Run <n> [-Out <file>]
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/StallStacks.ps1 -SelfTest [-Only <fixture>]
#
# It writes one JSON object: {run, uiThread, runtime, traceStartUtc, offsetMs, sampleMs, idleLeaf, idleShare,
# stretches:[{matchedPhase, waitMs, windowStartMs, windowEndMs, startMs, durMs, busyInWindowMs, sinceStartScanMs,
# samples, blockedUnder, chain, under, topLeaves, renderShare, unreadableShare, verdict, reason}], verdict, reason,
# blockedUnder, chain}. The top-level verdict is the primary phase's (the summary's primaryPhase: the larger of the
# two waits, the one figure T005Batch.ps1 decides on).
#
# How it reads a trace, fixed before any trace was read:
#   - The trace goes to Speedscope through dotnet-trace, pinned below and installed by eng/dotnet.ps1 into the
#     gitignored artifacts/tools/dotnet-trace. The UI thread is the profile "Thread (<uiThread>)", the OS id the hook
#     logged. Pseudo frames the converter adds (the process, the thread, CPU_TIME, UNMANAGED_CODE_TIME) are dropped:
#     whether a sample is idle or busy is never read from them, or from the sample's type (review M5).
#   - A sample is idle when one of its three deepest frames is the pump's wait for a message (Dispatcher.GetMessage,
#     GetMessageW). Busy samples closer than three sample intervals make one busy stretch.
#   - The hook's clock is put on the trace's by their two UTC origins: the trace's start from the nettrace header,
#     the hook's clock from its facts line (review m3). The longest wait's window is [maxAt, maxEndAt] on that clock.
#     The stretch matched to it is the busy stretch that overlaps it most, and it must be busy for at least 80% of
#     the wait: time first, duration only as the check. The stretch may run on past the wait's end, because the probe
#     is let through between two messages of one busy stretch, so only the samples inside the wait are read.
#   - Each busy sample is cut below its deepest pump, dispatch or hook frame ($Boundary): none of those is ever a
#     valid blockedUnder (review M2). blockedUnder is the deepest frame, below that cut, in at least 90% of the
#     matched stretch's samples, weighted by time; chain is the frames from the cut down to it.
#   - Per stretch: CANNOT-READ when more than 20% of its samples name nothing below the cut (an unresolved frame,
#     or nothing at all under the pump); SPLIT when no frame below the cut covers 90% ("only the pump is common");
#     RENDER-PATH when the chain reaches WPF's synchronous render (MediaContext.CompleteRender, Channel.SyncFlush) or
#     half its samples are in it, which is not an answer by itself but the trigger for T005Batch.ps1 -Plan Render;
#     otherwise NAMED.
#   - For the whole run: CANNOT-READ when there is no trace, dotnet-trace cannot be installed or run, the UI thread
#     has no samples, the idle share of the UI thread is under 60% (the pump's wait is not what this reader takes it
#     for), or the primary phase matched no stretch.

param(
    [string] $Trace,
    [string] $Summary,
    [int] $Run = -1,
    [string] $Out,
    [switch] $SelfTest,
    [string] $Only
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$DotnetTraceVersion = '10.0.745401'
$ToolDir = Join-Path $Repo 'artifacts\tools\dotnet-trace'
$FixtureDir = Join-Path $PSScriptRoot 'fixtures\StallStacks'

# Frames the converter adds, which are no call of the app's.
$Pseudo = '^(Process(32|64)? .*|Thread \(.*|\(Non-Activities\)|Threads|CPU_TIME|UNMANAGED_CODE_TIME|BLOCKED_TIME|READIED_TIME)$'
# A frame nobody could name.
$Unresolved = '^\?!\?$|!\?$|^\?$|^UNKNOWN|^0x[0-9a-f]+$'
# The pump waiting for a message: an idle UI thread.
$Idle = 'Threading\.Dispatcher\.GetMessage\(|UnsafeNativeMethods\.(Int)?GetMessageW\(|MsgWaitForMultipleObjects'
# The message pump, the dispatcher's plumbing, the window procedure's and the hook's frames: never a blockedUnder.
$Boundary = @(
    '^fsawindowhook(\.il)?!'
    '!FsAnalyser\.App\.App\.Main\('
    'System\.Windows\.Application\.(Run|RunInternal|RunDispatcher)\('
    'Threading\.Dispatcher\.(Run|PushFrame|PushFrameImpl|ProcessQueue|WndProcHook|Invoke|InvokeImpl|LegacyInvokeImpl|WrappedInvoke|InvokeDelegateCore|TranslateAndDispatchMessage)\('
    'Threading\.DispatcherOperation(`1)?\.(Invoke|InvokeImpl|InvokeInSecurityContext|InvokeDelegateCore)\('
    'Threading\.ExceptionWrapper\.(TryCatchWhen|InternalRealCall)\('
    'MS\.Win32\.HwndSubclass\.(SubclassWndProc|DispatcherCallbackOperation|CallOldWindowProc)\('
    'MS\.Win32\.HwndWrapper\.WndProc\('
    'MS\.Win32\.UnsafeNativeMethods\.(Int)?(DispatchMessage|CallWindowProc)'
    'System\.Threading\.ExecutionContext\.(Run|RunInternal)\('
    'MS\.Internal\.CulturePreservingExecutionContext\.(Run|RunInternal|CallbackWrapper)\('
    'ILStubClass\.IL_STUB_ReversePInvoke'
) -join '|'
# WPF's synchronous render: the UI thread waiting on the render thread.
$RenderSync = 'MediaContext\.CompleteRender\(|Channel\.SyncFlush\(|MilCompositionEngine|MilConnection_.*SyncFlush|MILConnection'
$StartScan = 'MainWindow\.StartScan\('

# An ISO time as UTC, to the tick. Summary lines are read with -DateKind String, because ConvertFrom-Json's own
# DateTime came back without its milliseconds once cast back to text (SL-080's first traced run: offset -225 ms, not +204).
function ConvertTo-Utc([string] $iso) { [DateTimeOffset]::Parse($iso, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }

function New-Result([string] $verdict, [string] $reason, [hashtable] $more = @{}) {
    $result = [ordered]@{ verdict = $verdict; reason = $reason }
    foreach ($key in $more.Keys) { $result[$key] = $more[$key] }
    $result
}

# ---- The trace ----

# The nettrace header's own start (UTC, to the millisecond), pid and sampling interval (ns), which dotnet-trace's
# Speedscope does not carry. Its "Trace" object starts with a SYSTEMTIME, then the sync QPC, QPC frequency, pointer
# size, pid, processor count and the sampling interval.
function Read-NettraceHeader([string] $path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $buffer = [byte[]]::new(256)
        $read = $stream.Read($buffer, 0, $buffer.Length)
    }
    finally { $stream.Dispose() }
    $text = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
    $at = $text.IndexOf('Trace', [StringComparison]::Ordinal)
    if (-not $text.StartsWith('Nettrace', [StringComparison]::Ordinal) -or $at -lt 0 -or $buffer[$at + 5] -ne 6) {
        throw "$path has no nettrace header this reader knows"
    }
    $p = $at + 6
    $word = { param($i) [int][BitConverter]::ToUInt16($buffer, $p + 2 * $i) }
    $start = [DateTime]::new((& $word 0), (& $word 1), (& $word 3), (& $word 4), (& $word 5), (& $word 6), (& $word 7), [DateTimeKind]::Utc)
    $p += 16 + 8 + 8 + 4
    [pscustomobject]@{
        StartUtc   = $start
        Pid        = [BitConverter]::ToInt32($buffer, $p)
        SamplingNs = [BitConverter]::ToInt32($buffer, $p + 8)
    }
}

function Get-DotnetTrace {
    $exe = Join-Path $ToolDir 'dotnet-trace.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    $output = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') tool install dotnet-trace `
            --tool-path $ToolDir --version $DotnetTraceVersion 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "dotnet-trace $DotnetTraceVersion could not be installed: $($output -join ' ')"
    }
    $exe
}

function Convert-ToSpeedscope([string] $trace) {
    $base = Join-Path ([System.IO.Path]::GetDirectoryName($trace)) ([System.IO.Path]::GetFileNameWithoutExtension($trace))
    $file = "$base.speedscope.json"
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        $output = @(& (Get-DotnetTrace) convert $trace --format Speedscope -o $base 2>&1)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "dotnet-trace convert failed ($LASTEXITCODE): $($output -join ' ')"
        }
    }
    $file
}

# The UI thread's timeline as segments: [Start, End) in trace ms, with the stack (frame ids, root first) open then.
function Read-Segments($profile, [int[]] $canon) {
    $open = [System.Collections.Generic.List[int]]::new()
    $segments = [System.Collections.Generic.List[object]]::new()
    $events = @($profile.events)
    $i = 0
    while ($i -lt $events.Count) {
        $t = [double]$events[$i].at
        while ($i -lt $events.Count -and [double]$events[$i].at -eq $t) {
            $frame = $canon[[int]$events[$i].frame]
            if ($events[$i].type -eq 'O') { $open.Add($frame) }
            else {
                $k = $open.LastIndexOf($frame)
                if ($k -ge 0) { $open.RemoveAt($k) }
            }
            $i++
        }
        $next = if ($i -lt $events.Count) { [double]$events[$i].at } else { $t }
        if ($open.Count -gt 0 -and $next -gt $t) {
            $segments.Add([pscustomobject]@{ Start = $t; End = $next; Stack = $open.ToArray() })
        }
    }
    , $segments
}

# ---- Reading one stretch ----

# A sample's real frames (pseudo frames dropped), and those below its deepest boundary frame.
function Split-Sample([int[]] $stack, $kinds) {
    $real = @($stack | Where-Object { -not $kinds.Pseudo[$_] })
    $cut = -1
    for ($k = $real.Count - 1; $k -ge 0; $k--) {
        if ($kinds.Boundary[$real[$k]]) { $cut = $k; break }
    }
    $below = if ($cut + 1 -lt $real.Count) { [int[]]$real[($cut + 1)..($real.Count - 1)] } else { [int[]]@() }
    [pscustomobject]@{ Real = [int[]]$real; Below = $below }
}

function Get-Top($weights, [double] $total, $names, [int] $count = 5) {
    @($weights.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $count | ForEach-Object {
            [ordered]@{ frame = $names[$_.Key]; share = [Math]::Round($_.Value / $total, 3) }
        })
}

# blockedUnder, chain and verdict for the busy samples of one stretch (each with a weight in ms).
function Read-Stretch($samples, $kinds, $names, [double] $sampleMs) {
    if (@($samples).Count -eq 0) { return [ordered]@{ samples = 0; verdict = 'CANNOT-READ'; reason = 'the stretch has no busy sample inside the wait' } }
    $total = ($samples | Measure-Object -Property W -Sum).Sum
    $unreadable = 0.0
    $render = 0.0
    $leaves = @{}
    foreach ($s in $samples) {
        $split = $s.Split
        $named = $split.Below.Count -gt 0 -and -not @($split.Below | Where-Object { $kinds.Unresolved[$_] })
        if (-not $named) { $unreadable += $s.W }
        if (@($split.Below | Where-Object { $kinds.Render[$_] }).Count -gt 0) { $render += $s.W }
        if ($split.Real.Count -gt 0) { $leaf = $split.Real[-1]; $leaves[$leaf] = ($leaves[$leaf] ?? 0) + $s.W }
    }
    # The deepest frame below the cut in at least 90% of the time, frame by frame down from the cut.
    $chain = [System.Collections.Generic.List[int]]::new()
    $pool = @($samples)
    $under = @{}
    for ($depth = 0; ; $depth++) {
        $groups = @{}
        foreach ($s in $pool) {
            if ($s.Split.Below.Count -gt $depth) { $f = $s.Split.Below[$depth]; $groups[$f] = ($groups[$f] ?? 0) + $s.W }
        }
        $under = $groups
        if ($groups.Count -eq 0) { break }
        $best = $groups.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
        if ($best.Value / $total -lt 0.9) { break }
        $chain.Add([int]$best.Key)
        $pool = @($pool | Where-Object { $_.Split.Below.Count -gt $depth -and $_.Split.Below[$depth] -eq $best.Key })
    }
    $chainNames = @($chain | ForEach-Object { $names[$_] })
    $renderShare = $render / $total
    $unreadableShare = $unreadable / $total
    $inRender = @($chain | Where-Object { $kinds.Render[$_] }).Count -gt 0 -or $renderShare -ge 0.5
    # A render landing is checked before SPLIT: a stretch of several messages each ending in SyncFlush has only the
    # pump in common and is still in the render path (SL-080's first traced run: 76.5% SyncFlush under three filters).
    $verdict, $reason = if ($unreadableShare -gt 0.2) {
        'CANNOT-READ', ('{0:P0} of the stretch names nothing below the pump (unresolved frames, or no managed caller)' -f $unreadableShare)
    }
    elseif ($inRender) { 'RENDER-PATH', ('{0:P0} of the stretch is in WPF''s synchronous render: not an answer by itself, run T005Batch.ps1 -Plan Render' -f $renderShare) }
    elseif ($chain.Count -eq 0) { 'SPLIT', 'only the pump is common' }
    else { 'NAMED', "$($chainNames[-1]) in at least 90% of the stretch" }
    [ordered]@{
        samples         = [int][Math]::Round($total / $sampleMs)
        blockedUnder    = if ($chain.Count -gt 0) { $chainNames[-1] } else { $null }
        chain           = $chainNames
        under           = Get-Top $under $total $names
        topLeaves       = Get-Top $leaves $total $names
        renderShare     = [Math]::Round($renderShare, 3)
        unreadableShare = [Math]::Round($unreadableShare, 3)
        verdict         = $verdict
        reason          = $reason
    }
}

# ---- One run ----

function Read-Run($speedscope, $line, [DateTime] $traceStartUtc, [double] $sampleMs) {
    $names = @($speedscope.shared.frames | ForEach-Object { [string]$_.name })
    $kinds = [pscustomobject]@{
        Pseudo     = [bool[]]::new($names.Count)
        Unresolved = [bool[]]::new($names.Count)
        Idle       = [bool[]]::new($names.Count)
        Boundary   = [bool[]]::new($names.Count)
        Render     = [bool[]]::new($names.Count)
        StartScan  = [bool[]]::new($names.Count)
    }
    for ($f = 0; $f -lt $names.Count; $f++) {
        $kinds.Pseudo[$f] = $names[$f] -match $Pseudo
        $kinds.Unresolved[$f] = $names[$f] -match $Unresolved
        $kinds.Idle[$f] = $names[$f] -match $Idle
        $kinds.Boundary[$f] = $names[$f] -match $Boundary
        $kinds.Render[$f] = $names[$f] -match $RenderSync
        $kinds.StartScan[$f] = $names[$f] -match $StartScan
    }
    $more = [ordered]@{ run = $line.run; uiThread = $line.uiThread; runtime = $line.runtime; sampleMs = $sampleMs }
    # One id per method: the converter names a module "PresentationCore.il" in one frame and "presentationcore.il" in
    # another (SL-080's first traced run), which would split one method's samples in two.
    $canon = [int[]]::new($names.Count)
    $first = @{}
    for ($f = 0; $f -lt $names.Count; $f++) {
        $key = $names[$f] -replace '^[^!]*!', { $_.Value.ToLowerInvariant() }
        if (-not $first.ContainsKey($key)) { $first[$key] = $f }
        $canon[$f] = $first[$key]
    }
    $profile = @($speedscope.profiles | Where-Object { $_.name -match "^Thread \($($line.uiThread)\)" }) | Select-Object -First 1
    if ($null -eq $line.uiThread -or $null -eq $profile) { return New-Result 'CANNOT-READ' "the UI thread ($($line.uiThread)) has no samples" $more }
    $segments = Read-Segments $profile $canon
    if ($segments.Count -eq 0) { return New-Result 'CANNOT-READ' "the UI thread ($($line.uiThread)) has no samples" $more }

    # Idle or busy, from the frames alone.
    $idleTime = 0.0
    $allTime = 0.0
    $idleLeaves = @{}
    foreach ($segment in $segments) {
        # The three deepest real frames, leaf first.
        $stack = $segment.Stack
        $deepest = [System.Collections.Generic.List[int]]::new()
        for ($k = $stack.Length - 1; $k -ge 0 -and $deepest.Count -lt 3; $k--) {
            if (-not $kinds.Pseudo[$stack[$k]]) { $deepest.Add($stack[$k]) }
        }
        $isIdle = $false
        foreach ($id in $deepest) { if ($kinds.Idle[$id]) { $isIdle = $true; break } }
        $segment | Add-Member -NotePropertyName Idle -NotePropertyValue $isIdle
        $length = $segment.End - $segment.Start
        $allTime += $length
        if ($isIdle) {
            $idleTime += $length
            $leaf = $deepest[0]
            $idleLeaves[$leaf] = ($idleLeaves[$leaf] ?? 0) + $length
        }
    }
    $more.idleShare = [Math]::Round($idleTime / $allTime, 3)
    $more.idleLeaf = if ($idleLeaves.Count -gt 0) { $names[($idleLeaves.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key] } else { $null }
    if ($idleTime / $allTime -lt 0.25) {
        return New-Result 'CANNOT-READ' ('the UI thread is idle {0:P0} of the trace, under 25%: its idle frames are not the pump''s wait this reader looks for' -f ($idleTime / $allTime)) $more
    }

    # Busy stretches, joined across gaps shorter than three sample intervals.
    $join = [Math]::Max(3.0, 3 * $sampleMs)
    $stretches = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($segment in $segments) {
        if ($segment.Idle) { continue }
        if ($null -ne $current -and $segment.Start - $current.End -le $join) {
            $current.End = $segment.End
            $current.Segments.Add($segment)
        }
        else {
            $current = [pscustomobject]@{ Start = $segment.Start; End = $segment.End; Segments = [System.Collections.Generic.List[object]]::new() }
            $current.Segments.Add($segment)
            $stretches.Add($current)
        }
    }

    $offset = if ($line.clockOriginUtc) { ((ConvertTo-Utc $line.clockOriginUtc) - $traceStartUtc).TotalMilliseconds } else { $null }
    $more.traceStartUtc = $traceStartUtc.ToString('o')
    $more.offsetMs = $offset
    $startScanIds = @(for ($f = 0; $f -lt $names.Count; $f++) { if ($kinds.StartScan[$f]) { $f } })
    $startScans = @(foreach ($segment in $segments) {
            foreach ($id in $startScanIds) { if ([Array]::IndexOf($segment.Stack, [int]$id) -ge 0) { $segment.Start; break } }
        })

    $read = foreach ($phase in @($line.phases | Where-Object { $_.name -in 'scan', 'rescan' })) {
        $entry = [ordered]@{ matchedPhase = $phase.name; waitMs = $phase.longestWaitMs }
        if (-not $phase.reached -or $null -eq $offset) {
            $entry.verdict = 'CANNOT-READ'
            $entry.reason = if ($null -eq $offset) { 'the summary has no clock origin to put the wait on the trace' } else { 'the hook never sampled this phase' }
            $entry
            continue
        }
        $wait = [double]$phase.longestWaitMs
        $from = [double]$phase.maxAt + $offset
        $to = if ($null -ne $phase.maxEndAt) { [double]$phase.maxEndAt + $offset } else { $from + $wait }
        $entry.windowStartMs = [Math]::Round($from, 1)
        $entry.windowEndMs = [Math]::Round($to, 1)
        $best = $null
        $bestOverlap = 0.0
        foreach ($stretch in $stretches) {
            $overlap = [Math]::Min($stretch.End, $to) - [Math]::Max($stretch.Start, $from)
            if ($overlap -gt $bestOverlap) { $best = $stretch; $bestOverlap = $overlap }
        }
        if ($null -eq $best -or $bestOverlap -lt 0.8 * $wait) {
            $entry.verdict = 'CANNOT-READ'
            $entry.reason = if ($null -eq $best) { 'no stretch within ±20% of the hook''s wait: no busy stretch overlaps it' }
            else { 'no stretch within ±20% of the hook''s wait (best: busy {0:N0} ms of its {1:N0})' -f $bestOverlap, $wait }
            if ($null -ne $best) { $entry.startMs = [Math]::Round($best.Start, 1); $entry.durMs = [Math]::Round($best.End - $best.Start, 1) }
            $entry
            continue
        }
        $entry.startMs = [Math]::Round($best.Start, 1)
        $entry.durMs = [Math]::Round($best.End - $best.Start, 1)
        $entry.busyInWindowMs = [Math]::Round($bestOverlap, 1)
        $scanStart = @($startScans | Where-Object { $_ -le $best.Start }) | Select-Object -Last 1
        $entry.sinceStartScanMs = if ($null -ne $scanStart) { [Math]::Round($best.Start - $scanStart, 1) } else { $null }
        # The busy samples inside the wait, give or take the join.
        $samples = foreach ($segment in $best.Segments) {
            $w = [Math]::Min($segment.End, $to + $join) - [Math]::Max($segment.Start, $from - $join)
            if ($w -gt 0) { [pscustomobject]@{ W = $w; Split = (Split-Sample $segment.Stack $kinds) } }
        }
        foreach ($pair in (Read-Stretch @($samples) $kinds $names $sampleMs).GetEnumerator()) { $entry[$pair.Key] = $pair.Value }
        $entry
    }
    $more.stretches = @($read)
    $primary = @($read | Where-Object { $_.matchedPhase -eq $line.primaryPhase }) | Select-Object -First 1
    if ($null -eq $primary) { return New-Result 'CANNOT-READ' 'the summary names no primary phase' $more }
    $more.blockedUnder = $primary.blockedUnder
    $more.chain = $primary.chain
    New-Result $primary.verdict "$($line.primaryPhase): $($primary.reason)" $more
}

function Invoke-Read([string] $trace, [string] $summaryPath, [int] $run) {
    $line = @(Get-Content -LiteralPath $summaryPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -DateKind String } | Where-Object { $_.run -eq $run }) | Select-Object -Last 1
    if ($null -eq $line) { return New-Result 'CANNOT-READ' "no summary line for run $run in $summaryPath" }
    if (-not $trace -or -not (Test-Path -LiteralPath $trace -PathType Leaf) -or (Get-Item -LiteralPath $trace).Length -eq 0) {
        return New-Result 'CANNOT-READ' 'no trace written' @{ run = $run }
    }
    try {
        $header = Read-NettraceHeader $trace
        $file = Convert-ToSpeedscope $trace
    }
    catch {
        return New-Result 'CANNOT-READ' $_.Exception.Message @{ run = $run }
    }
    $speedscope = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -Depth 64
    $result = Read-Run $speedscope $line $header.StartUtc ($header.SamplingNs / 1e6)
    $result.samplingNs = $header.SamplingNs
    $result
}

# ---- Self-test ----

# A fixture is a small timeline, not a Speedscope file: named stacks ("@name" splices another), and per thread the
# samples of each stack from one time to the next. This makes it the evented Speedscope dotnet-trace writes, so the
# reader runs on the same shape it reads from a real trace.
function ConvertTo-FixtureSpeedscope($fixture) {
    $stacks = @{}
    foreach ($p in $fixture.stacks.PSObject.Properties) { $stacks[$p.Name] = @($p.Value) }
    $expand = $null
    $expand = {
        param([string] $name)
        foreach ($frame in $stacks[$name]) { if ($frame.StartsWith('@')) { & $expand $frame.Substring(1) } else { $frame } }
    }
    $frames = [System.Collections.Generic.List[string]]::new()
    $id = @{}
    $toIds = { param($list) @(foreach ($f in $list) { if (-not $id.ContainsKey($f)) { $id[$f] = $frames.Count; $frames.Add($f) }; $id[$f] }) }
    $profiles = foreach ($thread in @($fixture.timeline | ForEach-Object thread | Sort-Object -Unique)) {
        $samples = foreach ($item in @($fixture.timeline | Where-Object thread -eq $thread)) {
            $every = if ($item.every) { [double]$item.every } else { [double]$fixture.sampleMs }
            $count = [int][Math]::Round(([double]$item.to - [double]$item.from) / $every)
            $mix = [System.Collections.Generic.List[object]]::new()
            if ($item.mix) { foreach ($m in $item.mix) { $mix.Add($m) } } else { $mix.Add(@($item.stack, 1.0)) }
            for ($k = 0; $k -lt $count; $k++) {
                $position = ($k + 0.5) / $count
                $cumulative = 0.0
                $chosen = $mix[-1][0]
                foreach ($m in $mix) { $cumulative += [double]$m[1]; if ($position -lt $cumulative) { $chosen = $m[0]; break } }
                [pscustomobject]@{ At = [double]$item.from + $k * $every; Every = $every; Stack = [int[]](& $toIds @(& $expand $chosen)) }
            }
        }
        $events = [System.Collections.Generic.List[object]]::new()
        $open = [int[]]@()
        $last = 0.0
        foreach ($s in $samples) {
            if ($s.At -gt $last + 1e-9 -and $open.Count -gt 0) {
                for ($k = $open.Count - 1; $k -ge 0; $k--) { $events.Add([ordered]@{ type = 'C'; frame = $open[$k]; at = $last }) }
                $open = [int[]]@()
            }
            $shared = 0
            while ($shared -lt $open.Count -and $shared -lt $s.Stack.Count -and $open[$shared] -eq $s.Stack[$shared]) { $shared++ }
            for ($k = $open.Count - 1; $k -ge $shared; $k--) { $events.Add([ordered]@{ type = 'C'; frame = $open[$k]; at = $s.At }) }
            for ($k = $shared; $k -lt $s.Stack.Count; $k++) { $events.Add([ordered]@{ type = 'O'; frame = $s.Stack[$k]; at = $s.At }) }
            $open = $s.Stack
            $last = $s.At + $s.Every
        }
        for ($k = $open.Count - 1; $k -ge 0; $k--) { $events.Add([ordered]@{ type = 'C'; frame = $open[$k]; at = $last }) }
        [ordered]@{ type = 'evented'; name = "Thread ($thread)"; unit = 'milliseconds'; startValue = $samples[0].At; endValue = $last; events = $events }
    }
    $json = [ordered]@{ shared = [ordered]@{ frames = @($frames | ForEach-Object { [ordered]@{ name = $_ } }) }; profiles = @($profiles) } |
        ConvertTo-Json -Depth 8 -Compress
    $json | ConvertFrom-Json -Depth 64
}

function Invoke-SelfTest([string] $only) {
    $files = @(Get-ChildItem -LiteralPath $FixtureDir -Filter '*.json' | Sort-Object Name)
    if ($only) { $files = @($files | Where-Object { $_.BaseName -eq $only }) }
    if ($files.Count -eq 0) { Write-Output "FAILED: no fixture$(if ($only) { " named $only" }) in $FixtureDir"; $script:SelfTestFailed = $true; return }
    $failed = 0
    foreach ($file in $files) {
        $fixture = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 64 -DateKind String
        $speedscope = ConvertTo-FixtureSpeedscope $fixture
        $result = Read-Run $speedscope $fixture.summary (ConvertTo-Utc $fixture.traceStartUtc) ([double]$fixture.sampleMs)
        $problems = @(
            if ($result.verdict -cne $fixture.expect.verdict) { "verdict $($result.verdict), expected $($fixture.expect.verdict)" }
            if ($fixture.expect.PSObject.Properties.Name -contains 'blockedUnder' -and $result.blockedUnder -cne $fixture.expect.blockedUnder) { "blockedUnder $($result.blockedUnder), expected $($fixture.expect.blockedUnder)" }
            if ($fixture.expect.reason -and $result.reason -notmatch $fixture.expect.reason) { "reason '$($result.reason)' does not match '$($fixture.expect.reason)'" }
            foreach ($p in @($fixture.expect.stretches)) {
                if ($null -eq $p) { continue }
                $s = @($result.stretches | Where-Object matchedPhase -eq $p.matchedPhase) | Select-Object -First 1
                if ($null -eq $s) { "no $($p.matchedPhase) stretch"; continue }
                if ($s.verdict -cne $p.verdict) { "$($p.matchedPhase): verdict $($s.verdict), expected $($p.verdict)" }
                if ($p.PSObject.Properties.Name -contains 'blockedUnder' -and $s.blockedUnder -cne $p.blockedUnder) { "$($p.matchedPhase): blockedUnder $($s.blockedUnder), expected $($p.blockedUnder)" }
            }
        )
        if ($problems.Count -eq 0) { Write-Output "  ok    $($file.BaseName): $($result.verdict) ($($result.reason))" }
        else {
            $failed++
            Write-Output "  FAIL  $($file.BaseName): $($problems -join '; ')"
            Write-Output ($result | ConvertTo-Json -Depth 8)
        }
    }
    if ($failed -gt 0) { Write-Output "FAILED: $failed of $($files.Count) fixtures"; $script:SelfTestFailed = $true; return }
    Write-Output "PASSED: $($files.Count) fixtures"
}

$script:SelfTestFailed = $false
if ($SelfTest) {
    Invoke-SelfTest $Only
    exit $(if ($script:SelfTestFailed) { 1 } else { 0 })
}
if (-not $Summary -or $Run -lt 0) { throw 'StallStacks.ps1 -Trace <run.nettrace> -Summary <runs.jsonl> -Run <n>, or -SelfTest' }
$result = Invoke-Read $Trace $Summary $Run
$json = $result | ConvertTo-Json -Depth 8
if ($Out) { Set-Content -LiteralPath $Out -Value $json -Encoding utf8NoBOM }
$json
