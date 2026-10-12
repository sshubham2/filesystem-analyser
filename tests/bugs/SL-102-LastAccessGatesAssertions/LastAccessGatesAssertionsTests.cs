// Bug (SL-102, review m2 of SL-056): two of M6's three gates on the write-class last-access set are branches no test reaches.
//
//   (a) Scanner.Run.KeepsLastAccess (Scanner.cs) returns false for a folder carrying RECALL_ON_DATA_ACCESS, RECALL_ON_OPEN,
//       OFFLINE, PINNED or UNPINNED. No test builds such a folder: NothingUnderASyncRootIsAskedToKeepItsLastAccessTime covers
//       only the SyncRoots path check. Deleting `|| (attributes & CloudFolderAttributes) != 0` keeps the whole default suite
//       green (870 of 870, measured by the investigation).
//   (b) Scanner.Run.DecideLastAccess marks a volume remote when GetFileInformationByHandleEx(FileRemoteProtocolInfo) succeeds.
//       Only the pure truth table MayKeepLastAccess(fileSystem, remote) is tested; no scan of a share, not even the opt-in
//       SMB-LOOPBACK rig, asserts that nothing was asked. Hard-wiring remote = false keeps the default suite green (870 of
//       870), while a scan of \\localhost\C$\... then asks and keeps every folder (0 -> 4 of 4): exactly what M6 exists to
//       prevent, since a share whose server reports 'NTFS' would get SetFileInformationByHandle calls on every folder.
//
// Expected: the suite asserts both gates. (a) A test sets an OFFLINE/PINNED/UNPINNED attribute on a temp subfolder
//   (TempTree.AddAttributes) and asserts, through LastAccessAsked, that the folder is left out while its children are still
//   counted. (b) A test that scans a share (this PC's admin share in ContainerLayerStoreTests, or the SMB-LOOPBACK rig in
//   StalledShareTests) asserts LastAccessAsked == 0.
//
// Actual: no test in tests/FsAnalyser.Core.Tests does either. No test method asserts LastAccessAsked after setting a cloud
//   attribute on a folder, and none asserts LastAccessAsked of a share scan, so a mutant that removes either gate survives.
//
// Why this test reads sources and does not scan: the scanner is right today, so every scan-based assertion of the correct
// behaviour already passes; the defect is that nothing in the suite would notice if it stopped being right. This test
// therefore checks the suite itself: it reads every .cs under tests/FsAnalyser.Core.Tests, drops comments, cuts each file into
// one unit per [Fact]/[Theory] (from its attribute to the next test's attribute, so helpers after a test belong to it), and
// asks whether some unit has the shape the fix names. It passes once such assertions exist.
//
// What it costs: it reads the .cs files of one test project (a few hundred KB, tens of files) and runs regexes over them,
// well under a second. It writes nothing, starts no process and touches nothing outside the repository.

using System.Text.RegularExpressions;

namespace SL102.LastAccessGatesAssertions.Tests;

public class LastAccessGatesAssertionsTests
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

    // Setting an attribute on a folder, the way OnDiskSizeTests does for a file (TempTree.AddAttributes).
    private static readonly Regex SetsAnAttribute = new(@"\b(?:AddAttributes|SetAttributes)\s*\(", RegexOptions.Compiled);

    // The bits Scanner.CloudFolderAttributes names. The two recall bits cannot be set from user mode, so the settable three
    // are enough, by name or as the (FileAttributes) number.
    private static readonly Regex CloudAttribute = new(
        @"\b(?:Offline|Pinned|Unpinned|RecallOnOpen|RecallOnDataAccess|RECALL_ON_OPEN|RECALL_ON_DATA_ACCESS|FILE_ATTRIBUTE_(?:OFFLINE|PINNED|UNPINNED))\b"
        + @"|\b0x(?:1000|40000|80000|100000|400000)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A scan of a share: this PC's admin share (ContainerLayerStoreTests.Loopback), or the SMB rig's LoopbackShare.RemotePath.
    private static readonly Regex ScansAShare = new(
        @"\bLoopback\w*\b|\bRemotePath\b|\blocalhost\b|\b127\.0\.0\.1\b|\bUNC\b", RegexOptions.Compiled);

    private static readonly Regex AssertsNothingWasAsked = new(
        @"\bAssert\.\w+(?:<[^>]*>)?\(\s*\(?\s*0\b[^;]*\bLastAccessAsked\b"
        + @"|\bLastAccessAsked\s*==\s*0\b"
        + @"|\bAssert\.\w+(?:<[^>]*>)?\([^;]*\bLastAccessAsked\b[^;]*,\s*0\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex LastAccessAsked = new(@"\bLastAccessAsked\b", RegexOptions.Compiled);
    private static readonly Regex Asserts = new(@"\bAssert\.", RegexOptions.Compiled);

    /// <summary>
    /// The default suite asserts M6's attribute gate (a folder carrying a cloud attribute is left out of LastAccessAsked) and
    /// its remote gate (a scan of a share asks nothing).
    /// </summary>
    [Fact]
    public void TheSuiteAssertsBothOfM6sUntestedLastAccessGates()
    {
        string coreTests = Path.Combine(RepositoryRoot(), "tests", "FsAnalyser.Core.Tests");
        Assert.True(Directory.Exists(coreTests), $"setup: {coreTests} does not exist, so there is no suite to read");

        List<Unit> units = Units(coreTests).ToList();
        Assert.True(units.Any(u => LastAccessAsked.IsMatch(u.Text) && Asserts.IsMatch(u.Text)),
            $"setup: of {units.Count} test units read under {coreTests}, none asserts LastAccessAsked at all, so the reader found nothing to judge");

        List<string> missing = [];

        // (a) a folder with OFFLINE / PINNED / UNPINNED set, and LastAccessAsked asserted in the same test.
        bool attributeGate = units.Any(u =>
            LastAccessAsked.IsMatch(u.Text) && Asserts.IsMatch(u.Text)
            && SetsAnAttribute.IsMatch(u.Text) && CloudAttribute.IsMatch(u.Text));
        if (!attributeGate)
        {
            missing.Add("(a) attribute gate: no test sets OFFLINE, PINNED or UNPINNED on a folder (TempTree.AddAttributes) and asserts LastAccessAsked, " +
                "so deleting `|| (attributes & CloudFolderAttributes) != 0` from Scanner.KeepsLastAccess leaves every test green");
        }

        // (b) a scan of a share, with LastAccessAsked asserted to be zero in the same test.
        bool remoteGate = units.Any(u =>
            ScansAShare.IsMatch(u.Text) && AssertsNothingWasAsked.IsMatch(u.Text));
        if (!remoteGate)
        {
            missing.Add("(b) remote gate: no test that scans a share (this PC's admin share, or the SMB-LOOPBACK rig) asserts LastAccessAsked == 0, " +
                "so hard-wiring remote = false in Scanner.DecideLastAccess leaves every test green while a share's folders are all asked and kept");
        }

        Assert.True(missing.Count == 0,
            $"M6's gates on the write-class last-access set are branches no test reaches ({missing.Count} of 2 unasserted, " +
            $"{units.Count} test units in tests/FsAnalyser.Core.Tests read):\n  " + string.Join("\n  ", missing));
    }

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
