# SL-064's cases: the standing EndingObeysSkip mutant in tests/window/WindowMutants.ps1, case by case. The
# harness's own definitions are parsed out of WindowMutants.ps1 and loaded into a module of their own, never
# re-typed here, so every case runs the code -Mutant runs.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-064-MutantCases.ps1 -Case C-003
#
# The acceptance test is tests/bugs/SL-064-EndingObeysSkipMutant.Tests.ps1, which this script leaves alone.
#
# C-001  -Mutant EndingObeysSkip binds through WindowMutants.ps1's own param block, and a name it does not
#        list is refused there.
# C-002  $CaseMutants['EndingObeysSkip'] names L-002 and MainWindow.xaml.cs, both of which exist, and the run
#        loop hands the name to Test-CaseMutant.
# C-003  Set-Mutation applies the entry once to a copy of the real MainWindow.xaml.cs, inside
#        ShowFinalTopLevel and nowhere else. The same anchor twice, or reformatted, is refused.
# C-004  The guard, compiled with Add-Type as the source has it and as the mutant leaves it, and given a new
#        final snapshot while _skipNextRefresh is true: the real one shows it and the mutant does not. With no
#        skip armed both show it, and both clear on a null.
# C-005  Test-CaseMutant, fed L-002 runs: killed when topLevel.final fails False (alone, or with reading,
#        bytes and files). Not killed when L-002 passed, when final held, when the hook never acted, when the
#        app crashed, when the app did not publish, or when final is missing rather than False.
# C-006  -Mutant All: the run's own roster, evaluated with $Mutant = 'All', holds EndingObeysSkip and every
#        other name the ValidateSet offers.
# C-007  WindowMutants.ps1 -Mutant EndingObeysSkip for real: exit 0, the mutated app published in the scratch
#        copy, the skip armed at the ending, L-002 FAILED on topLevel.final, and the real source untouched.
# C-008  L-002 in an unmutated copy made by the harness's own New-Scratch and run by its own
#        Invoke-ScratchCase: PASSED, the copy byte-identical to the real source, and the copy removed after.
#
# C-001 to C-006 take a few seconds and build nothing. C-007 and C-008 publish the app in a scratch copy
# under %TEMP% (about a minute each) and need the .NET 10 SDK, which eng/dotnet.ps1 installs into the
# gitignored .dotnet/ when the machine has none (Q-003). -Case All runs every case. Exit 0 when every check
# held, 1 when any failed.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Mutants = Join-Path $Repo 'tests\window\WindowMutants.ps1'
$Source = Join-Path $Repo 'src\FsAnalyser.App\MainWindow.xaml.cs'
$ScratchParent = Join-Path ([System.IO.Path]::GetTempPath()) 'fsa-window-mutants'
$Name = 'EndingObeysSkip'
$FinalLine = '  FAIL  topLevel.final against core (the ending showed the final snapshot)'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $what"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# ---- WindowMutants.ps1, as parsed ----

$tokens = $null
$parseErrors = $null
$Ast = [System.Management.Automation.Language.Parser]::ParseFile($Mutants, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "WindowMutants.ps1 does not parse: $($parseErrors[0])" }

function Get-Assignment([string] $variable) {
    $found = @($Ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq $variable
        })
    if ($found.Count -ne 1) { throw "WindowMutants.ps1 has $($found.Count) top-level assignments to `$$variable, not one" }
    $found[0]
}

# Everything the script defines before its run starts at $names (checks, scratch copies, Set-Mutation,
# Test-CaseMutant, $CaseMutants), in a module of its own, with $Repo this repo. Nothing in it runs anything.
function New-Harness {
    $run = Get-Assignment 'names'
    $repoLine = Get-Assignment 'Repo'
    $text = ($Ast.EndBlock.Statements |
            Where-Object { $_.Extent.StartOffset -lt $run.Extent.StartOffset -and $_ -ne $repoLine } |
            ForEach-Object { $_.Extent.Text }) -join "`n"
    # Exporting nothing: its Check and the rest stay in the module, and this script's own stay in charge here.
    $harness = New-Module -Name "WindowMutants-$([guid]::NewGuid().ToString('N'))" -ScriptBlock ([scriptblock]::Create("$text`nExport-ModuleMember"))
    & $harness { param($repo) $script:Repo = $repo } $Repo
    $harness
}

