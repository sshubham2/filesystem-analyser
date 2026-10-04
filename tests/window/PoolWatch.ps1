# SL-095's kernel-pool protocol: is the pool a C: scan leaves behind back within a stated tolerance once FsAnalyser has
# exited, with the Windows container layer store left out (DONE (c))? And if not, whose is the rest (O-003: outcome A, B
# or C)? Unelevated, under Windows PowerShell 5.1 and pwsh 7. Every reading is one JSON line in <RunDir>\marks.jsonl,
# every arm one line in <RunDir>\arms.jsonl, tags as an array (stack decision); the run logs are committed under
# docs/design/SL-095-runs/. The tolerance row and its routing are written in docs/design/SL-095-container-layer-store.md,
# before any run.
#
#   PoolWatch.ps1 -Mark -Label <name> [-RunDir <dir>]    one reading
#   PoolWatch.ps1 -Environment [-RunDir <dir>]            drivers, minifilter instances, antivirus, OneDrive, wcifs.sys, Fast
#                                                         Startup, the layer IDs, CmService; fltmc.txt in RunDir is appended
#   PoolWatch.ps1 -Attribute -Tags WCsc,FMfn [-PoolTagTxt <pooltag.txt>]   who owns a tag (pooltag.txt first; findstr
#                                                         otherwise, "ambiguous (N files)" for more than one hit)
#   PoolWatch.ps1 -Prepare [-RunDir <dir>]                BEFORE the Restart, on the boot as it is: builds every binary the
#                                                         protocol runs, takes Rpre, the environment, the store's size bound,
#                                                         the closing control's subtrees, and checks the hook's triggers and
#                                                         the pressure step's effect
#   PoolWatch.ps1 -Protocol [-RunDir <dir>]               AFTER a true Restart (Start > Restart), within 30 min of it, the
#                                                         PC left alone: about 4 h
#   PoolWatch.ps1 -Protocol -Session 2 [-RunDir <dir>]    a second Restart, only if arm L was over T: the non-app walk of C:
#                                                         (Q-178 condition 2, Q-185)
#   PoolWatch.ps1 -Report [-RunDir <dir>] [-NoWrite]      judges the run against the tolerance row: OUTCOME and VERDICT lines
#   PoolWatch.ps1 -Protocol -Smoke -Root <dir>            the protocol's machinery on a small folder, seconds instead of
#                                                         minutes, no boot-age check: proves the rig, measures nothing
#
# Nothing here elevates or asks for elevation (architecture rule). `fltmc filters` needs an administrator: a person who
# chooses to may run it in an elevated window of their own and paste what it prints into <RunDir>\fltmc.txt.
#
# What the protocol never does, and why: it never lists, walks or opens anything under the container layer store before
# the closing control, because a first touch per boot is single-use; every walker it runs either is the app's scanner
# (which leaves the store out) or does not follow links (robocopy /XJ): cmd's `dir /s` follows directory links, and
# C:\Users\All Users is one to C:\ProgramData, so `dir /s C:\Users` would walk the live layer (review X3).

[CmdletBinding(DefaultParameterSetName = 'Mark')]
param(
    [Parameter(ParameterSetName = 'Mark', Mandatory = $true)] [switch] $Mark,
    [Parameter(ParameterSetName = 'Mark', Mandatory = $true)] [string] $Label,
    [Parameter(ParameterSetName = 'Environment', Mandatory = $true)] [switch] $Environment,
    [Parameter(ParameterSetName = 'Attribute', Mandatory = $true)] [switch] $Attribute,
    [Parameter(ParameterSetName = 'Attribute', Mandatory = $true)] [string[]] $Tags,
    [Parameter(ParameterSetName = 'Attribute')] [string] $PoolTagTxt,
    [Parameter(ParameterSetName = 'Prepare', Mandatory = $true)] [switch] $Prepare,
    [Parameter(ParameterSetName = 'Protocol', Mandatory = $true)] [switch] $Protocol,
    [Parameter(ParameterSetName = 'Protocol')] [ValidateSet(1, 2)] [int] $Session = 1,
    [Parameter(ParameterSetName = 'Protocol')] [Parameter(ParameterSetName = 'Prepare')] [switch] $Smoke,
    [Parameter(ParameterSetName = 'Protocol')] [Parameter(ParameterSetName = 'Prepare')] [string] $Root,
    [Parameter(ParameterSetName = 'Report', Mandatory = $true)] [switch] $Report,
    [Parameter(ParameterSetName = 'Report')] [switch] $NoWrite,
    [Parameter(ParameterSetName = 'PressureChild', Mandatory = $true)] [switch] $PressureChild,
    [Parameter(ParameterSetName = 'PressureChild')] [long] $FloorMB = 4608,
    [Parameter(ParameterSetName = 'PressureChild')] [int] $HoldSec = 60,
    [string] $RunDir
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $RunDir) { $RunDir = Join-Path $Repo 'docs\design\SL-095-runs\session-1' }
$Artifacts = Join-Path $Repo 'artifacts\sl095'
$ScanArmExe = Join-Path $Repo 'artifacts\scan-arm\ScanArm.exe'
$HookDll = Join-Path $Repo 'artifacts\window-hook\FsaWindowHook.dll'
$FixedExe = Join-Path $Artifacts 'fixed\FsAnalyser.App.exe'
$UnfixedExe = Join-Path $Artifacts 'unfixed\FsAnalyser.App.exe'
$UnfixedCommit = 'afd021d'
$StoreText = Join-Path $env:ProgramData 'Microsoft\Windows\Containers\Layers'

# The tolerance row (docs/design/SL-095-container-layer-store.md), fixed before any run.
$Tolerance = @{
    FloorBytes       = 64MB     # T per pool is at least this
    DriftFactor      = 3        # ... or 3 x |idle drift| over a window of the same length, whichever is larger
    WindowSec        = 1200     # I0 and I1, 20 min each
    SettleSec        = 600      # every arm's after-reading is 10 min after its process exits
    BurstFactor      = 3        # an idle gap whose rate is over 3 x the median gap's is a burst, left out of drift
    DriftDisagree    = 3        # I0's and I1's drift more than 3 x apart: VOID
    BootAgeMaxSec    = 1800     # R0 within 30 min of the boot, or the protocol does not start
    ControlArmable   = 0.5      # the closing control is armable when WCsc rises by at least half the entries it listed
    GuardAvailableMB = 4096     # an arm is stopped when Available falls below this ...
    GuardPoolBytes   = 4GB      # ... or Paged + Nonpaged rise this far above R0
    PressureFloorMB  = 4608     # the pressure step commits memory down to this, above the guard (review X2)
    MarkEverySec     = 60
}
$WatchedTags = @('WCsc', 'WCfc', 'WCri', 'WCfn', 'WCse', 'WCss', 'WCce', 'WCrb', 'WCro', 'FMwi', 'FMfc', 'FMfn', 'File', 'NtfF', 'Ntff', 'NtFC', 'Toke', 'SeAt')
$LeakTags = @('WCsc', 'WCfc', 'WCri', 'FMwi')
$CacheTags = @('FMfn', 'File', 'NtfF')

# ---- Reading the machine ----

