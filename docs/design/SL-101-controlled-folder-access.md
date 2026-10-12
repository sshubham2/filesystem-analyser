# SL-101: Controlled Folder Access beside the last-access setting, and what a person must run to learn whether it blocks the scan

Status: criterion 1 built; criteria 2 and 3 filed for a person, with the recipe below. Q-101 stays open until a person's results
are in the table at the end. Nothing here answers it.

The question (m7/O-024, raised by SL-056): the scan lists a folder through a handle opened with FILE_LIST_DIRECTORY |
FILE_WRITE_ATTRIBUTES so that it can set the folder's last-access time back (`SynchronousIoCancel.TryOpenKeepingLastAccess`).
On a folder Controlled Folder Access (CFA) protects (Documents, Desktop, Pictures, or a folder added to its list), does that
open get blocked, or raise a CFA notification? Nobody had read `Get-MpPreference` or scanned with CFA on.

## What changes in the product

Nothing. If CFA refuses the keep-open, the scanner already falls back to a plain open and lists the folder in full: risk spike
#109 refused the write-attributes open with an ACL deny, on the scan root and on a subfolder, and the scan listed all 55
entries with no unreadable row, `LastAccessAsked - LastAccessKept` rising by one per refused folder. The two counters stay
internal (Q-159). No `KnownWorkarounds` entry is added: no defect outside the project is established, and CFA refusing an
untrusted app's write may be Defender working as intended. If the person's run shows a block or a notification caused by the
scan, a later slice decides the behaviour (see the end).

## What is built (criterion 1)

One line in the output of every `HeldOpenTreeTests` Fact, on the line after fsutil's `disablelastaccess` output, which is
where the scan's last-access results are recorded (SL-056's design asked for it: `SL-056-probe-every-file.md:107-108`).

```
Controlled Folder Access: EnableControlledFolderAccess=<raw> (<Disabled|Enabled|AuditMode|BlockDiskModificationOnly|AuditDiskModificationOnly|unknown>), protected folders=<n|hidden from non-administrators|?>, allowed apps=<n|hidden from non-administrators|?>; Defender AMRunningMode=<s|?>, RealTimeProtection=<True|False|?>, platform=<version|?>
Controlled Folder Access: not read (<reason>)
```

On this PC at the build, from an ordinary token, under `eng/dotnet.ps1` (pwsh 7):

```
Controlled Folder Access: EnableControlledFolderAccess=0 (Disabled), protected folders=0, allowed apps=hidden from non-administrators; Defender AMRunningMode=Normal, RealTimeProtection=True, platform=4.18.26080.4
```

- **Where:** `tests/FsAnalyser.Core.Tests/DefenderState.cs` (the read and `Describe`), written by `HeldOpenTreeTests.Record()`. The
  read is Windows PowerShell 5.1's `Get-MpPreference` and `Get-MpComputerStatus`, which print raw values as one JSON object;
  the lists are counted in C#, so PowerShell's `@($null).Count` of 1 cannot leak in. It runs once per test process.
