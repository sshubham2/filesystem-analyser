using System.Text.Json;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The proof-run cases' pure helpers (tests/window/WindowCases.ps1, SL-003): W-001's stopped-run comparison, its record,
/// W-002's hydration tally and W-003's timing. Each is read from the script's own parse and loaded by name under pwsh, as
/// W001OracleTests loads the oracle, so these tests reach the code the cases run and never a copy of it. None of them
/// scans a drive, registers a sync root or maps a share: those are the cases themselves, which a person runs.
/// </summary>
public class W001ProofTests
{
    private static async Task<string> Run(string[] functions, string body)
    {
        string loaded = string.Concat(functions.Select(WindowCasesHelperTests.Load));
        return await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + loaded + body);
    }

    private static string[] Lines(string output, string prefix) =>
        [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal))];

    // ---- Compare-StoppedRows ----

    /// <summary>
    /// A stopped run's rows against the finished run's, one class each (design review M1): a 995 row; a code-5 row the finished
    /// tab lacks for a folder that was there before the run began; the same created or written or changed after it began, and a
    /// folder created before and changed after (a change time moves with an ACL change, a write time does not); a code other
    /// than 5 absent from the finished tab; a row whose times cannot be read; the same path with another code; and a code-5 row
    /// whose kind changed. The paths are matched with their case ignored.
    /// </summary>
    [Fact]
    public async Task AStoppedRowIsPutInOneOfTheSixClasses()
    {
        string output = await Run(["Compare-StoppedRows"], """
            $start = [DateTime]::UtcNow
            $old = $start.AddHours(-2)
            $new = $start.AddMinutes(5)
            function Row($path, $kind, $code) { [pscustomobject]@{ path = $path; kind = $kind; code = $code } }
            $finished = @(
                (Row 'X:\same' 'NothingListed' 5), (Row 'X:\kindchange' 'NothingListed' 5),
                (Row 'X:\codediff' 'NothingListed' 32), (Row 'X:\codeflip' 'NothingListed' 5))
            $stopped = @(
                (Row 'x:\SAME' 'NothingListed' 5),
                (Row 'X:\kindchange' 'ListedPartway' 5),
                (Row 'X:\codediff' 'NothingListed' 5),
                (Row 'X:\codeflip' 'NothingListed' 59),
                (Row 'X:\cut' 'NothingListed' 995),
                (Row 'X:\absent-old' 'NothingListed' 5),
                (Row 'X:\absent-created-after' 'NothingListed' 5),
                (Row 'X:\absent-changed-after' 'NothingListed' 5),
                (Row 'X:\absent-unreadable' 'NothingListed' 5),
                (Row 'X:\absent-other-code' 'NothingListed' 59))
            $timesOf = {
                param($path)
                switch -wildcard ($path) {
                    '*absent-created-after' { [pscustomobject]@{ Read = $true; CreationUtc = $new; LastWriteUtc = $old; ChangeUtc = $new } }
                    '*absent-changed-after' { [pscustomobject]@{ Read = $true; CreationUtc = $old; LastWriteUtc = $old; ChangeUtc = $new } }
                    '*absent-unreadable' { [pscustomobject]@{ Read = $false } }
                    default { [pscustomobject]@{ Read = $true; CreationUtc = $old; LastWriteUtc = $old; ChangeUtc = $old } }
                }
            }
            $c = Compare-StoppedRows $finished $stopped $start $timesOf
            "counts matched=$($c.Matched) aborted=$($c.Aborted) notInFinished=$($c.NotInFinished) newSince=$($c.NewSinceFinished) other=$($c.OtherCodeDiffers) codeChanged=$($c.CodeChanged) offenders=$($c.Offenders.Count)"
            """);

        Assert.Contains("counts matched=1 aborted=1 notInFinished=1 newSince=2 other=4 codeChanged=1 offenders=7", output);
    }

    /// <summary>
    /// The verdict (design review M1, m3): failed on a 995 row, a code-5 row the finished tab lacks or a code-5 row of another
    /// kind; inconclusive on a difference the machine can explain and when the run was not partial; held with only rows that are
    /// new since the finished run began.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0, 0, 0, "$true", "failed")]
    [InlineData(0, 1, 0, 0, 0, "$true", "failed")]
    [InlineData(0, 0, 0, 1, 0, "$true", "failed")]
    [InlineData(1, 0, 0, 0, 0, "$false", "failed")]
    [InlineData(0, 0, 0, 0, 1, "$true", "inconclusive")]
    [InlineData(0, 0, 0, 0, 0, "$false", "inconclusive")]
    [InlineData(0, 0, 7, 0, 0, "$true", "held")]
    [InlineData(0, 0, 0, 0, 0, "$true", "held")]
    public async Task AStoppedRunsVerdictFollowsTheClasses(int aborted, int notInFinished, int newSince, int codeChanged, int other, string partial, string verdict)
    {
        string output = await Run(["Get-StoppedVerdict"],
            $"$c = [pscustomobject]@{{ Aborted = {aborted}; NotInFinished = {notInFinished}; NewSinceFinished = {newSince}; CodeChanged = {codeChanged}; OtherCodeDiffers = {other} }}; " +
            $"'verdict ' + (Get-StoppedVerdict $c {partial})");

        Assert.Contains($"verdict {verdict}", output);
    }

    // ---- Read-ActedLine and the partial rule ----

    /// <summary>
    /// The hook's acted line in both of its forms (the progress trigger's, whose 'atfolders=' comes before 'folders=', and the
    /// armed form's, whose ms is followed by sincearmed=), read with a leading space so that the trigger is not taken for the
    /// count, and from the end of the line for the time, so that a path in current= cannot supply either (design review m3).
    /// A run is partial only when it ended Cancelled with sized '-'.
    /// </summary>
    [Fact]
    public async Task TheActedLineIsReadInBothFormsAndPartialMeansStoppedWhileListing()
    {
        string output = await Run(["Read-ActedLine", "Test-StoppedPartial"], """
            function Show($line, $status) {
                $a = Read-ActedLine @('hook noise', $line)
                if ($null -eq $a) { return "acted none partial=$(Test-StoppedPartial $status $a)" }
                "acted folders=$($a.Folders) sized=$($a.Sized) ms=$($a.Ms) partial=$(Test-StoppedPartial $status $a)"
            }
            'spike ' + (Show 'acted mode=stop atfolders=15151 folders=15202 sized=- current=X:\b148\s026 ms=2289 utc=2026-11-17T19:05:45.0595231Z' 'Cancelled')
            'sizing ' + (Show 'acted mode=stop atfolders=100 folders=30302 sized=1200 current=Reading each file''s own size: 1200 ms=5000 utc=2026-11-17T19:05:45.0595231Z' 'Cancelled')
            'completed ' + (Show 'acted mode=stop atfolders=100 folders=150 sized=- current=X:\a ms=900 utc=2026-11-17T19:05:45Z' 'Completed')
            'armed ' + (Show 'acted mode=stop enabled=True visible=True ms=1234 sincearmed=2000' 'Cancelled')
            'pathy ' + (Show 'acted mode=stop atfolders=7 folders=9 sized=- current=X:\a folders=5 b ms=1 utc=2 ms=777 utc=2026-11-17T19:05:45Z' 'Cancelled')
            'none ' + (Show 'refused mode=stop reason=the scan ended before the trigger' 'Cancelled')
            """);

        Assert.Contains("spike acted folders=15202 sized= ms=2289 partial=True", output);
        Assert.Contains("sizing acted folders=30302 sized=1200 ms=5000 partial=False", output);
        Assert.Contains("completed acted folders=150 sized= ms=900 partial=False", output);
        Assert.Contains("armed acted folders= sized= ms=1234 partial=False", output);
        Assert.Contains("pathy acted folders=9 sized= ms=777 partial=True", output);
        Assert.Contains("none acted none partial=False", output);
    }

    // ---- Get-ReasonCodeMismatches ----

    /// <summary>
    /// C4: a row whose reason ends '(Windows error N)' with an N that is not its own code, and two rows with one code and
    /// different reasons, are counted; rows whose reasons belong to their codes are not.
    /// </summary>
    [Fact]
    public async Task AReasonThatDoesNotBelongToItsCodeIsCounted()
    {
        string output = await Run(["Get-ReasonCodeMismatches"], """
            function Row($code, $reason) { [pscustomobject]@{ code = $code; reason = $reason } }
            'clean ' + (Get-ReasonCodeMismatches @((Row 5 'access denied'), (Row 5 'access denied'), (Row 363 'The cloud file metadata is corrupt and unreadable. (Windows error 363)'), (Row 59 'network or share error')))
            'named ' + (Get-ReasonCodeMismatches @((Row 5 'access denied'), (Row 5 'The cloud file metadata is corrupt and unreadable. (Windows error 363)')))
            'twice ' + (Get-ReasonCodeMismatches @((Row 5 'access denied'), (Row 5 'in use by another program'), (Row 5 'access denied')))
            'both ' + (Get-ReasonCodeMismatches @((Row 32 'in use by another program'), (Row 32 'x (Windows error 5)')))
            'none ' + (Get-ReasonCodeMismatches @())
            """);

        Assert.Contains("clean 0", output);
        Assert.Contains("named 1", output);
        Assert.Contains("twice 1", output);
        Assert.Contains("both 1", output);
        Assert.Contains("none 0", output);
    }

    // ---- A whole drive's report ----

    /// <summary>
    /// Criterion 1: a whole local drive's ending must be 'completed', carry the coverage figures and show the coverage panel;
    /// each one missing is a gap, so a W-001 that never looked at coverage cannot pass a run on a machine where the panel
    /// shows (a report with coverage null, one with the panel hidden, one that was stopped, and one that lacks all three).
    /// </summary>
    [Fact]
    public async Task AWholeDriveReportMustBeCompletedAndShowItsCoveragePanel()
    {
        string output = await Run(["Show", "Get-WholeDriveReportGaps"], """
            function Report($outcome, $coverage, $visible) {
                [pscustomobject]@{ outcome = $outcome; coverage = $coverage; window = [pscustomobject]@{ CoveragePanel = [pscustomobject]@{ visible = $visible } } }
            }
            $cover = [pscustomobject]@{ volumeInUse = 5 }
            function Gaps($r) { @(Get-WholeDriveReportGaps $r).Count }
            "shown " + (Gaps (Report 'completed' $cover $true))
            "nocoverage " + (Gaps (Report 'completed' $null $true))
            "hidden " + (Gaps (Report 'completed' $cover $false))
            "cancelled " + (Gaps (Report 'cancelled' $cover $true))
            "missing " + (Gaps (Report $null $null $null))
            "text " + ((Get-WholeDriveReportGaps (Report 'cancelled' $null $false)) -join ' | ')
            """);

        Assert.Contains("shown 0", output);
        Assert.Contains("nocoverage 1", output);
        Assert.Contains("hidden 1", output);
        Assert.Contains("cancelled 1", output);
        Assert.Contains("missing 3", output);
        Assert.Contains("text the outcome is 'cancelled', not 'completed' | the report carries no coverage | the coverage panel is not on screen", output);
    }

    /// <summary>The scripts that read the earlier project's slice records, which the constraint retires, are not in the tree.</summary>
    [Theory]
    [InlineData("O-030-ledger-discharge.Tests.ps1")]
    [InlineData("SL-062-LedgerCases.ps1")]
    [InlineData("SL-068-DecisionCases.ps1")]
    [InlineData("SL-107-HarnessPlantsOverRecord.Tests.py")]
    [InlineData("SL-107-HandOffCases.ps1")]
    public void TheScriptsThatReadTheEnginesSliceRecordsAreRetired(string script)
    {
        Assert.False(File.Exists(Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "bugs", script)), $"tests/bugs/{script} is still there");
    }

    // ---- The oracle's verdict ----

    /// <summary>
    /// Design review M2, C4: a code-5 row whose folder now fails the plain open with another code is counted with the leaks, so
    /// the verdict is failed whenever there is one, with the controls behaving or not, and held only with none.
    /// </summary>
    [Fact]
    public async Task ARowRefusedWithAnotherCodeFailsTheRun()
    {
        string output = await Run(["Get-LeakCount", "Get-W001Verdict"], """
            $controls = [pscustomobject]@{ Openable = $true; Denied = $true; RefusedThenListed = $true }
            $none = @{ 'refused' = 4; 'changed-since-scan' = 2 }
            $another = @{ 'refused' = 4; 'refused-another-code' = 1 }
            $leak = @{ 'leak' = 1 }
            'none ' + (Get-LeakCount $none) + ' ' + (Get-W001Verdict $controls (Get-LeakCount $none) 0)
            'another ' + (Get-LeakCount $another) + ' ' + (Get-W001Verdict $controls (Get-LeakCount $another) 0)
            'leak ' + (Get-LeakCount $leak) + ' ' + (Get-W001Verdict $controls (Get-LeakCount $leak) 0)
            'both ' + (Get-LeakCount @{ 'leak' = 1; 'refused-another-code' = 2 })
            """);

        Assert.Contains("none 0 held", output);
        Assert.Contains("another 1 failed", output);
        Assert.Contains("leak 1 failed", output);
        Assert.Contains("both 3", output);
    }

    // ---- The record ----

    private sealed record StringRow(string Id, string Kind, string Before, string? Value, string? Shape, string? DriveType);

    /// <summary>
    /// The class of strings the writer refused only after the run (design review m1 and n3), as a corpus,
    /// tests/window/W001RecordStringsCorpus.json: each value is put through the writer as the cases built their records before
    /// SL-003 (naive) and as they build them now (fixed). The rows the old way lost are those with before 'refused'; every row
    /// is written the new way, and a root's shape is the one the corpus names.
    /// </summary>
    [Fact]
    public async Task EveryStringACaseWouldPutInARecordIsWritten()
    {
        string corpusFile = Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "window", "W001RecordStringsCorpus.json");
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(corpusFile));
        string[] beforeAllowed = [.. corpus.RootElement.GetProperty("before_allowed").EnumerateArray().Select(e => e.GetString()!)];
        StringRow[] rows = [.. corpus.RootElement.GetProperty("rows").EnumerateArray().Select(r => new StringRow(
            r.GetProperty("id").GetString()!, r.GetProperty("kind").GetString()!,
            r.TryGetProperty("before", out JsonElement b) ? b.GetString()! : "",
            r.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
            r.TryGetProperty("shape", out JsonElement s) ? s.GetString() : null,
            r.TryGetProperty("driveType", out JsonElement d) ? d.GetString() : null))];
        Assert.True(rows.Length >= 25, "the corpus is a table of the class, not a sample");
        Assert.Contains(rows, r => r.Kind == "reason");
        Assert.Contains(rows, r => r.Kind == "text");
        Assert.Contains(rows, r => r.Kind == "label");
        Assert.Contains(rows, r => r.Kind == "shape");
        Assert.Contains(rows, r => r.Kind == "coverage");

        string output = await Run(["Write-W001Record", "Get-RecordByCode", "Get-RecordString", "Get-W001RootShape", "Get-CountsOnly", "Test-CountsOnlyKept", "Get-RoundedOrNull", "Get-CoverageRecord"],
            $"$corpus = Get-Content -LiteralPath '{corpusFile.Replace("'", "''")}' -Raw | ConvertFrom-Json; " + """
            $dir = Join-Path ([System.IO.Path]::GetTempPath()) ('w001-strings-' + [guid]::NewGuid().ToString('N'))
            [void][System.IO.Directory]::CreateDirectory($dir)
            function TryWrite($record, $reasons) {
                try { Write-W001Record (Join-Path $dir 'record.json') $record $reasons; 'written' } catch { 'refused' }
            }
            try {
                foreach ($row in $corpus.rows) {
                    $naive = '-'; $fixed = '-'; $shape = '-'
                    switch ($row.kind) {
                        'reason' {
                            $reason = [string]$row.value
                            $naive = TryWrite ([ordered]@{ byCode = @([ordered]@{ code = 1; count = 1; reason = $reason }) }) @($reason)
                            $fixed = TryWrite ([ordered]@{ byCode = (Get-RecordByCode @([pscustomobject]@{ code = 1; reason = $reason })) }) @($reason)
                        }
                        'text' {
                            $naive = TryWrite ([ordered]@{ status = [string]$row.value }) @()
                            $fixed = TryWrite ([ordered]@{ status = (Get-RecordString $row.value) }) @()
                        }
                        'label' {
                            $naive = TryWrite ([ordered]@{ label = [string]$row.value }) @()
                            $fixed = $naive
                        }
                        'shape' {
                            $type = [string]$row.driveType
                            $shape = Get-W001RootShape ([string]$row.value) ([scriptblock]::Create("param(`$letter) '$type'"))
                            $fixed = TryWrite ([ordered]@{ rootShape = $shape }) @()
                        }
                        'coverage' {
                            $naive = TryWrite ([ordered]@{ coverage = ([string]$row.value | ConvertFrom-Json -AsHashtable) }) @()
                            $report = [pscustomobject]@{ coverage = ([string]$row.value | ConvertFrom-Json); window = [pscustomobject]@{ CoveragePanel = [pscustomobject]@{ top = 113.4 }; OutcomeTexts = [pscustomobject]@{ actualHeight = 76; verticalOffset = 0 } } }
                            $fixed = TryWrite ([ordered]@{ coverage = (Get-CoverageRecord $report) }) @()
                        }
                    }
                    "row $($row.id) naive=$naive fixed=$fixed shape=$shape"
                }
            }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force }
            """);

        var seen = new Dictionary<string, (string Naive, string Fixed, string Shape)>();
        foreach (string line in Lines(output, "row "))
        {
            string[] p = line.Split(' ');
            seen[p[1]] = (p[2]["naive=".Length..], p[3]["fixed=".Length..], p[4]["shape=".Length..]);
        }
        var wrong = new List<string>();
        foreach (StringRow row in rows)
        {
            if (!seen.TryGetValue(row.Id, out var got))
            {
                wrong.Add($"{row.Id}: not run");
                continue;
            }
            if (got.Fixed != "written")
                wrong.Add($"{row.Id}: the writer {got.Fixed} it as the cases build it now");
            string before = row.Kind switch
            {
                "label" => beforeAllowed.Contains(row.Value!, StringComparer.OrdinalIgnoreCase) ? "written" : "refused",
                "shape" => beforeAllowed.Contains(got.Shape, StringComparer.OrdinalIgnoreCase) ? "written" : "refused",
                _ => got.Naive,
            };
            if (row.Kind is "reason" or "text" or "coverage" && before != row.Before)
                wrong.Add($"{row.Id}: the value built as before was {before}, the corpus says {row.Before}");
            if (row.Kind == "shape" && got.Shape != row.Shape)
                wrong.Add($"{row.Id}: the shape is {got.Shape}, the corpus says {row.Shape}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong) + "\n" + output);

        // The class this closes is not empty: the writer before refused the rows below, so the first build lost a record to each.
        Assert.True(rows.Count(r => r.Kind is "reason" or "text" or "coverage" && r.Before == "refused") >= 5);
        Assert.Contains(seen, kv => kv.Value is { Shape: "unc" } && !beforeAllowed.Contains("unc", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>byCode is a JSON array whether it holds no code, one or two: a single code written as an object is a record nothing reads.</summary>
    [Fact]
    public async Task ByCodeIsAnArrayWhateverItsCount()
    {
        string output = await Run(["Get-RecordByCode"], """
            function Row($code) { [pscustomobject]@{ code = $code; reason = 'access denied' } }
            foreach ($rows in @(@(), @((Row 5)), @((Row 5), (Row 5)), @((Row 5), (Row 59)))) {
                $json = ([ordered]@{ byCode = (Get-RecordByCode $rows) } | ConvertTo-Json -Depth 4 -Compress)
                "json $json"
            }
            """);

        Assert.Contains("json {\"byCode\":[]}", output);
        Assert.Contains("json {\"byCode\":[{\"code\":5,\"count\":1,\"reason\":\"access denied\"}]}", output);
        Assert.Contains("json {\"byCode\":[{\"code\":5,\"count\":2,\"reason\":\"access denied\"}]}", output);
        Assert.Contains("json {\"byCode\":[{\"code\":5,\"count\":1,\"reason\":\"access denied\"},{\"code\":59,\"count\":1,\"reason\":\"access denied\"}]}", output);
    }

    /// <summary>
    /// A coverage object holding the panel's wording (remainder.text, measured text, size-not-read text), as the report has it,
    /// is kept as numbers, booleans and nulls alone, its folders as two counts, with where the panel stands as whole numbers;
    /// and the record is written, with no text key in it (design review m5, Q-001, Q-002).
    /// </summary>
    [Fact]
    public async Task ACoverageRecordHoldsNoStringAndIsWritten()
    {
        string output = await Run(["Write-W001Record", "Get-CountsOnly", "Test-CountsOnlyKept", "Get-RoundedOrNull", "Get-CoverageRecord"], """
            $report = @'
            {"coverage": {"volumeInUse": 100, "seenOnDisk": 60, "unseen": 40,
              "folders": [{"kind": "not-scanned", "size": null}, {"kind": "unreadable", "size": null}, {"kind": "unreadable", "size": null}],
              "measured": [{"id": "mft", "bytes": 10, "text": "The $MFT, 10 bytes"}],
              "remainder": {"bytes": 30, "includesStore": true, "text": "Space an ordinary account cannot read: C:\\System Volume Information"},
              "sizeNotRead": {"files": 2, "bytes": 5, "text": "2 files"}},
             "window": {"CoveragePanel": {"visible": true, "actualHeight": 90, "top": 113.6}, "OutcomeTexts": {"actualHeight": 76.2, "extentHeight": 300, "verticalOffset": 0}}}
            '@ | ConvertFrom-Json
            $cover = Get-CoverageRecord $report
            $file = Join-Path ([System.IO.Path]::GetTempPath()) ('w001-cover-' + [guid]::NewGuid().ToString('N') + '.json')
            try {
                Write-W001Record $file ([ordered]@{ coverage = $cover }) @()
                $text = [System.IO.File]::ReadAllText($file)
                $back = $text | ConvertFrom-Json
                "keys " + (($back.coverage.PSObject.Properties | ForEach-Object Name) -join ',')
                "folders unreadable=$($back.coverage.folders.unreadable) notScanned=$($back.coverage.folders.notScanned)"
                "top $($back.coverage.panelTop) height $($back.coverage.textsHeight) offset $($back.coverage.textsOffset)"
                "remainder " + (($back.coverage.remainder.PSObject.Properties | ForEach-Object Name) -join ',')
                "measured " + (($back.coverage.measured[0].PSObject.Properties | ForEach-Object Name) -join ',')
                "texts " + ([regex]::Matches($text, '"text"')).Count + " separators " + ([regex]::Matches($text, '[\\/]')).Count
            }
            finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
            "none " + ($null -eq (Get-CoverageRecord ([pscustomobject]@{ coverage = $null })))
            """);

        Assert.Contains("keys volumeInUse,seenOnDisk,unseen,folders,measured,remainder,sizeNotRead,panelTop,textsHeight,textsOffset", output);
        Assert.Contains("folders unreadable=2 notScanned=1", output);
        Assert.Contains("top 114 height 76 offset 0", output);
        Assert.Contains("remainder bytes,includesStore", output);
        Assert.Contains("measured bytes", output);
        Assert.Contains("texts 0 separators 0", output);
        Assert.Contains("none True", output);
    }

    /// <summary>
    /// Design review m2 and n1: the records a run with no report leaves are still written. A stopped part from a run that timed
    /// out (no report, no acted line, no rows, exit TIMEOUT) has null status and outcome and verdict 'failed'; W-002's record from
    /// an app that left no report has null status, outcome and unreadable count; both pass the writer.
    /// </summary>
    [Fact]
    public async Task ARecordFromARunWithNoReportIsWritten()
    {
        string output = await Run(["Write-W001Record", "Get-RecordString", "New-StoppedPart", "New-HydrationRecord"], """
            $file = Join-Path ([System.IO.Path]::GetTempPath()) ('w001-noreport-' + [guid]::NewGuid().ToString('N') + '.json')
            try {
                $run = [pscustomobject]@{ Exit = 'TIMEOUT'; Report = $null; Log = @() }
                $part = New-StoppedPart $run $null $null $null 15151 2 'failed'
                Write-W001Record $file ([ordered]@{ commit = (Get-RecordString ''); stopped = $part }) @()
                $back = [System.IO.File]::ReadAllText($file) | ConvertFrom-Json
                "stopped status=$($null -eq $back.stopped.status) outcome=$($null -eq $back.stopped.outcome) verdict=$($back.stopped.verdict) atFolders=$($back.stopped.atFolders) attempts=$($back.stopped.attempts) commit=$($null -eq $back.commit)"

                $tally = [pscustomobject]@{ OnlineOnlyBefore = 0; OnlineOnlyAfter = 0; Hydrated = 0; GoneSinceBefore = 0 }
                $record = New-HydrationRecord '' $true $null 'TIMEOUT' 0 0 $tally 0 'failed'
                Write-W001Record $file $record @()
                $back = [System.IO.File]::ReadAllText($file) | ConvertFrom-Json
                "hydration status=$($null -eq $back.status) outcome=$($null -eq $back.outcome) exit=$($null -eq $back.exit) unreadable=$($null -eq $back.unreadable) source=$($back.source) verdict=$($back.verdict)"
                "keys " + (($record.Keys | Where-Object { $_ -match 'online.?only.*before' }) -join ',') + ' / ' + (($record.Keys | Where-Object { $_ -match 'online.?only.*after' }) -join ',') + ' / ' + (($record.Keys | Where-Object { $_ -match 'hydrat' }) -join ',')
            }
            finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
            """);

        Assert.Contains("stopped status=True outcome=True verdict=failed atFolders=15151 attempts=2 commit=True", output);
        Assert.Contains("hydration status=True outcome=True exit=True unreadable=True source=rig verdict=failed", output);
        Assert.Contains("keys onlineOnlyBefore / onlineOnlyAfter / hydrated", output);
    }

    /// <summary>Every -Network and -Link value the script accepts is a label the record writer accepts (design review m1).</summary>
    [Fact]
    public async Task EveryNetworkAndLinkValueIsAcceptedByTheWriter()
    {
        string output = await Run(["Write-W001Record"], """
            $file = Join-Path ([System.IO.Path]::GetTempPath()) ('w001-labels-' + [guid]::NewGuid().ToString('N') + '.json')
            try {
                foreach ($param in @($script.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -in 'Network', 'Link' })) {
                    foreach ($attribute in $param.Attributes) {
                        if ($attribute.TypeName.Name -ne 'ValidateSet') { continue }
                        foreach ($argument in $attribute.PositionalArguments) {
                            $value = [string]$argument.Value
                            $result = try { Write-W001Record $file ([ordered]@{ label = $value }) @(); 'written' } catch { 'refused' }
                            "label $($param.Name.VariablePath.UserPath) $value $result"
                        }
                    }
                }
            }
            finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
            """);

        string[] labels = Lines(output, "label ");
        Assert.Equal(["label Network home written", "label Network tailscale written", "label Link wifi-off written", "label Link tunnel-down written", "label Link server-asleep written"],
            labels.OrderBy(l => Array.IndexOf(new[] { "Network home", "Network tailscale", "Link wifi-off", "Link tunnel-down", "Link server-asleep" }, string.Join(' ', l.Split(' ').Skip(1).Take(2)))));
    }

    // ---- Root shapes and where records are kept ----

    /// <summary>
    /// W-001's root is one of four shapes, and only a drive letter is kept under docs/proof-runs, named by its letter alone:
    /// 'C:\' and 'C:' are the local letter, a mapped letter is a network letter (stubbed here, as a default-suite test must not
    /// ask this PC's Z:), a UNC share root is 'unc', and a folder is a folder. No root text becomes a file name.
    /// </summary>
    [Fact]
    public async Task RootsAreClassedAndKeptWhereTheirShapeSays()
    {
        string output = await Run(["Get-W001RootShape", "Get-ProofRecordPath"], """
            $fixed = { param($letter) 'Fixed' }
            $network = { param($letter) 'Network' }
            function Show($root, $type) { $s = Get-W001RootShape $root $type; $p = (Get-ProofRecordPath 'R:\repo' 'W-001' $s $root).Replace('R:\repo\', ''); "shape [$root] $s $p" }
            Show 'C:\' $fixed
            Show 'C:' $fixed
            Show 'y:' $fixed
            Show 'Z:\' $network
            Show '\\srv\share\' $fixed
            Show '\\srv\share\folder' $fixed
            Show 'C:\Users' $fixed
            Show 'D:\data\x' $fixed
            "rig " + (Get-ProofRecordPath 'R:\repo' 'W-002' 'folder' '' 'rig').Replace('R:\repo\', '')
            "onedrive " + (Get-ProofRecordPath 'R:\repo' 'W-002' 'folder' '' 'onedrive').Replace('R:\repo\', '')
            "share " + (Get-ProofRecordPath 'R:\repo' 'W-003' 'folder' '' 'home-wifi-off').Replace('R:\repo\', '')
            foreach ($bad in 'C:\x', '..\evil', 'Upper', '', 'a b') {
                $r = try { Get-ProofRecordPath 'R:\repo' 'W-003' 'folder' '' $bad; 'accepted' } catch { 'refused' }
                "variant [$bad] $r"
            }
            "other " + $(try { Get-ProofRecordPath 'R:\repo' 'N-005' 'folder' '' 'rig'; 'accepted' } catch { 'refused' })
            """);

        Assert.Contains(@"shape [C:\] local-letter docs\proof-runs\w001-c-drive.json", output);
        Assert.Contains(@"shape [C:] local-letter docs\proof-runs\w001-c-drive.json", output);
        Assert.Contains(@"shape [y:] local-letter docs\proof-runs\w001-y-drive.json", output);
        Assert.Contains(@"shape [Z:\] network-letter docs\proof-runs\w001-z-drive.json", output);
        Assert.Contains(@"shape [\\srv\share\] unc artifacts\w001-run.json", output);
        Assert.Contains(@"shape [\\srv\share\folder] folder artifacts\w001-run.json", output);
        Assert.Contains(@"shape [C:\Users] folder artifacts\w001-run.json", output);
        Assert.Contains(@"shape [D:\data\x] folder artifacts\w001-run.json", output);
        Assert.Contains(@"rig docs\proof-runs\w002-rig.json", output);
        Assert.Contains(@"onedrive docs\proof-runs\w002-onedrive.json", output);
        Assert.Contains(@"share docs\proof-runs\w003-home-wifi-off.json", output);
        foreach (string bad in new[] { @"C:\x", @"..\evil", "Upper", "", "a b" })
            Assert.Contains($"variant [{bad}] refused", output);
        Assert.Contains("other refused", output);
    }

    /// <summary>
    /// W-001, W-002 and W-003 write under docs/proof-runs, and W-002 registers a sync root: -Case All, with -IncludeCloud and
    /// -IncludeNetwork or without, never runs them. They are in the network and cloud lists all the same, so that their own
    /// switches gate them when they are named.
    /// </summary>
    [Fact]
    public async Task ProofCasesAreNeverRunByCaseAll()
    {
        // The script's own statements, evaluated: the three lists, then the '$cases = if ($Case -eq ''All'') ...' assignment for -Case All and for a named case.
        string output = await Run([], """
            foreach ($s in $script.EndBlock.Statements) {
                if ($s -is [System.Management.Automation.Language.AssignmentStatementAst] -and $s.Left.Extent.Text -in '$NetworkCases', '$CloudCases', '$ProofCases') { Invoke-Expression $s.Extent.Text }
            }
            $assigned = @($script.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and $_.Left.VariablePath.UserPath -eq 'cases' })
            if ($assigned.Count -ne 1) { 'no single $cases assignment'; exit 1 }
            "network " + ($NetworkCases -join ',')
            "cloud " + ($CloudCases -join ',')
            "proof " + ($ProofCases -join ',')
            $Case = 'All'; Invoke-Expression $assigned[0].Extent.Text
            "all " + ($cases -join ',')
            $Case = 'W-002'; Invoke-Expression $assigned[0].Extent.Text
            "single " + ($cases -join ',')
            """);

        Assert.Contains("proof W-001,W-002,W-003", output);
        Assert.Contains("W-003", Lines(output, "network ")[0]);
        Assert.Contains("W-002", Lines(output, "cloud ")[0]);
        string[] all = Lines(output, "all ")[0]["all ".Length..].Split(',');
        Assert.DoesNotContain("W-001", all);
        Assert.DoesNotContain("W-002", all);
        Assert.DoesNotContain("W-003", all);
        Assert.Contains("N-005", all);
        Assert.Contains("N-006", all);
        Assert.Contains("C-001", all);
        Assert.Contains("single W-002", output);
    }

    // ---- W-002 ----

    /// <summary>
    /// The tally of a scan that must hydrate nothing: a file still online-only; one hydrated; one gone since the first listing,
    /// which is no hydration; and a root with no online-only file at all, which proves nothing. The verdict, in the order of
    /// design review n1: no report or a non-zero exit fails; a scan not Completed, a root with nothing online-only, a scan that
    /// left folders unreadable and (on the rig) a file gone are inconclusive; a hydration or a provider fetch from the app fails.
    /// Data fetches are the provider's FETCH_DATA lines from the app's own pid.
    /// </summary>
    [Fact]
    public async Task AScanIsJudgedAgainstTheOnlineOnlyFilesBeforeAndAfter()
    {
        string output = await Run(["Get-HydrationTally", "Get-HydrationVerdict", "Get-DataFetches"], """
            $online = 0x400000 -bor 0x20
            $local = 0x20
            $before = @{ 'r\still' = $online; 'r\hydrated' = $online; 'r\gone' = $online; 'r\plain' = $local }
            $after = @{ 'r\still' = $online; 'r\hydrated' = $local; 'r\plain' = $local; 'r\new' = $online }
            $t = Get-HydrationTally $before $after
            "tally before=$($t.OnlineOnlyBefore) after=$($t.OnlineOnlyAfter) hydrated=$($t.Hydrated) gone=$($t.GoneSinceBefore)"
            $none = Get-HydrationTally @{ 'r\plain' = $local } @{ 'r\plain' = $local }
            "none before=$($none.OnlineOnlyBefore) hydrated=$($none.Hydrated)"
            $held = Get-HydrationTally @{ 'r\a' = $online } @{ 'r\a' = $online }
            $report = { param($status, $unreadable) [pscustomobject]@{ core = [pscustomobject]@{ status = $status; unreadable = $unreadable } } }
            "v noreport " + (Get-HydrationVerdict $held 0 '0' $null $true)
            "v timeout " + (Get-HydrationVerdict $held 0 'TIMEOUT' (& $report 'Completed' 0) $true)
            "v cancelled " + (Get-HydrationVerdict $held 0 '0' (& $report 'Cancelled' 0) $true)
            "v refused " + (Get-HydrationVerdict $held 0 '0' (& $report 'Refused' 0) $true)
            "v unreadable " + (Get-HydrationVerdict $held 0 '0' (& $report 'Completed' 3) $true)
            "v nothing " + (Get-HydrationVerdict $none 0 '0' (& $report 'Completed' 0) $true)
            "v hydrated " + (Get-HydrationVerdict $t 0 '0' (& $report 'Completed' 0) $true)
            "v fetched " + (Get-HydrationVerdict $held 2 '0' (& $report 'Completed' 0) $true)
            "v gone-rig " + (Get-HydrationVerdict ([pscustomobject]@{ OnlineOnlyBefore = 3; Hydrated = 0; GoneSinceBefore = 1 }) 0 '0' (& $report 'Completed' 0) $true)
            "v gone-real " + (Get-HydrationVerdict ([pscustomobject]@{ OnlineOnlyBefore = 3; Hydrated = 0; GoneSinceBefore = 1 }) $null '0' (& $report 'Completed' 0) $false)
            "v held " + (Get-HydrationVerdict $held 0 '0' (& $report 'Completed' 0) $true)
            "v held-real " + (Get-HydrationVerdict $held $null '0' (& $report 'Completed' 0) $false)
            $lines = @('READY', 'CB FETCH_DATA pid=4242 path=\r\a\f1.bin offset=0 length=4096', 'CB FETCH_DATA pid=777 path=\r\a\f2.bin offset=0 length=1', 'CB FETCH_PLACEHOLDERS pid=4242 path=\r\a', 'CB FETCH_DATA pid=42420 path=\r\b', 'ERR x hr=0x1')
            "fetches app=" + (Get-DataFetches $lines 4242) + " other=" + (Get-DataFetches $lines 777) + " none=" + (Get-DataFetches $lines 1) + " empty=" + (Get-DataFetches @() 4242)
            """);

        Assert.Contains("tally before=3 after=2 hydrated=1 gone=1", output);
        Assert.Contains("none before=0 hydrated=0", output);
        Assert.Contains("v noreport failed", output);
        Assert.Contains("v timeout failed", output);
        Assert.Contains("v cancelled inconclusive", output);
        Assert.Contains("v refused inconclusive", output);
        Assert.Contains("v unreadable inconclusive", output);
        Assert.Contains("v nothing inconclusive", output);
        Assert.Contains("v hydrated failed", output);
        Assert.Contains("v fetched failed", output);
        Assert.Contains("v gone-rig inconclusive", output);
        Assert.Contains("v gone-real held", output);
        Assert.Contains("v held held", output);
        Assert.Contains("v held-real held", output);
        Assert.Contains("fetches app=1 other=1 none=0 empty=0", output);
    }

    // ---- W-003 ----

    /// <summary>
    /// The time from Stop to the stopped ending is the 'rows read ms' minus the acted line's ms, one clock; a missing line, or a
    /// difference that is not above 0, is no time (never 0 or a negative one). The cut folders are every 995 row and, on the rig,
    /// every row for s, t or u whatever the time (design review n2). The verdict puts a missed marker deadline first, even when the
    /// app then timed out.
    /// </summary>
    [Fact]
    public async Task TheStopToEndingTimeAndTheCutFoldersAreRead()
    {
        string output = await Run(["Read-ActedLine", "Get-StopToEndingMs", "Get-CutFolderRows"], """
            $armed = 'acted mode=stop enabled=True visible=True ms=1000 sincearmed=2000'
            $progress = 'acted mode=stop atfolders=10 folders=12 sized=- current=X:\a ms=1000 utc=2026-11-17T19:05:45Z'
            $read = 'rows read count=2 notScanned=0 realized=1 selected=True ms=1750'
            "armed " + (Get-StopToEndingMs @('hook', $armed, '{"event":"row"}', $read))
            "progress " + (Get-StopToEndingMs @($progress, $read))
            "noacted [" + (Get-StopToEndingMs @('hook', '{"event":"row"}', $read)) + "]"
            "noread [" + (Get-StopToEndingMs @($armed, '{"event":"row"}')) + "]"
            "backwards [" + (Get-StopToEndingMs @($armed, 'rows read count=2 notScanned=0 realized=1 selected=True ms=900')) + "]"
            "equal [" + (Get-StopToEndingMs @($armed, 'rows read count=2 notScanned=0 realized=1 selected=True ms=1000')) + "]"
            "twice [" + (Get-StopToEndingMs @($armed, $read, $read)) + "]"
            function Row($path, $code) { [pscustomobject]@{ path = $path; code = $code } }
            $ab = @((Row 'Z:\rig\a' 5), (Row 'Z:\rig\b' 59))
            "clean rig=" + (Get-CutFolderRows $ab $true) + " real=" + (Get-CutFolderRows $ab $false)
            "s rig=" + (Get-CutFolderRows ($ab + (Row 'Z:\rig\s' 59)) $true) + " real=" + (Get-CutFolderRows ($ab + (Row 'Z:\rig\s' 59)) $false)
            "t rig=" + (Get-CutFolderRows ($ab + (Row 'Z:\rig\t' 59)) $true)
            "u rig=" + (Get-CutFolderRows ($ab + (Row 'Z:\rig\u\' 59)) $true)
            "995 rig=" + (Get-CutFolderRows ($ab + (Row 'Z:\rig\x' 995)) $true) + " real=" + (Get-CutFolderRows ($ab + (Row 'Z:\x' 995)) $false)
            "case rig=" + (Get-CutFolderRows ($ab + (Row 'Z:\rig\S' 59)) $true)
            """);

        Assert.Contains("armed 750", output);
        Assert.Contains("progress 750", output);
        Assert.Contains("noacted []", output);
        Assert.Contains("noread []", output);
        Assert.Contains("backwards []", output);
        Assert.Contains("equal []", output);
        Assert.Contains("twice []", output);
        Assert.Contains("clean rig=0 real=0", output);
        Assert.Contains("s rig=1 real=0", output);
        Assert.Contains("t rig=1", output);
        Assert.Contains("u rig=1", output);
        Assert.Contains("995 rig=1 real=1", output);
        Assert.Contains("case rig=0", output); // 'S' is not s: leaf names are matched exactly
    }

    [Theory]
    [InlineData("$true", "$true", "500", 0, "TIMEOUT", 0, "inconclusive")]
    [InlineData("$false", "$true", "500", 1, "0", 0, "failed")]
    [InlineData("$false", "$true", "500", 0, "0", 1, "failed")]
    [InlineData("$false", "$false", "$null", 0, "0", 0, "inconclusive")]
    [InlineData("$false", "$true", "$null", 0, "0", 0, "failed")]
    [InlineData("$false", "$true", "500", 0, "TIMEOUT", 0, "failed")]
    [InlineData("$false", "$true", "500", 0, "0", 0, "held")]
    public async Task AShareRunsVerdictPutsAMissedMarkerDeadlineFirst(string markerRefused, string judged, string ms, int cut, string exit, int failures, string verdict)
    {
        string output = await Run(["Get-ShareVerdict"], $"'verdict ' + (Get-ShareVerdict {markerRefused} {judged} {ms} {cut} '{exit}' {failures})");

        Assert.Contains($"verdict {verdict}", output);
    }

    // ---- The script itself ----

    /// <summary>
    /// The window harness names no path under the engine's slice records (the settled rule has the pattern; this is its
    /// run-time half, Q-010), and the records are kept under docs/proof-runs.
    /// </summary>
    [Fact]
    public void TheWindowHarnessKeepsItsRecordsUnderDocsProofRuns()
    {
        string text = File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "window", "WindowCases.ps1"));
        Assert.DoesNotContain("taad", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"docs\proof-runs", text);
    }
}
