# Checks that the two copies of the architecture rules still hold the same patterns (SL-031): the xUnit
# twin, tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs, and the twinned entries of .taad/fitness.yaml
# that the engine's Fitness check runs. It reads the two files as UTF-8 text and writes nothing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File eng/rules-parity.ps1 [-Root <repo>] [-ExpectFacts <n>]
#
# Exit 0 AGREE. Exit 1 DRIFT, followed by up to 10 lines, each naming an entry or Fact that pairs with
# nothing, where it is, the nearest one on the other side, and the patterns that differ. Exit 2
# CANNOT-READ: a file is missing, a shape either reader cannot classify, zero Facts or patterns, a Fact
# count that differs from -ExpectFacts, or a self-test that did not see drift. Not being able to check
# never reads as agreement. The first line of output is the verdict, and all output is ASCII.
#
# PATTERNS ONLY. Scopes and exemptions are written in different notations in the two copies, and the two
# engines walk, decode and match files differently. Both are the job of eng/rules-harness/harness.py,
# which needs the TAAD engine. RuleCopiesAgreeTests runs this script on every test run.
#
# Both readers are closed grammars: every line is classified, and a line that fits none of the shapes
# below is refused with file:line, never skipped. The other reader of each file (the C# compiler, YAML)
# reads every line, so a line skipped here could change what one copy enforces while this says AGREE.
#   C#   The file holds 'using System.Text.RegularExpressions;', 'namespace FsAnalyser.Core.Tests;' and
#        one type, 'public class ArchitectureRulesTests'. No block comment, preprocessor directive, raw
#        string or string that runs onto the next line. An attribute is '[Fact]' or '[Theory]' alone on
#        its line, and each is followed by 'public void Name()'. A Fact's body is '{', one AssertNoMatch(
#        call and '}', with blank and // lines anywhere. The call's files argument uses only the words in
#        $FilesWords and ends its line; every line after it is one verbatim @"..." literal ending ',' or
#        ');'. AssertNoMatch is named once in each Fact and declared once, and its body is pinned below,
#        because the normaliser rests on what that body does with a pattern.
#   yaml Before 'rules:', comments and blank lines only. After it, indentation is ASCII spaces, and only
#        comments and '  - name: ' entry starts sit at indent 0-3. The entries above the first
#        '# settled by SL-' comment are twinned: a single-quoted name, then keys at indent 4, each at most
#        once, from $EntryKeys. The list keys hold single-quoted items at one indent; the others hold one
#        single-quoted scalar, or true or false. The marker must stand between entries. The entries below
#        it have no xUnit twin and are only counted.
# The normaliser is the yaml header's authoring rules and nothing else. A twinned yaml pattern must start
# with (?m), which is stripped: xUnit applies RegexOptions.Multiline to every pattern, so a yaml pattern
# without it is drift. '^\ufeff?' (the escape in either case, or a literal U+FEFF) reads as '^'. '' in
# yaml and "" in C# read as one quote. Entries pair with Facts by the multiset of their normalised
# patterns, so a Fact split across two scopes pairs with both of its entries. A scope entry deleted from
# one of those two is invisible here; the harness's per-scope plants are what catch it.

param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    # A string, so a bad value is CANNOT-READ (exit 2) and not a binding error, whose exit 1 reads as DRIFT.
    [string] $ExpectFacts = ''
)

$ErrorActionPreference = 'Stop'

$CsRel = 'tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs'
$YamlRel = '.taad/fitness.yaml'
$Bom = [string][char]0xFEFF
$BomGuard = '\^(?:\\u[fF][eE][fF][fF]|' + $Bom + ')\?'
$NoMultiline = [string][char]1
$MaxShown = 10

# Non-ASCII shown as \uXXXX, so the output reads the same whatever the console's code page.
function Show([string] $text) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($c in $text.ToCharArray()) {
        if ([int]$c -lt 32 -or [int]$c -gt 126) { [void]$builder.AppendFormat('\u{0:X4}', [int]$c) } else { [void]$builder.Append($c) }
    }
    $builder.ToString()
}

function Show-Name([string] $name) { "'$(Show $name)'" }

function Stop-Reading([string] $where, [string] $why) {
    throw [System.IO.InvalidDataException]::new("${where}: $why")
}