- **It records and never judges:** no assertion, no skip, no exception. A child that exits non-zero (Defender not running, a
  third-party antivirus, the Sandbox's 0x800106ba), prints nothing or something that is not one JSON object, runs past 20 s
  (its process tree is killed) or cannot be started, becomes `not read (<reason>)` and the test goes on as before.
- **The raw value is always printed beside its name.** The names of 0 to 4 are recalled from Microsoft's documentation, not
  measured; 0 and 1 are the only values this PC has shown (0).
- **The child gets no PSModulePath.** testhost is started by `eng/dotnet.ps1` under pwsh 7 and inherits its module path, which
  Windows PowerShell 5.1 would otherwise take over. Measured: with the variable left in, the child reports pwsh 7's entries;
  with it removed, its own three (`DefenderStateRecordTests.TheChildPowerShellStartsWithItsOwnModulePath`).
- **An ordinary user is not given the lists.** Review M1: for a caller who is not an administrator `Get-MpPreference`
  replaces a list with one entry that begins `N/A:`. Measured on this PC, unelevated, `ControlledFolderAccessAllowedApplications` is
  `["N/A: Must be an administrator to view exclusions"]`. So the survey's "the allowed-apps list has 1 entry" was never a
  count of apps, and the line prints `hidden from non-administrators`, not `1`. The class is closed in
  `tests/FsAnalyser.Core.Tests/DefenderStateCorpus.json` (17 rows, 9 placeholder shapes and 8 real lists) and its census
  (11 properties of `Get-MpPreference` carry the placeholder on this PC; the record reads one of them and
  `ControlledFolderAccessProtectedFolders`), run by `DefenderStateRecordTests.EachShapeOfAListIsCountedOrCalledHiddenAsTheCorpusSays`.
  Before the fix, with the design's plain counting, the 9 placeholder rows were red (they counted 1 or 2) and the 8 real rows
  green. The grammar is the `N/A:` prefix, compared without case after leading white space, never the text after it, and a list
  that holds a placeholder beside anything else reads as hidden too.
- **What an ordinary user cannot see is read in step 1 below, by the person, from the elevated window.**

Not built, on purpose (design): an xUnit Fact that runs a scan with CFA on. `testhost.exe` and `dotnet.exe` are Microsoft-signed
and CFA may trust them by reputation, so it could measure the wrong image; arming CFA takes an administrator; and an opt-in
Fact would cost a Trait, a `FsaDefaultTestFilter` clause and an `OptInTraitTests` pin. A settled architecture rule
(`.taad/fitness.yaml`, "no test or test script changes Microsoft Defender's settings", over `tests/**`) keeps any test from
arming it. This file is outside that glob on purpose: the commands below are the person's.

## Criteria 2 and 3: the person's run

Needs an administrator, for arming CFA only, and a person at the desktop for the notification. Windows Sandbox cannot do it:
Defender is disabled there (measured: WinDefend and WdFilter `START_TYPE 4`, `sc start` fails with 1058, changing the start
type is refused with error 5 even as SYSTEM, `Set-MpPreference` fails with 0x800106ba).

**Use two windows, and keep them apart (review M2).** `W-admin` is Windows PowerShell started with Run as administrator: it
arms and disarms CFA and does nothing else. `W-user` is an ordinary PowerShell window: it builds, publishes, scans, runs the
canary and reads the events. The app is a program that runs as an ordinary user; a scan or a canary run from `W-admin` is not
what a person's PC does, and its row is void. `W-user` checks its own integrity level before every run (below) and refuses to
go on at High.

Every `W-user` command starts from the repository root, with `$base` set in step 2.

### 1. `W-admin`: write down the state, with both lists by name

```powershell
Get-MpPreference | Select-Object EnableControlledFolderAccess, ControlledFolderAccessProtectedFolders, ControlledFolderAccessAllowedApplications, ControlledFolderAccessDefaultProtectedFolders | Format-List
```

Write down `EnableControlledFolderAccess` (call it `$was`; this PC read 0 at the build) and the two lists. This is the only
place the allowed-apps list can be read by name, and the line the tests record cannot show it.

### 2. `W-user`: publish, build the fixtures, back-date them, build the canary

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File eng\dotnet.ps1 publish src\FsAnalyser.App\FsAnalyser.App.csproj -c Release -o artifacts\publish

$base = Join-Path $env:LOCALAPPDATA ('Temp\fsa-cfa-' + [guid]::NewGuid().ToString('N').Substring(0, 8))   # never %TEMP%: it is C:\WINDOWS\TEMP in an engine shell
$base                                                                                                      # write this path down; W-admin needs it
foreach ($n in 'R-off', 'R-audit', 'R-on', 'control') {
    New-Item -ItemType Directory -Force -Path "$base\$n\sub" | Out-Null
    1..10 | ForEach-Object { Set-Content -LiteralPath "$base\$n\file$_.txt" -Value ('x' * (100 * $_)) }
    1..5  | ForEach-Object { Set-Content -LiteralPath "$base\$n\sub\inner$_.txt" -Value ('y' * (50 * $_)) }
}
$old = (Get-Date).AddHours(-48)
foreach ($n in 'R-off', 'R-audit', 'R-on', 'control') {
    [IO.Directory]::SetLastAccessTime("$base\$n\sub", $old)
    [IO.Directory]::SetLastAccessTime("$base\$n", $old)
}

New-Item -ItemType Directory -Force -Path "$base\canary" | Out-Null
@'
using System;
using System.IO;
class FsaCfaCanary
{
    static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(Path.Combine(args[0], "canary-" + Guid.NewGuid().ToString("N") + ".txt"), "x");
            Console.WriteLine("written");
            return 0;
        }
        catch (UnauthorizedAccessException e) { Console.WriteLine("refused: " + e.Message); return 5; }
        catch (Exception e) { Console.WriteLine("failed: " + e.GetType().Name + ": " + e.Message); return 1; }
    }
}
'@ | Set-Content -LiteralPath "$base\canary\canary.cs" -Encoding UTF8
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:"$base\canary\fsa-cfa-canary.exe" "$base\canary\canary.cs"
```

Three fixtures, one per run, each 10 files and a `sub` folder of 5, so every armed run starts from a stamp that no one has had
to rewrite inside a protected folder: back-dating is a write-attributes open, and a protected folder might refuse it. `control`
is outside any protected folder. The canary is an unsigned exe: it writes one file into the folder it is given and exits 0
(written), 5 (access denied) or 1 (anything else).

### 3. `W-user`: paste these helpers once

```powershell
$il = whoami /groups | findstr /c:"Mandatory Label"
$il
if ($il -notmatch 'Medium Mandatory Level') { throw 'This window is not at Medium integrity: a row taken here is void.' }

