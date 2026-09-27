# SL-062's correction of SL-038's O-030, case by case. SL-038's build record listed O-030 (StalledShareTests on
# the extracted rig) as done although its own evidence says the test never ran and remains owed to the person.
# SL-062 changes the ledger only: that one value in .taad/slices/SL-038/build-slice-2.json, and an O-030 entry
# in the person-run network check of docs/design/SL-020-scanner.md. Every case here only reads files and git
# objects. None runs the SMB-LOOPBACK test it talks about, and none changes anything on the machine.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-062-LedgerCases.ps1 -Case C-003
#
# C-001 is tests/bugs/O-030-ledger-discharge.Tests.ps1, the slice's acceptance test, which this script leaves alone.
#
# C-002  O-030's how-text is byte-identical to the one at $Base, SL-038's dated attestation, and still says
#        'NEVER run' and 'remains owed to the person'.
# C-003  build-slice-2.json differs from $Base's in exactly one line, O-030's "done": true to "done": false.
#        `git diff --numstat $Base` reads 1 1. There is no BOM and only one line-ending style.
# C-004  Every copy of O-030's own state under .taad/slices/SL-038/ says it is not discharged, and at least
#        build-slice-2.json's and code-review-2.json's copies are found. The person-run network check in
#        SL-020-scanner.md carries O-030 with its command, as owed to the person, and says that the agent run
#        does not discharge it. O-030 in other slices is a different obligation, because keys are numbered per
#        slice: those files are listed and not judged. The engine's own obligation store is not in this tree,
#        and nothing here can see it.
# C-005  This slice changed nothing but the ledger. Every path it touched is on $Allowed, so StalledShareTests.cs,
#        Rig/SmbRig.cs and all other code are untouched, and the acceptance test still holds the content the
#        repro stage wrote.
#
# -Case All runs every case in about a second. Exit 0 when every check held, 1 when any failed.
#
# What this slice touched is read from the engine's own commits (subject starting 'SL-062 ' or 'SL-062:'), plus
# the uncommitted tree while slice/SL-062 is checked out. So C-005 still means "this slice" after the merge,
# and it is not simply everything since $Base. C-002 and C-003 compare against $Base. A later slice that changes
# build-slice-2.json or the acceptance test is going against SL-020-scanner.md's O-030 entry, and it retires
# these cases in the same commit.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-002', 'C-003', 'C-004', 'C-005')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = [System.Collections.Generic.List[string]]::new()

# HEAD when SL-062 was opened, the merge of SL-059: the ledger as SL-038 left it.
$Base = '6c57f0ca438315b7761315d7d134b64ad5686f9b'
$Ledger = '.taad/slices/SL-038/build-slice-2.json'
$Record = 'docs/design/SL-020-scanner.md'
$Repro = 'tests/bugs/O-030-ledger-discharge.Tests.ps1'
$Command = 'eng/dotnet.ps1 test tests/FsAnalyser.Core.Tests/FsAnalyser.Core.Tests.csproj --filter Network=SMB-LOOPBACK'
# SHA-256 of the acceptance test as the repro stage wrote it, with line endings read as LF.
$ReproSha256 = 'B0DD9D40E38F707F5B196BE739EF69F55C1F147DABC4A1161F59A8180413FBC4'
$Allowed = @(
    $Ledger,
    $Record,
    $Repro,
    'tests/bugs/SL-062-LedgerCases.ps1'
)
# The engine's own records of this slice.
$AllowedUnder = '.taad/slices/SL-062/'

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

# Not named Git: PowerShell names are case-insensitive, so a function Git would call itself for git.
function Invoke-Git {
    $output = & git -C $Repo @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') exited $LASTEXITCODE"
    }
    $output
}

function Read-Tree([string] $path) {
    [System.IO.File]::ReadAllText((Join-Path $Repo $path))
}

function Get-Obligation($ledgerText) {
    @(($ledgerText | ConvertFrom-Json).payload.obligations | Where-Object { $_.key -eq 'O-030' })
}

# ---- C-002: O-030's how-text survives the edit byte-identical ----