# -Report and -Attribute read files only, so they compile nothing (Windows PowerShell's Add-Type needs a writable %TEMP%).
if ($PSCmdlet.ParameterSetName -notin 'Report', 'Attribute' -and -not ('PoolWatchNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class PoolWatchNative
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMemoryResourceNotification(int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryMemoryResourceNotification(IntPtr handle, out bool state);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

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
                throw new InvalidOperationException(string.Format("NtQuerySystemInformation({0}) failed: 0x{1:X8}", infoClass, status));
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
            throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        return (long)(status.AvailPhys / (1024 * 1024));
    }

    // LowMemoryResourceNotification (0): whether Windows says physical memory is low right now.
    public static bool LowMemory()
    {
        IntPtr handle = CreateMemoryResourceNotification(0);
        bool state;
        return handle != IntPtr.Zero && QueryMemoryResourceNotification(handle, out state) && state;
    }

    // Commits and touches memory, 256 MiB at a time, until Available is at or under floorMB; holds it; returns
    // {chunks, minAvailableMB, lowMemorySeen (0/1)}. The memory goes back when the process exits.
    public static long[] Pressure(long floorMB, int holdSeconds)
    {
        var chunks = new List<byte[]>();
        long min = AvailableMB();
        bool low = false;
        while (AvailableMB() > floorMB)
        {
            long room = AvailableMB() - floorMB;
            int size = (int)Math.Min(256L, Math.Max(16L, room)) * 1024 * 1024;
            byte[] chunk = new byte[size];
            for (int i = 0; i < size; i += 4096)
                chunk[i] = 1;
            chunks.Add(chunk);
            min = Math.Min(min, AvailableMB());
            low |= LowMemory();
        }
        DateTime until = DateTime.UtcNow.AddSeconds(holdSeconds);
        while (DateTime.UtcNow < until)
        {
            System.Threading.Thread.Sleep(1000);
            foreach (byte[] chunk in chunks)
                for (int i = 0; i < chunk.Length; i += 4096)
                    chunk[i]++;
            min = Math.Min(min, AvailableMB());
            low |= LowMemory();
        }
        GC.KeepAlive(chunks);
        return new long[] { chunks.Count, min, low ? 1 : 0 };
    }
}
'@
}

function Get-Boot {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{ BootUtc = $os.LastBootUpTime.ToUniversalTime(); UptimeSec = [long]((Get-Date) - $os.LastBootUpTime).TotalSeconds }
}

# One reading: pool counters, Available, the watched tags, the System process's handles. A part Windows will not give
# is null, and -Report says UNDECIDED wherever a verdict needs it (review m7).
function Read-Mark([string] $label, [hashtable] $extra = @{}) {
    $memory = Get-CimInstance Win32_PerfRawData_PerfOS_Memory
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
    $mark = [ordered]@{
        kind          = 'mark'
        label         = $label
        utc           = (Get-Date).ToUniversalTime().ToString('o')
        bootUtc       = $boot.BootUtc.ToString('o')
        uptimeSec     = $boot.UptimeSec
        pagedBytes    = [long]$memory.PoolPagedBytes
        nonpagedBytes = [long]$memory.PoolNonpagedBytes
        availableMB   = [long]$memory.AvailableMBytes
        lowMemory     = [PoolWatchNative]::LowMemory()
        systemHandles = $system
        system120089  = $system120089
        tags          = $tags
    }
    foreach ($k in $extra.Keys) { $mark[$k] = $extra[$k] }
    $mark
}

function Add-Line([string] $file, $value) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
    Add-Content -LiteralPath $file -Value ($value | ConvertTo-Json -Compress -Depth 6) -Encoding UTF8
}

