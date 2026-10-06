# Proves the hooked window cases can fail (SL-033). Each mutant is this repo with one change, made in a
# scratch copy under %TEMP% and never committed. The committed WindowCases.ps1, run from that copy, must
# fail its case for the reason the mutant plants and for no other. A case that cannot fail against its
# mutant proves nothing when it passes against the real app.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowMutants.ps1 -Mutant StopDisabled
#
# -Mutant All runs every one. Exit 0 when every case failed as expected, 1 otherwise. Opt-in, and not part
# of WindowCases.ps1 -Case All: each app mutant publishes an exe of its own (140 MB, in the scratch copy).
#
#   StopDisabled      CancelButton disabled during the scan. C-006's hook must refuse, so C-006 fails.
#   StopCollapsed     CancelButton collapsed during the scan. The same.
#   NoCrashHandler    The DispatcherUnhandledException handler removed. C-012 fails on 0xE0434352.
#   NoExitRule        The Exit handler's rule (exit 0 without a report becomes 70) removed. O-016 fails on 0.
#   CancelThrows      OnCancel throws. The hook must hand that to the app, as a real click does, so C-006
#                     fails on the app's exit 70 and missing report, and not on the hook's own checks.
#   HookFailsAfterActing  The hook throws once Stop is pressed. C-006 must fail on the hook, even though
#                     the app still ends Stopped.
#   HookCompileError  A compile error in the hook, with the dll of an earlier build in place. The run stops
#                     with exit 1 and the compiler's error, and no case runs on the old dll.
#   HookLocked        The hook's source changed while its dll is held open, as an app left behind by a
#                     timed-out run holds it. The same.
#   EndingObeysSkip   ShowFinalTopLevel skips the final snapshot while a refresh skip is armed, as RefreshTopLevel
#                     does mid-scan (SL-064). L-002 ends with the skip armed, so it fails on topLevel.final: the
#                     list still shows a snapshot from before the ending.
#   ListingFailsAnyFolder  The listing failpoint's name check removed (SL-082, review M1): it fails every folder's
#                     second page. T-007 fails on Q, listed before P and read partway, and on the failpoint firing
#                     more than once; the rows' order may fail besides, since Q and P then keep a page each.
#   NoFailListingMember  Core's FailListingOf renamed, so the hook's reflection finds nothing to arm (SL-082). The
#                     hook refuses and the app ends before its Main, so T-007 fails on its first check.
#   NoExplorerViewMember  Core's OverrideExplorerView renamed, so the hook cannot inject Explorer's view (SL-091). The
#                     hook refuses and the app ends before its Main, so no reveal is clicked under the real view: R-003
#                     fails on its armed check in each injected step, (a) and (c), and on nothing else.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'StopDisabled', 'StopCollapsed', 'NoCrashHandler', 'NoExitRule', 'CancelThrows', 'HookFailsAfterActing', 'HookCompileError', 'HookLocked', 'EndingObeysSkip',
        'ListingFailsAnyFolder', 'NoFailListingMember', 'NoExplorerViewMember')]
    [string] $Mutant
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$MainWindow = 'src\FsAnalyser.App\MainWindow.xaml.cs'
$App = 'src\FsAnalyser.App\App.xaml.cs'
$HookSource = 'tests\window\FsaWindowHook\StartupHook.cs'
$IoCancel = 'src\FsAnalyser.Core\Scanning\SynchronousIoCancel.cs'
$ShellReveal = 'src\FsAnalyser.Core\Native\ShellReveal.cs'
$HookProject = 'tests\window\FsaWindowHook\FsaWindowHook.csproj'
$HookOut = 'artifacts\window-hook'
$script:Failures = [System.Collections.Generic.List[string]]::new()