function Read-Lines([string] $rel) {
    $path = Join-Path $Root $rel
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Stop-Reading $rel "file not found under $Root" }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
    if ($text.StartsWith($Bom, [StringComparison]::Ordinal)) { $text = $text.Substring(1) }
    $lines = $text -split "`r?`n"
    # Python's universal newlines break a line at a lone CR, and YAML also at U+0085, U+2028 and U+2029, so
    # a whole entry could sit inside what this reader takes for one line. Refuse them rather than guess.
    $breaks = '[' + [char]13 + [char]0x85 + [char]0x2028 + [char]0x2029 + ']'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $found = [regex]::Match($lines[$i], $breaks)
        if ($found.Success) {
            Stop-Reading "${rel}:$($i + 1)" ('a line break that Python and YAML read and this reader does not (U+{0:X4}); save the file with CRLF or LF line ends only' -f [int]$found.Value[0])
        }
    }
    return , $lines
}

# ---- The C# copy ----

# The words a Fact's files argument may use. The argument is code, and code can do more than choose files,
# so a scope written with any other word is refused rather than read.
$FilesWords = @('Files', 'Except', 'Concat', 'Where', 'StartsWith', 'f', 'StringComparison', 'OrdinalIgnoreCase')

# The AssertNoMatch the normaliser was written against: each pattern compiled as given, with
# RegexOptions.Multiline, and matched one line at a time. A change to it changes what a pattern means to
# xUnit, so it is refused until the normaliser and this copy change with it. Compared with runs of
# whitespace collapsed.
$AssertNoMatchText = @'
    private static void AssertNoMatch(IEnumerable<string> files, params string[] patterns)
    {
        var checkedFiles = files.ToList();
        Assert.NotEmpty(checkedFiles);
        var regexes = patterns.Select(p => new Regex(p, RegexOptions.Multiline)).ToList();
        var violations = new List<string>();
        foreach (string file in checkedFiles)
        {
            string[] lines = File.ReadAllLines(Path.Combine(Repo, file));
            for (int n = 0; n < lines.Length; n++)
            {
                foreach (Regex regex in regexes)
                {
                    if (regex.IsMatch(lines[n]))
                        violations.Add($"{file}:{n + 1}: /{regex}/ in: {lines[n].Trim()}");
                }
            }
        }
        Assert.True(violations.Count == 0, "Architecture rule broken:\n" + string.Join("\n", violations));
    }
'@
$AssertNoMatchDeclaration = '^    private static void AssertNoMatch\(IEnumerable<string> files, params string\[\] patterns\)\s*$'

function Get-Collapsed([string[]] $text) { ((($text -join ' ') -replace '\s+', ' ')).Trim() }