function Write-Mark([string] $label, [hashtable] $extra = @{}) {
    $m = Read-Mark $label $extra
    Add-Line (Join-Path $RunDir 'marks.jsonl') $m
    Write-Host ('{0,-16} paged {1,7:N0} MB  nonpaged {2,7:N0} MB  available {3,6:N0} MB  System handles {4} (0x120089: {5})  WCsc live {6}' -f `
            $label, ($m.pagedBytes / 1MB), ($m.nonpagedBytes / 1MB), $m.availableMB, $m.systemHandles, $m.system120089, (Live $m 'WCsc'))
    $m
}

function Tag($mark, [string] $name) { if ($null -eq $mark.tags) { return $null }; @($mark.tags | Where-Object { $_.tag -ceq $name })[0] }
function Live($mark, [string] $name) {
    $t = Tag $mark $name
    if ($null -eq $t) { if ($null -eq $mark.tags) { return $null } else { return 0 } }
    ([long]$t.pa - [long]$t.pf) + ([long]$t.na - [long]$t.nf)
}

# ---- The environment (criterion 3) ----

function Get-EnvironmentRecord {
    $drivers = @(foreach ($name in 'wcifs', 'CldFlt', 'WdFilter', 'bindflt', 'FileInfo', 'luafv', 'storqosflt') {
            $d = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'" -ErrorAction SilentlyContinue
            if ($d) { [ordered]@{ name = $d.Name; state = $d.State; startMode = $d.StartMode; path = $d.PathName } }
        })
    # The minifilters' registered instances and altitudes: what `fltmc filters` lists, as the registry holds it, unelevated.
    $instances = @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction SilentlyContinue | ForEach-Object {
            $key = Join-Path $_.PSPath 'Instances'
            if (Test-Path $key) {
                foreach ($i in Get-ChildItem $key -ErrorAction SilentlyContinue) {
                    $alt = (Get-ItemProperty $i.PSPath -ErrorAction SilentlyContinue).Altitude
                    if ($alt) { [ordered]@{ service = $_.PSChildName; instance = $i.PSChildName; altitude = "$alt" } }
                }
            }
        })
    $antivirus = @(try { Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop | ForEach-Object { [ordered]@{ name = $_.displayName; state = ('0x{0:X}' -f $_.productState) } } } catch { })
    $wcifs = Join-Path $env:SystemRoot 'System32\drivers\wcifs.sys'
    $cm = Get-CimInstance Win32_Service -Filter "Name='CmService'" -ErrorAction SilentlyContinue
    $cmStart = $null
    if ($cm -and $cm.ProcessId) { try { $cmStart = (Get-Process -Id $cm.ProcessId).StartTime.ToUniversalTime().ToString('o') } catch { } }
    $layers = @()
    if (Test-Path -LiteralPath $StoreText) { $layers = @(Get-ChildItem -LiteralPath $StoreText -Directory -Force | ForEach-Object Name) } # names only
    $os = Get-CimInstance Win32_OperatingSystem
    $fltmc = Join-Path $RunDir 'fltmc.txt'
    [ordered]@{
        kind              = 'environment'
        utc               = (Get-Date).ToUniversalTime().ToString('o')
        os                = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
        bootUtc           = $os.LastBootUpTime.ToUniversalTime().ToString('o')
        wcifsVersion      = if (Test-Path $wcifs) { (Get-Item $wcifs).VersionInfo.FileVersion } else { $null }
        drivers           = $drivers
        minifilterInstances = $instances
        antivirus         = $antivirus
        oneDriveRunning   = [bool](Get-Process OneDrive -ErrorAction SilentlyContinue)
        hiberbootEnabled  = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -ErrorAction SilentlyContinue).HiberbootEnabled
        cmService         = if ($cm) { [ordered]@{ state = $cm.State; processId = $cm.ProcessId; startUtc = $cmStart } } else { $null }
        layerIds          = $layers
        fltmcFilters      = if (Test-Path $fltmc) { @(Get-Content -LiteralPath $fltmc) } else { 'not supplied: needs an administrator; paste `fltmc filters` into fltmc.txt' }
    }
}

# Who owns a tag: Microsoft's pooltag.txt when given (it names ntoskrnl's own tags, File and Toke among them), else the
# drivers and system programs whose bytes hold it, and never one owner from several files (review N6).
function Get-TagOwner([string] $tag, [string] $poolTag) {
    if ($poolTag -and (Test-Path -LiteralPath $poolTag)) {
        $line = Select-String -LiteralPath $poolTag -SimpleMatch -CaseSensitive -Pattern "$tag " | Select-Object -First 1
        if ($line) { return "pooltag.txt: $($line.Line.Trim())" }
    }
    # One findstr per folder, over its wildcard: it prints each file that holds the tag's four bytes (case-sensitive).
    $hits = @(foreach ($files in (Join-Path $env:SystemRoot 'System32\drivers\*.sys'), (Join-Path $env:SystemRoot 'System32\*.exe')) {
            & findstr.exe /m /l /c:$tag $files 2>$null
        }) | Where-Object { $_ } | ForEach-Object { if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path (Join-Path $env:SystemRoot 'System32') $_ } }
    $hits = @($hits)
    if ($hits.Count -eq 0) { return 'not found' }
    if ($hits.Count -gt 1) { return "ambiguous ($($hits.Count) files)" }
    $v = (Get-Item -LiteralPath $hits[0]).VersionInfo
    "$(Split-Path -Leaf $hits[0]) ($($v.CompanyName), $($v.FileVersion)) by findstr"
}

# ---- Running an arm ----

# Starts a process with its own environment variables (5.1 and 7 alike), never this shell's.
function Start-Arm([string] $file, [string[]] $arguments, [hashtable] $variables = @{}, [string] $stdout) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $file
    if ($file -like '*cmd.exe') { $info.Arguments = $arguments -join ' ' } # a command line cmd parses itself
    else { $info.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '(\\+)$', '$1$1') + '"' } else { $_ } }) -join ' ' }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = [bool]$stdout
    foreach ($k in $variables.Keys) { $info.EnvironmentVariables[$k] = $variables[$k] }
    $p = [System.Diagnostics.Process]::Start($info)
    $null = $p.Handle
    $p
}

# One arm: a mark before, a mark every minute while it runs (and the guard), a mark as it exits, and the settled mark
# after the settle time. The arm's own line goes to arms.jsonl. Returns that line.
function Invoke-Arm([string] $name, [string] $kind, [string] $file, [string[]] $arguments, [hashtable] $variables = @{}, [hashtable] $info = @{}) {
    $r0 = $script:R0
    $null = Write-Mark "$name/before"
    $out = Join-Path $RunDir "$name.stdout.txt"
    $p = Start-Arm $file $arguments $variables $out
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $guard = $null
    $every = [Math]::Max(1, $script:MarkEvery)
    $next = (Get-Date).AddSeconds($every); $n = 0
    while (-not $p.WaitForExit(2000)) {
        $avail = [PoolWatchNative]::AvailableMB()
        $memory = Get-CimInstance Win32_PerfRawData_PerfOS_Memory
        $pool = [long]$memory.PoolPagedBytes + [long]$memory.PoolNonpagedBytes
        if ($avail -lt $Tolerance.GuardAvailableMB -or ($r0 -and $pool - ([long]$r0.pagedBytes + [long]$r0.nonpagedBytes) -gt $Tolerance.GuardPoolBytes)) {
            $guard = "available $avail MB, pool $([Math]::Round($pool / 1GB, 2)) GB"
            try { $p.Kill() } catch { }
            $p.WaitForExit()
            break
        }
        if ((Get-Date) -ge $next) { $n++; $null = Write-Mark ('{0}/t+{1:D3}' -f $name, $n); $next = (Get-Date).AddSeconds($every) }
    }
    $exit = $null
    if ($p.HasExited) { $exit = $p.ExitCode }
    $text = $stdoutTask.Result
    Set-Content -LiteralPath $out -Value $text -Encoding UTF8
    $null = Write-Mark "$name/exit"
    Start-Sleep -Seconds $script:Settle
    $null = Write-Mark "$name/settled"
    $summary = $null
    $json = @($text -split "`r?`n" | Where-Object { $_.StartsWith('{') }) | Select-Object -Last 1
    if ($json) { try { $summary = $json | ConvertFrom-Json } catch { } }
    $line = [ordered]@{ kind = 'arm'; arm = $name; type = $kind; exitCode = $exit; guardStop = $guard; summary = $summary }
    foreach ($k in $info.Keys) { $line[$k] = $info[$k] }
    Add-Line (Join-Path $RunDir 'arms.jsonl') $line
    $line
}

# An exe run in report mode, with the hook when asked: its report's core, and the hook's log line of where it acted.
function Invoke-ExeArm([string] $name, [string] $kind, [string] $exe, [string] $root, [hashtable] $hook = $null) {
    $reportFile = Join-Path $RunDir "$name.report.json"
    $hookLog = Join-Path $RunDir "$name.hook.log"
    $vars = @{}
    if ($hook) {
        $vars['DOTNET_STARTUP_HOOKS'] = $HookDll
        $vars['FSA_HOOK_LOG'] = $hookLog
        foreach ($k in $hook.Keys) { $vars[$k] = "$($hook[$k])" }
    }
    $line = Invoke-Arm $name $kind $exe @($root, '--report', $reportFile) $vars @{ hook = $hook }
    $core = $null; $acted = $null
    if (Test-Path -LiteralPath $reportFile) { $r = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json; $core = $r.core; $line['outcome'] = $r.outcome }
    if (Test-Path -LiteralPath $hookLog) { $acted = @(Get-Content -LiteralPath $hookLog | Where-Object { $_ -match '^(acted|refused|passed)' }) }
    $line['report'] = $core; $line['hookLog'] = $acted
    $folders = $null
    foreach ($l in @($acted)) { if ($l -match '^acted .* folders=(\d+)') { $folders = [long]$Matches[1] } }
    $line['foldersAtCancel'] = $folders
    $line['ranFull'] = [bool](@($acted) -match 'ended before the trigger')
    # Rewrite the arm's line with what the report and the hook said.
    $lines = @(Get-Content -LiteralPath (Join-Path $RunDir 'arms.jsonl'))
    $lines[-1] = $line | ConvertTo-Json -Compress -Depth 6
    Set-Content -LiteralPath (Join-Path $RunDir 'arms.jsonl') -Value $lines -Encoding UTF8
    $line
}

function Wait-Idle([string] $name, [int] $seconds) {
    $marks = [Math]::Max(1, [int]($seconds / [Math]::Max(1, $script:MarkEvery)))
    $null = Write-Mark "$name/00"
    for ($i = 1; $i -le $marks; $i++) {
        Start-Sleep -Seconds ([Math]::Max(1, $script:MarkEvery))
        $null = Write-Mark ('{0}/{1:D2}' -f $name, $i)
    }
}

function Invoke-Pressure([string] $name) {
    $null = Write-Mark "$name/before"
    $shell = (Get-Process -Id $PID).Path
    $out = & $shell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -PressureChild -FloorMB $script:PressureFloor -HoldSec $script:PressureHold
    $m = Write-Mark "$name/after"
    $result = $null; try { $result = ($out | Select-Object -Last 1) | ConvertFrom-Json } catch { }
    $line = [ordered]@{ kind = 'arm'; arm = $name; type = 'pressure'; exitCode = 0; guardStop = $null; summary = $result }
    Add-Line (Join-Path $RunDir 'arms.jsonl') $line
    $line
}

# A non-app walk that never follows a directory link: robocopy's list-only mode with /XJ, per root (review X3).
function Invoke-NoLinkWalk([string[]] $roots, [string] $log) {
    $empty = Join-Path $RunDir 'robocopy-target-never-created'
    foreach ($r in $roots) { & robocopy.exe $r $empty /L /E /XJ /R:0 /W:0 /NP /BYTES /NFL /NDL /NJH /LOG+:$log | Out-Null }
}

# Robocopy's own byte total from a /L /BYTES log, the store's upper bound (review Y4).
function Read-RobocopyBytes([string] $log) {
    $line = @(Get-Content -LiteralPath $log | Where-Object { $_ -match '^\s*Bytes\s*:' }) | Select-Object -Last 1
    if ($line -match ':\s*(\d+)') { [long]$Matches[1] } else { $null }
}

# ---- -Report: the judge ----

function Read-Jsonl([string] $file) { if (Test-Path -LiteralPath $file) { @(Get-Content -LiteralPath $file | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
# pwsh 7's ConvertFrom-Json turns an ISO time into a DateTime already; Windows PowerShell 5.1's leaves the string.
function Utc($m) { if ($m.utc -is [DateTime]) { $m.utc.ToUniversalTime() } else { [DateTime]::Parse([string]$m.utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() } }
function Seconds($a, $b) { ((Utc $b) - (Utc $a)).TotalSeconds }
function Median([double[]] $xs) { if ($xs.Count -eq 0) { return 0 }; $s = $xs | Sort-Object; if ($s.Count % 2) { $s[[int][Math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }

# The idle gaps of a window (consecutive marks name/00, name/01, ...): per-second rate of a quantity in each.
function Gap-Rates($marks, [string] $window, [scriptblock] $value) {
    $w = @($marks | Where-Object { $_.label -like "$window/*" } | Sort-Object { Utc $_ })
    $rates = @()
    for ($i = 1; $i -lt $w.Count; $i++) {
        $a = & $value $w[$i - 1]; $b = & $value $w[$i]
        $dt = Seconds $w[$i - 1] $w[$i]
        if ($null -ne $a -and $null -ne $b -and $dt -gt 0) { $rates += (($b - $a) / $dt) }
    }
    , $rates
}

# Drift per second with bursts left out: gaps whose |rate| is over BurstFactor x the median |rate| (review M6, X5).
function Drift([double[]] $rates) {
    if ($rates.Count -eq 0) { return $null }
    $med = Median ($rates | ForEach-Object { [Math]::Abs($_) })
    $kept = @($rates | Where-Object { [Math]::Abs($_) -le $Tolerance.BurstFactor * [Math]::Max($med, 1MB / 60) }) # below 1 MB a minute a gap is never a burst
    if ($kept.Count -eq 0) { $kept = $rates }
    ($kept | Measure-Object -Average).Average
}

# The WCsc rise of each named arm, before to settled, against the envelope of I0's idle gaps: the reason the first arm
# over it is, or null. -Protocol asks it after L and P, before any exe run (review N1).
function Test-ArmWcifs([string[]] $names) {
    $marks = Read-Jsonl (Join-Path $RunDir 'marks.jsonl')
    $byLabel = @{}; foreach ($m in $marks) { $byLabel[$m.label] = $m }
    [double[]] $rates = Gap-Rates $marks 'I0' { param($m) Live $m 'WCsc' } # one array, as Gap-Rates returns it
    [double] $rate = 0; if ($rates.Count) { $rate = [Math]::Max([double]0, [double]($rates | Measure-Object -Maximum).Maximum) }
    foreach ($n in $names) {
        $a = $byLabel["$n/before"]; $b = $byLabel["$n/settled"]
        if ($null -eq $a -or $null -eq $b) { return "arm $n has no before or settled mark" }
        $wa = Live $a 'WCsc'; $wb = Live $b 'WCsc'
        if ($null -eq $wa -or $null -eq $wb) { return "arm $n has no WCsc reading" }
        $allowed = $rate * (Seconds $a $b)
        if ($wb - $wa -gt $allowed) { return ('arm {0} raised WCsc by {1}, over the idle envelope of {2:N1}' -f $n, ($wb - $wa), $allowed) }
    }
    $null
}

function Get-Verdict($marks, $arms) {
    $why = New-Object System.Collections.Generic.List[string]
    $byLabel = @{}; foreach ($m in $marks) { $byLabel[$m.label] = $m }
    $armBy = @{}; foreach ($a in $arms) { $armBy[$a.arm] = $a }
    $result = [ordered]@{ outcome = $null; verdict = $null; reasons = $why; tolerance = $null; arms = [ordered]@{} }
    function Done([string] $o, [string] $v) { $result.outcome = $o; $result.verdict = $v; return $result }

    # -Protocol stopped before F1 because wcifs moved during L or P beyond the idle envelope (review N1): a person decides.
    if ($byLabel.ContainsKey('abort')) { $why.Add("WCIFS-UNATTRIBUTED: the protocol stopped before F1: $($byLabel['abort'].reason)"); return (Done 'WCIFS-UNATTRIBUTED' $null) }

    foreach ($need in 'R0', 'I0/00', 'I1/00', 'L/before', 'L/settled', 'C/before', 'C/settled') {
        if (-not $byLabel.ContainsKey($need)) { $why.Add("UNDECIDED: mark $need is missing"); return (Done 'UNDECIDED' $null) }
    }
    $R0 = $byLabel['R0']
    if ($null -eq $R0.uptimeSec -or $R0.uptimeSec -gt $Tolerance.BootAgeMaxSec) { $why.Add("VOID: the boot was $($R0.uptimeSec) s old at R0, over $($Tolerance.BootAgeMaxSec) s"); return (Done 'VOID' $null) }
    foreach ($a in $arms) {
        if ($a.type -ne 'pressure' -and $null -eq $a.exitCode -and -not $a.guardStop) { $why.Add("VOID: arm $($a.arm)'s process did not exit"); return (Done 'VOID' $null) }
    }

    # Drift and T per pool, from both idle windows.
    $T = @{}; $drift = @{}
    foreach ($pool in 'pagedBytes', 'nonpagedBytes') {
        $sb = [scriptblock]::Create("param(`$m) `$m.$pool")
        $r0 = Gap-Rates $marks 'I0' $sb; $r1 = Gap-Rates $marks 'I1' $sb
        $d0 = Drift $r0; $d1 = Drift $r1
        if ($null -eq $d0 -or $null -eq $d1) { $why.Add("UNDECIDED: no idle gaps for $pool"); return (Done 'UNDECIDED' $null) }
        $hi = [Math]::Max([Math]::Abs($d0), [Math]::Abs($d1)); $lo = [Math]::Min([Math]::Abs($d0), [Math]::Abs($d1))
        $noise = 1MB / $Tolerance.WindowSec # below 1 MB per window the two are both quiet, however their ratio reads
        if ($hi -gt $noise -and $hi -gt $Tolerance.DriftDisagree * [Math]::Max($lo, $noise)) {
            $why.Add(("VOID: idle drift of {0} disagrees between I0 ({1:N0} B/s) and I1 ({2:N0} B/s), over {3}x" -f $pool, $d0, $d1, $Tolerance.DriftDisagree)); return (Done 'VOID' $null)
        }
        $drift[$pool] = ($d0 + $d1) / 2
        $T[$pool] = [Math]::Max([double]$Tolerance.FloorBytes, $Tolerance.DriftFactor * [Math]::Abs($drift[$pool]) * $Tolerance.WindowSec)
    }
    $result.tolerance = [ordered]@{ pagedBytes = [long]$T['pagedBytes']; nonpagedBytes = [long]$T['nonpagedBytes']; driftPagedPerSec = $drift['pagedBytes']; driftNonpagedPerSec = $drift['nonpagedBytes'] }

    # The WC* and handle envelope of the idle gaps: what wcifs does with no arm running.
    $wcIdle = @((Gap-Rates $marks 'I0' { param($m) Live $m 'WCsc' }) + (Gap-Rates $marks 'I1' { param($m) Live $m 'WCsc' }))
    $hIdle = @((Gap-Rates $marks 'I0' { param($m) $m.system120089 }) + (Gap-Rates $marks 'I1' { param($m) $m.system120089 }))
    $wcRate = 0; if ($wcIdle.Count) { $wcRate = [Math]::Max(0, ($wcIdle | Measure-Object -Maximum).Maximum) }
    $hRate = 0; if ($hIdle.Count) { $hRate = [Math]::Max(0, ($hIdle | Measure-Object -Maximum).Maximum) }

    function Net([string] $name, [string] $from = "$name/before", [string] $to = "$name/settled") {
        $a = $byLabel[$from]; $b = $byLabel[$to]
        if ($null -eq $a -or $null -eq $b) { return $null }
        $dt = Seconds $a $b
        $wcA = Live $a 'WCsc'; $wcB = Live $b 'WCsc'
        [ordered]@{
            seconds       = $dt
            pagedBytes    = [long](([long]$b.pagedBytes - [long]$a.pagedBytes) - $drift['pagedBytes'] * $dt)
            nonpagedBytes = [long](([long]$b.nonpagedBytes - [long]$a.nonpagedBytes) - $drift['nonpagedBytes'] * $dt)
            wcsc          = if ($null -ne $wcA -and $null -ne $wcB) { $wcB - $wcA } else { $null }
            handles120089 = if ($null -ne $a.system120089 -and $null -ne $b.system120089) { [long]$b.system120089 - [long]$a.system120089 } else { $null }
            wcAllowed     = $wcRate * $dt
            handleAllowed = $hRate * $dt
        }
    }
    function Over($net) { $net.pagedBytes -gt $T['pagedBytes'] -or $net.nonpagedBytes -gt $T['nonpagedBytes'] }

    # The closing control: was the layer still armable on this boot (spike 85's VOID; review Y3)?
    $c = Net 'C'; $cArm = $armBy['C']
    $result.arms['C'] = $c
    if ($null -eq $c.wcsc -or $null -eq $cArm -or $null -eq $cArm.entries) { $why.Add('UNDECIDED: the closing control has no WCsc reading or no entry count'); return (Done 'UNDECIDED' $null) }
    if ($null -eq $c.handles120089) { $why.Add('UNDECIDED: the closing control has no 0x120089 handle reading (the class 64 handle query failed)'); return (Done 'UNDECIDED' $null) }
    $armable = $c.wcsc -ge $Tolerance.ControlArmable * [long]$cArm.entries -and $c.wcsc -gt 0 `
        -and [Math]::Abs($c.handles120089 - $c.wcsc) -le [Math]::Max(5, 0.5 * $c.wcsc)
    if (-not $armable) { $why.Add("VOID: the closing control leaked $($c.wcsc) WCsc (0x120089 handles $($c.handles120089)) over $($cArm.entries) entries: the layer was not armable, so 0 WC* in the arms says nothing"); return (Done 'VOID' $null) }

    # Every fixed arm: pool against T, and wcifs against the idle envelope (reviews N1, N2, X5).
    $unattributed = @()
    foreach ($name in @('L', 'S', 'P') + @($arms | Where-Object { $_.type -like 'exe-*' } | ForEach-Object { $_.arm })) {
        $n = Net $name
        if ($null -eq $n) { continue }
        $result.arms[$name] = $n
        $a = $armBy[$name]
        if ($a -and $a.type -like 'exe-*' -and $a.report -and $null -eq $a.report.opensUnderExcludedStore) { $why.Add("UNDECIDED: $name's report has no opensUnderExcludedStore"); return (Done 'UNDECIDED' $null) }
        if ($a -and $a.report -and [long]$a.report.opensUnderExcludedStore -ne 0) { $unattributed += "$name opened $($a.report.opensUnderExcludedStore) folders under the store" }
        if ($a -and $a.type -like 'exe-*' -and -not $a.ranFull -and $a.report -and [long]$a.report.skippedStores -lt 1 -and $a.type -eq 'exe-full') { $unattributed += "$name's report has no skipped store entry" }
        if ($null -eq $n.wcsc) { $why.Add("UNDECIDED: arm $name has no WCsc reading"); return (Done 'UNDECIDED' $null) }
        if ($null -eq $n.handles120089) { $why.Add("UNDECIDED: arm $name has no 0x120089 handle reading (the class 64 handle query failed)"); return (Done 'UNDECIDED' $null) }
        if ($n.wcsc -gt $n.wcAllowed) { $unattributed += "$name WCsc +$($n.wcsc) over the idle envelope $([Math]::Round($n.wcAllowed, 1))" }
        if ($n.handles120089 -gt [Math]::Max($n.wcsc, 0) + $n.handleAllowed + 48) { $unattributed += "$name 0x120089 handles +$($n.handles120089) over its WCsc rise" }
    }
    $L = $result.arms['L']
    if ($null -eq $L) { $why.Add('UNDECIDED: arm L has no net reading'); return (Done 'UNDECIDED' $null) }
    if ($unattributed.Count) { foreach ($u in $unattributed) { $why.Add("WCIFS-UNATTRIBUTED: $u") }; return (Done 'WCIFS-UNATTRIBUTED' $null) }

    # The pressure step: did it take back anything (reviews M3, N3, X2)? Only what survives it can be an OS leak.
    $pr = $armBy['PR']; $prNet = Net 'PR' 'PR/before' 'PR/after'; $effective = $null; $post = $null
    if ($prNet) {
        $fell = @($CacheTags | Where-Object { $x = Live $byLabel['PR/before'] $_; $y = Live $byLabel['PR/after'] $_; $null -ne $x -and $null -ne $y -and $y -lt 0.99 * $x })
        $effective = $fell.Count -gt 0
        $post = Net 'post-pressure' 'R0' 'PR/after'
        $result.arms['PR'] = [ordered]@{ effective = $effective; fell = $fell; minAvailableMB = $pr.summary.minAvailableMB; lowMemorySeen = $pr.summary.lowMemorySeen }
        $result.arms['post-pressure'] = $post
    }

    $listed = $null; if ($armBy['L'] -and $armBy['L'].summary) { $listed = [long]$armBy['L'].summary.listed }
    $reclaimed = $false
    $lOver = ($armBy['L'] -and $armBy['L'].guardStop) -or (Over $L)
    if ($lOver) {
        if ($null -eq $effective) { $why.Add('UNDECIDED: L is over T and there is no pressure step to tell cache from leak'); return (Done 'UNDECIDED' $null) }
        if (-not $effective) { $why.Add('UNDECIDED: L is over T and the pressure step was INEFFECTIVE: no watched tag fell, so cache and leak cannot be told apart'); return (Done 'UNDECIDED' $null) }
        if (Over $post) { $why.Add(("C: L is over T (paged {0:N0} MB, nonpaged {1:N0} MB) and {2:N0} / {3:N0} MB survive the pressure step" -f ($L.pagedBytes / 1MB), ($L.nonpagedBytes / 1MB), ($post.pagedBytes / 1MB), ($post.nonpagedBytes / 1MB))); return (Done 'C' 'OS-recorded only if Q-178 conditions (1)-(3) hold, with the session-2 non-app walk; otherwise FAIL') }
        $why.Add('A: L was over T before the pressure step and within T after it: the retention was cache, reclaimed under pressure')
        $reclaimed = $true
    }

    # The per-file opens, projected to full C: from L's file count (O-003).
    foreach ($arm in 'S', 'P') {
        $n = $result.arms[$arm]; $line = $armBy[$arm]
        if ($null -eq $n -or $null -eq $line -or $null -eq $line.summary -or $null -eq $listed) { $why.Add("UNDECIDED: arm $arm or L's file count is missing"); return (Done 'UNDECIDED' $null) }
        $count = [Math]::Max(1, [long]$line.summary.opened + [long]$line.summary.failed)
        $n['perFilePaged'] = $n.pagedBytes / $count; $n['perFileNonpaged'] = $n.nonpagedBytes / $count
        # What L kept and the pressure step took back was cache: the projection starts from nothing, not from it.
        $baseP = $L.pagedBytes; $baseN = $L.nonpagedBytes; if ($reclaimed) { $baseP = 0; $baseN = 0 }
        $n['projectedPaged'] = [long]($baseP + $n.perFilePaged * $listed); $n['projectedNonpaged'] = [long]($baseN + $n.perFileNonpaged * $listed)
    }
    $S = $result.arms['S']; $P = $result.arms['P']
    $sOver = $S.projectedPaged -gt $T['pagedBytes'] -or $S.projectedNonpaged -gt $T['nonpagedBytes']
    $pOver = $P.projectedPaged -gt $T['pagedBytes'] -or $P.projectedNonpaged -gt $T['nonpagedBytes']
    if ($sOver -and -not $pOver) { $why.Add('B: the app''s per-file probe projects over T where a plain open does not'); return (Done 'B' 'FAIL') }
    if ($sOver -and $pOver) {
        $excess = [Math]::Max($S.projectedPaged - $P.projectedPaged, $S.projectedNonpaged - $P.projectedNonpaged)
        $noise = 2 * [Math]::Max([Math]::Abs($drift['pagedBytes']), [Math]::Abs($drift['nonpagedBytes'])) * $Tolerance.WindowSec
        if ($excess -le [Math]::Max([Math]::Min($T['pagedBytes'], $T['nonpagedBytes']) / 2, $noise)) { $why.Add('C: the probe and a plain open both project over T, alike'); return (Done 'C' 'OS-recorded only if Q-178 conditions (1)-(3) hold, with the session-2 non-app walk; otherwise FAIL') }
        $why.Add('B: both project over T, the probe by more than half T beyond the plain open'); return (Done 'B' 'FAIL')
    }

    # The ten exe runs, one by one and together (the reporter's "scan after scan").
    $full = @($arms | Where-Object { $_.type -eq 'exe-full' -or ($_.type -like 'exe-c*' -and $_.ranFull) })
    $cancelled = @($arms | Where-Object { $_.type -like 'exe-c*' -and -not $_.ranFull })
    $fullOver = @($full | Where-Object { Over $result.arms[$_.arm] } | ForEach-Object { $_.arm })
    $cancelOver = @($cancelled | Where-Object { Over $result.arms[$_.arm] } | ForEach-Object { $_.arm })
    $exe = @($arms | Where-Object { $_.type -like 'exe-*' })
    if ($exe.Count) {
        $cumulative = Net 'exe' "$($exe[0].arm)/before" "$($exe[-1].arm)/settled"
        $result.arms['cumulative'] = $cumulative
        if ($fullOver.Count) { $why.Add("FAIL: full exe runs over T: $($fullOver -join ', ')"); return (Done 'FAIL' 'FAIL') }
        if ($cancelOver.Count) { $why.Add("FAIL-cancel: cancelled or closed runs over T while the full runs are within: $($cancelOver -join ', ')"); return (Done 'FAIL-cancel' 'FAIL') }
        if (Over $cumulative) { $why.Add('FAIL: the exe runs together are over T'); return (Done 'FAIL' 'FAIL') }
    }
    $why.Add('A: L, the projections and every exe run are within T, wcifs stayed inside its idle envelope, and the closing control shows the layer was armable')
    Done 'A' 'PASS'
}

