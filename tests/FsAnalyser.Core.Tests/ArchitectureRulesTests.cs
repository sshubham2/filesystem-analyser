using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

// One verbatim literal per pattern line: eng/rules-parity.ps1 reads them against .taad/fitness.yaml (RuleCopiesAgreeTests).
/// <summary>
/// The architecture rules, checked over the source tree, so breaking one fails a test and not only a
/// review. The patterns are written so that this file does not match its own rules.
/// </summary>
public class ArchitectureRulesTests
{
    private static readonly string Repo = FindRepo();

    [Fact]
    public void TheScannerCoreDoesNotKnowAboutTheScreen()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core", "*.cs", "*.csproj"),
            @"^\s*using\s+(static\s+)?(System\.Windows|PresentationFramework|FsAnalyser\.App)",
            @"<UseWPF>\s*true",
            @"<UseWindowsForms>\s*true",
            @"<ProjectReference[^>]*FsAnalyser\.App");
    }

    /// <summary>
    /// Deciding link-or-data by the reparse attribute dropped 241.7 MiB and the OneDrive tree in the
    /// spike. The rule covers all of src, because the store exposes Attributes to every later reader.
    /// </summary>
    [Fact]
    public void OnlyTheClassifierReadsTheReparseAttribute()
    {
        AssertNoMatch(Files("src", "*.cs", "*.xaml")
                .Except([@"src\FsAnalyser.Core\Scanning\EntryClassifier.cs", @"src\FsAnalyser.Core\Native\Kernel32.cs"]),
            @"FileAttributes\.ReparsePoint",
            @"FILE_ATTRIBUTE_REPARSE_POINT",
            @"IsReparsePoint",
            @"0x0*400\b");
    }

    [Fact]
    public void TheScannerListsDirectoriesOnlyThroughDirectoryReader()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "*.cs"),
            @"Directory\.(Enumerate\w*|GetFiles|GetDirectories|GetFileSystemEntries)",
            @"new\s+(DirectoryInfo|FileInfo)\b",
            @"FileSystemEnumerable",
            @"EnumerationOptions",
            @"AttributesToSkip");
    }

    /// <summary>
    /// Every Win32 filesystem call goes through SynchronousIoCancel, which brackets it so Cancel can
    /// interrupt it and discards its result once a cancel was issued (SL-024). A call made anywhere else
    /// would block with no way to cancel it, or pass a cancelled cloud listing off as an empty folder.
    /// </summary>
    [Fact]
    public void FileSystemCallsGoThroughTheCancelGuard()
    {
        AssertNoMatch(Files("src", "*.cs")
                .Except([@"src\FsAnalyser.Core\Native\Kernel32.cs", @"src\FsAnalyser.Core\Scanning\SynchronousIoCancel.cs"]),
            @"\bKernel32\.(CreateFile|GetFileInformationByHandleEx)\s*\(");
    }

    /// <summary>
    /// A BCL file-system call on the scan thread would block with no guard around it (SL-024). Path is not
    /// covered: GetFullPath and GetPathRoot are lexical and do no I/O.
    /// </summary>
    [Fact]
    public void TheScannerMakesNoBclFileSystemCalls()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "*.cs"),
            @"\b(File|Directory|DirectoryInfo|FileInfo|DriveInfo)\.");
    }

    /// <summary>Covers xUnit v2 and v3: Skip properties, dynamic skips, and v3's explicit-only tests.</summary>
    [Fact]
    public void CountingRuleTestsAreNeverSkipped()
    {
        AssertNoMatch(Files(@"tests\FsAnalyser.Core.Tests", "*.cs"),
            @"\[(Fact|Theory)\([^)]*\bSkip\w*\s*=",
            @"Assert\.Skip\w*\(",
            @"Skip\.(If|IfNot|Unless)",
            @"\bExplicit\s*=\s*true",
            @"Skip(?:Exception)");
    }

    /// <summary>
    /// Every Win32 call goes through Native, where paths are \\?\ and built by LongPath. Fixture-only
    /// interop lives in TestNative.cs, and the opt-in cloud rig's in its two files (SL-049): the
    /// Trait-gated class that unregisters its sync root, and the provider process under tests\CfProvider.
    /// The held-open rig under tests\HoldRig (SL-056) is a process of its own that reads metadata through
    /// handles, the observer the scan is checked against, so it shares nothing with Native either.
    /// The window tests that press Ctrl+Enter with the thread's key state and make another window the active one
    /// (SL-100) call user32 directly, which no Native call does: the repro project under tests\bugs\SL-100-ResultsListRefresh,
    /// and the reveal mode of the window hook.
    /// </summary>
    [Fact]
    public void InteropLivesOnlyInNative()
    {
        AssertNoMatch(Files("src", "*.cs").Concat(Files("tests", "*.cs"))
                .Where(f => !f.StartsWith(@"src\FsAnalyser.Core\Native\", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.StartsWith(@"tests\CfProvider\", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.StartsWith(@"tests\HoldRig\", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.StartsWith(@"tests\bugs\SL-100-ResultsListRefresh\", StringComparison.OrdinalIgnoreCase))
                .Except([@"tests\FsAnalyser.Core.Tests\TestNative.cs", @"tests\FsAnalyser.Core.Tests\CloudPlaceholderTests.cs", @"tests\window\FsaWindowHook\RevealMode.cs"]),
            @"(Library|Dll)Import");
    }

    /// <summary>
    /// The analysis code reads sizes only as the store charged them, and only from the store (SL-040). Raw
    /// Size and OnDisk skip the counting rules: a hardlink's later names, links and a duplicate's stale size.
    /// A ranking built on them lists one file twice or shows a wrong size while its own tests still pass, and
    /// asking the disk again bypasses what the scan measured. Comments are matched too. Outside Core the
    /// compiler holds the rule, because the raw accessors are internal (SL-040, M2).
    /// </summary>
    [Fact]
    public void TheAnalysisReadsOnlyChargedSizesAndOnlyFromTheStore()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Analysis", "*.cs"),
            @"\.(?:Size|OnDisk)\s*[\(\[]",
            @"(?<![\w.])(?:File|Directory|FileInfo|DirectoryInfo|DriveInfo)\.\w+|System\.IO\.(?:File|Directory)\b|new\s+(?:FileInfo|DirectoryInfo|DriveInfo|FileStream)\b");
    }

    /// <summary>
    /// A link is a reparse point whose tag has the name-surrogate bit (SL-043, U-004). Listing tags again
    /// brings DFS back as a link, so its folders go unread, and makes every surrogate tag outside the list
    /// (WSL symlinks and others) a file or a folder that is charged or entered. Comments are matched too.
    /// </summary>
    [Fact]
    public void TheClassifierListsNoLinkTags()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "EntryClassifier.cs"),
            @"(?i)IO_REPARSE_TAG_(MOUNT_POINT|SYMLINK|DFS)\b|0x(A0000003|A000000C|8000000A)\b");
    }

    /// <summary>
    /// A volume that does not answer (a dead mapped drive) must not hold a scan (O-031), so nothing in Core
    /// waits on the used-space figure. Nor does the window: a wait there would hold the ending and keep the
    /// scan's store alive for as long as the read is stuck (SL-043, M1). The completion source the scan holds
    /// is named after the figure, so a wait on it matches too (M2). A tripwire on names, not a proof:
    /// VolumeUsageTests is the behaviour.
    /// </summary>
    [Fact]
    public void NothingWaitsOnTheVolumeFigure()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core", "*.cs").Concat(Files(@"src\FsAnalyser.App", "*.cs")),
            @"(?i)VolumeUsed\w*\.(Task\.)?(Wait|WaitAsync|GetAwaiter|Result)\b",
            @"(?i)\bawait\s+[\w.]*VolumeUsed");
    }

    /// <summary>
    /// The volume read runs on a thread a dead drive can park for good, so it is handed only the completion
    /// source it answers through. A read that named the store would keep a discarded scan (about 700 MiB at
    /// 5M entries) alive for as long as it is stuck (SL-043).
    /// </summary>
    [Fact]
    public void TheVolumeReadHoldsNoScanStore()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "VolumeSpace.cs"),
            @"\bScanStore\b");
    }

    /// <summary>
    /// Every path Core opens is opened in SynchronousIoCancel.Open, inside the critical-error scope (SL-077). A path
    /// call made anywhere else runs with the thread's error mode as it found it, and on a removable drive with no media
    /// raises the "insert a disk" box and holds the scan. The first pattern is a call by the Win32 name, ANSI forms
    /// included; the second is that name as a string, which is how an import that renames the call (EntryPoint =
    /// "CreateFileW" on a method of another name) or a NativeLibrary.GetExport lookup names it (review m2, R2-m2, and the
    /// third review's m1, which added GetCompressedFileSize, FindFirstFileName, FindFirstStream, GetFileInformationByName
    /// and CreateFileTransacted). It holds the path-taking entry points it names, and a call by a name missing from the
    /// list passes it. Of the two excepted files, the compiler holds SynchronousIoCancel.cs: its only path call is
    /// Kernel32.CreateFile, which takes the scope as an argument. It does not hold Kernel32.cs, whose own members can call
    /// its private CreateFileW and GetDiskFreeSpaceEx with no scope: review holds that one, and FailCriticalErrorsTests
    /// counts its calls of the named entry points with these two patterns, read from this source
    /// (Kernel32MakesOnlyThePathCallsThatRunInsideTheScope). This holds every other file, a new Native file with its own
    /// import included. Comments are matched too.
    /// </summary>
    [Fact]
    public void PathOpensGoOnlyThroughTheGuardedOpen()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core", "*.cs")
                .Except([@"src\FsAnalyser.Core\Native\Kernel32.cs", @"src\FsAnalyser.Core\Scanning\SynchronousIoCancel.cs"]),
            @"\b(CreateFile[AW2]?|CreateFileTransacted[AW]?|OpenFileById|(Nt|Zw)(Create|Open)File|(Nt|Zw)Query(Full)?AttributesFile|FindFirstFile(Ex)?[AW]?|FindFirstFileName[AW]?|FindFirstStream[AW]?|GetFileAttributes(Ex)?[AW]?|GetFileInformationByName|GetCompressedFileSize[AW]?|GetVolumeInformation[AW]?|GetDiskFreeSpace(Ex)?[AW]?)\s*\(",
            @"""(CreateFile[AW2]?|CreateFileTransacted[AW]?|OpenFileById|(Nt|Zw)(Create|Open)File|(Nt|Zw)Query(Full)?AttributesFile|FindFirstFile(Ex)?[AW]?|FindFirstFileName[AW]?|FindFirstStream[AW]?|GetFileAttributes(Ex)?[AW]?|GetFileInformationByName|GetCompressedFileSize[AW]?|GetVolumeInformation[AW]?|GetDiskFreeSpace(Ex)?[AW]?)""");
    }

    /// <summary>
    /// The unreadable-folders list reads the scan's record and never the filesystem (SL-087, Done-when #7). A list that
    /// asked the disk about a folder again would describe the drive as it is now, not the scan it belongs to, and could
    /// hang on a dropped share or meet the same refusals on the UI thread. Windows' message for a code, through
    /// Win32Exception, is the one lookup it makes, and no pattern here matches it. The App has implicit usings, so
    /// System.IO is in scope with no token naming it: Path.Exists, the stream readers and the Info and RandomAccess
    /// types are named here too (review m4). A tripwire on names: a call through a helper in another file, an alias or
    /// a global:: spelling passes it. Comments are matched too.
    /// </summary>
    [Fact]
    public void TheUnreadableViewTouchesNoFileSystem()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.App", "UnreadableRows.cs"),
            @"\b(File|Directory)\.\w+\s*\(",
            @"\bnew\s+(FileInfo|DirectoryInfo|FileStream|DriveInfo|FileSystemWatcher)\b",
            @"\bSystem\.IO\b",
            @"\b(Kernel32|Scanner|DirectoryReader|SizeProbe)\.",
            @"\[\s*(Dll|Library)Import\b",
            @"\bPath\.Exists\b",
            @"\bnew\s+(StreamReader|StreamWriter)\b",
            @"\b(DriveInfo|RandomAccess|File(System)?Info)\.");
    }

    /// <summary>
    /// The App never decides how much of a failed folder was listed (SL-087, Done-when #3): the kind is the engine's,
    /// read off its record, so the list cannot disagree with the engine about a folder. The compiler holds the rest of
    /// it: the record's constructor is internal to Core and nothing on it can be set, so the App cannot make a record
    /// or copy one with another kind (review M3). This catches the kind chosen by a ternary, the shape a kind worked
    /// out again from a child count takes; a switch or an if/else passes it. The pattern holds on the '?' line alone,
    /// so a ternary laid out over three lines, as this repo writes them, fails here as well as in Fitness, which
    /// matches the whole file (review R2-m1); '??' is not a ternary and is left out. Comments are matched too. The pattern
    /// names any member of the enum, not the members it had when it was written, so a member added later is fenced from the
    /// day it is added (SL-106: a ternary choosing OpenedNothingListed passed the pattern that named the first two).
    /// </summary>
    [Fact]
    public void TheAppNeverDecidesAFailedFolderKind()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.App", "*.cs"),
            @"(?<!\?)\?\s*UnreadableKind\.\w+\b");
    }

    /// <summary>
    /// Nothing in the app hands Explorer its select switch (SL-088, criterion 3). U-114 measured that switch, given a path
    /// of 300 or 1000 characters, silently opening another folder with no dialog and no error code, so a launch with it,
    /// fallback or not, lands the person in the wrong place with no signal. The reveal goes through the shell's parse
    /// and open-and-select calls alone. Every spelling is caught: the switch as its own argument, in upper case, or
    /// followed by a space and a quote, which SL-047's settled pattern let through. No file is exempt, ShellReveal.cs
    /// included, and comments are matched too; SelectSwitchRuleTests feeds this pattern the shapes it must catch and the
    /// look-alikes it must leave.
    /// </summary>
    [Fact]
    public void NothingInTheAppHandsExplorerItsSelectSwitch()
    {
        AssertNoMatch(Files("src", "*.cs"),
            @"/[Ss][Ee][Ll][Ee][Cc][Tt]\b");
    }

    /// <summary>
    /// The window report takes only the count from the scan's failed-folder record (SL-089). The record holds each failed
    /// folder's entry, one step from its path, and Q-016 bars names and paths from the schema-1 report, which every window
    /// case reads and keeps as evidence. The pattern names the record and allows it only followed by .Count, so it holds on
    /// one line: the record alone at the end of a line, the first line of the two-line fluent layout the report writes its
    /// LINQ in, fails here as it fails in Fitness, which matches the whole file (review M4). TopLevelState.Unreadable, a row
    /// state the report counts, is not the record. WindowReportRuleTests feeds this pattern the shapes it must catch and
    /// the look-alikes it must leave. Comments are matched too.
    /// </summary>
    [Fact]
    public void TheWindowReportReadsOnlyTheFailedFolderCount()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.App", "WindowReport.cs"),
            @"(?<!TopLevelState\.)\bUnreadable\b(?!\s*\??\.\s*Count\b)");
    }

    /// <summary>
    /// Only ShellReveal.cs reads Explorer's view settings or sets the test override of them (SL-091). Walk's visibility
    /// verdict, its precedence and the hook's hide-only AND are all applied there; a second reader elsewhere in src would
    /// decide on its own what Explorer shows, outside the override, and could report a landing Walk would refuse.
    /// Comments are matched too.
    /// </summary>
    [Fact]
    public void OnlyShellRevealReadsOrOverridesExplorersViewSettings()
    {
        AssertNoMatch(Files("src", "*.cs")
                .Except([@"src\FsAnalyser.Core\Native\ShellReveal.cs"]),
            @"\bSHGetSetSettings\b",
            @"\bOverrideExplorerView\b");
    }

    /// <summary>
    /// The app never writes Explorer's settings (SL-091, Q-169, Q-175): it reads Explorer's view to decide a reveal and
    /// never changes it. Each pattern holds on one line, so this per-line match fires where Fitness's whole-file match does
    /// (SL-087). They name the write spellings an ordinary C# producer reaches for, bounded by who writes such a line by
    /// accident and not closed against every spelling (review n2, widened by SL-103): the shell call with its set flag, a
    /// key's SetValue or DeleteValue of either value, a writable OpenSubKey of Explorer\Advanced (true, ReadWriteSubTree or
    /// a RegistryRights overload with SetValue, WriteKey or FullControl), CreateSubKey, DeleteSubKey and DeleteSubKeyTree of
    /// it, the static Registry.SetValue, and the Win32 and shlwapi registry writes and deletes. ExplorerSettingsWriteCorpus.json
    /// pins them, and feeds them to these patterns, read from here. A tripwire on names: a key path or value name held in a
    /// variable or constant, rights held in a variable, or a call laid over two lines, passes it. Comments are matched too.
    /// </summary>
    [Fact]
    public void TheAppNeverWritesExplorersSettings()
    {
        AssertNoMatch(Files("src", "*.cs"),
            @"\bSHGetSetSettings\b[^;\r\n]*\btrue\b",
            @"(?i)\.(SetValue|DeleteValue)[ \t]*\([ \t]*@?""(Hidden|ShowSuperHidden)""",
            @"(?i)\bOpenSubKey[ \t]*\([^;\r\n]*Explorer\\{1,2}Advanced[^;\r\n]*\b(true|ReadWriteSubTree|RegistryRights\]?(\.|::)(SetValue|WriteKey|FullControl))\b",
            @"\b(RegSetValue|RegSetKeyValue|SHSetValue|SHRegSetUSValue|RegDelete|SHDelete)\w*",
            @"(?i)\bRegistry\.SetValue[ \t]*\([^;\r\n]*Explorer\\{1,2}Advanced",
            @"(?i)\b(CreateSubKey|DeleteSubKey|DeleteSubKeyTree)[ \t]*\([^;\r\n]*Explorer\\{1,2}Advanced");
    }

    private static IEnumerable<string> Files(string folder, params string[] patterns) =>
        patterns
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(Repo, folder), p, SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(Repo, f))
            .Where(f => !f.Split('\\').Any(part => part is "bin" or "obj"))
            .ToList();

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

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FsAnalyser.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"No FsAnalyser.slnx above {AppContext.BaseDirectory}.");
    }
}