# Each line of the C# file as code only: comments dropped, each string literal left as "" ($"" when it
# is interpolated) and each char literal as ''. Refuses what a line-by-line reading cannot place: a block
# comment, a preprocessor directive, a raw string, a string that runs onto the next line, and a literal
# inside an interpolation hole. Any of those can hide a [Fact] or a call from this reader and not from
# the compiler.
function Get-CsCode([string[]] $lines) {
    $out = New-Object 'string[]' $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $s = $lines[$i]
        $where = "${CsRel}:$($i + 1)"
        if ($s -cmatch '^\s*#') { Stop-Reading $where "a preprocessor directive: $(Show $s.Trim())" }
        $code = New-Object System.Text.StringBuilder
        $c = 0
        while ($c -lt $s.Length) {
            $ch = $s[$c]
            if ($ch -ceq '/' -and $c + 1 -lt $s.Length) {
                if ($s[$c + 1] -ceq '/') { break }
                if ($s[$c + 1] -ceq '*') { Stop-Reading $where 'a block comment' }
            }
            if ($ch -ceq '"' -or $ch -ceq '$' -or $ch -ceq '@') {
                $rest = $s.Substring($c)
                if ($rest -cmatch '^\$*"""') { Stop-Reading $where 'a raw string literal' }
                if ($rest -cmatch '^(\$@|@\$|@|\$)?"') {
                    $prefix = [string]$Matches[1]
                    $verbatim = $prefix.Contains('@')
                    $interpolated = $prefix.Contains('$')
                    $c += $Matches[0].Length
                    $hole = 0
                    while ($true) {
                        if ($c -ge $s.Length) { Stop-Reading $where 'a string literal that runs onto the next line' }
                        $x = $s[$c]
                        if ($hole -gt 0) {
                            if ($x -ceq '"' -or $x -ceq "'") { Stop-Reading $where 'a string or char literal inside an interpolation hole' }
                            if ($x -ceq '{') { $hole++ } elseif ($x -ceq '}') { $hole-- }
                            $c++
                            continue
                        }
                        if ($interpolated -and $x -ceq '{') {
                            if ($c + 1 -lt $s.Length -and $s[$c + 1] -ceq '{') { $c += 2 } else { $hole = 1; $c++ }
                            continue
                        }
                        if (-not $verbatim -and $x -ceq '\') { $c += 2; continue }
                        if ($x -ceq '"') {
                            if ($verbatim -and $c + 1 -lt $s.Length -and $s[$c + 1] -ceq '"') { $c += 2; continue }
                            $c++
                            break
                        }
                        $c++
                    }
                    [void]$code.Append($(if ($interpolated) { '$""' } else { '""' }))
                    continue
                }
            }
            if ($ch -ceq "'") {
                $from = $c + $(if ($c + 1 -lt $s.Length -and $s[$c + 1] -ceq '\') { 3 } else { 2 })
                $close = if ($from -le $s.Length) { $s.IndexOf("'", $from) } else { -1 }
                if ($close -lt 0) { Stop-Reading $where 'a char literal the reader cannot close' }
                [void]$code.Append("''")
                $c = $close + 1
                continue
            }
            [void]$code.Append($ch)
            $c++
        }
        $out[$i] = $code.ToString()
    }
    return , $out
}

# Where the files argument of the AssertNoMatch( call starting at ($line, $col) ends -- the line holding
# its top-level comma -- and its code. It may not run to line $limit, the Fact's closing brace.
function Read-FilesArgument([string[]] $code, [int] $line, [int] $col, [int] $limit) {
    $depth = 0
    $text = New-Object System.Text.StringBuilder
    for ($i = $line; $i -lt $limit; $i++) {
        $s = $code[$i]
        [void]$text.Append(' ')
        for ($c = $(if ($i -eq $line) { $col } else { 0 }); $c -lt $s.Length; $c++) {
            $ch = $s[$c]
            if ($ch -ceq '(' -or $ch -ceq '[') { $depth++ }
            elseif ($ch -ceq ')' -or $ch -ceq ']') {
                $depth--
                if ($depth -lt 0) { Stop-Reading "${CsRel}:$($i + 1)" 'AssertNoMatch( closes before any pattern' }
            }
            elseif ($ch -ceq ',' -and $depth -eq 0) {
                if ($s.Substring($c + 1).Trim().Length -ne 0) {
                    Stop-Reading "${CsRel}:$($i + 1)" 'the files argument of AssertNoMatch( must end its line, with each pattern on a line of its own'
                }
                return [pscustomobject]@{ End = $i; Code = $text.ToString() }
            }
            [void]$text.Append($ch)
        }
    }
    Stop-Reading "${CsRel}:$($line + 1)" 'AssertNoMatch( never reaches its patterns'
}

function Test-FilesArgument([string] $text, [string] $where, [string] $name) {
    $allowed = "only $($FilesWords -join ', '), string literals and . ( ) [ ] , ! =>"
    foreach ($word in [regex]::Matches($text, '[A-Za-z_][A-Za-z0-9_]*')) {
        if ($FilesWords -cnotcontains $word.Value) { Stop-Reading $where "Fact $name's files argument uses $($word.Value); it may use $allowed" }
    }
    $odd = [regex]::Match([regex]::Replace($text, '[A-Za-z_][A-Za-z0-9_]*|=>|""', ''), '[^ \t.(),\[\]!]')
    if ($odd.Success) { Stop-Reading $where "Fact $name's files argument holds '$(Show $odd.Value)'; it may use $allowed" }
}

function Read-Facts {
    $lines = Read-Lines $CsRel
    $code = Get-CsCode $lines
    $attribute = '^\s*\[(Fact|Theory)(\(.*\))?\]\s*$'
    $signature = '^\s*public\s+(?:async\s+)?(?:void|Task)\s+(\w+)\s*\([^()]*\)\s*$'
    $literal = '^\s*@"((?:[^"]|"")*)"\s*(,|\);)\s*$'
    $blank = '^\s*$'
    $classLine = '^public class ArchitectureRulesTests\s*$'
    $prologue = @('using System.Text.RegularExpressions;', 'namespace FsAnalyser.Core.Tests;')

    # One type, the test class, and nothing before it but the two lines the file has always had. Another
    # type could carry Facts that xUnit runs and the reflected count does not see.
    $classAt = -1
    for ($i = 0; $i -lt $code.Count; $i++) {
        if ($code[$i] -cnotmatch '\b(class|struct|interface|enum|record|delegate)\b') { continue }
        if ($classAt -lt 0 -and $code[$i] -cmatch $classLine) { $classAt = $i; continue }
        Stop-Reading "${CsRel}:$($i + 1)" "a type declaration other than 'public class ArchitectureRulesTests': $(Show $lines[$i].Trim())"
    }
    if ($classAt -lt 0) { Stop-Reading $CsRel "no line 'public class ArchitectureRulesTests'" }
    for ($i = 0; $i -lt $classAt; $i++) {
        if ($code[$i] -cmatch $blank -or $prologue -ccontains $code[$i].TrimEnd()) { continue }
        Stop-Reading "${CsRel}:$($i + 1)" "a line before the class other than '$($prologue -join "' and '")': $(Show $lines[$i].Trim())"
    }

    # Every attribute is '[Fact]' or '[Theory]' alone on its line, so each one is a Fact this reads.
    $attributeLines = @(for ($i = 0; $i -lt $code.Count; $i++) { if ($code[$i] -cmatch $attribute) { $i } })
    if ($attributeLines.Count -eq 0) { Stop-Reading $CsRel 'zero [Fact]s found' }
    for ($i = 0; $i -lt $code.Count; $i++) {
        if ($attributeLines -contains $i) { continue }
        if ($code[$i] -cmatch '^\s*\[' -or $code[$i] -cmatch '\b(Fact|Theory)(Attribute)?\b') {
            Stop-Reading "${CsRel}:$($i + 1)" "an attribute, or a Fact or Theory name, that is not '[Fact]' or '[Theory]' alone on its line: $(Show $lines[$i].Trim())"
        }
    }

    $facts = New-Object System.Collections.Generic.List[object]
    $bodies = New-Object System.Collections.Generic.List[object]
    for ($k = 0; $k -lt $attributeLines.Count; $k++) {
        $start = $attributeLines[$k]
        $end = if ($k + 1 -lt $attributeLines.Count) { $attributeLines[$k + 1] } else { $lines.Count }
        $j = $start + 1
        if ($j -ge $end -or $code[$j] -cnotmatch $signature) {
            Stop-Reading "${CsRel}:$($start + 1)" 'a [Fact] that is not followed on the next line by a public method the reader can name, alone on its line'
        }
        $name = $Matches[1]

        # The body: '{' alone on a line, and the first '}' after it at the same indent.
        $o = $j + 1
        while ($o -lt $end -and $code[$o] -cmatch $blank) { $o++ }
        $opened = if ($o -lt $end) { [regex]::Match($code[$o], '^(\s*)\{\s*$') } else { [regex]::Match('', 'x') }
        if (-not $opened.Success) { Stop-Reading "${CsRel}:$($o + 1)" "Fact $name's body does not open with '{' alone on a line" }
        $closeShape = '^' + [regex]::Escape($opened.Groups[1].Value) + '\}\s*$'
        $close = -1
        for ($i = $o + 1; $i -lt $end; $i++) { if ($code[$i] -cmatch $closeShape) { $close = $i; break } }
        if ($close -lt 0) { Stop-Reading "${CsRel}:$($o + 1)" "Fact $name's body never closes with '}' at the indent of its '{'" }
        $bodies.Add(@($o, $close))

        $named = @(for ($i = $o + 1; $i -lt $close; $i++) { foreach ($m in [regex]::Matches($code[$i], '\bAssertNoMatch\b')) { $i } })
        if ($named.Count -eq 0) { Stop-Reading "${CsRel}:$($j + 1)" "Fact $name makes no AssertNoMatch( call" }
        if ($named.Count -gt 1) {
            Stop-Reading "${CsRel}:$($named[1] + 1)" "Fact $name names AssertNoMatch more than once; its body is one AssertNoMatch( call and nothing else"
        }

        # Before the call: blank and // lines only.
        $call = $o + 1
        while ($call -lt $close -and $code[$call] -cmatch $blank) { $call++ }
        if ($code[$call] -cnotmatch '^\s*AssertNoMatch\(') {
            Stop-Reading "${CsRel}:$($call + 1)" "a line in Fact $name before its AssertNoMatch( call that is not blank or a // comment: $(Show $lines[$call].Trim())"
        }
        $files = Read-FilesArgument $code $call ($code[$call].IndexOf('AssertNoMatch(') + 'AssertNoMatch('.Length) $close
        Test-FilesArgument $files.Code "${CsRel}:$($call + 1)" $name

        $patterns = New-Object System.Collections.Generic.List[string]
        $closed = $false
        $i = $files.End + 1
        for (; $i -lt $close -and -not $closed; $i++) {
            if ($lines[$i] -cnotmatch $literal) {
                Stop-Reading "${CsRel}:$($i + 1)" "a line in Fact $name's AssertNoMatch( call that is not one verbatim @`"...`" literal ending ',' or ');': $(Show $lines[$i].Trim())"
            }
            $patterns.Add($Matches[1].Replace('""', '"'))
            $closed = $Matches[2] -ceq ');'
        }
        if (-not $closed) { Stop-Reading "${CsRel}:$($call + 1)" "Fact $name's AssertNoMatch( call never ends with ');'" }

        # After the call: blank and // lines only, up to the closing '}'.
        for (; $i -lt $close; $i++) {
            if ($code[$i] -cnotmatch $blank) {
                Stop-Reading "${CsRel}:$($i + 1)" "a line in Fact $name after its AssertNoMatch( call that is not blank or a // comment: $(Show $lines[$i].Trim())"
            }
        }
        $facts.Add([pscustomobject]@{ Name = $name; Where = "${CsRel}:$($j + 1)"; Patterns = $patterns.ToArray() })
    }

    # Outside the Facts, AssertNoMatch is named once: its declaration, whose body is the pinned one. A
    # second declaration could be the overload a call binds to, and another caller is not read.
    $declared = -1
    for ($i = 0; $i -lt $code.Count; $i++) {
        if ($code[$i] -cnotmatch '\bAssertNoMatch\b') { continue }
        if (@($bodies | Where-Object { $i -gt $_[0] -and $i -lt $_[1] }).Count) { continue }
        if ($declared -lt 0 -and $code[$i] -cmatch $AssertNoMatchDeclaration -and [regex]::Matches($code[$i], '\bAssertNoMatch\b').Count -eq 1) { $declared = $i; continue }
        Stop-Reading "${CsRel}:$($i + 1)" "AssertNoMatch named outside a Fact's one call and its one declaration: $(Show $lines[$i].Trim())"
    }
    if ($declared -lt 0) { Stop-Reading $CsRel "no declaration 'private static void AssertNoMatch(IEnumerable<string> files, params string[] patterns)'" }
    $end = -1
    for ($i = $declared + 2; $i -lt $code.Count; $i++) { if ($code[$i] -cmatch '^    \}\s*$') { $end = $i; break } }
    if ($end -lt 0 -or (Get-Collapsed $lines[$declared..$end]) -cne (Get-Collapsed ($AssertNoMatchText -split "`r?`n"))) {
        Stop-Reading "${CsRel}:$($declared + 1)" ('the body of AssertNoMatch is not the one the normaliser rests on (each pattern compiled as given with ' +
            'RegexOptions.Multiline and matched one line at a time); change the normaliser and the copy in eng/rules-parity.ps1 with it')
    }
    return , $facts.ToArray()
}

# ---- The yaml copy ----

# The keys a twinned entry may hold besides name. The list keys take block lists, the others one scalar.
$EntryKeys = @('files', 'except_files', 'must_not_match', 'including_comments', 'because')
$ListKeys = @('except_files', 'must_not_match')

function Read-SingleQuoted([string] $value, [string] $where, [string] $what) {
    if ($value -cmatch "^'((?:[^']|'')*)'( +#.*| *)$") { return $Matches[1].Replace("''", "'") }
    $kind = if ($value.StartsWith('"')) { 'a double-quoted scalar' }
        elseif ($value -cmatch '^[|>]') { 'a block scalar' }
        elseif ($value.StartsWith("'")) { 'a single-quoted scalar that does not end on its line, or has more than a comment after it' }
        elseif ($value.Length -eq 0) { 'empty' }
        else { 'a plain scalar' }
    Stop-Reading $where "$what is $kind; only a single-quoted scalar on one line is read: $(Show $value)"
}

function Read-Entries {
    $lines = Read-Lines $YamlRel
    $blank = '^ *$'
    $comment = '^ *#'
    $entryStart = '^  - name:( |$)'

    # Before 'rules:', comments and blank lines only, so 'rules' is the one top-level key.
    $rulesAt = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^rules:( +#.*| *)$') { $rulesAt = $i; break }
        if ($lines[$i] -cnotmatch $blank -and $lines[$i] -cnotmatch $comment) {
            Stop-Reading "${YamlRel}:$($i + 1)" "a line before 'rules:' that is not blank or a comment; 'rules:' must be the only top-level key: $(Show $lines[$i])"
        }
    }
    if ($rulesAt -lt 0) { Stop-Reading $YamlRel "no top-level 'rules:' line" }

    # After it, every line: blank, a comment, an entry start at indent 2, or deeper. Indentation is ASCII
    # spaces only, because .NET counts U+00A0, U+3000 and tab as whitespace and YAML does not.
    $markerAt = -1
    $starts = New-Object System.Collections.Generic.List[int]
    $settled = 0
    for ($i = $rulesAt + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $where = "${YamlRel}:$($i + 1)"
        if ($line -cmatch $blank) { continue }
        $odd = [regex]::Match($line, '^ *([^\S ])')
        if ($odd.Success) {
            Stop-Reading $where ('leading whitespace that is not an ASCII space (U+{0:X4}), which YAML does not read as indentation' -f [int]$odd.Groups[1].Value[0])
        }
        if ($line -cmatch $comment) {
            if ($markerAt -lt 0 -and $line -cmatch '^ *# *settled by SL-') { $markerAt = $i }
            continue
        }
        if ($line -cmatch $entryStart) {
            if ($markerAt -lt 0) { $starts.Add($i) } else { $settled++ }
            continue
        }
        $indent = ([regex]::Match($line, '^ *')).Length
        if ($indent -lt 4) {
            if ($line -cmatch '^ {0,2}-(\s|$)') { Stop-Reading $where "an entry that does not start with 'name:' on its dash line" }
            if ($indent -eq 0) {
                Stop-Reading $where "a column-0 line after 'rules:', which must stay the only top-level key and the last thing in the file: $(Show $line)"
            }
            Stop-Reading $where "a line at indent $indent, where only comments and '  - name: ' entry starts may sit: $(Show $line)"
        }
        if ($markerAt -lt 0 -and $starts.Count -eq 0) { Stop-Reading $where "a line after 'rules:' and before its first entry: $(Show $line)" }
    }

    # The marker stands between entries: after it, nothing but blank and comment lines before the next
    # entry start. Otherwise YAML reads what follows it as part of the twinned entry above it.
    if ($markerAt -ge 0) {
        for ($i = $markerAt + 1; $i -lt $lines.Count -and $lines[$i] -cnotmatch $entryStart; $i++) {
            if ($lines[$i] -cnotmatch $blank -and $lines[$i] -cnotmatch $comment) {
                Stop-Reading "${YamlRel}:$($markerAt + 1)" "the settled marker sits inside an entry: line $($i + 1) after it is not blank, a comment or an entry start"
            }
        }
    }

    $regionEnd = if ($markerAt -ge 0) { $markerAt } else { $lines.Count }
    $entries = New-Object System.Collections.Generic.List[object]
    for ($k = 0; $k -lt $starts.Count; $k++) {
        $start = $starts[$k]
        $end = if ($k + 1 -lt $starts.Count) { $starts[$k + 1] } else { $regionEnd }
        [void]($lines[$start] -cmatch '^  - name:(.*)$')
        $name = Read-SingleQuoted ($Matches[1] -replace '^ +', '') "${YamlRel}:$($start + 1)" 'the name of a twinned entry'
        $keys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        [void]$keys.Add('name')
        $list = $null
        $listAt = -1
        $listIndent = -1
        $listItems = 0
        $patterns = New-Object System.Collections.Generic.List[string]
        # YAML carries a list on past blank and comment lines, so this does too.
        for ($i = $start + 1; $i -lt $end; $i++) {
            $line = $lines[$i]
            if ($line -cmatch $blank -or $line -cmatch $comment) { continue }
            $where = "${YamlRel}:$($i + 1)"
            if ($null -ne $list -and $line -cmatch '^( +)- (.*)$' -and $Matches[1].Length -ge 4) {
                $n = $Matches[1].Length
                $value = $Matches[2] -replace '^ +', ''
                if ($listIndent -lt 0) { $listIndent = $n }
                elseif ($n -ne $listIndent) { Stop-Reading $where "an item of $list in $(Show-Name $name) at indent $n, where the items before it sit at $listIndent" }
                $what = if ($list -ceq 'must_not_match') { "a pattern of $(Show-Name $name)" } else { "an item of $list in $(Show-Name $name)" }
                $item = Read-SingleQuoted $value $where $what
                if ($list -ceq 'must_not_match') { $patterns.Add($item) }
                $listItems++
                continue
            }
            if ($line -cmatch '^    ([A-Za-z_][A-Za-z0-9_]*):(.*)$') {
                $key = $Matches[1]
                $rest = $Matches[2]
                if ($null -ne $list -and $listItems -eq 0) { Stop-Reading "${YamlRel}:$($listAt + 1)" "$list of $(Show-Name $name) has no items" }
                if (-not $keys.Add($key)) { Stop-Reading $where "the key $key appears twice in $(Show-Name $name); YAML keeps the last one, and Fitness reads only that" }
                if ($EntryKeys -cnotcontains $key) { Stop-Reading $where "a key $key in twinned entry $(Show-Name $name); a twinned entry holds only name, $($EntryKeys -join ', ')" }
                if ($rest.Length -gt 0 -and -not $rest.StartsWith(' ')) { Stop-Reading $where "the key $key has no space after its colon" }
                $value = $rest -replace '^ +', ''
                if ($ListKeys -ccontains $key) {
                    if ($value.Length -ne 0 -and -not $value.StartsWith('#')) { Stop-Reading $where "$key of $(Show-Name $name) is not a block list with one item per line" }
                    $list = $key
                    $listAt = $i
                    $listIndent = -1
                    $listItems = 0
                }
                else {
                    if ($value -cnotmatch '^(true|false)( +#.*| *)$') { [void](Read-SingleQuoted $value $where "$key of $(Show-Name $name)") }
                    $list = $null
                }
                continue
            }
            Stop-Reading $where "a line in twinned entry $(Show-Name $name) that is not a key at indent 4, an item under must_not_match or except_files, a comment or blank: $(Show $line)"
        }
        if ($null -ne $list -and $listItems -eq 0) { Stop-Reading "${YamlRel}:$($listAt + 1)" "$list of $(Show-Name $name) has no items" }
        if (-not $keys.Contains('must_not_match')) { Stop-Reading "${YamlRel}:$($start + 1)" "twinned entry $(Show-Name $name) has no must_not_match" }
        $entries.Add([pscustomobject]@{ Name = $name; Where = "${YamlRel}:$($start + 1)"; Patterns = $patterns.ToArray() })
    }
    return [pscustomobject]@{ Entries = $entries.ToArray(); Settled = $settled; MarkerFound = $markerAt -ge 0 }
}

# ---- Normalising and pairing ----

function Get-Normalised([string] $pattern, [bool] $fromYaml) {
    if ($fromYaml) {
        if (-not $pattern.StartsWith('(?m)', [StringComparison]::Ordinal)) { return $NoMultiline + $pattern }
        $pattern = $pattern.Substring(4)
    }
    return [regex]::Replace($pattern, $BomGuard, '^')
}

function Show-Pattern([string] $normalised) {
    if ($normalised.StartsWith($NoMultiline, [StringComparison]::Ordinal)) { return "'$(Show $normalised.Substring(1))' (lacks (?m))" }
    "'$(Show $normalised)'"
}

function Get-Key([string[]] $normalised) {
    $sorted = [string[]]$normalised.Clone()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return [string]::Join("`n", $sorted)
}

# The patterns of $a that $b lacks, counting repeats. Ordinal: pwsh 7's -ceq compares by culture, and
# under ICU that ignores characters such as U+0001 and U+FEFF.
function Get-Missing([string[]] $a, [string[]] $b) {
    $rest = New-Object System.Collections.Generic.List[string]
    foreach ($p in $b) { $rest.Add($p) }
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($p in $a) {
        $at = -1
        for ($i = 0; $i -lt $rest.Count; $i++) { if ([string]::Equals($rest[$i], $p, [StringComparison]::Ordinal)) { $at = $i; break } }
        if ($at -ge 0) { $rest.RemoveAt($at) } else { $out.Add($p) }
    }
    return , $out.ToArray()
}

function Get-CommonPrefix([string] $a, [string] $b) {
    $n = [Math]::Min($a.Length, $b.Length)
    $i = 0
    while ($i -lt $n -and [int]$a[$i] -eq [int]$b[$i]) { $i++ }
    $i
}

# Only for naming the other side in a drift line: most patterns shared, then fewest differing, then the
# unshared patterns closest to one of the other's by common prefix (so a one-character typo finds its twin).
function Get-Nearest($item, [object[]] $others) {
    $best = $null
    $bestScore = $null
    foreach ($o in $others) {
        $missing = Get-Missing $item.Normalised $o.Normalised
        $extra = Get-Missing $o.Normalised $item.Normalised
        $prefix = 0
        foreach ($p in $missing) {
            $top = 0
            foreach ($q in $o.Normalised) { $top = [Math]::Max($top, (Get-CommonPrefix $p $q)) }
            $prefix += $top
        }
        $score = @(($item.Normalised.Count - $missing.Count), (-($missing.Count + $extra.Count)), $prefix)
        $better = $null -eq $bestScore
        for ($i = 0; -not $better -and $i -lt 3; $i++) {
            if ($score[$i] -ne $bestScore[$i]) { $better = $score[$i] -gt $bestScore[$i]; break }
        }
        if ($better) { $best = $o; $bestScore = $score }
    }
    return $best
}

function Get-Difference($yamlSide, $csSide) {
    $missing = Get-Missing $yamlSide.Normalised $csSide.Normalised
    $onlyYaml = @(foreach ($p in $missing) { Show-Pattern $p })
    $missing = Get-Missing $csSide.Normalised $yamlSide.Normalised
    $onlyCs = @(foreach ($p in $missing) { Show-Pattern $p })
    $parts = @()
    if ($onlyYaml.Count) { $parts += "only in yaml $($onlyYaml -join ', ')" }
    if ($onlyCs.Count) { $parts += "only in C# $($onlyCs -join ', ')" }
    if (-not $parts.Count) { return 'the same patterns' }
    $parts -join '; '
}

function Add-Normalised([object[]] $items, [bool] $fromYaml) {
    foreach ($item in $items) {
        $normalised = [string[]]@($item.Patterns | ForEach-Object { Get-Normalised $_ $fromYaml })
        $item | Add-Member -NotePropertyName Normalised -NotePropertyValue $normalised -Force
        $item | Add-Member -NotePropertyName Key -NotePropertyValue (Get-Key $normalised) -Force
    }
}

function Group-ByKey([object[]] $items) {
    $groups = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[object]]' ([StringComparer]::Ordinal)
    foreach ($item in $items) {
        if (-not $groups.ContainsKey($item.Key)) { $groups[$item.Key] = New-Object System.Collections.Generic.List[object] }
        $groups[$item.Key].Add($item)
    }
    return , $groups
}

# One line per entry or Fact that pairs with nothing. Empty when the copies agree.
function Compare-Copies([object[]] $facts, [object[]] $entries) {
    $drift = New-Object System.Collections.Generic.List[string]
    $factGroups = Group-ByKey $facts
    $entryGroups = Group-ByKey $entries
    foreach ($key in $factGroups.Keys) {
        $group = $factGroups[$key]
        $paired = if ($entryGroups.ContainsKey($key)) { $entryGroups[$key].Count } else { 0 }
        if ($paired -eq 0) {
            foreach ($fact in $group) {
                $near = Get-Nearest $fact $entries
                $drift.Add("Fact $($fact.Name) ($($fact.Where)) pairs with no twinned entry; nearest entry $(Show-Name $near.Name) ($($near.Where)): $(Get-Difference $near $fact)")
            }
        }
        elseif ($paired -lt $group.Count) {
            $names = ($group | ForEach-Object { "$($_.Name) ($($_.Where))" }) -join ', '
            $drift.Add("$($group.Count) Facts share one pattern set but only $paired twinned entries carry it: $names")
        }
    }
    foreach ($key in $entryGroups.Keys) {
        if ($factGroups.ContainsKey($key)) { continue }
        foreach ($entry in $entryGroups[$key]) {
            $near = Get-Nearest $entry $facts
            $drift.Add("yaml entry $(Show-Name $entry.Name) ($($entry.Where)) pairs with no Fact; nearest Fact $($near.Name) ($($near.Where)): $(Get-Difference $entry $near)")
        }
    }
    return , $drift.ToArray()
}

# ---- Main ----

try {
    $expected = $null
    if ($ExpectFacts.Length -gt 0) {
        $parsed = 0
        if (-not [int]::TryParse($ExpectFacts, [ref]$parsed)) { Stop-Reading '-ExpectFacts' "not a whole number: $(Show $ExpectFacts)" }
        $expected = $parsed
    }

    $facts = Read-Facts
    $yaml = Read-Entries
    $entries = $yaml.Entries
    if ($facts.Count -eq 0) { Stop-Reading $CsRel 'zero [Fact]s found' }
    if ($entries.Count -eq 0) { Stop-Reading $YamlRel 'zero twinned entries found' }
    if ($null -ne $expected -and $facts.Count -ne $expected) {
        Stop-Reading $CsRel "the reader found $($facts.Count) Facts where the test class has $expected; a Fact the reader cannot see is a Fact nobody compares"
    }
    Add-Normalised $facts $false
    Add-Normalised $entries $true

    # Self-test: the comparator must see one pattern dropped from the first twinned entry.
    $first = $entries[0]
    $mutant = [pscustomobject]@{ Name = $first.Name; Where = $first.Where; Patterns = @($first.Patterns | Select-Object -Skip 1) }
    Add-Normalised @($mutant) $true
    $mutated = @($mutant) + @($entries | Select-Object -Skip 1)
    if ((Compare-Copies $facts $mutated).Count -eq 0) {
        Stop-Reading 'self-test' "dropping one pattern from $(Show-Name $first.Name) ($($first.Where)) did not register as drift"
    }

    $patternCount = ($facts | ForEach-Object { $_.Patterns.Count } | Measure-Object -Sum).Sum
    $drift = Compare-Copies $facts $entries
    if ($drift.Count -eq 0) {
        Write-Output ("rules-parity: AGREE $($facts.Count) facts <- $($entries.Count) twinned entries, $patternCount patterns; " +
            "$($yaml.Settled) settled entries not compared (patterns only; engine behaviour is rules-harness)")
        exit 0
    }
    Write-Output "rules-parity: DRIFT $($drift.Count) unpaired ($($facts.Count) facts, $($entries.Count) twinned entries, $patternCount patterns)"
    $drift | Select-Object -First $MaxShown | ForEach-Object { Write-Output "  $_" }
    if ($drift.Count -gt $MaxShown) { Write-Output "  ... and $($drift.Count - $MaxShown) more" }
    exit 1
}
catch {
    Write-Output "rules-parity: CANNOT-READ $(Show $_.Exception.Message)"
    exit 2
}