function Invoke-C002 {
    Write-Output 'C-002  O-030''s how-text is SL-038''s, unchanged'
    $before = Get-Obligation ((Invoke-Git show "${Base}:$Ledger") -join "`n")
    $after = Get-Obligation (Read-Tree $Ledger)
    CheckEqual 1 $before.Count "exactly one O-030 in $Ledger at $Base"
    CheckEqual 1 $after.Count "exactly one O-030 in $Ledger now"
    if ($before.Count -ne 1 -or $after.Count -ne 1) { return }
    CheckEqual $before[0].how $after[0].how "O-030's how-text is the one at $Base, byte for byte"
    Check ($after[0].how -cmatch 'NEVER run') "O-030's how-text still says 'NEVER run'"
    Check ($after[0].how -cmatch 'remains owed to the person') "O-030's how-text still says 'remains owed to the person'"
    CheckEqual 'key,done,how' (($after[0].PSObject.Properties.Name) -join ',') "O-030 keeps its shape {key, done, how}"
}

# ---- C-003: the rest of build-slice-2.json is untouched ----

function Invoke-C003 {
    Write-Output "C-003  $Ledger differs from $($Base.Substring(0, 7)) in one line only"
    CheckEqual "1`t1`t$Ledger" ((Invoke-Git diff --numstat $Base -- $Ledger) -join "`n") "git diff --numstat $($Base.Substring(0, 7)) reads 1 1"

    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $Repo $Ledger))
    Check (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) "$Ledger has no BOM"
    $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    $crlf = [regex]::Matches($text, "`r`n").Count
    $lf = [regex]::Matches($text, "(?<!`r)`n").Count
    Check ($crlf -eq 0 -or $lf -eq 0) "$Ledger uses one line-ending style (CRLF $crlf, bare LF $lf)"

    $old = @(Invoke-Git show "${Base}:$Ledger")
    $new = @($text -split "`r?`n")
    if ($new.Count -gt 0 -and $new[-1] -eq '') { $new = $new[0..($new.Count - 2)] }
    CheckEqual $old.Count $new.Count "$Ledger has as many lines as at $($Base.Substring(0, 7))"
    $changed = @(0..([Math]::Min($old.Count, $new.Count) - 1) | Where-Object { $old[$_] -cne $new[$_] })
    CheckEqual 1 $changed.Count "exactly one line differs"
    if ($changed.Count -ne 1) { return }
    $at = $changed[0]
    CheckEqual '    "key": "O-030",' $old[$at - 1] "the line above it is O-030's key"
    CheckEqual '    "done": true,' $old[$at] "it read O-030's done: true"
    CheckEqual '    "done": false,' $new[$at] "it now reads done: false"
}

# ---- C-004: no other copy of O-030's state says discharged, and the person's record carries it ----

function Find-O030([object] $node, [string] $file, [System.Collections.Generic.List[object]] $found) {
    if ($node -is [System.Collections.IEnumerable] -and $node -isnot [string]) {
        foreach ($item in $node) { Find-O030 $item $file $found }
    }
    elseif ($node -is [System.Management.Automation.PSCustomObject]) {
        $names = $node.PSObject.Properties.Name
        if ($names -contains 'key' -and $node.key -ceq 'O-030' -and ($names -contains 'done' -or $names -contains 'discharged')) {
            $found.Add([pscustomobject]@{ File = $file; Node = $node })
        }
        foreach ($property in $node.PSObject.Properties) { Find-O030 $property.Value $file $found }
    }
}