function Get-Entry($harness) {
    $entry = & $harness { param($name) $CaseMutants[$name] } $Name
    if ($null -eq $entry) { throw "`$CaseMutants in WindowMutants.ps1 has no '$Name' entry" }
    $entry
}

# ShowFinalTopLevel's text, bounded as the acceptance test bounds it.
function Get-FinalMethod([string] $text) {
    $start = $text.IndexOf('private void ShowFinalTopLevel(TopLevelSnapshot? final)')
    $end = if ($start -ge 0) { $text.IndexOf('private void ShowTopLevel(TopLevelSnapshot snapshot)', $start) } else { -1 }
    if ($start -lt 0 -or $end -lt 0) { throw "ShowFinalTopLevel is not where it was in $Source" }
    [pscustomobject]@{ Start = $start; End = $end; Text = $text.Substring($start, $end - $start) }
}

# A root holding only a copy of MainWindow.xaml.cs, at the path the entry names; $text replaces its content.
function New-SourceRoot([string] $file, [string] $text) {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-sl064-cases\{0}" -f [guid]::NewGuid().ToString('N'))
    $target = Join-Path $root $file
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
    [System.IO.File]::WriteAllText($target, $text)
    $root
}

function Remove-Root([string] $root) {
    if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
}

# The real ShowFinalTopLevel's text with the entry applied by the harness's own Set-Mutation.
function Get-MutatedSource($harness, $entry) {
    $root = New-SourceRoot $entry.File ([System.IO.File]::ReadAllText($Source))
    try {
        & $harness { param($root, $e) Set-Mutation $root $e.File $e.Find $e.Replace } $root $entry
        [System.IO.File]::ReadAllText((Join-Path $root $entry.File))
    }
    finally { Remove-Root $root }
}

# ---- Cases ----

function Test-C001 {
    $block = [scriptblock]::Create("$($Ast.ParamBlock.Extent.Text)`n`$Mutant")
    $bound = & $block -Mutant $Name
    Check ($bound -ceq $Name) "-Mutant $Name binds through WindowMutants.ps1's param block (bound $bound)"
    $bound = & $block -Mutant 'All'
    Check ($bound -ceq 'All') "-Mutant All still binds (bound $bound)"
    $refused = $null
    try { [void](& $block -Mutant "${Name}X") }
    catch { $refused = $_.Exception }
    Check ($refused -is [System.Management.Automation.ParameterBindingException]) "a name the ValidateSet does not list is refused at binding ($(if ($refused) { $refused.GetType().Name } else { 'bound' }))"
}

function Test-C002 {
    $harness = New-Harness
    $entry = Get-Entry $harness
    $mainWindow = & $harness { $MainWindow }
    Check ($entry.Case -ceq 'L-002') "it runs case L-002 (Case $($entry.Case))"
    Check ($entry.File -ceq $mainWindow) "it mutates `$MainWindow, $mainWindow (File $($entry.File))"
    Check (Test-Path -LiteralPath (Join-Path $Repo $entry.File) -PathType Leaf) "$($entry.File) exists in this repo"
    $cases = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Repo 'tests\window\WindowCases.ps1'), [ref]$null, [ref]$null)
    $l002 = @($cases.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-L002' }, $true))
    Check ($l002.Count -eq 1) 'WindowCases.ps1 defines Test-L002, the function -Case L-002 calls'
    # The run loop's own test: a name $CaseMutants holds goes to Test-CaseMutant, not to a Test-<name> of its own.
    $routed = & $harness { param($name) $CaseMutants.Contains($name) -and -not (Get-Command "Test-$name" -ErrorAction SilentlyContinue) } $Name
    Check $routed "the run loop hands $Name to Test-CaseMutant (in `$CaseMutants, and no Test-$Name)"
}

