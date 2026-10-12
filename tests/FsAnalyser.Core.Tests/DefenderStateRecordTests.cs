using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using static FsAnalyser.Core.Tests.DefenderState;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-101: the Controlled Folder Access line DefenderState records beside fsutil's in HeldOpenTreeTests. Describe is fed
/// fixed child outputs, and the runner is driven for real on the two ways a read can go wrong that no fixed output shows: a
/// child past its timeout and a program that will not start. None of it needs Defender, and none of it is skipped: on a
/// PC with no Defender the line says "not read", and so do these tests' real reads, which check the line, not the PC.
/// </summary>
/// <remarks>
/// Nothing here arms Controlled Folder Access, or reads the line as the answer to whether it blocks the scan's keep-last-access
/// open: that needs an administrator and a person (docs/design/SL-101-controlled-folder-access.md, Q-101).
/// </remarks>
public class DefenderStateRecordTests(ITestOutputHelper output)
{
    private const string Prefix = "Controlled Folder Access: ";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(TimeoutSeconds);

    // A child's whole output, as Windows PowerShell 5.1 wrote it on this PC, with each value replaceable by raw JSON text.
    private static string Stdout(string enable = "0", string protectedList = "null", string allowedList = "[\"C:\\\\Program Files\\\\App\\\\app.exe\"]",
        string mode = "\"Normal\"", string rtp = "true", string platform = "\"4.18.26080.4\"") =>
        $"{{\"enable\":{enable},\"protected\":{protectedList},\"allowed\":{allowedList},\"mode\":{mode},\"rtp\":{rtp},\"platform\":{platform}}}";

    private static string Described(string stdout) => Describe(0, stdout, "", false, null);

    private static string After(string line, string label, char end)
    {
        int start = line.IndexOf(label, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{label}' is not in: {line}");
        start += label.Length;
        return line[start..line.IndexOf(end, start)];
    }

    // ---- Criterion 1: the line, on the PC the tests run on -----------------------------------------------------

    /// <summary>
    /// The line carries what a fresh, separate read of the setting gives (0, off, on this PC), or says "not read" where
    /// there is no Defender to ask. The PC's setting is not asserted: it is a machine's, and the tests are never skipped.
    /// </summary>
    [Fact]
    public void TheLineAgreesWithAFreshReadOnThisPc()
    {
        string line = DefenderState.Line;
        ChildResult fresh = Run(PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; (Get-MpPreference).EnableControlledFolderAccess"], Wait);
        output.WriteLine(line);
        output.WriteLine($"a fresh read: exit {fresh.ExitCode}, timed out {fresh.TimedOut}, said '{fresh.Stdout.Trim()}' {fresh.Stderr.Trim()}");

        Assert.StartsWith(Prefix, line, StringComparison.Ordinal);
        if (fresh.StartException is null && !fresh.TimedOut && fresh.ExitCode == 0 && int.TryParse(fresh.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int setting))
        {
            Assert.StartsWith($"{Prefix}EnableControlledFolderAccess={setting} (", line, StringComparison.Ordinal);
            Assert.Matches(
                @"^Controlled Folder Access: EnableControlledFolderAccess=\d+ \(\w+\), protected folders=(\d+|hidden from non-administrators|\?), " +
                @"allowed apps=(\d+|hidden from non-administrators|\?); Defender AMRunningMode=\S+, RealTimeProtection=(True|False|\?), platform=\S+$", line);
        }
        else if (fresh.StartException is null && !fresh.TimedOut)
        {
            Assert.StartsWith(Prefix + "not read (", line, StringComparison.Ordinal);
        }
    }

    /// <summary>The line is written by HeldOpenTreeTests' record, on the line after the fsutil output it sits beside.</summary>
    [Fact]
    public void TheLineIsRecordedRightAfterTheFsutilLine()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\HeldOpenTreeTests.cs"));
        int fsutil = Array.FindIndex(lines, l => l.Contains("output.WriteLine($\"fsutil behavior query disablelastaccess", StringComparison.Ordinal));
        Assert.True(fsutil >= 0, "HeldOpenTreeTests no longer records fsutil's disablelastaccess line");
        Assert.Equal("output.WriteLine(DefenderState.Line);", lines[fsutil + 1].Trim());
    }

    // ---- The child's environment ----------------------------------------------------------------------------------

    /// <summary>
    /// testhost is started by eng/dotnet.ps1 under pwsh 7, so it inherits pwsh 7's PSModulePath. The child does not get it
    /// (it builds Windows PowerShell's own), seen here by what the child says its path is.
    /// </summary>
    [Fact]
    public void TheChildPowerShellStartsWithItsOwnModulePath()
    {
        output.WriteLine($"PSModulePath of this test process: {Environment.GetEnvironmentVariable("PSModulePath") ?? "(not set)"}");
        ChildResult child = Run(PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "$env:PSModulePath"], Wait);
        output.WriteLine($"PSModulePath of the child: {child.Stdout.Trim()}");

        Assert.Null(child.StartException);
        Assert.False(child.TimedOut);
        Assert.Equal(0, child.ExitCode);
        Assert.NotEmpty(child.Stdout.Trim());
        Assert.DoesNotMatch(@"(?i)(?<![a-z])PowerShell\\(7\\)?Modules", child.Stdout);
    }