# Criterion 3's filter list, apart from the pool verdict above, which does not rest on it: the `fltmc filters` output a
# person pasted (fltmc.txt beside the run or its prepare folder, or as an environment record carries it). Missing, it
# reads UNDECIDED, never recorded (review m7).
function Get-FiltersLine {
    foreach ($f in (Join-Path $RunDir 'fltmc.txt'), (Join-Path $RunDir 'prepare\fltmc.txt')) {
        if ((Test-Path -LiteralPath $f) -and @(Get-Content -LiteralPath $f | Where-Object { $_.Trim() }).Count) { return "FILTERS recorded: $f" }
    }
    foreach ($f in (Join-Path $RunDir 'environment.json'), (Join-Path $RunDir 'prepare\prepare.json')) {
        if (-not (Test-Path -LiteralPath $f)) { continue }
        $e = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
        if ($e.kind -eq 'prepare') { $e = $e.environment }
        if ($e.fltmcFilters -is [array] -and @($e.fltmcFilters | Where-Object { "$_".Trim() }).Count) { return "FILTERS recorded: $f" }
    }
    'FILTERS UNDECIDED: no `fltmc filters` output was supplied (it needs an administrator), so criterion 3''s filter list is not recorded'
}

# ---- Modes ----

$script:MarkEvery = $Tolerance.MarkEverySec
$script:Settle = $Tolerance.SettleSec
$script:PressureFloor = $Tolerance.PressureFloorMB
$script:PressureHold = 60
$script:R0 = $null