function Witness($name) {
    foreach ($p in "$base\$name", "$base\$name\sub") {
        $t = [IO.Directory]::GetLastAccessTime($p)
        '{0:u}  {1,-9}  {2}' -f $t.ToUniversalTime(), $(if (((Get-Date) - $t).TotalHours -gt 24) { 'unchanged' } else { 'MOVED' }), $p
    }
}
function Scan($name) {
    $out = pwsh -NoProfile -ExecutionPolicy Bypass -File eng\read-window.ps1 -Root "$base\$name" -Outcome
    $out | Where-Object { $_ -match '^(EXIT|TIMEOUT|NO REPORT|OUTCOME)' }
    $json = ($out | Where-Object { $_ -notmatch '^(EXIT|TIMEOUT|NO REPORT|OUTCOME)' }) -join "`n"
    if ($json) { ($json | ConvertFrom-Json).core | Format-List status, files, folders, bytes, unreadable }
}
function Control {
    [IO.Directory]::SetLastAccessTime("$base\control", (Get-Date).AddHours(-48))
    Get-ChildItem -LiteralPath "$base\control" | Out-Null
    $t = [IO.Directory]::GetLastAccessTime("$base\control")
    'control: ' + $(if (((Get-Date) - $t).TotalHours -gt 24) { 'did NOT move, so this witness cannot see a listing: the session is void' } else { 'moved, as a plain listing must' })
}
function Canary($name) {
    & "$base\canary\fsa-cfa-canary.exe" "$base\$name"
    "canary exit $LASTEXITCODE"
}
function Events($since) {
    Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1123, 1124; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'FsAnalyser\.App\.exe|fsa-cfa-canary\.exe|fsa-cfa-' } |
        ForEach-Object { '{0:u}  {1}  {2}' -f $_.TimeCreated, $_.Id, (($_.Message -split "`r?`n" | Where-Object { $_ -match '^\s*(Path|Process Name)\s*:' } | ForEach-Object { $_.Trim() }) -join '  ') }
}
```

The witness is the one the repo already uses (`HeldOpenTreeTests`): NTFS records an access only when the stored time is over
an hour old, so a folder back-dated 48 hours reads `unchanged` after a scan that kept its last-access time and `MOVED`
after one that did not, and a plain `Get-ChildItem` of `control` must move it. It needs `fsutil behavior query disablelastaccess`
to say last-access updates are enabled (this PC: `DisableLastAccess = 2 (System Managed, Last Access Time Updates ENABLED)`).

### 4. `W-user`, run A, the baseline: `R-off`, which CFA does not protect

```powershell
$t0 = Get-Date; Witness 'R-off'; Scan 'R-off'; Witness 'R-off'; Control; Canary 'R-off'; Events $t0
```

The first `Witness` must read `unchanged` twice (the fixture is back-dated), the second must read `unchanged` twice too (the
scan asked for and kept the last-access time), `Control` must say it moved, `Canary` must write. Anything else voids the
whole session: the witness cannot see what it is meant to see. Write down the scan's `files`, `folders`, `bytes`: the later
runs must match them (15, 1 and 6280 at the build, with the same fixture).

### 5. `W-admin`: arm AuditMode, protect the two armed fixtures

```powershell
$base = '<the path step 2 printed>'
Set-MpPreference -EnableControlledFolderAccess AuditMode
Add-MpPreference -ControlledFolderAccessProtectedFolders "$base\R-audit", "$base\R-on"
Get-MpPreference | Select-Object EnableControlledFolderAccess, ControlledFolderAccessProtectedFolders | Format-List
```

Audit before enabled is Microsoft's recommendation, and AuditMode lets the first run go through and be logged (1124). If
Tamper Protection or policy refuses the cmdlet, arm Enabled from Windows Security, Virus & threat protection, Manage
ransomware protection, Controlled folder access, Protected folders, and record run B as skipped: AuditMode has no switch
in that window (recalled).

### 6. `W-user`, run B, AuditMode: `R-audit`

```powershell
$t0 = Get-Date; Witness 'R-audit'; Scan 'R-audit'; Witness 'R-audit'; Control; Canary 'R-audit'; Events $t0
```

Watch for a Windows Security notification while the scan runs, and write down whether one came and what it said.

### 7. `W-admin`: enable

```powershell
Set-MpPreference -EnableControlledFolderAccess Enabled
```

### 8. `W-user`, run C, Enabled: `R-on`

```powershell
$t0 = Get-Date; Witness 'R-on'; Scan 'R-on'; Witness 'R-on'; Control; Canary 'R-on'; Events $t0
```

Watch for a notification again. A toast on the canary alone is the control, not the answer.

### 9. `W-admin`: disarm to what step 1 recorded

```powershell
Remove-MpPreference -ControlledFolderAccessProtectedFolders "$base\R-audit", "$base\R-on"
Set-MpPreference -EnableControlledFolderAccess <the value from step 1; 0 was the value at the build>
Get-MpPreference | Select-Object EnableControlledFolderAccess, ControlledFolderAccessProtectedFolders, ControlledFolderAccessAllowedApplications | Format-List
```

### 10. `W-user`: delete the fixtures, only after step 9 shows both folders off the list

```powershell
[IO.Directory]::Delete($base, $true)
```

Optional: a run on the real Documents folder, only if `[Environment]::GetFolderPath('MyDocuments')` is not under `$env:OneDrive`
(the scanner never asks to keep last access under a OneDrive root, so a run there would read "not blocked" for the wrong
reason), and only with the person's own say-so, since it scans their files.

### Results table

One row per run. A row taken at High integrity, or whose `Witness` before the scan was not `unchanged`, is void.

| Run | CFA state (`$was`, mode seen) | Integrity (must be Medium) | Folder protected? | EXIT / outcome | files / folders / bytes / unreadable | `Witness` after: root, `sub` | `Control` | Canary exit and text | Events 1123 / 1124 naming FsAnalyser.App.exe, canary, folder | Notification seen |
|---|---|---|---|---|---|---|---|---|---|---|
| A, baseline `R-off` | | | no | | | | | | | |
| B, Audit `R-audit` | | | yes | | | | | | | |
| C, Enabled `R-on` | | | yes | | | | | | | |

### Reading key (tied to spike #109)

The canary says whether the folder is armed at all; the witness says whether the scan's keep-last-access open went through;
the events say who CFA acted on.

1. **Void:** the baseline's `Witness` did not read `unchanged`, or `Control` did not move, or a row is at High, or run B or C
   shows no canary event naming its folder (the folder was not armed, or CFA was not in the mode written down). Nothing is
   concluded about CFA from a void row.
2. **Not blocked:** in B or C the witness reads `unchanged` and there is no event naming FsAnalyser.App.exe. The scan asked
   to keep last access on that folder (the baseline proves it asks there) and CFA let it.
3. **Audited, would block (run B):** the witness reads `unchanged` and there is a 1124 naming FsAnalyser.App.exe and the
   folder. CFA logged the open and let it through.
4. **Blocked, fell back (run C):** a 1123 naming FsAnalyser.App.exe and the folder, the witness reads `MOVED`, and `files`,
   `folders`, `bytes` match the baseline with `unreadable` 0. That is the outcome spike #109 mapped from an ACL deny: a full listing
   from a plain open, counted as asked but not kept.
5. **Contradicts spike #109, needs a defect slice:** `files`, `folders` or `bytes` short of the baseline, or `unreadable` above
   0, in B or C. A refused keep-open must not cost the listing.
6. **Moved without an event:** the witness reads `MOVED` in B or C with no event. Something refused the open that CFA did not
   log, or the scan did not ask; record it, do not attribute it.
7. **The notification** is recorded on its own. A toast with no event, or an event with no toast, is a result too.

The event ids (1123 blocked, 1124 audited) and the lines `Path:` and `Process Name:` in their messages are recalled, not
measured: this PC has none to read. If `Events` prints nothing in a run where the canary was refused, print the whole
message of the newest 1123/1124 and read it by eye.

## What was rehearsed at the build, and what was not

Rehearsed on this PC with CFA off, from an ordinary token at Medium integrity: step 2 (publish, fixtures, back-dating,
csc build of the canary), step 3's helpers, and runs A, B and C against their three fixtures. Each scan completed with
15 files, 1 folder, 6280 bytes, 0 unreadable; each `Witness` read `unchanged` before and after; `Control` moved; the canary
wrote and exited 0; `Events` printed nothing. That is the CFA-off control the key relies on. It shows the commands run and
the witness can see; it shows nothing about CFA.

Not rehearsed: every `W-admin` step (no administrator here), the canary being refused, the shape of the 1123/1124 messages,
and any notification.

## Open and filed

- Criteria 2 and 3, and Q-101: a person's run of the recipe above.
- If a result is 4 (blocked) or shows a notification caused by the scan: decide the behaviour, whether skipping the
  keep-last-access open in CFA-protected folders is a `KnownWorkarounds` entry (one checkbox, on by default, with the in-app
  notice) and whether Q-159 reopens.
- The branch where the write-attributes open succeeds but `SetFileInformationByHandle(FileBasicInfo)` fails (CFA might take
  it; an ACL cannot make it, and Core has no seam to inject it). Spike #109 did not exercise it; the fallback there rests on
  reading `SynchronousIoCancel.cs:529-543`.
- Windows 10, and a Defender platform later than 4.18.26080.4 (Q-205, Q-224).
- Review findings carried to the backlog, not done here: a fixture inside the default protected set
  (`C:\Users\Public\Documents`), a case-insensitive Defender-settings rule that also covers `eng/**`, the helper's tests holding
  the timeout kill to a child that is really gone, and listing the allowed apps before and after the session.