    // ---- The lists --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void ANullOrEmptyListCountsAsZero(string list)
    {
        string line = Described(Stdout(protectedList: list, allowedList: list));
        Assert.Equal("0", After(line, "protected folders=", ','));
        Assert.Equal("0", After(line, "allowed apps=", ';'));
    }

    [Theory]
    [InlineData("\"C:\\\\Users\\\\Public\\\\Documents\\\\fsa-cfa-0\\\\R\"")]
    [InlineData("\"R\"")]
    public void AScalarListCountsAsOneNotAsItsLength(string list)
    {
        string line = Described(Stdout(protectedList: list, allowedList: list));
        Assert.Equal("1", After(line, "protected folders=", ','));
        Assert.Equal("1", After(line, "allowed apps=", ';'));
    }

    [Theory]
    [InlineData("[\"C:\\\\a\"]", "1")]
    [InlineData("[\"C:\\\\a\",\"C:\\\\b\"]", "2")]
    [InlineData("[\"C:\\\\a\",\"C:\\\\b\",\"D:\\\\c\"]", "3")]
    public void AnArrayListCountsItsLength(string list, string count)
    {
        string line = Described(Stdout(protectedList: list, allowedList: list));
        Assert.Equal(count, After(line, "protected folders=", ','));
        Assert.Equal(count, After(line, "allowed apps=", ';'));
    }

    // ---- The placeholder an ordinary user is given in place of a list (review M1, O-017) -------------------------

    public static TheoryData<string> CorpusIds()
    {
        var ids = new TheoryData<string>();
        foreach (JsonElement row in Corpus().GetProperty("rows").EnumerateArray())
            ids.Add(row.GetProperty("id").GetString()!);
        return ids;
    }

    /// <summary>
    /// Each row of DefenderStateCorpus.json as the value of both lists: the placeholder Defender gives a caller who is not an
    /// administrator is "hidden", never a count, and every real list keeps its count.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusIds))]
    public void EachShapeOfAListIsCountedOrCalledHiddenAsTheCorpusSays(string id)
    {
        JsonElement row = Corpus().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        string list = row.GetProperty("list").GetRawText();
        string expect = row.GetProperty("expect").GetString()!;

        string line = Described(Stdout(protectedList: list, allowedList: list));
        Assert.True(After(line, "protected folders=", ',') == expect, $"{id} ({row.GetProperty("note").GetString()}): protected folders read '{After(line, "protected folders=", ',')}', the corpus says '{expect}': {line}");
        Assert.True(After(line, "allowed apps=", ';') == expect, $"{id} ({row.GetProperty("note").GetString()}): allowed apps read '{After(line, "allowed apps=", ';')}', the corpus says '{expect}': {line}");
    }

