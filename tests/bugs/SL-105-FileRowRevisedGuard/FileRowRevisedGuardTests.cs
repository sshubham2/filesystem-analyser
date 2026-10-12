// Bug (SL-105, from the code review of SL-063, m1 / O-018): the file-row half of SL-063's Revised latch is a branch no test reaches.
//
//   TopLevelTracker settles a row's figures in two places (M2): FolderExited for a folder row, and Seed for a file or link row.
//   Build then latches Revised on any Done row whose figures differ from the settled ones. Every positive Revised assertion in
//   the suite is over a FOLDER row (TopLevelTests' HandDriven rows "a" and "b", and the "1-folder" rows of
//   AnInPlaceCorrectionOfAnEarlierNameIsCreditedToThatNamesRow). The watcher asserts only the negatives for a file row: it is
//   never Revised in the snapshot it settled in, and a row no correction reaches is never Revised. Adding
//   `r.Kind != EntryKind.File &&` to the latch at TopLevelTracker.Build keeps TopLevel* 24 of 24 and the SL-063 bug project
//   10 of 10 green (measured by the investigation), while a root-level file row that a probe corrects is then never marked.
//   The real trigger is NTFS's documented hardlink behaviour: a directory entry's size is updated only at the name the write
//   went through, so a root-level name is listed at its stale size, Seed settles its File row there, and the probe pass corrects
//   it through Moved (whose parent walk exits at once for a root child). C:\Windows' own root holds 14 hardlinked files in 37.
//
// Expected: the suite has a real-tree test for it. It hardlinks a name directly under the scan root to a file in a subfolder
//   (TempTree.Hardlink(tree.PathOf("a-stale.bin"), current)), grows the file through the subfolder's name (TempTree.Append),
//   scans (Watch with correctedRows ["a-stale.bin"]) and asserts that the File row is Done at the grown size and Revised, and
//   that the subfolder's row is not.
//
// Actual: no test in tests/FsAnalyser.Core.Tests builds a stale hardlink name at the root of a scan and asserts a File row
//   Revised, so a mutant that gates the latch by row kind survives the whole suite.
//
// Why this test reads sources and does not scan: the scanner is right today (a root-level stale hardlink File row scans to
// Done at 64 MiB with Revised true, 5 of 5 runs), so every scan-based assertion of the correct behaviour already passes; the
// defect is that nothing in the suite would notice if it stopped being right. This test therefore checks the suite itself, as
// SL-102's does: it reads every .cs under tests/FsAnalyser.Core.Tests, drops comments, cuts each file into one unit per
// [Fact]/[Theory] (from its attribute to the next test's attribute, so helpers after a test belong to it), and asks whether some
// unit has the shape the fix names. It passes once such a test exists.
//
// What it costs: it reads the .cs files of one test project (a few hundred KB, tens of files) and runs regexes over them, well
// under a second. It writes nothing, starts no process and touches nothing outside the repository.

using System.Text.RegularExpressions;

namespace SL105.FileRowRevisedGuard.Tests;

public class FileRowRevisedGuardTests
{
    private sealed record Unit(string File, string Text);

