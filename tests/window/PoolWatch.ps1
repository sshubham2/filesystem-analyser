# SL-095's kernel-pool protocol, as the slice was narrowed: it writes complete JSONL logs for every arm of one session a
# person runs after a true Restart, and -Report says whether the run is complete and prints each arm's raw pool nets.
# It judges no tolerance and names no outcome: every pool judgement (the five-plus-five scans against a tolerance, the
# settle gate, idle drift, cache against leak) went to SC-128, which the design record hands them to by finding id.
# Unelevated, under Windows PowerShell 5.1 and pwsh 7. Every reading is one JSON line in <RunDir>\marks.jsonl, every arm
# one line in <RunDir>\arms.jsonl after the plan line, tags as an array (stack decision); a real session's logs are
# committed under docs/design/SL-095-runs/.
#
#   PoolWatch.ps1 -Prepare [-RunDir <dir>]       on the boot as it is, before the Restart: publishes the app, builds the
#                                                window hook, and writes <RunDir>\prepare\prepare.json with the Rpre
#                                                reading and the environment record
#   PoolWatch.ps1 -Protocol [-RunDir <dir>]      after Start > Restart (not Shut down), within 30 min of it, the PC left
#                                                alone: R0, I0 (20 min idle), 5 full exe scans of C:, 4 cancelled and 1
#                                                closed, I1 (20 min idle), each arm marked before, at exit and 10 min after
#   PoolWatch.ps1 -Protocol -Rehearse [-Keep]    the same code over a generated %TEMP% tree, with short windows,
#                                                folder-count triggers and no boot-age check; every line says
#                                                rehearsal, and the scratch is deleted afterwards unless -Keep
#   PoolWatch.ps1 -Report [-RunDir <dir>]        RUN COMPLETE, or RUN INCOMPLETE and one line per missing item, then the
#                                                raw nets and the environment; exit 0 when complete, 2 when not
#
# Nothing here elevates or asks for elevation (architecture rule). `fltmc filters` needs an administrator: a person who
# chooses to may run it in an elevated window of their own and paste what it prints into <RunDir>\fltmc.txt, before or
# after -Protocol. -Report reads that file (or <RunDir>\prepare\fltmc.txt) each time it runs, and counts it only when it
# is the filter list; -Prepare also copies a paste it finds in the prepare folder into the environment record.
#
# Nothing here lists or opens anything under the container layer store: the only walker it starts is the app itself,
# which leaves the store out. The machine guard is per arm: combined pool growth over the arm's own /before mark above
# GuardPoolBytes, or Available under GuardAvailableMB, kills FsAnalyser.exe and records guardStop; an arm that would
# start with Available already under it is not started and records guardAtStart, named as a cascade by -Report.

[CmdletBinding(DefaultParameterSetName = 'Report')]
param(
    [Parameter(ParameterSetName = 'Prepare', Mandatory = $true)] [switch] $Prepare,
    [Parameter(ParameterSetName = 'Protocol', Mandatory = $true)] [switch] $Protocol,
    [Parameter(ParameterSetName = 'Protocol')] [switch] $Rehearse,
    [Parameter(ParameterSetName = 'Protocol')] [switch] $Keep,
    [Parameter(ParameterSetName = 'Report', Mandatory = $true)] [switch] $Report,
    [string] $RunDir
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $RunDir) { $RunDir = Join-Path $Repo 'docs\design\SL-095-runs\session-1' }
$AppExe = Join-Path $Repo 'artifacts\sl095\app\FsAnalyser.App.exe'
$HookDll = Join-Path $Repo 'artifacts\window-hook\FsaWindowHook.dll'
$StoreText = Join-Path $env:ProgramData 'Microsoft\Windows\Containers\Layers'

# The session's timing and the machine guard (docs/design/SL-095-container-layer-store.md). A rehearsal shortens the
# timing only; the guard is the same.
$Timing = @{
    WindowSec          = 1200    # I0 and I1, 20 min each
    MarkEverySec       = 60      # a mark every minute in an idle window and while an arm runs
    SettleSec          = 600     # every arm's /settled mark, 10 min after its process exits
    BootAgeMaxSec      = 1800    # R0 within 30 min of the boot, or -Protocol does not start (review X4)
    TriggerDeadlineSec = 3600    # how long the hook waits for a cancel or close trigger
}
# Per arm (review R4-2, F2): pool growth over the arm's own /before, never a cumulative figure from R0, so one arm's
# growth cannot stop the next. The pool ceiling sits above a cold full C: scan's predicted cost (spike #87: about 800 B
# of pool per directory listed, 0.7 GiB for C:'s ~920,000 folders, plus about 1 KB per first-time file open, 2.3 to
# 3.6 GiB for its 2.35M to 3.7M files: 3 to 4.4 GiB), so a cold scan is measured, not cut off; Available under 3 GiB is
# what protects the machine.
$Guard = @{
    PoolBytes   = 6GB
    AvailableMB = 3072
}
$WatchedTags = @('WCsc', 'WCfc', 'WCri', 'WCfn', 'WCse', 'WCss', 'WCce', 'WCrb', 'WCro', 'FMwi', 'FMfc', 'FMfn', 'File', 'NtfF', 'Toke')
$NetTags = @('WCsc', 'WCfc', 'WCri', 'FMwi', 'FMfc', 'FMfn', 'File', 'NtfF', 'Toke')
$FullArms = @('F1', 'F2', 'F3', 'F4', 'F5')
$StopArms = @('X1', 'X2', 'X3', 'X4', 'X5')
$Windows = @('I0', 'I1')