# Each change is a regex that must match exactly once, so a mutant the source has moved away from fails
# here instead of quietly running the real app. Fail is every FAIL line the case must print, in order, and
# Expect any other line the run must show. MayFail is any FAIL line the case may print besides, and is set
# aside before Fail is compared: a symptom that follows from the mutant on some runs and not on others.
$CaseMutants = @{
    StopDisabled   = @{
        File = $MainWindow; Find = [regex]::Escape('CancelButton.IsEnabled = true;'); Replace = 'CancelButton.IsEnabled = false;'
        Case = 'C-006'; Fail = '^  FAIL  the hook acted '
    }
    StopCollapsed  = @{
        File = $MainWindow; Find = [regex]::Escape('CancelButton.IsEnabled = true;')
        Replace = 'CancelButton.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed;'
        Case = 'C-006'; Fail = '^  FAIL  the hook acted '
    }
    NoCrashHandler = @{
        File = $App; Find = '(?s)DispatcherUnhandledException \+= \(_, ex\) =>\s*\{.*?\};'; Replace = ''
        Case = 'C-012'; Fail = '^  FAIL  exit code is the app''s 70 \(actual -532462766 \(0xE0434352\)\)$'
    }
    NoExitRule     = @{
        File = $App; Find = '(?s)Exit \+= \(_, e\) =>\s*\{.*?\};'; Replace = ''
        Case = 'O-016'; Fail = '^  FAIL  exit code is 70, .* \(actual 0 \(0x00000000\)\)$'
    }
    CancelThrows   = @{
        File = $MainWindow; Find = '(private void OnCancel\(object sender, RoutedEventArgs e\)\s*\{)'
        Replace = '$1 throw new InvalidOperationException("mutant: OnCancel threw");'
        Case = 'C-006'; Fail = '^  FAIL  exit code$', '^  FAIL  a report was written$'
        Expect = '^        hook: rethrown mode=stop reason=InvalidOperationException: mutant: OnCancel threw$', '^          actual   ''70''$'
    }
    HookFailsAfterActing = @{
        File = $HookSource; Find = [regex]::Escape('acted = true;')
        Replace = 'acted = true; throw new InvalidOperationException("hook mutant: failed after acting");'
        Case = 'C-006'; Fail = '^  FAIL  the hook did not fail after it acted '
    }
    # The ending's snapshot is always non-final, so topLevel.final is the one FAIL line every run prints. The
    # stale rows' reading, bytes and files differ from core's on most runs, not all (19 of 20 in SL-064's spike).
    EndingObeysSkip = @{
        File = $MainWindow; Find = [regex]::Escape('else if (!ReferenceEquals(final, _shownTopLevel))')
        Replace = 'else if (!ReferenceEquals(final, _shownTopLevel) && !_skipNextRefresh)'
        Case = 'L-002'; Fail = '^  FAIL  topLevel.final against core \(the ending showed the final snapshot\)$'
        MayFail = '^  FAIL  topLevel\.(reading|bytes|files) against core \(the ending showed the final snapshot\)$'
        Expect = '^          actual   ''False''$'
    }
    ListingFailsAnyFolder = @{
        File = $IoCancel
        Find = [regex]::Escape("`n            || !win32Path.AsSpan(win32Path.LastIndexOf('\\') + 1).Equals(fail.FolderName, StringComparison.OrdinalIgnoreCase))")
        Replace = ')'
        Case = 'T-007'
        Fail = '^  FAIL  the failpoint fired once, on P at its second page, during the scan ', '^  FAIL  Q, listed before P with no fault armed against it, was read in full: '
        MayFail = '^  FAIL  the root''s rows are Q, P, R: '
        Expect = '^        hook: fail-listing fired a-[0-9a-f]{32} page 2$'
    }
    NoFailListingMember = @{
        File = $IoCancel; Find = [regex]::Escape('internal static void FailListingOf('); Replace = 'internal static void FailListingOfRenamed('
        Case = 'T-007'; Fail = '^  FAIL  the hook armed the listing failpoint for P before the app''s Main, its first line '
        Expect = '^        hook: fail-listing refused reason=MissingMethodException: ', '^        exit -532462766 \(0xE0434352\)$'
    }
    NoExplorerViewMember = @{
        File = $ShellReveal; Find = [regex]::Escape('internal static void OverrideExplorerView('); Replace = 'internal static void OverrideExplorerViewRenamed('
        Case = 'R-003'
        Fail = '^  FAIL  the hook armed the injected view 0\|0 before the app''s Main, its first line ', '^  FAIL  the hook armed the injected view 0\|0 before the app''s Main, its first line '
        Expect = '^        hook: explorer-view refused reason=member MissingMethodException: ', '^        exit -532462766 \(0xE0434352\)$'
    }
}