function Test-C003 {
    $harness = New-Harness
    $entry = Get-Entry $harness
    $original = [System.IO.File]::ReadAllText($Source)
    $method = Get-FinalMethod $original
    $hits = @([regex]::Matches($original, $entry.Find))
    Check ($hits.Count -eq 1) "the anchor matches once in all of MainWindow.xaml.cs ($($hits.Count))"
    if ($hits.Count -eq 1) {
        Check ($hits[0].Index -gt $method.Start -and $hits[0].Index + $hits[0].Length -le $method.End) "and that once is inside ShowFinalTopLevel (offset $($hits[0].Index), method $($method.Start) to $($method.End))"
    }

    $mutated = Get-MutatedSource $harness $entry
    $mutatedMethod = Get-FinalMethod $mutated
    Check ($original.Substring(0, $method.Start) -ceq $mutated.Substring(0, $mutatedMethod.Start)) 'Set-Mutation left everything before ShowFinalTopLevel as it was (RefreshTopLevel included)'
    Check ($original.Substring($method.End) -ceq $mutated.Substring($mutatedMethod.End)) 'and everything after it'
    Check ($mutatedMethod.Text -cne $method.Text -and $mutatedMethod.Text.Contains('&& !_skipNextRefresh')) "and ShowFinalTopLevel now consults _skipNextRefresh"

    # The anchor twice, and the anchor reformatted: Set-Mutation refuses both, and the file is left alone.
    $shapes = @(
        [pscustomobject]@{ Name = 'twice'; Text = $original.Insert($method.Start, "// $($hits[0].Value)`r`n    "); Count = 2 }
        [pscustomobject]@{ Name = 'reformatted'; Text = $original.Replace($hits[0].Value, $hits[0].Value.Replace('(final, ', "(final,`r`n            ")); Count = 0 })
    foreach ($shape in $shapes) {
        $root = New-SourceRoot $entry.File $shape.Text
        try {
            $refused = $null
            try { & $harness { param($root, $e) Set-Mutation $root $e.File $e.Find $e.Replace } $root $entry }
            catch { $refused = $_.Exception.Message }
            Check ($refused -like "the mutation matches $($shape.Count) times in *") "the anchor $($shape.Name): Set-Mutation refuses it ($(if ($refused) { $refused } else { 'applied' }))"
            Check ([System.IO.File]::ReadAllText((Join-Path $root $entry.File)) -ceq $shape.Text) "the anchor $($shape.Name): the file is left as it was"
        }
        finally { Remove-Root $root }
    }
}