    /// <summary>
    /// The corpus is what its note says: the counting rule the design handed to build is recomputed for every row, the rows it
    /// got wrong are the placeholder ones in both serialisations, and every real row is one it got right.
    /// </summary>
    [Fact]
    public void TheCorpusHoldsTheRowsTheDesignsCountingGotWrongBesideRowsItGotRight()
    {
        List<JsonElement> rows = [.. Corpus().GetProperty("rows").EnumerateArray()];
        Assert.Equal(rows.Count, rows.Select(r => r.GetProperty("id").GetString()).Distinct().Count());
        foreach (JsonElement row in rows)
        {
            string id = row.GetProperty("id").GetString()!;
            string kind = row.GetProperty("kind").GetString()!;
            Assert.Contains(kind, new[] { "placeholder", "real" });
            Assert.True(NaiveCount(row.GetProperty("list")) == row.GetProperty("naive").GetString(), $"{id}: the design's counting reads {NaiveCount(row.GetProperty("list"))}, the corpus says {row.GetProperty("naive").GetString()}");
            Assert.True(kind == "real" == (row.GetProperty("naive").GetString() == row.GetProperty("expect").GetString()),
                $"{id}: a {kind} row where the design's counting {(row.GetProperty("naive").GetString() == row.GetProperty("expect").GetString() ? "was right" : "was wrong")}");
        }

        foreach (JsonValueKind shape in new[] { JsonValueKind.String, JsonValueKind.Array })
        {
            Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "placeholder" && r.GetProperty("list").ValueKind == shape);
            Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "real" && r.GetProperty("list").ValueKind == shape);
        }
        Assert.Contains(rows, r => r.GetProperty("list").ValueKind == JsonValueKind.Null);
    }

    // The counting rule the design handed to build, kept here only to say what it got wrong: null 0, a string 1, an array its length.
    private static string NaiveCount(JsonElement list) => list.ValueKind switch
    {
        JsonValueKind.Null => "0",
        JsonValueKind.String => "1",
        _ => list.GetArrayLength().ToString(CultureInfo.InvariantCulture),
    };

    private static JsonElement Corpus()
    {
        string path = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\DefenderStateCorpus.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    // ---- The setting's names ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", "Disabled")]
    [InlineData("1", "Enabled")]
    [InlineData("2", "AuditMode")]
    [InlineData("3", "BlockDiskModificationOnly")]
    [InlineData("4", "AuditDiskModificationOnly")]
    public void TheModeIsNamedAndTheRawValueKept(string value, string name)
    {
        Assert.StartsWith($"{Prefix}EnableControlledFolderAccess={value} ({name}), ", Described(Stdout(enable: value)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("7", "7")]
    [InlineData("5", "5")]
    [InlineData("-1", "-1")]
    [InlineData("255", "255")]
    [InlineData("1.5", "1.5")]
    [InlineData("\"2\"", "\"2\"")]
    [InlineData("true", "true")]
    public void AnEnableValueOutsideTheKnownRangeIsKeptRawAndCalledUnknown(string value, string printed)
    {
        Assert.StartsWith($"{Prefix}EnableControlledFolderAccess={printed} (unknown), ", Described(Stdout(enable: value)), StringComparison.Ordinal);
    }

    // ---- What an older Defender may not report --------------------------------------------------------------------

    [Theory]
    [InlineData("enable", "EnableControlledFolderAccess=?,")]
    [InlineData("protected", "protected folders=?,")]
    [InlineData("allowed", "allowed apps=?;")]
    [InlineData("mode", "AMRunningMode=?,")]
    [InlineData("rtp", "RealTimeProtection=?,")]
    [InlineData("platform", "platform=?")]
    public void AMissingPropertyPrintsAQuestionMarkAndTheRestOfTheLineStays(string missing, string shown)
    {
        using JsonDocument whole = JsonDocument.Parse(Stdout());
        var kept = whole.RootElement.EnumerateObject().Where(p => p.Name != missing).ToDictionary(p => p.Name, p => p.Value.GetRawText());
        string line = Described("{" + string.Join(",", kept.Select(p => $"\"{p.Key}\":{p.Value}")) + "}");

        Assert.StartsWith(Prefix, line, StringComparison.Ordinal);
        Assert.Contains(shown, line, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(line, @"=\?"));
    }

    // ---- A read that fails is a line, never a failed test ------------------------------------------------------------

    [Theory]
    [InlineData(1, "Get-MpPreference : Operation failed with the following error: 0x800106ba\r\nAt line:1 char:1\r\n", "Get-MpPreference : Operation failed with the following error: 0x800106ba")]
    [InlineData(3, "\r\n  \r\nboom\r\nsecond line\r\n", "boom")]
    [InlineData(1, "The term 'Get-MpPreference' is not recognized as the name of a cmdlet", "The term 'Get-MpPreference' is not recognized as the name of a cmdlet")]
    [InlineData(-1, "", "exit code -1")]
    [InlineData(2, "  \r\n\r\n", "exit code 2")]
    public void ANonZeroExitIsNotReadWithTheFirstStderrLine(int exitCode, string stderr, string reason)
    {
        Assert.Equal($"{Prefix}not read ({reason})", Describe(exitCode, "", stderr, false, null));
        Assert.Equal($"{Prefix}not read ({reason})", Describe(exitCode, Stdout(), stderr, false, null));
    }

    [Fact]
    public void ANonZeroExitIsNotReadWithTheFirstStderrLineFromARealChild()
    {
        string line = Read(PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine('boom'); exit 3"], Wait);
        Assert.Equal($"{Prefix}not read (boom)", line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("WARNING: a profile banner\r\n{\"enable\":0}")]
    [InlineData("{\"enable\":0} trailing")]
    public void EmptyOrUnparsedOutputIsNotRead(string stdout)
    {
        Assert.Equal($"{Prefix}not read (unparsed output)", Describe(0, stdout, "", false, null));
    }

    [Fact]
    public void NullOutputIsNotReadEither()
    {
        Assert.Equal($"{Prefix}not read (unparsed output)", Describe(0, null, null, false, null));
        Assert.Equal($"{Prefix}not read (exit code 1)", Describe(1, null, null, false, null));
    }

    /// <summary>A child that runs past the timeout is killed and the call returns promptly (it would take 30 s if it waited).</summary>
    [Fact]
    public void AChildPastTheTimeoutIsKilledAndNotRead()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string line = Read(PowerShellPath, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 30"], TimeSpan.FromSeconds(1));
        output.WriteLine($"returned after {clock.ElapsedMilliseconds} ms: {line}");

        Assert.Equal($"{Prefix}not read (timed out after 1 s)", line);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the call took {clock.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void AnExecutableThatCannotBeStartedIsNotRead()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"fsa-no-such-program-{Guid.NewGuid():N}.exe");
        string line = Read(missing, ["-NoProfile"], TimeSpan.FromSeconds(5));
        output.WriteLine(line);

        Assert.StartsWith(Prefix + "not read (", line, StringComparison.Ordinal);
        Assert.Contains("Win32Exception", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartFailureAndTheTimeoutReachTheLineWhateverElseWasSaid()
    {
        Assert.Equal($"{Prefix}not read (timed out after 20 s)", Describe(0, Stdout(), "", true, null));
        Assert.StartsWith($"{Prefix}not read (InvalidOperationException: nope", Describe(0, Stdout(), "", false, new InvalidOperationException("nope")), StringComparison.Ordinal);
    }
}