# ---- Reading the machine ----

# -Report reads files only, so it compiles nothing (Windows PowerShell's Add-Type needs a writable %TEMP%).
if ($PSCmdlet.ParameterSetName -ne 'Report' -and -not ('PoolWatchNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class PoolWatchNative
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private static IntPtr Query(int infoClass, int start, out int length)
    {
        length = start;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            int returned;
            int status = NtQuerySystemInformation(infoClass, buffer, length, out returned);
            if (status == 0)
                return buffer;
            Marshal.FreeHGlobal(buffer);
            if (status != StatusInfoLengthMismatch)
                throw new InvalidOperationException(string.Format("NtQuerySystemInformation({0}) returned 0x{1:X8}", infoClass, status));
            length = Math.Max(length * 2, returned + 65536);
        }
    }

    // SystemPoolTagInformation (22), x64: ULONG Count, then 40-byte entries from offset 8:
    // Tag[4], PagedAllocs, PagedFrees, PagedUsed (SIZE_T at 16), NonPagedAllocs, NonPagedFrees, NonPagedUsed (SIZE_T at 32).
    public static List<object[]> PoolTags()
    {
        int length;
        IntPtr buffer = Query(22, 1 << 20, out length);
        try
        {
            int count = Marshal.ReadInt32(buffer);
            var tags = new List<object[]>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr e = new IntPtr(buffer.ToInt64() + 8 + (long)i * 40);
                var raw = new byte[4];
                Marshal.Copy(e, raw, 0, 4);
                var name = new StringBuilder(4);
                foreach (byte b in raw)
                    name.Append(b >= 32 && b < 127 ? (char)b : '?');
                tags.Add(new object[] {
                    name.ToString(),
                    (long)(uint)Marshal.ReadInt32(e, 4), (long)(uint)Marshal.ReadInt32(e, 8), Marshal.ReadInt64(e, 16),
                    (long)(uint)Marshal.ReadInt32(e, 24), (long)(uint)Marshal.ReadInt32(e, 28), Marshal.ReadInt64(e, 32) });
            }
            return tags;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // SystemExtendedHandleInformation (64), x64: count and a reserved word, then 40-byte entries from offset 16:
    // Object, UniqueProcessId (8), HandleValue (8), GrantedAccess (4 at 24). Returns {all of pid's, those with access}.
    public static long[] HandlesOf(long pid, uint access)
    {
        int length;
        IntPtr buffer = Query(64, 32 << 20, out length);
        try
        {
            long count = Marshal.ReadInt64(buffer);
            long all = 0, matching = 0;
            for (long i = 0; i < count; i++)
            {
                IntPtr e = new IntPtr(buffer.ToInt64() + 16 + i * 40);
                if (Marshal.ReadInt64(e, 8) != pid)
                    continue;
                all++;
                if ((uint)Marshal.ReadInt32(e, 24) == access)
                    matching++;
            }
            return new long[] { all, matching };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static long AvailableMB()
    {
        var status = new MEMORYSTATUSEX();
        status.Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException("GlobalMemoryStatusEx returned false");
        return (long)(status.AvailPhys / (1024 * 1024));
    }
}
'@
}

function Get-Boot {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{ BootUtc = $os.LastBootUpTime.ToUniversalTime(); UptimeSec = [long]((Get-Date) - $os.LastBootUpTime).TotalSeconds }
}

function Get-Pool {
    $memory = Get-CimInstance Win32_PerfRawData_PerfOS_Memory
    [pscustomobject]@{ Paged = [long]$memory.PoolPagedBytes; Nonpaged = [long]$memory.PoolNonpagedBytes; AvailableMB = [long]$memory.AvailableMBytes }
}

# One reading: pool counters, Available, the watched tags, the System process's handles. A part Windows will not give is
# null, and -Report lists the arm as incomplete for it (review m7).
function Read-Mark([string] $label) {
    $pool = Get-Pool
    $boot = Get-Boot
    $tags = $null
    try {
        $all = [PoolWatchNative]::PoolTags()
        $tags = @($all | Where-Object { $WatchedTags -ccontains $_[0] } | ForEach-Object {
                [ordered]@{ tag = $_[0]; pa = $_[1]; pf = $_[2]; pagedBytes = $_[3]; na = $_[4]; nf = $_[5]; nonpagedBytes = $_[6] } })
    } catch { $tags = $null }
    $system = $null; $system120089 = $null
    try { $h = [PoolWatchNative]::HandlesOf(4, 0x120089); $system = $h[0]; $system120089 = $h[1] } catch { }
    if ($null -eq $system) { try { $system = (Get-Process -Id 4).HandleCount } catch { } }
    [ordered]@{
        kind          = 'mark'
        label         = $label
        utc           = (Get-Date).ToUniversalTime().ToString('o')
        bootUtc       = $boot.BootUtc.ToString('o')
        uptimeSec     = $boot.UptimeSec
        pagedBytes    = $pool.Paged
        nonpagedBytes = $pool.Nonpaged
        availableMB   = $pool.AvailableMB
        systemHandles = $system
        system120089  = $system120089
        tags          = $tags
        rehearsal     = [bool]$script:Rehearsing
    }
}

function Add-Line([string] $file, $value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
    Add-Content -LiteralPath $file -Value ($value | ConvertTo-Json -Compress -Depth 6) -Encoding UTF8
}

function Write-Mark([string] $label) {
    $m = Read-Mark $label
    Add-Line (Join-Path $RunDir 'marks.jsonl') $m
    Write-Host ('{0,-14} paged {1,7:N0} MB  nonpaged {2,7:N0} MB  available {3,6:N0} MB  System handles {4} (0x120089: {5})' -f `
            $label, ($m.pagedBytes / 1MB), ($m.nonpagedBytes / 1MB), $m.availableMB, $m.systemHandles, $m.system120089)
    $m
}

function Tag($mark, [string] $name) { if ($null -eq $mark.tags) { return $null }; @($mark.tags | Where-Object { $_.tag -ceq $name })[0] }
function Live($mark, [string] $name) {
    if ($null -eq $mark -or $null -eq $mark.tags) { return $null }
    $t = Tag $mark $name
    if ($null -eq $t) { return 0 }
    ([long]$t.pa - [long]$t.pf) + ([long]$t.na - [long]$t.nf)
}

# A text file's lines as plain strings, for whatever is written back out as JSON: Windows PowerShell 5.1's ConvertTo-Json
# writes each line Get-Content returns as an object of its PSPath, PSDrive and provider, about 200 KB a line.
function Read-Lines([string] $file) { [IO.File]::ReadAllLines((Convert-Path -LiteralPath $file)) }

# ---- The environment (the slice's step 2 and criterion 3) ----

# A pasted `fltmc filters` counts only when it is the filter list: fltmc's header line, and no refusal. Unelevated, fltmc
# prints 'Filter listing failed with error: 0x80070005' and 'Access is denied.', which is no list (review R2-N3).
function Test-FltmcList($lines) {
    $lines = @($lines | ForEach-Object { "$_" } | Where-Object { $_.Trim() })
    $header = @($lines | Where-Object { $_ -match '^\s*Filter Name\s+Num Instances' }).Count -gt 0
    $refused = @($lines | Where-Object { $_ -match '0x8007|Access is denied|failed' }).Count -gt 0
    [pscustomobject]@{ isList = $header -and -not $refused; first = $(if ($lines.Count) { $lines[0].Trim() } else { '' }) }
}

function Get-EnvironmentRecord([string] $pasteDir) {
    $drivers = @(foreach ($name in 'wcifs', 'bindflt', 'CldFlt', 'WdFilter') {
            $d = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'" -ErrorAction SilentlyContinue
            if ($d) { [ordered]@{ name = $d.Name; state = $d.State; startMode = $d.StartMode } }
        })
    $antivirus = @(try { Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop | ForEach-Object { [ordered]@{ name = $_.displayName; state = ('0x{0:X}' -f $_.productState) } } } catch { })
    $wcifs = Join-Path $env:SystemRoot 'System32\drivers\wcifs.sys'
    $os = Get-CimInstance Win32_OperatingSystem
    $paste = Join-Path $pasteDir 'fltmc.txt'
    $filters = 'not supplied: needs an administrator; paste what `fltmc filters` prints into fltmc.txt'
    if (Test-Path -LiteralPath $paste) {
        $lines = @(Read-Lines $paste)
        $t = Test-FltmcList $lines
        if ($t.isList) { $filters = $lines } elseif ($t.first) { $filters = "not a filter list: $($t.first)" }
    }
    [ordered]@{
        kind            = 'environment'
        utc             = (Get-Date).ToUniversalTime().ToString('o')
        os              = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
        bootUtc         = $os.LastBootUpTime.ToUniversalTime().ToString('o')
        wcifsVersion    = $(if (Test-Path $wcifs) { (Get-Item $wcifs).VersionInfo.FileVersion } else { $null })
        drivers         = $drivers
        antivirus       = $antivirus
        oneDriveRunning = [bool](Get-Process OneDrive -ErrorAction SilentlyContinue)
        fltmcFilters    = $filters
    }
}

# ---- Running an arm ----

# Starts a process with its own environment variables (5.1 and 7 alike), never this shell's.
function Start-Exe([string] $file, [string[]] $arguments, [hashtable] $variables) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $file
    $info.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '(\\+)$', '$1$1') + '"' } else { $_ } }) -join ' '
    $info.UseShellExecute = $false
    foreach ($k in $variables.Keys) { $info.EnvironmentVariables[$k] = [string]$variables[$k] }
    $p = [System.Diagnostics.Process]::Start($info)
    $null = $p.Handle
    $p
}

# An idle window: a mark at its start, one every MarkEverySec, and one at its end.
function Wait-Idle([string] $name) {
    $null = Write-Mark "$name/start"
    $every = [Math]::Max(1, $Timing.MarkEverySec)
    $marks = [Math]::Max(1, [int]($Timing.WindowSec / $every))
    for ($i = 1; $i -lt $marks; $i++) {
        Start-Sleep -Seconds $every
        $null = Write-Mark ('{0}/t{1:D3}' -f $name, $i)
    }
    Start-Sleep -Seconds $every
    $null = Write-Mark "$name/end"
}

# One exe run of the published app in report mode, with the window hook when it cancels or closes: a mark before, one
# every MarkEverySec while it runs (and the guard), one as it exits, and the settled mark SettleSec later. Its line in
# arms.jsonl carries the exit code, the report's figures, what the hook logged, and any guard stop.
function Invoke-ExeArm([string] $name, [string] $type, [string] $root, [hashtable] $hook) {
    $reportFile = Join-Path $RunDir "$name.report.json"
    $hookLog = Join-Path $RunDir "$name.hook.log"
    $line = [ordered]@{ kind = 'arm'; arm = $name; type = $type; root = $root; hook = $hook; exitCode = $null; report = $null; hookLog = @();
        acted = $false; foldersAtCancel = $null; ranFull = $false; guardStop = $null; guardAtStart = $false; cascadeFrom = $null; interrupted = $false
        rehearsal = [bool]$script:Rehearsing }
    $before = Write-Mark "$name/before"
    if ($before.availableMB -lt $Guard.AvailableMB) {
        # Not started: the guard is already exceeded, so it would be stopped at its first poll and say nothing of its own.
        $line['guardAtStart'] = $true
        $line['guardStop'] = "available $($before.availableMB) MB at start"
        $line['cascadeFrom'] = $script:LastGuardStop
        Add-Line (Join-Path $RunDir 'arms.jsonl') $line
        return $line
    }
    $vars = @{}
    if ($hook) {
        $vars['DOTNET_STARTUP_HOOKS'] = $HookDll
        $vars['FSA_HOOK_LOG'] = $hookLog
        $vars['FSA_HOOK_TRIGGER_DEADLINE_S'] = "$($Timing.TriggerDeadlineSec)"
        foreach ($k in $hook.Keys) { $vars[$k] = "$($hook[$k])" }
    }
    $p = Start-Exe $AppExe @($root, '--report', $reportFile) $vars
    $written = $false
    try {
        $start = [long]$before.pagedBytes + [long]$before.nonpagedBytes
        $every = [Math]::Max(1, $Timing.MarkEverySec)
        $next = (Get-Date).AddSeconds($every); $n = 0
        while (-not $p.WaitForExit(2000)) {
            $pool = Get-Pool
            $growth = $pool.Paged + $pool.Nonpaged - $start
            if ($pool.AvailableMB -lt $Guard.AvailableMB -or $growth -gt $Guard.PoolBytes) {
                $line['guardStop'] = "available $($pool.AvailableMB) MB, pool +$([Math]::Round($growth / 1MB)) MiB over $name/before"
                $script:LastGuardStop = $name
                try { $p.Kill() } catch { }
                $p.WaitForExit()
                break
            }
            if ((Get-Date) -ge $next) { $n++; $null = Write-Mark ('{0}/t{1:D3}' -f $name, $n); $next = (Get-Date).AddSeconds($every) }
        }
        if ($p.HasExited) { $line['exitCode'] = $p.ExitCode }
        $null = Write-Mark "$name/exit"
        Start-Sleep -Seconds $Timing.SettleSec
        $null = Write-Mark "$name/settled"
        if (Test-Path -LiteralPath $reportFile) {
            $r = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
            $line['report'] = [ordered]@{ status = $r.core.status; outcome = $r.outcome; files = $r.core.files; folders = $r.core.folders;
                notScanned = $r.core.notScanned; skippedStores = $r.core.skippedStores; opensUnderExcludedStore = $r.core.opensUnderExcludedStore; elapsedMs = $r.core.elapsedMs }
        }
        if (Test-Path -LiteralPath $hookLog) {
            $logged = @(Read-Lines $hookLog | Where-Object { $_ -match '^(acted|refused|passed) ' })
            $line['hookLog'] = $logged
            # Whether the hook pressed Cancel or closed the window at all. It refuses without acting when the scan ends
            # first (ranFull), when the app shuts down before the trigger, and when the trigger's deadline passes (review
            # R2-R3-3).
            $line['acted'] = [bool](@($logged) -match '^acted ')
            $line['ranFull'] = [bool](@($logged) -match 'ended before the trigger')
            foreach ($l in $logged) { if ($l -match '^acted .* folders=(\d+)') { $line['foldersAtCancel'] = [long]$Matches[1] } }
        }
        Add-Line (Join-Path $RunDir 'arms.jsonl') $line
        $written = $true
    }
    finally {
        # A terminating error (a CIM reading Windows would not give) or Ctrl+C while the arm runs: FsAnalyser.exe is not
        # left scanning C: with no guard polling it, and the arm still has its line, marked interrupted (review R2-n2).
        if (-not $written) {
            if (-not $p.HasExited) { try { $p.Kill() } catch { }; try { $null = $p.WaitForExit(30000) } catch { } }
            $line['interrupted'] = $true
            try { Add-Line (Join-Path $RunDir 'arms.jsonl') $line } catch { }
        }
    }
    $line
}

# ---- -Report: completeness and raw nets, never a judgement ----

# Each line of a JSONL log parsed on its own. A torn last line (a kill or a closed console mid-write) and a NUL-filled tail
# (a bugcheck before the data was flushed) are the likely endings of a kernel-pool session: they come back as the numbers
# of the lines that are not JSON, never as an error that stops -Report (review R2-n1).
function Read-Jsonl([string] $file) {
    $items = New-Object System.Collections.Generic.List[object]
    $bad = New-Object System.Collections.Generic.List[int]
    if (Test-Path -LiteralPath $file) {
        $n = 0
        foreach ($raw in @(Read-Lines $file)) {
            $n++
            if (-not $raw.Trim()) { continue }
            $text = $raw.Trim([char]0, [char]0xFEFF, ' ', "`t")
            if (-not $text) { $bad.Add($n); continue }
            try { $items.Add(($text | ConvertFrom-Json)) } catch { $bad.Add($n) }
        }
    }
    [pscustomobject]@{ Items = $items.ToArray(); Bad = $bad.ToArray() }
}

# The fltmc paste, wherever and whenever the person put it: <RunDir>\fltmc.txt, then <RunDir>\prepare\fltmc.txt, read now;
# then the record -Prepare froze into prepare.json. Recorded only when one of them is the filter list (reviews m1, R2-N3).
function Get-FiltersLine([string] $dir, $environment) {
    $first = $null
    foreach ($paste in (Join-Path $dir 'fltmc.txt'), (Join-Path $dir 'prepare\fltmc.txt')) {
        if (-not (Test-Path -LiteralPath $paste)) { continue }
        $t = Test-FltmcList @(Read-Lines $paste)
        if ($t.isList) { return "FILTERS recorded: $paste" }
        if ($null -eq $first) { $first = "$paste holds no filter list ($(if ($t.first) { $t.first } else { 'it is empty' }))" }
    }
    if ($null -ne $environment) {
        $t = Test-FltmcList @($environment.fltmcFilters)
        if ($t.isList) { return "FILTERS recorded: $(Join-Path $dir 'prepare\prepare.json')" }
        if ($null -eq $first) { $first = $(if ($t.first) { $t.first } else { 'no fltmc paste was recorded' }) }
    }
    "FILTERS UNDECIDED: $(if ($first) { $first } else { 'no fltmc paste was recorded' })"
}

# What makes the run incomplete, one line each, against the plan line -Protocol wrote before R0.
function Get-Missing($plan, $marks, $arms) {
    $missing = New-Object System.Collections.Generic.List[string]
    if ($null -eq $plan) { $missing.Add('the run has no plan line, so which arms it should hold is not known'); return , $missing }
    $byLabel = @{}; foreach ($m in $marks) { $byLabel[$m.label] = $m }
    $byArm = @{}; foreach ($a in $arms) { $byArm[$a.arm] = $a }
    $rehearsal = [bool]$plan.rehearsal
    if (-not $byLabel.ContainsKey('R0')) { $missing.Add('mark R0 is missing') }
    foreach ($w in @($plan.windows)) {
        foreach ($need in "$w/start", "$w/end") { if (-not $byLabel.ContainsKey($need)) { $missing.Add("window ${w}: mark $need is missing") } }
    }
    foreach ($name in @($plan.arms)) {
        $a = $byArm[$name]
        if ($null -eq $a) {
            if ($byLabel.ContainsKey("$name/before")) { $missing.Add("arm ${name}: started (its /before mark is in marks.jsonl) but its arm line was never written") }
            else { $missing.Add("arm ${name}: in the plan but not in arms.jsonl, so it never started") }
            continue
        }
        if ($a.guardAtStart) { $missing.Add("arm ${name}: not measured: guard already exceeded at start (cascade from $(if ($a.cascadeFrom) { $a.cascadeFrom } else { 'an earlier arm' }))"); continue }
        if ($a.interrupted) { $missing.Add("arm ${name}: interrupted: -Protocol stopped while it ran, and FsAnalyser.exe was killed"); continue }
        foreach ($need in "$name/before", "$name/exit", "$name/settled") {
            if (-not $byLabel.ContainsKey($need)) { $missing.Add("arm ${name}: mark $need is missing"); continue }
            $m = $byLabel[$need]
            foreach ($field in 'pagedBytes', 'nonpagedBytes', 'tags', 'system120089') {
                if ($null -eq $m.$field) { $missing.Add("arm ${name}: mark $need has no $field") }
            }
        }
        if ($a.guardStop) {
            $at = Get-Net $byLabel "$name/before" "$name/exit"
            $where = if ($null -eq $at) { 'its net at the stop is unread' } else { 'paged {0:+#,0;-#,0;0} MiB, nonpaged {1:+#,0;-#,0;0} MiB from /before to /exit' -f ($at.paged / 1MB), ($at.nonpaged / 1MB) }
            $missing.Add("arm ${name}: stopped by the machine guard ($($a.guardStop)): $where")
            continue
        }
        if ($null -eq $a.exitCode) { $missing.Add("arm ${name}: no exit code, so its process did not exit") }
        $refusedLine = @(@($a.hookLog) | Where-Object { $_ -match '^refused ' }) | Select-Object -First 1
        switch ($a.type) {
            'exe-full' {
                if ($a.exitCode -ne 0) { $missing.Add("arm ${name}: a full scan that exited $($a.exitCode), not 0") }
                if ($null -eq $a.report) { $missing.Add("arm ${name}: a full scan that wrote no report"); break }
                if ($a.report.status -ne 'Completed') { $missing.Add("arm ${name}: a full scan whose report says $($a.report.status), not Completed") }
                if (-not $rehearsal) {
                    if ($null -eq $a.report.notScanned -or [long]$a.report.notScanned -lt 1) { $missing.Add("arm ${name}: its report records no store left out (notScanned $($a.report.notScanned))") }
                    if ($null -eq $a.report.opensUnderExcludedStore -or [long]$a.report.opensUnderExcludedStore -ne 0) { $missing.Add("arm ${name}: its report records $($a.report.opensUnderExcludedStore) opens under the store, not 0") }
                }
            }
            { $_ -in 'exe-cancel', 'exe-close' } {
                $what = if ($a.type -eq 'exe-close') { 'closed' } else { 'cancelled' }
                if (-not $a.acted -or $null -eq $a.foldersAtCancel) {
                    $said = if ($refusedLine) { "the hook logged '$refusedLine'" } else { 'the hook logged no acted line' }
                    $missing.Add("arm ${name}: a $what run whose hook never acted: $said")
                }
                elseif ($a.type -eq 'exe-cancel') {
                    if ($null -eq $a.report) { $missing.Add("arm ${name}: a cancelled run that wrote no report") }
                    elseif ($a.report.status -ne 'Cancelled') { $missing.Add("arm ${name}: a cancelled run whose report says $($a.report.status), not Cancelled") }
                }
            }
            default { $missing.Add("arm ${name}: of a type -Report does not know ($($a.type))") }
        }
    }
    $full = @($arms | Where-Object { $_.type -eq 'exe-full' }).Count
    $stop = @($arms | Where-Object { $_.type -eq 'exe-cancel' -or $_.type -eq 'exe-close' }).Count
    if ($full -ne 5 -or $stop -ne 5) { $missing.Add("the run holds $full full and $stop cancelled or closed exe runs, not 5 and 5") }
    , $missing
}

# The raw net between two marks: pool bytes, the watched tags' live counts, the System handles. Nothing taken out.
function Get-Net($byLabel, [string] $from, [string] $to) {
    $a = $byLabel[$from]; $b = $byLabel[$to]
    if ($null -eq $a -or $null -eq $b -or $null -eq $a.pagedBytes -or $null -eq $b.pagedBytes) { return $null }
    $net = [ordered]@{ paged = [long]$b.pagedBytes - [long]$a.pagedBytes; nonpaged = [long]$b.nonpagedBytes - [long]$a.nonpagedBytes }
    foreach ($t in $NetTags) { $x = Live $a $t; $y = Live $b $t; $net[$t] = $(if ($null -ne $x -and $null -ne $y) { $y - $x } else { $null }) }
    $net['handles'] = $(if ($null -ne $a.systemHandles -and $null -ne $b.systemHandles) { [long]$b.systemHandles - [long]$a.systemHandles } else { $null })
    $net['h120089'] = $(if ($null -ne $a.system120089 -and $null -ne $b.system120089) { [long]$b.system120089 - [long]$a.system120089 } else { $null })
    $net
}

function Format-Net([string] $name, [string] $type, [string] $exit, [string] $status, $net) {
    $cells = @('{0,-4} {1,-11} {2,5} {3,-10}' -f $name, $type, $exit, $status)
    if ($null -eq $net) { return ($cells[0] + ' (no net: a mark is missing)') }
    $cells += '{0,10:N1} {1,10:N1}' -f ($net.paged / 1MB), ($net.nonpaged / 1MB)
    foreach ($t in $NetTags + 'handles', 'h120089') { $cells += '{0,8}' -f $(if ($null -eq $net[$t]) { '-' } else { $net[$t] }) }
    $cells -join ' '
}

function Write-RunReport([string] $dir) {
    $markLog = Read-Jsonl (Join-Path $dir 'marks.jsonl')
    $armLog = Read-Jsonl (Join-Path $dir 'arms.jsonl')
    $marks = $markLog.Items
    $lines = $armLog.Items
    $plan = @($lines | Where-Object { $_.kind -eq 'plan' }) | Select-Object -Last 1
    $arms = @($lines | Where-Object { $_.kind -eq 'arm' })
    $byLabel = @{}; foreach ($m in $marks) { $byLabel[$m.label] = $m }
    $missing = Get-Missing $plan $marks $arms
    foreach ($n in $markLog.Bad) { $missing.Add("marks.jsonl line $n is not JSON (a torn or unflushed write)") }
    foreach ($n in $armLog.Bad) { $missing.Add("arms.jsonl line $n is not JSON (a torn or unflushed write)") }
    $out = New-Object System.Collections.Generic.List[string]
    if ($plan -and $plan.rehearsal) { $out.Add('REHEARSAL: a generated %TEMP% tree with short windows, not a measurement of C:') }
    if ($missing.Count -eq 0) { $out.Add('RUN COMPLETE') }
    else {
        $out.Add('RUN INCOMPLETE')
        foreach ($m in $missing) { $out.Add("  MISSING $m") }
    }
    $out.Add('')
    $out.Add('Raw nets, /before to /settled (an idle window start to end): no drift taken out and no tolerance applied (SC-128 judges them).')
    $out.Add(('{0,-4} {1,-11} {2,5} {3,-10} {4,10} {5,10}' -f 'arm', 'type', 'exit', 'status', 'paged MiB', 'nonp. MiB') + ' ' + (($NetTags + 'handles', '0x120089' | ForEach-Object { '{0,8}' -f $_ }) -join ' '))
    $order = @()
    if ($plan) { $order = @($plan.windows)[0], @($plan.arms), @($plan.windows)[1] | ForEach-Object { $_ } | Where-Object { $_ } }
    foreach ($name in $order) {
        if ($Windows -contains $name) { $out.Add((Format-Net $name 'idle' '' '' (Get-Net $byLabel "$name/start" "$name/end"))); continue }
        $a = @($arms | Where-Object { $_.arm -eq $name })[0]
        if ($null -eq $a) { $out.Add((Format-Net $name '-' '' 'not run' $null)); continue }
        $exit = if ($null -eq $a.exitCode) { '-' } else { "$($a.exitCode)" }
        $status = if ($a.guardStop) { 'guard' } elseif ($a.report) { "$($a.report.status)" } else { '-' }
        $out.Add((Format-Net $name $a.type $exit $status (Get-Net $byLabel "$name/before" "$name/settled")))
    }
    $out.Add('')
    $prepare = Join-Path $dir 'prepare\prepare.json'
    $e = $null
    if (-not (Test-Path -LiteralPath $prepare)) { $out.Add("ENVIRONMENT not recorded: $prepare is missing") }
    else {
        $text = ([IO.File]::ReadAllText((Convert-Path -LiteralPath $prepare))).Trim([char]0, [char]0xFEFF, ' ', "`t", "`r", "`n")
        try { $e = ($text | ConvertFrom-Json).environment } catch { $e = $null }
        if ($null -eq $e) { $out.Add("ENVIRONMENT not readable: $prepare is not JSON with an environment record (a torn or unflushed write)") }
        else { $out.Add("ENVIRONMENT $($e.os); wcifs.sys $($e.wcifsVersion); drivers $(@($e.drivers | ForEach-Object { "$($_.name) $($_.state)" }) -join ', '); antivirus $(@($e.antivirus | ForEach-Object name) -join ', '); OneDrive running $($e.oneDriveRunning)") }
    }
    $out.Add((Get-FiltersLine $dir $e))
    [pscustomobject]@{ Lines = $out; Complete = ($missing.Count -eq 0) }
}

# ---- -Prepare and -Protocol ----

function Invoke-Prepare([string] $dir) {
    $dotnet = Join-Path $Repo 'eng\dotnet.ps1'
    $shell = (Get-Process -Id $PID).Path
    Write-Host 'Publishing the app and building the window hook ...'
    & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet publish (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Split-Path $AppExe) | Out-Host
    & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet build (Join-Path $Repo 'tests\window\FsaWindowHook\FsaWindowHook.csproj') -c Release -o (Split-Path $HookDll) | Out-Host
    foreach ($built in $AppExe, $HookDll) { if (-not (Test-Path -LiteralPath $built)) { throw "PoolWatch -Prepare: $built was not built" } }
    $prepDir = Join-Path $dir 'prepare'
    New-Item -ItemType Directory -Force -Path $prepDir | Out-Null
    $prepared = [ordered]@{ kind = 'prepare'; utc = (Get-Date).ToUniversalTime().ToString('o'); rehearsal = [bool]$script:Rehearsing; rpre = (Read-Mark 'Rpre'); environment = (Get-EnvironmentRecord $prepDir) }
    $prepared | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $prepDir 'prepare.json') -Encoding UTF8
    Join-Path $prepDir 'prepare.json'
}