function Test-C004 {
    $harness = New-Harness
    $entry = Get-Entry $harness
    $real = (Get-FinalMethod ([System.IO.File]::ReadAllText($Source))).Text
    $mutant = (Get-FinalMethod (Get-MutatedSource $harness $entry)).Text
    # The method's text verbatim, in a class with the fields it reads and the two methods it calls, counted.
    $class = {
        param([string] $name, [string] $method)
        @"
public sealed class $name
{
    public TopLevelSnapshot? _shownTopLevel;
    public bool _skipNextRefresh;
    public int Shown;
    public int Cleared;
    private void ShowTopLevel(TopLevelSnapshot snapshot) { Shown++; _shownTopLevel = snapshot; }
    private void ClearTopLevel() { Cleared++; _shownTopLevel = null; _skipNextRefresh = false; }
    public void End(TopLevelSnapshot? final) => ShowFinalTopLevel(final);
$method
}
"@
    }
    $ns = "Sl064Guard$([guid]::NewGuid().ToString('N'))"
    Add-Type -Language CSharp -TypeDefinition @"
#nullable enable
namespace $ns
{
public sealed class TopLevelSnapshot { }
$(& $class 'Real' $real)
$(& $class 'Mutant' $mutant)
}
"@
    foreach ($kind in 'Real', 'Mutant') {
        $shown = New-Object "$ns.TopLevelSnapshot"
        $final = New-Object "$ns.TopLevelSnapshot"

        # The shape: the scan ends with a refresh skip armed, and the last tick showed an earlier snapshot.
        $guard = New-Object "$ns.$kind"
        $guard._shownTopLevel = $shown
        $guard._skipNextRefresh = $true
        $guard.End($final)
        $showsFinal = [object]::ReferenceEquals($guard._shownTopLevel, $final)
        if ($kind -eq 'Real') {
            Check ($showsFinal -and $guard.Shown -eq 1) "skip armed at the ending: the real guard shows the final snapshot (shown $($guard.Shown))"
        }
        else {
            Check (-not $showsFinal -and $guard.Shown -eq 0 -and [object]::ReferenceEquals($guard._shownTopLevel, $shown)) "skip armed at the ending: the mutant leaves the earlier snapshot on screen (shown $($guard.Shown))"
        }

        $guard = New-Object "$ns.$kind"
        $guard._shownTopLevel = $shown
        $guard._skipNextRefresh = $false
        $guard.End($final)
        Check ([object]::ReferenceEquals($guard._shownTopLevel, $final) -and $guard.Shown -eq 1) "no skip armed: the $($kind.ToLower()) guard shows the final snapshot (shown $($guard.Shown))"

        $guard = New-Object "$ns.$kind"
        $guard._shownTopLevel = $shown
        $guard._skipNextRefresh = $true
        $guard.End($null)
        Check ($null -eq $guard._shownTopLevel -and $guard.Cleared -eq 1) "no final snapshot: the $($kind.ToLower()) guard clears the list (cleared $($guard.Cleared))"
    }
}

# The L-002 lines WindowCases.ps1 prints (its Check and CheckEqual), as -Mutant's run of it shows them.
function Get-L002Lines([string[]] $failed, [string] $final = "'False'") {
    $figures = @{ reading = "'0'", "'1'"; bytes = "'524288'", "'393216'"; files = "'8'", "'6'"; final = "'True'", $final }
    'L-002'
    '        built 63,000 folders in 21.8 s'
    '        hook: watch rows=3000 folderBytes=65536 notRead=2498 containers=11 skipped=1 armed=True running=True listHeight=434.4'
    '        hook: acted mode=watch enabled=True visible=True ms=1791'
    "  ok    the hook acted (one 'acted mode=watch' line in FSA_HOOK_LOG); if not, the hook never acted and nothing after it is this case's ending"
    '  ok    the next skip was armed when Cancel was pressed'
    '  ok    exit code'
    '  ok    a report was written'
    '  ok    the report has topLevel (the results list shows a snapshot)'
    '  ok    topLevel.rows against core (the ending showed the final snapshot)'
    foreach ($key in 'reading', 'bytes', 'files', 'final') {
        if ($key -in $failed) {
            "  FAIL  topLevel.$key against core (the ending showed the final snapshot)"
            "          expected $($figures[$key][0])"
            "          actual   $($figures[$key][1])"
        }
        else {
            "  ok    topLevel.$key against core (the ending showed the final snapshot)"
        }
    }
}