function Invoke-C004 {
    Write-Output 'C-004  O-030 is owed everywhere it has a state, and it is on the person''s record'
    $found = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Repo '.taad/slices/SL-038') -Filter '*.json' -File) {
        $json = [System.IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json -Depth 200
        Find-O030 $json ".taad/slices/SL-038/$($file.Name)" $found
    }
    $files = @($found | ForEach-Object File | Sort-Object -Unique)
    Check ($files -contains $Ledger) "O-030's state is found in $Ledger"
    Check ($files -contains '.taad/slices/SL-038/code-review-2.json') 'O-030''s state is found in .taad/slices/SL-038/code-review-2.json'
    foreach ($copy in $found) {
        $node = $copy.Node
        if ($node.PSObject.Properties.Name -contains 'done') {
            Check ($node.done -is [bool] -and -not $node.done) "$($copy.File): O-030 done is false (it is $(Show $node.done))"
        }
        if ($node.PSObject.Properties.Name -contains 'discharged') {
            Check (-not ($node.discharged -eq $true -or [string]$node.discharged -eq 'yes')) "$($copy.File): O-030 discharged is not yes (it is $(Show $node.discharged))"
        }
    }

    $elsewhere = @(& git -C $Repo grep -l '"O-030"' -- .taad ':(exclude).taad/slices/SL-038' ':(exclude).taad/slices/SL-062')
    Write-Output "        other slices' own O-030s, not judged: $(if ($elsewhere.Count) { $elsewhere -join ', ' } else { 'none' })"

    # PowerShell names are case-insensitive, so no local here may be called $record.
    $recordText = Read-Tree $Record
    $start = $recordText.IndexOf('*The person-run network check.*', [StringComparison]::Ordinal)
    $end = $recordText.IndexOf('*When that pass goes stale.*', [StringComparison]::Ordinal)
    Check ($start -ge 0 -and $end -gt $start) "$Record still has the person-run network check"
    if ($start -lt 0 -or $end -le $start) { return }
    $section = $recordText.Substring($start, $end - $start) -replace '\s+', ' '
    Check ($section.Contains('*O-030, owed to the person.*')) 'the person-run network check has an O-030 entry, owed to the person'
    Check ($section.Contains("``$Command``")) 'the entry names the command whole'
    Check ($section.Contains('about 70 s')) 'the entry says it takes about 70 s'
    Check ($section.Contains('next to N-001 to N-004')) 'the entry puts its result next to N-001 to N-004'
    Check ($section.Contains('this record is where O-030 is discharged')) 'the entry says this record is where O-030 is discharged'
    Check ($section.Contains('does not discharge O-030')) 'the entry says the agent run does not discharge O-030'
}

# ---- C-005: nothing but the ledger changed ----

function Get-SliceChanges {
    $paths = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in @(Invoke-Git log --no-merges --format= --name-only --grep '^SL-062[ :]' HEAD)) {
        if ($path) { [void]$paths.Add($path) }
    }
    if ((Invoke-Git rev-parse --abbrev-ref HEAD) -ceq 'slice/SL-062') {
        foreach ($path in @(Invoke-Git diff --name-only HEAD) + @(Invoke-Git ls-files --others --exclude-standard)) {
            if ($path) { [void]$paths.Add($path) }
        }
    }
    , $paths
}

function Invoke-C005 {
    Write-Output 'C-005  this slice changed the ledger and nothing else'
    $changes = Get-SliceChanges
    Write-Output "        touched: $(if ($changes.Count) { @($changes) -join ', ' } else { 'nothing' })"
    Check ($changes.Contains($Ledger)) "the slice changed $Ledger"
    $outside = @($changes | Where-Object { $_ -notin $Allowed -and -not $_.StartsWith($AllowedUnder, [StringComparison]::Ordinal) })
    Check ($outside.Count -eq 0) "every path the slice touched is on the allowed list (outside it: $(if ($outside.Count) { $outside -join ', ' } else { 'none' }))"
    foreach ($path in 'tests/FsAnalyser.Core.Tests/StalledShareTests.cs', 'tests/FsAnalyser.Core.Tests/Rig/SmbRig.cs') {
        Check (-not $changes.Contains($path)) "$path is untouched"
    }

    $reproPath = Join-Path $Repo $Repro
    Check (Test-Path -LiteralPath $reproPath) "$Repro is still there"
    if (-not (Test-Path -LiteralPath $reproPath)) { return }
    $text = [System.IO.File]::ReadAllText($reproPath).Replace("`r`n", "`n")
    $sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.UTF8Encoding]::new($false).GetBytes($text)))
    CheckEqual $ReproSha256 $sha "$Repro holds the content the repro stage wrote"
}

$cases = if ($Case -eq 'All') { 'C-002', 'C-003', 'C-004', 'C-005' } else { @($Case) }
foreach ($name in $cases) {
    & "Invoke-$($name.Replace('-', ''))"
}

if ($script:Failures.Count -gt 0) {
    Write-Output "$($script:Failures.Count) check(s) failed"
    exit 1
}
Write-Output 'every check held'
exit 0