    // A strings-or-comments scanner: strings are kept (a "//" inside one is not a comment), comments are blanked.
    private static readonly Regex StringsAndComments = new(
        "(?:\\$@|@\\$|@)\"(?:[^\"]|\"\")*\"" // verbatim and interpolated-verbatim strings
        + "|\\$?\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"" // ordinary and interpolated strings
        + "|'(?:[^'\\\\\\r\\n]|\\\\.)+'" // char literals
        + "|//[^\\r\\n]*" // line comments (and doc comments)
        + "|/\\*.*?\\*/", // block comments
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TestAttribute = new(@"\[\s*\w*(?:Fact|Theory)\w*\b", RegexOptions.Compiled);

    // A real tree is scanned, through the watcher TopLevelTests uses or the public entry point.
    private static readonly Regex ScansARealTree = new(@"\bWatch\s*\(|\bScanner\s*\.\s*Scan\s*\(", RegexOptions.Compiled);

    // A hardlink whose NEW name sits directly under the scan root: tree.PathOf("name") or Path.Combine(root, "name"), the
    // name holding no separator. A name in a subfolder never reaches Moved's root-child exit, so it is not this branch.
    private static readonly Regex RootLevelHardlink = new(
        @"\bHardlink\s*\(\s*(?:\w+\.PathOf\(\s*@?""[^""\\/]+""\s*\)|Path\.Combine\(\s*[\w.]+\s*,\s*@?""[^""\\/]+""\s*\))",
        RegexOptions.Compiled);

    // The file is grown after the second name exists, which is what leaves the first name's directory entry stale.
    private static readonly Regex GrowsTheFile = new(@"\bAppend\s*\(|\bFileMode\s*\.\s*Append\b", RegexOptions.Compiled);

    // The row under test is a FILE row: named as EntryKind.File, or a file-like (dotted, separator-free) name handed to the
    // watcher as a row a correction may reach.
    private static readonly Regex FileRow = new(
        @"\bEntryKind\s*\.\s*File\b|\bcorrectedRows\s*:\s*\[\s*""[^""\\/]*\.[^""\\/]*""",
        RegexOptions.Compiled);

    private static readonly Regex RevisedWord = new(@"\bRevised\b", RegexOptions.Compiled);
    private static readonly Regex Asserts = new(@"\bAssert\.", RegexOptions.Compiled);
    private static readonly Regex AssertsFalse = new(@"\bAssert\s*\.\s*False\b", RegexOptions.Compiled);
    private static readonly Regex LowerTrue = new(@"\btrue\b", RegexOptions.Compiled);
    private static readonly Regex AssertsTrue = new(@"\bAssert\s*\.\s*True\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// The default suite asserts the file-row half of the Revised latch: a root-level file row, charged at its stale hardlink
    /// size, corrected by the probe pass, is Done and Revised.
    /// </summary>
    [Fact]
    public void TheSuiteAssertsAStaleRootLevelHardlinkFileRowIsRevised()
    {
        string coreTests = Path.Combine(RepositoryRoot(), "tests", "FsAnalyser.Core.Tests");
        Assert.True(Directory.Exists(coreTests), $"setup: {coreTests} does not exist, so there is no suite to read");

        List<Unit> units = Units(coreTests).ToList();
        Assert.True(units.Any(u => RevisedWord.IsMatch(u.Text) && Asserts.IsMatch(u.Text)),
            $"setup: of {units.Count} test units read under {coreTests}, none asserts Revised at all, so the reader found nothing to judge");

        // The stale shape built for real at the root of a scan: a name under the root hardlinked, the file grown, then scanned.
        List<Unit> staleRootName = units
            .Where(u => RootLevelHardlink.IsMatch(u.Text) && GrowsTheFile.IsMatch(u.Text) && ScansARealTree.IsMatch(u.Text))
            .ToList();

        // And in the same test, a positive assertion on Revised over a File row.
        List<Unit> guarded = staleRootName
            .Where(u => FileRow.IsMatch(u.Text) && AssertsRevisedTrue(u.Text))
            .ToList();

        Assert.True(guarded.Count > 0,
            "the file-row half of SL-063's Revised latch (TopLevelTracker.Seed settles a file or link row, Build latches Revised) is a branch " +
            $"no test reaches: of {units.Count} test units in tests/FsAnalyser.Core.Tests read, {staleRootName.Count} build a hardlinked name directly under the scan root " +
            "that is grown through another name and then scanned, and none of them asserts that File row Revised. Adding `r.Kind != EntryKind.File &&` to the latch " +
            "in TopLevelTracker.Build leaves every test green, while a root-level file row the probe pass corrected (as C:\\Windows' hardlinked root files can be) is then never marked. " +
            "Expected a real-tree test: TempTree.Hardlink(tree.PathOf(\"a-stale.bin\"), current), TempTree.Append(current, 63 * MiB), Watch(tree.Root, correctedRows: [\"a-stale.bin\"]), " +
            "and the File row asserted Done at 64 MiB with Revised true and the subfolder's row not Revised.");
    }

    // Some statement of the unit asserts, for a Revised value, that it is true: a lower-case `true` among an Assert.Equal's values,
    // or an Assert.True over Revised with no negation. A negative (Assert.False, `!row.Revised`) is not a guard for the latch.
    private static bool AssertsRevisedTrue(string unit) =>
        unit.Split(';').Any(statement =>
            Asserts.IsMatch(statement)
            && RevisedWord.IsMatch(statement)
            && !AssertsFalse.IsMatch(statement)
            && (LowerTrue.IsMatch(statement) || (AssertsTrue.IsMatch(statement) && !statement.Contains('!'))));

    private static IEnumerable<Unit> Units(string folder)
    {
        string separatorBin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        string separatorObj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(separatorBin, StringComparison.OrdinalIgnoreCase) || file.Contains(separatorObj, StringComparison.OrdinalIgnoreCase))
                continue;
            string text = WithoutComments(File.ReadAllText(file));
            List<int> starts = TestAttribute.Matches(text).Select(m => m.Index).ToList();
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
                yield return new Unit(file, text[starts[i]..end]);
            }
        }
    }

    private static string WithoutComments(string source) =>
        StringsAndComments.Replace(source, m =>
            m.Value.StartsWith("//", StringComparison.Ordinal) || m.Value.StartsWith("/*", StringComparison.Ordinal)
                ? Regex.Replace(m.Value, @"[^\r\n]", " ")
                : m.Value);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FsAnalyser.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("setup: no FsAnalyser.slnx above " + AppContext.BaseDirectory);
    }
}