# A tree that takes the app many 250 ms window ticks to list, so the hook's folder-count triggers act mid-scan (review
# F5; WindowCases' New-LargeTree, 30,150 folders, lists in well over a second here).
function New-RehearsalTree([string] $root) {
    for ($i = 0; $i -lt 150; $i++) {
        $top = Join-Path $root "n$i"
        for ($j = 0; $j -lt 200; $j++) { [void][IO.Directory]::CreateDirectory((Join-Path $top "m$j")) }
        [IO.File]::WriteAllBytes((Join-Path $top 'data.bin'), (New-Object byte[] 65536))
        for ($f = 0; $f -lt 29; $f++) { [IO.File]::WriteAllBytes((Join-Path $top "empty$f.txt"), (New-Object byte[] 0)) }
    }
    150 + 150 * 200
}

function Invoke-Protocol([string] $root, [hashtable[]] $stops) {
    $planned = @($FullArms + $StopArms)
    Add-Line (Join-Path $RunDir 'arms.jsonl') ([ordered]@{ kind = 'plan'; utc = (Get-Date).ToUniversalTime().ToString('o'); root = $root; windows = $Windows; arms = $planned; rehearsal = [bool]$script:Rehearsing })
    $script:LastGuardStop = $null
    $null = Write-Mark 'R0'
    Wait-Idle 'I0'
    foreach ($name in $FullArms) { $null = Invoke-ExeArm $name 'exe-full' $root $null }
    for ($i = 0; $i -lt $StopArms.Count; $i++) {
        $s = $stops[$i]
        $null = Invoke-ExeArm $StopArms[$i] $s.type $root $s.hook
    }
    Wait-Idle 'I1'
}