# ---- Checks ----

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $what"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

function CheckEqualExit($expected, $actual, [string] $case) {
    Check ([string]$expected -eq [string]$actual) "WindowCases.ps1 -Case $case exits $expected (actual $actual)"
}

# ---- Scratch copies ----

# The copies build with this repo's SDK: their own eng/dotnet.ps1 finds no .dotnet beside it and would
# install a second one. So make sure there is one (eng/dotnet.ps1 installs it once if not) and put it
# first on PATH for this process and what it starts.
function Use-RepoSdk {
    $output = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') --version 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "eng/dotnet.ps1 found and installed no SDK: $($output -join ' ')"
    }
    $local = Join-Path $Repo '.dotnet'
    if (Test-Path -LiteralPath (Join-Path $local 'dotnet.exe')) {
        $env:PATH = "$local;$env:PATH"
        $env:DOTNET_ROOT = $local
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    }
}

# What WindowCases.ps1 needs to publish the app and build the hook, without anyone's bin, obj or artifacts.
function New-Scratch {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-window-mutants\{0}" -f [guid]::NewGuid().ToString('N'))
    foreach ($file in 'Directory.Build.props', 'global.json', 'eng\dotnet.ps1', 'eng\read-window.ps1') {
        $target = Join-Path $root $file
        [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        Copy-Item -LiteralPath (Join-Path $Repo $file) -Destination $target
    }
    foreach ($folder in 'src', 'tests\window') {
        $from = Join-Path $Repo $folder
        Get-ChildItem -LiteralPath $from -Recurse -File |
            Where-Object { $_.FullName.Substring($from.Length) -notmatch '\\(bin|obj)\\' } |
            ForEach-Object {
                $target = Join-Path (Join-Path $root $folder) $_.FullName.Substring($from.Length)
                [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
                Copy-Item -LiteralPath $_.FullName -Destination $target
            }
    }
    $root
}

function Remove-Scratch([string] $root) {
    try {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
    catch {
        Write-Output "  (could not remove $root, delete it by hand: $($_.Exception.Message))"
    }
}

function Set-Mutation([string] $root, [string] $file, [string] $find, [string] $replace) {
    $path = Join-Path $root $file
    $text = [System.IO.File]::ReadAllText($path)
    $count = [regex]::Matches($text, $find).Count
    if ($count -ne 1) {
        throw "the mutation matches $count times in $file, not once: the source has moved on and the mutant must follow it"
    }
    [System.IO.File]::WriteAllText($path, [regex]::Replace($text, $find, $replace))
}

# The copy's own WindowCases.ps1, so everything it builds and runs is the copy's.
function Invoke-ScratchCase([string] $root, [string] $case) {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\window\WindowCases.ps1') -Case $case 2>&1 |
            ForEach-Object { "$_" })
    [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = $lines }
}

function Show-Run($run) {
    foreach ($line in $run.Lines) { Write-Output "    | $line" }
}

# The hook as an earlier run left it, in the copy's artifacts/window-hook.
function Build-ScratchHook([string] $root) {
    $output = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'eng\dotnet.ps1') build (Join-Path $root $HookProject) `
            -c Release -o (Join-Path $root $HookOut) 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "the unmutated hook did not build in the copy: $($output -join ' ')"
    }
    Join-Path $root "$HookOut\FsaWindowHook.dll"
}

# ---- Mutants ----

function Test-CaseMutant([string] $name) {
    $mutant = $CaseMutants[$name]
    $root = New-Scratch
    try {
        Set-Mutation $root $mutant.File $mutant.Find $mutant.Replace
        $run = Invoke-ScratchCase $root $mutant.Case
        Show-Run $run
        CheckEqualExit 1 $run.Exit $mutant.Case
        Check ($run.Lines -contains "FAILED: $($mutant.Case)") "$($mutant.Case) is reported FAILED"
        $expected = @($mutant.Fail)
        $allowed = @($mutant.MayFail | Where-Object { $_ })
        $failures = @(foreach ($line in $run.Lines) {
                if ($line -match '^  FAIL  ' -and @($allowed | Where-Object { $line -match $_ }).Count -eq 0) { $line }
            })
        $besides = if ($allowed.Count -gt 0) { " besides /$($allowed -join '/, /')/" } else { '' }
        $matched = $failures.Count -eq $expected.Count
        for ($i = 0; $matched -and $i -lt $expected.Count; $i++) { $matched = $failures[$i] -match $expected[$i] }
        Check $matched "$($mutant.Case) failed on /$($expected -join '/, /')/ and nothing else$besides (got $($failures.Count): $($failures -join ' | '))"
        foreach ($line in @($mutant.Expect)) {
            if ($line) { Check (@($run.Lines -match $line).Count -gt 0) "the run shows /$line/" }
        }
    }
    finally { Remove-Scratch $root }
}

# Both hook mutants: the run stops at Update-Hook, before any case, with the build's own error, and the
# earlier dll is left where it was, unused.
function CheckHookRefused($run, [string] $dll, [string] $hash, [string] $buildError) {
    Show-Run $run
    CheckEqualExit 1 $run.Exit 'C-006'
    Check (@($run.Lines -match '^FAILED: the hook did not build \(exit \d+\), so no case ran$').Count -eq 1) 'Update-Hook stopped the run'
    Check (@($run.Lines -match $buildError).Count -gt 0) "the build output is shown (/$buildError/)"
    Check (-not ($run.Lines -contains 'C-006')) 'C-006 never started'
    Check ((Test-Path -LiteralPath $dll) -and (Get-FileHash -LiteralPath $dll).Hash -eq $hash) 'the earlier dll is still there, so it was refused, not missing'
}

function Test-HookCompileError {
    $root = New-Scratch
    try {
        $dll = Build-ScratchHook $root
        $hash = (Get-FileHash -LiteralPath $dll).Hash
        Set-Mutation $root $HookSource 'internal class StartupHook' "#error FsaWindowHook mutant: a compile error`r`ninternal class StartupHook"
        CheckHookRefused (Invoke-ScratchCase $root 'C-006') $dll $hash 'error CS1029'
    }
    finally { Remove-Scratch $root }
}

function Test-HookLocked {
    $root = New-Scratch
    $lock = $null
    try {
        $dll = Build-ScratchHook $root
        $hash = (Get-FileHash -LiteralPath $dll).Hash
        # A real change, so the build has a new dll to copy over the held one.
        Set-Mutation $root $HookSource ([regex]::Escape('an unhandled exception on the UI thread.')) 'an unhandled exception on the UI thread (changed).'
        # Read and share read only, as a process that has the dll loaded refuses writers.
        $lock = [System.IO.File]::Open($dll, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        $run = Invoke-ScratchCase $root 'C-006'
        $lock.Dispose()
        $lock = $null
        CheckHookRefused $run $dll $hash 'error MSB30(21|27)'
    }
    finally {
        if ($lock) { $lock.Dispose() }
        Remove-Scratch $root
    }
}

# ---- Run ----

$names = if ($Mutant -eq 'All') { 'StopDisabled', 'StopCollapsed', 'NoCrashHandler', 'NoExitRule', 'CancelThrows', 'HookFailsAfterActing', 'HookCompileError', 'HookLocked', 'EndingObeysSkip', 'ListingFailsAnyFolder', 'NoFailListingMember', 'NoExplorerViewMember' } else { @($Mutant) }
Use-RepoSdk
$failed = @()
foreach ($name in $names) {
    Write-Output $name
    $before = $script:Failures.Count
    try {
        if ($CaseMutants.Contains($name)) { Test-CaseMutant $name } else { & "Test-$name" }
    }
    catch {
        Write-Output "  FAIL  $($_.Exception.Message)"
        $script:Failures.Add("$name threw: $($_.Exception.Message)")
    }
    if ($script:Failures.Count -gt $before) {
        $failed += $name
    }
}

if ($failed.Count -gt 0) {
    Write-Output "FAILED: $($failed -join ', ')"
    exit 1
}
Write-Output "PASSED: $($names -join ', ')"
exit 0
