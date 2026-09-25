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
    /// interop lives in TestNative.cs.
    /// </summary>
    [Fact]
    public void InteropLivesOnlyInNative()
    {
        AssertNoMatch(Files("src", "*.cs").Concat(Files("tests", "*.cs"))
                .Where(f => !f.StartsWith(@"src\FsAnalyser.Core\Native\", StringComparison.OrdinalIgnoreCase))
                .Except([@"tests\FsAnalyser.Core.Tests\TestNative.cs"]),
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