function Test-C005 {
    $harness = New-Harness
    # A scratch copy of MainWindow.xaml.cs alone, so Set-Mutation still runs for real, and the run L-002 would
    # have made in it handed in rather than made.
    & $harness {
        param($source)
        $script:RealSource = $source
        function script:New-Scratch {
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-sl064-cases\{0}" -f [guid]::NewGuid().ToString('N'))
            $target = Join-Path $root $MainWindow
            [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            Copy-Item -LiteralPath $script:RealSource -Destination $target
            $root
        }
        function script:Invoke-ScratchCase([string] $root, [string] $case) {
            $mutated = [System.IO.File]::ReadAllText((Join-Path $root $MainWindow)).Contains('&& !_skipNextRefresh')
            if (-not $mutated) { throw 'Test-CaseMutant ran the case on an unmutated copy' }
            $script:CaseRun
        }
    } $Source

    $runs = @(
        [pscustomobject]@{ Name = 'reading, bytes, files and final fail (the run -Mutant made)'; Killed = $true; Exit = 1
            Lines = @(Get-L002Lines 'reading', 'bytes', 'files', 'final') + 'FAILED: L-002' }
        [pscustomobject]@{ Name = 'final alone fails'; Killed = $true; Exit = 1
            Lines = @(Get-L002Lines 'final') + 'FAILED: L-002' }
        [pscustomobject]@{ Name = 'L-002 passed (the mutant survived)'; Killed = $false; Exit = 0
            Lines = @(Get-L002Lines @()) + 'PASSED: L-002' }
        [pscustomobject]@{ Name = 'reading and bytes fail, final held'; Killed = $false; Exit = 1
            Lines = @(Get-L002Lines 'reading', 'bytes') + 'FAILED: L-002' }
        [pscustomobject]@{ Name = 'final missing from the report, not False'; Killed = $false; Exit = 1
            Lines = @(Get-L002Lines 'final' '<null>') + 'FAILED: L-002' }
        [pscustomobject]@{ Name = 'the hook never acted'; Killed = $false; Exit = 1
            Lines = 'L-002', "  FAIL  the hook acted (one 'acted mode=watch' line in FSA_HOOK_LOG); if not, the hook never acted and nothing after it is this case's ending", 'FAILED: L-002' }
        [pscustomobject]@{ Name = 'the app crashed after the hook acted'; Killed = $false; Exit = 1
            Lines = 'L-002', '  ok    the hook acted (one ''acted mode=watch'' line in FSA_HOOK_LOG); if not, the hook never acted and nothing after it is this case''s ending',
            '  FAIL  exit code', "          expected '0'", "          actual   '-532462766'", '  FAIL  a report was written', 'FAILED: L-002' }
        [pscustomobject]@{ Name = 'the mutated app did not publish'; Killed = $false; Exit = 1
            Lines = 'publishing the app (eng/dotnet.ps1 publish)', '  MainWindow.xaml.cs(338,59): error CS0103: The name ''_skipNextRefresh'' does not exist in the current context',
            'publish failed with exit 1' }
    )
    foreach ($shape in $runs) {
        $result = & $harness {
            param($name, $shape)
            $script:CaseRun = [pscustomobject]@{ Exit = $shape.Exit; Lines = [string[]]$shape.Lines }
            $before = $script:Failures.Count
            $output = @(Test-CaseMutant $name)
            [pscustomobject]@{ Killed = $script:Failures.Count -eq $before; Output = $output }
        } $Name $shape
        $says = if ($result.Killed) { 'killed' } else { 'not killed' }
        Check ($result.Killed -eq $shape.Killed) "$($shape.Name): Test-CaseMutant says $says"
        if ($result.Killed -ne $shape.Killed) {
            foreach ($line in $result.Output) { Write-Output "        $line" }
        }
    }
}

function Test-C006 {
    $allowed = @(($Ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Mutant' }).Attributes |
            Where-Object { $_.TypeName.Name -eq 'ValidateSet' } | ForEach-Object { $_.PositionalArguments.Value })
    $roster = [scriptblock]::Create("param(`$Mutant)`n$((Get-Assignment 'names').Extent.Text)`n`$names")
    $names = @(& $roster 'All')
    Check ($names -ccontains $Name) "-Mutant All runs $Name ($($names -join ', '))"
    $missing = @($allowed | Where-Object { $_ -cne 'All' -and $names -cnotcontains $_ })
    Check ($missing.Count -eq 0) "-Mutant All runs every name the ValidateSet offers (missing: $($missing -join ', '))"
    Check ($names.Count -eq @($names | Select-Object -Unique).Count) 'and each once'
    $one = @(& $roster $Name)
    Check ($one.Count -eq 1 -and $one[0] -ceq $Name) "-Mutant $Name runs it alone ($($one -join ', '))"
}

function Get-Scratches {
    if (Test-Path -LiteralPath $ScratchParent) { @(Get-ChildItem -LiteralPath $ScratchParent -Directory | ForEach-Object { $_.FullName }) } else { @() }
}

function Test-C007 {
    $hash = (Get-FileHash -LiteralPath $Source).Hash
    $scratches = Get-Scratches
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Mutants -Mutant $Name 2>&1 | ForEach-Object { "$_" })
    $exit = $LASTEXITCODE
    foreach ($line in $lines) { Write-Output "    > $line" }
    Check ($exit -eq 0) "WindowMutants.ps1 -Mutant $Name exits 0 (actual $exit)"
    Check ($lines -ccontains "PASSED: $Name") "it says PASSED: $Name"
    Check ($lines -ccontains '    | publishing the app (eng/dotnet.ps1 publish)') 'the mutated app was published in the scratch copy, not reused or hand-patched'
    Check ($lines -ccontains '    |   ok    the next skip was armed when Cancel was pressed') 'L-002 ended with the refresh skip armed'
    Check ($lines -ccontains "    | $FinalLine") 'L-002 printed the topLevel.final FAIL line'
    Check ($lines -ccontains "    |           actual   'False'") "and topLevel.final was 'False'"
    Check ($lines -ccontains '    | FAILED: L-002') 'L-002 was reported FAILED'
    Check (@($lines -match '^  ok    L-002 failed on /').Count -eq 1) 'the harness matched its Fail pattern and nothing else'
    Check ((Get-FileHash -LiteralPath $Source).Hash -eq $hash) 'the real MainWindow.xaml.cs is unchanged'
    $left = @(Get-Scratches | Where-Object { $_ -notin $scratches })
    Check ($left.Count -eq 0) "the scratch copy was removed ($($left -join ', '))"
}

function Test-C008 {
    $harness = New-Harness
    $hash = (Get-FileHash -LiteralPath $Source).Hash
    & $harness { Use-RepoSdk }
    $root = & $harness { New-Scratch }
    try {
        $copy = Join-Path $root 'src\FsAnalyser.App\MainWindow.xaml.cs'
        Check ((Get-FileHash -LiteralPath $copy).Hash -eq $hash) "New-Scratch's copy of MainWindow.xaml.cs is the real one, unmutated"
        $run = & $harness { param($root) Invoke-ScratchCase $root 'L-002' } $root
        foreach ($line in $run.Lines) { Write-Output "    | $line" }
        Check ($run.Exit -eq 0) "L-002 exits 0 on the unmutated copy (actual $($run.Exit))"
        Check ($run.Lines -ccontains 'PASSED: L-002') 'it says PASSED: L-002'
        Check (@($run.Lines -match '^  FAIL  ').Count -eq 0) 'with no FAIL line'
        Check ($run.Lines -ccontains '  ok    the next skip was armed when Cancel was pressed') 'and ended with the refresh skip armed, as it does under the mutant'
        Check ($run.Lines -ccontains '  ok    topLevel.final against core (the ending showed the final snapshot)') 'and topLevel.final held'
    }
    finally { & $harness { param($root) Remove-Scratch $root } $root }
    Check (-not (Test-Path -LiteralPath $root)) 'Remove-Scratch removed the copy'
    Check ((Get-FileHash -LiteralPath $Source).Hash -eq $hash) 'the real MainWindow.xaml.cs is unchanged'
}

# ---- Run ----

$cases = if ($Case -eq 'All') { 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008' } else { @($Case) }
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

if ($failed.Count -gt 0) {
    Write-Output "FAILED: $($failed -join ', ')"
    exit 1
}
Write-Output "PASSED: $($cases -join ', ')"
exit 0