switch ($PSCmdlet.ParameterSetName) {
    'Mark' {
        $null = Write-Mark $Label
    }
    'Environment' {
        $e = Get-EnvironmentRecord
        New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
        $e | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunDir 'environment.json') -Encoding UTF8
        $e | ConvertTo-Json -Depth 6
    }
    'Attribute' {
        # Under -File a list arrives as one string, "WCsc,FMfn": split it here.
        foreach ($t in @($Tags | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) { '{0}  {1}' -f $t, (Get-TagOwner $t $PoolTagTxt) }
    }
    'PressureChild' {
        $r = [PoolWatchNative]::Pressure($FloorMB, $HoldSec)
        [ordered]@{ chunks = $r[0]; minAvailableMB = $r[1]; lowMemorySeen = [bool]$r[2]; floorMB = $FloorMB; holdSec = $HoldSec } | ConvertTo-Json -Compress
    }
    'Report' {
        $v = Get-Verdict (Read-Jsonl (Join-Path $RunDir 'marks.jsonl')) (Read-Jsonl (Join-Path $RunDir 'arms.jsonl'))
        $filters = Get-FiltersLine
        "OUTCOME $($v.outcome)"
        "VERDICT $(if ($v.verdict) { $v.verdict } else { 'none' })"
        foreach ($r in $v.reasons) { "REASON $r" }
        $filters
        if (-not $NoWrite) {
            $v | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunDir 'result.json') -Encoding UTF8
            @("OUTCOME $($v.outcome)", "VERDICT $($v.verdict)") + @($v.reasons | ForEach-Object { "REASON $_" }) + @($filters) | Set-Content -LiteralPath (Join-Path $RunDir 'result.txt') -Encoding UTF8
        }
    }
    'Prepare' {
        $RunDir = Join-Path $RunDir 'prepare' # its marks and records apart from the protocol's
        New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
        $dotnet = Join-Path $Repo 'eng\dotnet.ps1'
        $shell = (Get-Process -Id $PID).Path
        Write-Host 'Building ScanArm, the hook, and the fixed app ...'
        & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet publish (Join-Path $Repo 'tests\window\ScanArm\ScanArm.csproj') -c Release -o (Join-Path $Repo 'artifacts\scan-arm') | Out-Host
        & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet build (Join-Path $Repo 'tests\window\FsaWindowHook\FsaWindowHook.csproj') -o (Join-Path $Repo 'artifacts\window-hook') | Out-Host
        & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet publish (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Split-Path $FixedExe) | Out-Host
        if (-not $Smoke) {
            Write-Host "Building the unfixed app from $UnfixedCommit (read through git archive; nothing in the work tree changes) ..."
            $src = Join-Path $Artifacts 'unfixed-src'
            if (Test-Path $src) { Remove-Item -Recurse -Force $src }
            New-Item -ItemType Directory -Force -Path $src | Out-Null
            $tar = Join-Path $Artifacts 'unfixed.tar'
            & git -C $Repo archive --format=tar -o $tar $UnfixedCommit
            & tar.exe -xf $tar -C $src
            & $shell -NoProfile -ExecutionPolicy Bypass -File $dotnet publish (Join-Path $src 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Split-Path $UnfixedExe) | Out-Host
        }
        foreach ($exe in $ScanArmExe, $HookDll, $FixedExe) { if (-not (Test-Path $exe)) { throw "-Prepare: $exe was not built" } }

        $null = Write-Mark 'Rpre'
        $envRecord = Get-EnvironmentRecord

        # Small checks on this boot, already touched: the triggers fire, against both builds; a closed window writes no report.
        # A real folder big enough that the window's counters move before its scan ends (a tree of a few thousand
        # folders is listed inside one 250 ms tick), and far from the store: WinSxS, ~150,000 folders. Under -Smoke, -Root
        # instead: a smoke run is on a small tree, and a trigger that failed to fire there must not turn into a cold walk of
        # all of WinSxS on the boot the rig is being proved on.
        $probe = if ($Smoke -and $Root) { $Root } else { Join-Path $env:SystemRoot 'WinSxS' }
        # The size-pass trigger (X3, X5) only on the real run: a smoke tree's files are all sized inside one tick, so the
        # window never shows the pass, and only a tree the size of WinSxS proves it fires before the Restart.
        $triggers = @(@{ mode = 'stop'; at = @{ FSA_HOOK_AT_FOLDERS = '2000' } }, @{ mode = 'close'; at = @{ FSA_HOOK_AT_FOLDERS = '2000' } })
        if (-not $Smoke) { $triggers += @{ mode = 'stop'; at = @{ FSA_HOOK_AT_SIZED = '20000' } } }
        $checks = @()
        foreach ($exe in @($FixedExe) + @(if (Test-Path $UnfixedExe) { $UnfixedExe })) {
            foreach ($trigger in $triggers) {
                $mode = $trigger.mode
                $log = Join-Path $Artifacts "trigger-$mode.log"; $rep = Join-Path $Artifacts "trigger-$mode.json"
                Remove-Item $log, $rep -ErrorAction SilentlyContinue
                $vars = @{ DOTNET_STARTUP_HOOKS = $HookDll; FSA_HOOK_MODE = $mode; FSA_HOOK_LOG = $log } + $trigger.at
                $p = Start-Arm $exe @($probe, '--report', $rep) $vars $null
                $null = $p.WaitForExit(600000) # close mode exits 70 with no report, as WindowReport documents
                $checks += [ordered]@{ exe = $exe; mode = $mode; trigger = $trigger.at; exit = $p.ExitCode; report = (Test-Path $rep); acted = @(if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match '^(acted|refused)' } }) }
            }
        }
        $dead = @($checks | Where-Object { -not (@($_.acted) -match '^acted ') })
        if ($dead.Count) { Write-Warning "-Prepare: $($dead.Count) trigger check(s) did not act; the runs that use them would count as full runs, not cancelled ones. See triggerChecks in prepare.json before restarting." }

        $prepared = [ordered]@{ kind = 'prepare'; utc = (Get-Date).ToUniversalTime().ToString('o'); environment = $envRecord; triggerChecks = $checks }
        if (-not $Smoke) {
            # The store's size, an upper bound on what is left out, never added to a total: once, on this boot, with links not followed (Y4).
            $log = Join-Path $RunDir 'store-size.robocopy.log'
            Remove-Item $log -ErrorAction SilentlyContinue
            Invoke-NoLinkWalk @($StoreText) $log
            $prepared['storeBytesUpperBound'] = Read-RobocopyBytes $log
            # The closing control's subtrees: touched on this boot already, so picking them now spends nothing after the
            # Restart; none may hold a directory link (X3); those the repro found untouched late in a boot first (Y3).
            $layer = @(Get-ChildItem -LiteralPath $StoreText -Directory -Force)[0]
            $controls = @()
            foreach ($rel in 'Files\Windows\Globalization', 'Files\Windows\Help', 'Files\Windows\Media', 'Files\Windows\schemas') {
                $dir = Join-Path $layer.FullName $rel
                if (-not (Test-Path -LiteralPath $dir)) { continue }
                $items = @(Get-ChildItem -LiteralPath $dir -Recurse -Force -Attributes !ReparsePoint -ErrorAction SilentlyContinue)
                $links = @(Get-ChildItem -LiteralPath $dir -Recurse -Force -Directory -Attributes ReparsePoint -ErrorAction SilentlyContinue)
                if ($links.Count -eq 0 -and $items.Count -ge 10 -and $items.Count -le 200) { $controls += [ordered]@{ path = $dir; entries = $items.Count } }
                if ($controls.Count -ge 3) { break }
            }
            $prepared['controls'] = $controls
            # The no-link walker must not go through C:\Users\All Users (a link to C:\ProgramData).
            $allUsers = Join-Path $Artifacts 'allusers-check.log'; Remove-Item $allUsers -ErrorAction SilentlyContinue
            & robocopy.exe 'C:\Users' (Join-Path $RunDir 'never') /L /E /XJ /R:0 /W:0 /NP /NJH /NJS /NFL /LEV:2 /LOG:$allUsers | Out-Null
            $prepared['noLinkWalkerSkipsAllUsers'] = -not [bool](Select-String -LiteralPath $allUsers -SimpleMatch 'All Users\Microsoft' -Quiet)
            # The pressure step's positive control: a cold tree's retention must fall under it, or the floor and hold change (N3).
            $tree = Join-Path $Artifacts 'pressure-control'
            if (-not (Test-Path $tree)) {
                New-Item -ItemType Directory -Force -Path $tree | Out-Null
                for ($d = 0; $d -lt 100; $d++) { $sub = New-Item -ItemType Directory -Force -Path (Join-Path $tree ("{0:D3}" -f $d)); for ($f = 0; $f -lt 1000; $f++) { [IO.File]::WriteAllText((Join-Path $sub.FullName ("{0:D4}.txt" -f $f)), '') } }
            }
            $null = Write-Mark 'PC/before'
            Get-ChildItem -LiteralPath $tree -Recurse -File | ForEach-Object { $null = $_.Attributes }
            $null = Write-Mark 'PC/opened'
            $effective = $false
            foreach ($hold in 60, 180) {
                $script:PressureHold = $hold
                $null = Invoke-Pressure "PC/pressure-$hold"
                $after = @(Read-Jsonl (Join-Path $RunDir 'marks.jsonl'))[-1]
                $opened = @(Read-Jsonl (Join-Path $RunDir 'marks.jsonl') | Where-Object { $_.label -eq 'PC/opened' })[-1]
                if (@($CacheTags | Where-Object { $x = Live $opened $_; $y = Live $after $_; $null -ne $x -and $null -ne $y -and $y -lt 0.99 * $x }).Count) { $effective = $true; $prepared['pressureHoldSec'] = $hold; break }
            }
            $prepared['pressureEffective'] = $effective
        }
        $prepared | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunDir 'prepare.json') -Encoding UTF8
        Write-Host "Prepared: $RunDir\prepare.json. Now restart with Start > Restart (not Shut down), sign in, open nothing but one PowerShell window, and run:"
        Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File tests\window\PoolWatch.ps1 -Protocol"
    }
    'Protocol' {
        New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
        $prep = $null
        $prepFile = Join-Path $RunDir 'prepare\prepare.json'
        if (Test-Path $prepFile) { $prep = Get-Content $prepFile -Raw | ConvertFrom-Json }
        $scanRoot = 'C:\'
        if ($Smoke) {
            if (-not $Root) { throw '-Smoke needs -Root, a small folder' }
            $scanRoot = $Root; $script:MarkEvery = 2; $script:Settle = 2; $Tolerance.WindowSec = 6
        }
        else {
            $boot = Get-Boot
            if ($boot.UptimeSec -gt $Tolerance.BootAgeMaxSec) { throw "PoolWatch -Protocol: this boot is $($boot.UptimeSec) s old, over $($Tolerance.BootAgeMaxSec) s. Restart (Start > Restart) and run it within 30 min (review X4)." }
            if (-not $prep) { throw "PoolWatch -Protocol: run -Prepare before the Restart first ($prepFile)" }
        }
        if (Test-Path (Join-Path $RunDir 'marks.jsonl')) { $RunDirOld = $RunDir; $RunDir = Join-Path $RunDir ("protocol-{0:yyyyMMdd-HHmmss}" -f (Get-Date)); New-Item -ItemType Directory -Force -Path $RunDir | Out-Null; Write-Host "Writing to $RunDir" }
        Add-Type -Namespace PoolWatchPower -Name Native -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);' -ErrorAction SilentlyContinue
        [PoolWatchPower.Native]::SetThreadExecutionState([Convert]::ToUInt32('80000001', 16)) | Out-Null # ES_CONTINUOUS | ES_SYSTEM_REQUIRED: no sleep while it runs
        try {
            $script:R0 = Write-Mark 'R0'
            Wait-Idle 'I0' $Tolerance.WindowSec
            if ($Session -eq 2) {
                # Q-178 condition (2): the non-app walk of C:, per root, never following a link, from the same fresh state,
                # and never into the store (/XD), which the app's walk also leaves out.
                $roots = @(Get-ChildItem -LiteralPath $scanRoot -Directory -Force | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | ForEach-Object FullName)
                $log = Join-Path $RunDir 'D.robocopy.log'
                $walk = @($roots | ForEach-Object { "robocopy `"$_`" `"$RunDir\never`" /L /E /XJ /XD `"$StoreText`" /R:0 /W:0 /NP /BYTES /NFL /NDL /NJH /LOG+:`"$log`" >nul &" })
                $null = Invoke-Arm 'D' 'dir' 'cmd.exe' (@('/c') + $walk + @('ver')) @{} @{ roots = $roots }
                Wait-Idle 'I1' $Tolerance.WindowSec
            }
            else {
            $list = Join-Path $RunDir 'L.files.txt'
            $l = Invoke-Arm 'L' 'listing' $ScanArmExe @('listing', $scanRoot, '--list-out', $list, '--progress', (Join-Path $RunDir 'L.progress.tsv'))
            $n = 1; if ($l.summary) { $n = [long]$l.summary.listed }
            # S and P sized under the guard: at most 200,000 files each, from their own halves.
            $sample = [Math]::Min(200000, [Math]::Max(1, [long]($n / 2)))
            $null = Invoke-Arm 'S' 'probe' $ScanArmExe @('probe', "$list.A", '--max', "$sample")
            $null = Invoke-Arm 'P' 'plain' $ScanArmExe @('plain', "$list.B", '--max', "$sample")
            # wcifs moving during L or P beyond the idle envelope is something no exe run should be stacked on (review N1).
            $stopped = Test-ArmWcifs @('L', 'P')
            if ($stopped) {
                $null = Write-Mark 'abort' @{ reason = $stopped }
                Write-Warning "PoolWatch -Protocol stopped before F1: $stopped. The marks so far are in $RunDir; -Report says WCIFS-UNATTRIBUTED. Look at L.progress.tsv against the marks before deciding whether to run again after another Restart."
            }
            else {
            foreach ($i in 1..5) { $null = Invoke-ExeArm "F$i" 'exe-full' $FixedExe $scanRoot }
            $store = if ($Smoke) { (Get-ChildItem -LiteralPath $scanRoot -Directory | Select-Object -First 1).FullName } else { $StoreText }
            $users = if ($Smoke) { $store } else { 'C:\Users' }
            $smokeAt = if ($Smoke) { 1 } else { 0 }
            $null = Invoke-ExeArm 'X1' 'exe-cancel' $FixedExe $scanRoot @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_PAST = $store; FSA_HOOK_PAST_FOLDERS = [Math]::Max($smokeAt, 500 * (1 - $smokeAt)) }
            $null = Invoke-ExeArm 'X2' 'exe-cancel' $FixedExe $scanRoot @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_PAST = $users; FSA_HOOK_PAST_FOLDERS = [Math]::Max($smokeAt, 1000 * (1 - $smokeAt)) }
            $null = Invoke-ExeArm 'X3' 'exe-cancel' $FixedExe $scanRoot @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_SIZED = [Math]::Max($smokeAt, 200000 * (1 - $smokeAt)) }
            $null = Invoke-ExeArm 'X4' 'exe-close' $FixedExe $scanRoot @{ FSA_HOOK_MODE = 'close'; FSA_HOOK_PAST = $store; FSA_HOOK_PAST_FOLDERS = [Math]::Max($smokeAt, 5000 * (1 - $smokeAt)) }
            $null = Invoke-ExeArm 'X5' 'exe-close' $FixedExe $scanRoot @{ FSA_HOOK_MODE = 'close'; FSA_HOOK_AT_SIZED = [Math]::Max($smokeAt, 400000 * (1 - $smokeAt)) }
            if (-not $Smoke) { $null = Invoke-Pressure 'PR' }
            Wait-Idle 'I1' $Tolerance.WindowSec
            # The closing control: a non-app walk of layer subtrees picked before the Restart. It must leak, or nothing above counts.
            $controls = @(); if ($prep -and $prep.controls) { $controls = @($prep.controls) }
            if ($Smoke) { $controls = @([pscustomobject]@{ path = $store; entries = @(Get-ChildItem -LiteralPath $store -Recurse -Force).Count }) }
            $entries = ($controls | Measure-Object -Property entries -Sum).Sum
            $null = Invoke-Arm 'C' 'control' 'cmd.exe' (@('/c') + @($controls | ForEach-Object { "dir /a /s /b `"$($_.path)`" >nul &" }) + @('ver')) @{} @{ entries = $entries; paths = @($controls | ForEach-Object path) }
            # After the control, never before it (review Y1): the unfixed app, which walks the live layer, so the positive
            # control that the APP leaks, then the completed-versus-cancelled split on the app as it was reported.
            if (Test-Path $UnfixedExe) {
                $null = Invoke-ExeArm 'U1' 'unfixed-full' $UnfixedExe $scanRoot
                $null = Invoke-ExeArm 'U2' 'unfixed-full' $UnfixedExe $scanRoot
                $null = Invoke-ExeArm 'U3' 'unfixed-cancel' $UnfixedExe $scanRoot @{ FSA_HOOK_MODE = 'stop'; FSA_HOOK_PAST = $store; FSA_HOOK_PAST_FOLDERS = [Math]::Max($smokeAt, 500 * (1 - $smokeAt)) }
            }
            }
            }
        }
        finally {
            [PoolWatchPower.Native]::SetThreadExecutionState([Convert]::ToUInt32('80000000', 16)) | Out-Null
        }
        & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Report -RunDir $RunDir
        Write-Host "Done. Commit $RunDir with the slice."
    }
}