$script:Rehearsing = $false
$script:LastGuardStop = $null

switch ($PSCmdlet.ParameterSetName) {
    'Report' {
        $r = Write-RunReport $RunDir
        $r.Lines
        if ($r.Complete) { exit 0 } else { exit 2 }
    }
    'Prepare' {
        $written = Invoke-Prepare $RunDir
        Write-Host "Prepared: $written. Now restart with Start > Restart (not Shut down), sign in, open nothing but one PowerShell window, and within 30 minutes run:"
        Write-Host '  powershell -NoProfile -ExecutionPolicy Bypass -File tests\window\PoolWatch.ps1 -Protocol'
        Write-Host "If you choose to run 'fltmc filters' in an elevated window of your own, paste what it prints into $(Join-Path $RunDir 'fltmc.txt'), at any time: -Report reads it each time it runs."
    }
    'Protocol' {
        $flags = [Convert]::ToUInt32('80000001', 16) # ES_CONTINUOUS | ES_SYSTEM_REQUIRED: no sleep while it runs
        if ($Rehearse) {
            $script:Rehearsing = $true
            $scratch = Join-Path ([IO.Path]::GetTempPath()) ('fsa-poolwatch\rehearse-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
            Write-Output "REHEARSAL scratch: $scratch"
            $RunDir = Join-Path $scratch 'run'
            $Timing.WindowSec = 30; $Timing.MarkEverySec = 5; $Timing.SettleSec = 15; $Timing.TriggerDeadlineSec = 120
            $code = 2
            try {
                $null = Invoke-Prepare $RunDir
                $tree = Join-Path $scratch 'tree'
                $folders = New-RehearsalTree $tree
                # Folder-count triggers at 10% to 25% of the tree's folders, which the scan reaches well before its end.
                $at = { param($share) [string][int]($folders * $share) }
                $stops = @(
                    @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_FOLDERS = (& $at 0.10) } },
                    @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_FOLDERS = (& $at 0.15) } },
                    @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_FOLDERS = (& $at 0.20) } },
                    @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_FOLDERS = (& $at 0.25) } },
                    @{ type = 'exe-close'; hook = @{ FSA_HOOK_MODE = 'close'; FSA_HOOK_AT_FOLDERS = (& $at 0.20) } })
                [PoolWatchNative]::SetThreadExecutionState($flags) | Out-Null
                Invoke-Protocol $tree $stops
                $r = Write-RunReport $RunDir
                $r.Lines
                if ($r.Complete) { $code = 0 }
            }
            finally {
                [PoolWatchNative]::SetThreadExecutionState([Convert]::ToUInt32('80000000', 16)) | Out-Null
                if ($Keep) { Write-Output "REHEARSAL kept: $scratch" }
                else {
                    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Output "REHEARSAL scratch deleted: $(-not (Test-Path -LiteralPath $scratch))"
                }
            }
            exit $code
        }
        # The boot's age first: on a boot that has already walked the layer, every run would be the vacuous kind that voided
        # the earlier spikes, so nothing is started and nothing is written (review X4, case C-011).
        $boot = Get-Boot
        if ($boot.UptimeSec -gt $Timing.BootAgeMaxSec) { throw "PoolWatch -Protocol: this boot is $($boot.UptimeSec) s old, over $($Timing.BootAgeMaxSec) s. Restart (Start > Restart) and run it within 30 min (review X4)." }
        $prepFile = Join-Path $RunDir 'prepare\prepare.json'
        if (-not (Test-Path -LiteralPath $prepFile)) { throw "PoolWatch -Protocol: run -Prepare before the Restart first ($prepFile is missing)" }
        foreach ($built in $AppExe, $HookDll) { if (-not (Test-Path -LiteralPath $built)) { throw "PoolWatch -Protocol: $built is missing; run -Prepare before the Restart" } }
        if (Test-Path -LiteralPath (Join-Path $RunDir 'marks.jsonl')) {
            $fresh = Join-Path $RunDir ('protocol-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
            New-Item -ItemType Directory -Force -Path (Join-Path $fresh 'prepare') | Out-Null
            Copy-Item -LiteralPath $prepFile -Destination (Join-Path $fresh 'prepare\prepare.json')
            # A pasted fltmc list goes along, so -Report over the fresh folder still finds it (review m1).
            foreach ($paste in 'fltmc.txt', 'prepare\fltmc.txt') {
                $from = Join-Path $RunDir $paste
                if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $fresh $paste) }
            }
            $RunDir = $fresh
            Write-Host "Writing to $RunDir"
        }
        # The cancels land after the walk has passed the store (review m6, N5), in the size pass, and past C:\Users; the
        # close past the store too. FSA_HOOK_PAST names a path for the hook to compare the folder being listed with; the
        # hook opens nothing.
        $stops = @(
            @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_PAST = $StoreText; FSA_HOOK_PAST_FOLDERS = 500 } },
            @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_PAST = 'C:\Users'; FSA_HOOK_PAST_FOLDERS = 1000 } },
            @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_SIZED = 200000 } },
            @{ type = 'exe-cancel'; hook = @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_SIZED = 400000 } },
            @{ type = 'exe-close'; hook = @{ FSA_HOOK_MODE = 'close'; FSA_HOOK_PAST = $StoreText; FSA_HOOK_PAST_FOLDERS = 5000 } })
        [PoolWatchNative]::SetThreadExecutionState($flags) | Out-Null
        try { Invoke-Protocol 'C:\' $stops }
        finally { [PoolWatchNative]::SetThreadExecutionState([Convert]::ToUInt32('80000000', 16)) | Out-Null }
        $r = Write-RunReport $RunDir
        $r.Lines
        $r.Lines | Set-Content -LiteralPath (Join-Path $RunDir 'report.txt') -Encoding UTF8
        Write-Host "Done. Commit $RunDir with the slice."
        if ($r.Complete) { exit 0 } else { exit 2 }
    }
}
