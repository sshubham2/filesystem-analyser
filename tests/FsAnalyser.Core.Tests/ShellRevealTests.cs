using FsAnalyser.Core.Native;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

public class ShellRevealTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private const int NotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int EFail = unchecked((int)0x80004005);

    [Theory]
    [InlineData(@"C:\folder\file.txt")]
    [InlineData(@"C:\folder\sub")]
    public void Plan_PlainFileAndFolder_TargetIsTheItemItself(string path)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Null(plan.UnaddressableName);
        Assert.Equal(path, plan.Target);
    }

    [Theory]
    [InlineData(@"C:\dup.", "dup.")]
    [InlineData(@"C:\folder\dup.", "dup.")]
    [InlineData(@"C:\folder\sp ", "sp ")]
    [InlineData(@"C:\con", "con")]
    [InlineData(@"C:\folder\NUL", "NUL")]
    [InlineData(@"C:\COM¹", "COM¹")]
    [InlineData(@"C:\CONIN$", "CONIN$")]
    [InlineData(@"C:\x\dir.\in.txt", "dir.")]
    public void Plan_UnaddressableNames_RecordTheNameAndAskTheShellForNothing(string path, string expectedBadName)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Equal(expectedBadName, plan.UnaddressableName);
        Assert.Null(plan.Target);
    }

    [Theory]
    [InlineData(@"C:\folder\dup")]
    [InlineData(@"C:\folder\ lead")]
    [InlineData(@"C:\folder\a,b;c.txt")]
    [InlineData(@"C:\folder\😀_face.txt")]
    [InlineData(@"C:\folder\%TEMP%.txt")]
    [InlineData(@"C:\con.txt")]
    [InlineData(@"C:\folder\aux.h")]
    [InlineData(@"C:\nul.log")]
    [InlineData(@"C:\folder\com1.txt")]
    [InlineData(@"C:\prn.md")]
    [InlineData(@"C:\con.d\inner.bin")]
    public void Plan_UnusualAddressableNames_StaysExact(string path)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Null(plan.UnaddressableName);
        Assert.Equal(path, plan.Target);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:", @"C:\")]
    [InlineData(@"C:\a\b", @"C:\a\b")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\sub\file.txt", @"\\server\share\sub\file.txt")]
    public void Plan_RootShapes_TargetIsTheItemAndABareDriveIsItsRoot(string path, string expectedTarget)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Null(plan.UnaddressableName);
        Assert.Equal(expectedTarget, plan.Target);
    }

    [Theory]
    [InlineData(@"\\?\C:\folder\file.txt", @"C:\folder\file.txt")]
    [InlineData(@"\\?\UNC\server\share\folder\file.txt", @"\\server\share\folder\file.txt")]
    public void Plan_PrefixesStripped(string extendedPath, string expectedDisplay)
    {
        RevealPlan plan = ShellReveal.Plan(extendedPath);
        Assert.Equal(expectedDisplay, plan.Target);
    }

    [Fact]
    public void Plan_LongPaths_NotTruncated()
    {
        // 300 characters path with components <= 100
        string seg = new string('a', 50);
        string path300 = @"C:\" + string.Join(@"\", Enumerable.Repeat(seg, 5)) + @"\test.txt";
        Assert.True(path300.Length >= 260);
        RevealPlan plan300 = ShellReveal.Plan(path300);
        Assert.Equal(path300, plan300.Target);

        // 600 characters path with components <= 100
        string path600 = @"C:\" + string.Join(@"\", Enumerable.Repeat(seg, 11)) + @"\test.txt";
        Assert.True(path600.Length >= 550);
        RevealPlan plan600 = ShellReveal.Plan(path600);
        Assert.Equal(path600, plan600.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path.txt")]
    [InlineData(@"filename.txt")]
    [InlineData(@"/leading/relative")]
    public void Plan_InvalidOrRelativeInput_ThrowsArgumentException(string? invalidPath)
    {
        Assert.Throws<ArgumentException>(() => ShellReveal.Plan(invalidPath!));
    }

    [Fact]
    public void Walk_TargetParses_OpensItOnceFreesItAndReportsExact()
    {
        var shell = new FakeShell(parse: 0, open: 0);
        RevealOutcome outcome = shell.Walk(@"C:\a\b.txt");

        Assert.Equal(new[] { @"C:\a\b.txt" }, shell.Parsed);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.Equal(RevealReason.None, outcome.Reason);
        Assert.Equal(@"C:\a\b.txt", outcome.Shown);
        Assert.Equal(0, outcome.HResult);
    }

    /// <summary>
    /// SL-088 criterion 6: the item, or its parent, is gone since the scan. SL-047 climbed to the nearest ancestor that
    /// parsed and opened it in the item's place; the reveal is refused instead, and nothing is opened.
    /// </summary>
    [Theory]
    [InlineData(NotFound)]
    [InlineData(PathNotFound)]
    public void Walk_TargetNoLongerExists_RefusesWithoutOpeningAnything(int hr)
    {
        var shell = new FakeShell(parse: hr, open: 0);
        RevealOutcome outcome = shell.Walk(@"C:\a\b.txt");

        Assert.Equal(new[] { @"C:\a\b.txt" }, shell.Parsed);
        Assert.Empty(shell.Opened);
        Assert.Empty(shell.Freed);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.NoLongerExists, outcome.Reason);
        Assert.Null(outcome.Shown);
        Assert.Equal(hr, outcome.HResult);
    }

    /// <summary>
    /// A parse that fails for any reason other than not-found is refused with its own code and is not said to be gone:
    /// access denied, a name the shell rejects, a network path, and an HRESULT outside FACILITY_WIN32.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED
    [InlineData(unchecked((int)0x8007007B))] // ERROR_INVALID_NAME
    [InlineData(unchecked((int)0x80070035))] // ERROR_BAD_NETPATH
    [InlineData(EFail)]
    public void Walk_ParseFailsOtherwise_RefusesWithThatCode(int hr)
    {
        var shell = new FakeShell(parse: hr, open: 0);
        RevealOutcome outcome = shell.Walk(@"C:\a\b.txt");

        Assert.Equal(new[] { @"C:\a\b.txt" }, shell.Parsed);
        Assert.Empty(shell.Opened);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.None, outcome.Reason);
        Assert.Null(outcome.Shown);
        Assert.Equal(hr, outcome.HResult);
    }

    /// <summary>
    /// The parse gave a valid ID list and the open failed: refused with the open's code, the list freed, and no other
    /// folder parsed or opened.
    /// </summary>
    [Fact]
    public void Walk_OpenFails_RefusesWithItsCodeFreesTheIdListAndTriesNothingElse()
    {
        var shell = new FakeShell(parse: 0, open: EFail);
        RevealOutcome outcome = shell.Walk(@"C:\a\b.txt");

        Assert.Equal(new[] { @"C:\a\b.txt" }, shell.Parsed);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.None, outcome.Reason);
        Assert.Null(outcome.Shown);
        Assert.Equal(EFail, outcome.HResult);
    }

    /// <summary>
    /// The parse succeeded but its ID list stands for another path, or for none: refused before anything is opened, the
    /// list freed. This is how 'dup.' parses when 'dup' is there, and how a device name with an extension would parse on
    /// a Windows that took it for the device (SL-088 review R2-m2).
    /// </summary>
    [Theory]
    [InlineData(@"C:\a\b")]
    [InlineData(null)]
    public void Walk_ParseStandsForAnotherItem_RefusesWithoutOpening(string? landsOn)
    {
        var shell = new FakeShell(parse: 0, open: 0, pathOf: _ => landsOn);
        RevealOutcome outcome = shell.Walk(@"C:\a\b.txt");

        Assert.Equal(new[] { @"C:\a\b.txt" }, shell.Parsed);
        Assert.Empty(shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.ParsedAsAnotherItem, outcome.Reason);
        Assert.Null(outcome.Shown);
    }

    [Theory]
    [InlineData(@"C:\folder\dup.", "dup.")]
    [InlineData(@"C:\x\dir.\in.txt", "dir.")]
    public void Walk_UnaddressableName_RefusesWithoutAskingTheShell(string path, string badName)
    {
        var shell = new FakeShell(parse: 0, open: 0);
        RevealOutcome outcome = shell.Walk(path);

        Assert.Empty(shell.Parsed);
        Assert.Empty(shell.Opened);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.NameUnaddressable, outcome.Reason);
        Assert.Equal(badName, outcome.UnaddressableName);
        Assert.Null(outcome.Shown);
        Assert.Equal(path, outcome.Requested);
    }

    // ---- Explorer's view settings (SL-091) ----

    /// <summary>
    /// The items the visibility cases reveal, each made in a fresh temp tree with the attributes its name says. The
    /// one past MAX_PATH is a Hidden file whose path is over 260 characters.
    /// </summary>
    private static string Item(TempTree tree, string item)
    {
        string path = item switch
        {
            "plain-file" => tree.File("plain.bin", 4096),
            "plain-folder" => tree.Folder("plain"),
            "system-file" => tree.File("system.bin", 4096),
            "system-folder" => tree.Folder("system"),
            "hidden-file" => tree.File("hidden.bin", 4096),
            "hidden-folder" => tree.Folder("hidden"),
            "hidden-system-file" => tree.File("hs_file.sys", 4096),
            "hidden-system-folder" => tree.Folder("hs_dir"),
            "hidden-file-in-hidden-folder" => TempTree.WriteFile(Path.Combine(tree.Folder("hf"), "hid.txt"), 4096),
            "hidden-file-past-max-path" => TempTree.WriteFile(Path.Combine(tree.Folder(string.Join('\\', Enumerable.Repeat(new string('l', 60), 4))), "far.bin"), 4096),
            _ => throw new ArgumentException(item, nameof(item))
        };
        FileAttributes attributes = item switch
        {
            "system-file" or "system-folder" => FileAttributes.System,
            "hidden-system-file" or "hidden-system-folder" => FileAttributes.Hidden | FileAttributes.System,
            "plain-file" or "plain-folder" => 0,
            _ => FileAttributes.Hidden
        };
        if (item == "hidden-file-in-hidden-folder")
            TempTree.AddAttributes(tree.PathOf("hf"), FileAttributes.Hidden);
        if (item == "hidden-file-past-max-path")
            Assert.True(path.Length > 260, $"{path.Length} characters");
        if (attributes != 0)
            TempTree.AddAttributes(path, attributes);
        return path;
    }

    private static readonly string[] Views = ["0|0", "0|1", "1|0", "1|1"];

    private static ExplorerView View(string view) => new(view[0] == '1', view[2] == '1');

    public static TheoryData<string, string> NotHiddenItemsUnderEveryView()
    {
        var data = new TheoryData<string, string>();
        foreach (string item in new[] { "plain-file", "plain-folder", "system-file", "system-folder" })
            foreach (string view in Views)
                data.Add(item, view);
        return data;
    }

    /// <summary>
    /// SL-091 C-001/C-015: an item that is not Hidden, plain or System-only, is revealed as before: Explorer's view is never
    /// read and the open is reached, whatever the view. System alone hides nothing (ProgramData\Microsoft, System-only,
    /// was selected 6 of 6 at Windows' defaults and 8 of 8 under this PC's settings).
    /// </summary>
    [Theory]
    [MemberData(nameof(NotHiddenItemsUnderEveryView))]
    public void Walk_ItemNotHidden_PlainOrSystemOnly_OpensWithoutReadingTheView(string item, string view)
    {
        using var tree = new TempTree();
        string path = Item(tree, item);
        var shell = new FakeShell(parse: 0, open: 0, view: View(view));
        RevealOutcome outcome = shell.Walk(path);

        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.Equal(RevealReason.None, outcome.Reason);
        Assert.Equal(ExplorerHides.None, outcome.HiddenBy);
        Assert.Null(outcome.View);
        Assert.False(outcome.Hidden);
        Assert.Equal(item.StartsWith("system", StringComparison.Ordinal), outcome.System);
        Assert.Equal(0, shell.ViewReads);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
    }

    /// <summary>
    /// SL-091 C-016: only the item's own attributes decide. A plain file or folder directly inside a +H folder or a +H+S
    /// folder, and one two folders below it, is revealed at Windows' defaults (0|0) with the view never read: Explorer
    /// selected every such item in its own parent (18 of 18 a class, in Windows Sandbox at Hidden=2), and the control of
    /// run 2285 under hidden AppData. A Walk that read the folders above would refuse every reveal under AppData.
    /// </summary>
    [Theory]
    [InlineData(FileAttributes.Hidden, false, 1)]
    [InlineData(FileAttributes.Hidden, true, 1)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, false, 1)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true, 1)]
    [InlineData(FileAttributes.Hidden, false, 3)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, false, 3)]
    public void Walk_PlainItemUnderHiddenOrHiddenSystemFolder_OpensWithoutReadingTheView(FileAttributes above, bool folder, int depth)
    {
        using var tree = new TempTree();
        string top = tree.Folder("above");
        TempTree.AddAttributes(top, above);
        string parent = tree.Folder(Path.Combine(["above", .. Enumerable.Repeat("mid", depth - 1)]));
        string path = folder ? tree.Folder(Path.Combine(parent, "inner")) : TempTree.WriteFile(Path.Combine(parent, "inner.bin"), 4096);
        var shell = new FakeShell(parse: 0, open: 0, view: new ExplorerView(false, false));
        RevealOutcome outcome = shell.Walk(path);

        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.False(outcome.Hidden);
        Assert.Null(outcome.View);
        Assert.Equal(0, shell.ViewReads);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Opened);
    }

    /// <summary>
    /// SL-091 C-003/C-017: a Hidden item while hidden files are not shown is refused before the open: run 2285 measured 0
    /// of 13 such reveals selected at Windows' defaults, each with S_OK and the parent opened, which Walk reported as
    /// Exact. The view is read once, the ID list freed once, the open never called, and the refusal names the hidden-files
    /// setting alone, whatever the protected-files one says. A +H file directly inside a +H folder and a Hidden file past
    /// MAX_PATH are refused the same.
    /// </summary>
    [Theory]
    [InlineData("hidden-file", "0|0")]
    [InlineData("hidden-file", "0|1")]
    [InlineData("hidden-folder", "0|0")]
    [InlineData("hidden-folder", "0|1")]
    [InlineData("hidden-file-in-hidden-folder", "0|0")]
    [InlineData("hidden-file-past-max-path", "0|0")]
    [InlineData("hidden-file-past-max-path", "0|1")]
    public void Walk_HiddenOnlyWhileHiddenFilesAreNotShown_RefusesWithoutOpening(string item, string view)
    {
        using var tree = new TempTree();
        string path = Item(tree, item);
        var shell = new FakeShell(parse: 0, open: 0, view: View(view));
        RevealOutcome outcome = shell.Walk(path);

        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.HiddenByExplorerSettings, outcome.Reason);
        Assert.Equal(ExplorerHides.HiddenFiles, outcome.HiddenBy);
        Assert.Equal(View(view), outcome.View);
        Assert.Equal(0, outcome.HResult);
        Assert.Null(outcome.Shown);
        Assert.True(outcome.Hidden);
        Assert.False(outcome.System);
        Assert.Equal(1, shell.ViewReads);
        Assert.Empty(shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
    }

    /// <summary>
    /// SL-091 C-002/C-018/C-006: a Hidden item whose every hiding setting is set to show lands as before. Hidden-only items
    /// were selected 5 of 5 under this PC's settings (1|0), so refusing them would break reveals that work; a Hidden+System
    /// item at 1|1 is the one cell the predicate lets through unmeasured, pinned here so that a person's measurement of it
    /// changes one row. The view is read once and the open reached once; the view it was decided under is on the outcome.
    /// </summary>
    [Theory]
    [InlineData("hidden-file", "1|0")]
    [InlineData("hidden-file", "1|1")]
    [InlineData("hidden-folder", "1|0")]
    [InlineData("hidden-folder", "1|1")]
    [InlineData("hidden-file-past-max-path", "1|0")]
    [InlineData("hidden-system-file", "1|1")]
    [InlineData("hidden-system-folder", "1|1")]
    public void Walk_HiddenItemWhoseEverySettingShowsIt_Lands(string item, string view)
    {
        using var tree = new TempTree();
        string path = Item(tree, item);
        var shell = new FakeShell(parse: 0, open: 0, view: View(view));
        RevealOutcome outcome = shell.Walk(path);

        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.Equal(RevealReason.None, outcome.Reason);
        Assert.Equal(ExplorerHides.None, outcome.HiddenBy);
        Assert.Equal(View(view), outcome.View);
        Assert.True(outcome.Hidden);
        Assert.Equal(item.StartsWith("hidden-system", StringComparison.Ordinal), outcome.System);
        Assert.Equal(1, shell.ViewReads);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
    }

    /// <summary>
    /// SL-091 C-004/C-005/C-019: a Hidden+System item while a setting hides it is refused before the open, naming every
    /// setting that hides it. Under this PC's settings (1|0) that is the protected-files setting alone: SL-088 measured 0
    /// of 8 selected there while Walk reported Exact. At Windows' defaults (0|0) both settings hide it, so both are named:
    /// naming one would send a person to change one box and still find nothing selected.
    /// </summary>
    [Theory]
    [InlineData("hidden-system-file", "1|0", ExplorerHides.ProtectedFiles)]
    [InlineData("hidden-system-folder", "1|0", ExplorerHides.ProtectedFiles)]
    [InlineData("hidden-system-file", "0|0", ExplorerHides.HiddenFiles | ExplorerHides.ProtectedFiles)]
    [InlineData("hidden-system-folder", "0|0", ExplorerHides.HiddenFiles | ExplorerHides.ProtectedFiles)]
    [InlineData("hidden-system-file", "0|1", ExplorerHides.HiddenFiles)]
    [InlineData("hidden-system-folder", "0|1", ExplorerHides.HiddenFiles)]
    public void Walk_HiddenSystemWhileASettingHidesIt_RefusesNamingEachSettingThatHides(string item, string view, ExplorerHides hides)
    {
        using var tree = new TempTree();
        string path = Item(tree, item);
        var shell = new FakeShell(parse: 0, open: 0, view: View(view));
        RevealOutcome outcome = shell.Walk(path);

        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.HiddenByExplorerSettings, outcome.Reason);
        Assert.Equal(hides, outcome.HiddenBy);
        Assert.Equal(View(view), outcome.View);
        Assert.Equal(0, outcome.HResult);
        Assert.True(outcome.Hidden);
        Assert.True(outcome.System);
        Assert.Equal(1, shell.ViewReads);
        Assert.Empty(shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
    }

    /// <summary>
    /// SL-091 C-020: a +H+S folder this user cannot open, with a deny-all ACE for this user inside a readable parent. Its
    /// attributes come from the parent's listing; were the read to fail, the item would count as not hidden and be opened,
    /// the false landing this slice removes, for exactly the folders SL-088 was about. Under this PC's settings (1|0) it
    /// is refused for the protected-files setting and never opened.
    /// </summary>
    [Fact]
    public void Walk_HiddenSystemFolderTheUserCannotOpen_IsRefusedNotTakenForVisible()
    {
        using var tree = new TempTree();
        string denied = tree.Folder("denied_hs");
        TempTree.AddAttributes(denied, FileAttributes.Hidden | FileAttributes.System);
        tree.Deny(denied, 0x1F01FF); // FILE_ALL_ACCESS
        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(denied));

        var shell = new FakeShell(parse: 0, open: 0, view: new ExplorerView(true, false));
        RevealOutcome outcome = shell.Walk(denied);

        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(RevealReason.HiddenByExplorerSettings, outcome.Reason);
        Assert.Equal(ExplorerHides.ProtectedFiles, outcome.HiddenBy);
        Assert.True(outcome.Hidden);
        Assert.True(outcome.System);
        Assert.Empty(shell.Opened);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Freed);
    }

    /// <summary>
    /// SL-091 C-007/C-020: Windows' own protected items at the system drive's root, through the shell's real parse and
    /// resolve-back on an STA thread as RevealAsync runs them, with an open that only records (no window is opened), under
    /// this PC's settings (1|0). System Volume Information is a folder no ordinary user can open, and the paging file is
    /// held open by the system so that any open of it fails with a sharing violation; neither can be planted in a temp
    /// tree. Each Hidden+System *.sys at the root (pagefile.sys, hiberfil.sys, swapfile.sys where they exist) is refused
    /// with System Volume Information, and none is opened. Never skipped: a root with no such file says so here and the
    /// folder is still asserted.
    /// </summary>
    [Fact]
    public void Walk_RealProtectedItemsAtTheSystemRoot_AreRefusedAndNeverOpened()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;
        string volumeInformation = Path.Combine(root, "System Volume Information");
        var protectedFiles = Directory.EnumerateFiles(root, "*.sys", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
            .Where(f => (File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System))
            .ToList();
        _output.WriteLine(protectedFiles.Count == 0
            ? $"no Hidden+System *.sys at {root}: only System Volume Information is revealed"
            : $"Hidden+System *.sys at {root}: {string.Join(", ", protectedFiles)}");

        var opened = new List<IntPtr>();
        int viewReads = 0;
        var outcomes = new Dictionary<string, RevealOutcome>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (string path in protectedFiles.Prepend(volumeInformation))
                {
                    outcomes[path] = ShellReveal.Walk(ShellReveal.Plan(path), path, ShellReveal.ParseDisplayName, ShellReveal.PathOfIdList,
                        pidl => { opened.Add(pidl); return 0; }, ShellReveal.FreeIdList, () => { viewReads++; return new ExplorerView(true, false); });
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the reveals did not finish within 60 s");
        Assert.Null(failure);

        foreach ((string path, RevealOutcome outcome) in outcomes)
        {
            Assert.True(outcome.Kind == RevealKind.Failed && outcome.Reason == RevealReason.HiddenByExplorerSettings &&
                outcome.HiddenBy == ExplorerHides.ProtectedFiles, $"{path}: {outcome}");
        }
        Assert.Equal(protectedFiles.Count + 1, outcomes.Count);
        Assert.Equal(outcomes.Count, viewReads);
        Assert.Empty(opened);
    }

    /// <summary>
    /// SL-091 C-008/C-021: a volume root's own attributes read Hidden and System, and Explorer shows it all the same, so a
    /// drive is revealed at Windows' defaults with no view read and none on the outcome.
    /// </summary>
    [Fact]
    public void Walk_VolumeRoot_IsExemptAndReadsNoView()
    {
        var shell = new FakeShell(parse: 0, open: 0, view: new ExplorerView(false, false));
        RevealOutcome outcome = shell.Walk(@"C:\");

        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.False(outcome.Hidden);
        Assert.False(outcome.System);
        Assert.Null(outcome.View);
        Assert.Equal(0, shell.ViewReads);
        Assert.Equal(new[] { FakeShell.Pidl }, shell.Opened);
    }

    /// <summary>
    /// SL-091 C-009/C-022: a Hidden+System target that fails earlier keeps that failure's reason, with no view read, under
    /// Windows' defaults where the view would hide it: gone since the scan (the rescan advice stays), parsed as another
    /// item, or a name the shell cannot address ('hs.', which Plan refuses before anything is asked). The ID list is freed
    /// once wherever one was made.
    /// </summary>
    [Theory]
    [InlineData("gone", RevealReason.NoLongerExists)]
    [InlineData("another", RevealReason.ParsedAsAnotherItem)]
    [InlineData("unaddressable", RevealReason.NameUnaddressable)]
    public void Walk_EarlierFailureOnAHiddenSystemTarget_KeepsItsReasonAndReadsNoView(string failure, RevealReason reason)
    {
        using var tree = new TempTree();
        string path = failure == "unaddressable"
            ? TempTree.WriteFile(tree.ExtendedRoot + @"\hs.", 4096)[4..]
            : tree.File("hs_file.sys", 4096);
        TempTree.AddAttributes(LongPathOf(path), FileAttributes.Hidden | FileAttributes.System);
        var shell = new FakeShell(
            parse: failure == "gone" ? NotFound : 0,
            open: 0,
            pathOf: failure == "another" ? (Func<IntPtr, string?>)(_ => tree.Root) : null,
            view: new ExplorerView(false, false));
        RevealOutcome outcome = shell.Walk(path);

        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(reason, outcome.Reason);
        Assert.Equal(ExplorerHides.None, outcome.HiddenBy);
        Assert.Null(outcome.View);
        Assert.Equal(0, shell.ViewReads);
        Assert.Empty(shell.Opened);
        Assert.Equal(failure == "another" ? new[] { FakeShell.Pidl } : Array.Empty<IntPtr>(), shell.Freed);
    }

    private static string LongPathOf(string path) => @"\\?\" + path;

    /// <summary>
    /// SL-091 C-010/C-023: the shell state's first DWORD read as Explorer's view. Bit 0 is fShowAllObjects, bit 15
    /// fShowSuperHidden, and every other bit is another setting: 0x5 is what this PC read with fShowSysFiles asked for too,
    /// 0x8 the Sandbox at Windows' defaults, 0x7FFE every bit below 15 but 0. Only ever seen at 0 here, bit 15 is pinned
    /// by this table.
    /// </summary>
    [Theory]
    [InlineData(0x0u, false, false)]
    [InlineData(0x1u, true, false)]
    [InlineData(0x5u, true, false)]
    [InlineData(0x8u, false, false)]
    [InlineData(0x7FFEu, false, false)]
    [InlineData(0x8000u, false, true)]
    [InlineData(0x8001u, true, true)]
    [InlineData(0xFFFF7FFEu, false, false)]
    public void FromShellState_ReadsBitZeroAsHiddenAndBitFifteenAsProtected(uint state, bool showsHidden, bool showsProtected)
    {
        Assert.Equal(new ExplorerView(showsHidden, showsProtected), ExplorerView.FromShellState(state));
    }

    /// <summary>
    /// SL-091 C-011/C-023: the real read of the shell's state on this PC agrees with the registry Explorer keeps it in,
    /// read here and never written: Hidden is 1 when hidden files are shown (2, or nothing, when not) and ShowSuperHidden
    /// is 1 when protected operating system files are. Proves the buffer, the mask and the decode against the live shell.
    /// </summary>
    [Fact]
    public void ReadExplorerView_AgreesWithTheRegistryOnThisPc()
    {
        using Microsoft.Win32.RegistryKey? advanced = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        int hidden = advanced?.GetValue("Hidden") as int? ?? 2;
        int superHidden = advanced?.GetValue("ShowSuperHidden") as int? ?? 0;

        ExplorerView view = ShellReveal.ReadExplorerView();

        _output.WriteLine($"HKCU Hidden={hidden} ShowSuperHidden={superHidden}; the shell's state reads {view}");
        Assert.Equal(new ExplorerView(hidden == 1, superHidden == 1), view);
    }

    /// <summary>
    /// SL-091 C-024: a test's view can only hide. For every real view and every view a test sets, the view a reveal is
    /// decided under is the two ANDed field by field, so an item the real settings hide stays hidden under every mask,
    /// and no mask makes a landing out of a refusal. No mask leaves the real view as it is.
    /// </summary>
    [Fact]
    public void ExplorerViewOverride_OnlyHides_ForEveryRealViewAndMask()
    {
        foreach (string real in Views)
        {
            Assert.Equal(View(real), ShellReveal.Effective(View(real), null));
            foreach (string mask in Views)
            {
                ExplorerView effective = ShellReveal.Effective(View(real), View(mask));
                Assert.Equal(new ExplorerView(real[0] == '1' && mask[0] == '1', real[2] == '1' && mask[2] == '1'), effective);
                foreach (bool hidden in new[] { false, true })
                    foreach (bool system in new[] { false, true })
                        Assert.False(!View(real).Shows(hidden, system) && effective.Shows(hidden, system), $"real {real}, mask {mask}: an item hidden {hidden}, system {system} would be shown");
            }
        }
    }

    /// <summary>
    /// SL-091 C-024: the process-wide view the hook sets before the app's Main is the mask RevealAsync's reveals are
    /// decided under, and clearing it gives the real view back.
    /// </summary>
    [Fact]
    public void OverrideExplorerView_IsTheMaskRevealsAreDecidedUnder()
    {
        try
        {
            ShellReveal.OverrideExplorerView(new ExplorerView(false, false));
            Assert.Equal(new ExplorerView(false, false), ShellReveal.EffectiveExplorerView());
        }
        finally
        {
            ShellReveal.OverrideExplorerView(null);
        }
        Assert.Equal(ShellReveal.ReadExplorerView(), ShellReveal.EffectiveExplorerView());
    }

    /// <summary>The table the gate decides by (SL-091): what Explorer lists, against each view.</summary>
    [Theory]
    [InlineData(false, false, "0|0", true)]
    [InlineData(false, true, "0|0", true)]
    [InlineData(true, false, "0|0", false)]
    [InlineData(true, false, "1|0", true)]
    [InlineData(true, true, "1|0", false)]
    [InlineData(true, true, "0|1", false)]
    [InlineData(true, true, "1|1", true)]
    public void ExplorerView_ShowsAnItemByItsOwnAttributes(bool hidden, bool system, string view, bool shown)
    {
        Assert.Equal(shown, View(view).Shows(hidden, system));
        Assert.Equal(shown, View(view).Hides(hidden, system) == ExplorerHides.None);
    }

    /// <summary>
    /// SL-088 criterion 6 on a real tree, with the shell's own parse on an STA thread as RevealAsync runs it, and an
    /// open that only records (no window is opened). A file whose parent was deleted after it was found, and a file
    /// deleted from a parent that is still there, are both refused as gone with no open. The parent that survives
    /// parses, which is the folder SL-047's climb would have opened in the file's place. A present file and a folder
    /// with a deny-all ACE in the same run parse and reach the open, so a parse that cannot work in this process
    /// (COM not initialised, say) fails here rather than passing 'never opened' for nothing. So do files whose names are
    /// device names with an extension (con.txt, aux.h, nul.log, com1.txt, and a file in a folder con.d), which the shell
    /// addresses exactly (SL-088 review R2-m2), and a file past MAX_PATH, whose ID list the shell gives back in 8.3 form.
    /// 'dup.' asked of the shell directly, beside 'dup', parses to 'dup' and is refused before the open.
    /// </summary>
    [Fact]
    public void Walk_OnRealTree_RefusesGoneTargetsAndLandsThePresentOne()
    {
        using var tree = new TempTree();
        tree.Folder("gone");
        tree.Folder("kept");
        string victim = tree.File(@"gone\victim.bin", 4096);
        string deleted = tree.File(@"kept\deleted.bin", 4096);
        string present = tree.File(@"kept\present.bin", 4096);
        string denied = tree.Folder(@"kept\denied");
        Directory.Delete(tree.PathOf("gone"), recursive: true);
        File.Delete(deleted);
        tree.Deny(denied, 0x1F01FF); // FILE_ALL_ACCESS
        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(denied));
        tree.Folder("reserved");
        Directory.CreateDirectory(tree.ExtendedRoot + @"\reserved\con.d");
        string[] reserved = [.. new[] { "con.txt", "aux.h", "nul.log", "com1.txt", @"con.d\inner.bin" }
            .Select(name => TempTree.WriteFile(tree.ExtendedRoot + @"\reserved\" + name, 4096)[4..])];
        string longFolder = tree.Folder(string.Join('\\', Enumerable.Repeat(new string('l', 60), 4)));
        string beyondMaxPath = tree.File(Path.Combine(longFolder, "far.bin"), 4096);
        Assert.True(beyondMaxPath.Length > 300, $"{beyondMaxPath.Length} characters");
        string decoy = tree.File(@"kept\dup", 4096);
        TempTree.WriteFile(tree.ExtendedRoot + @"\kept\dup.", 4096);
        string dupDot = decoy + ".";

        var opened = new List<IntPtr>();
        int survivorParse = -1;
        int viewReads = 0;
        var outcomes = new Dictionary<string, RevealOutcome>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // None of these is Hidden, so Explorer's view is never read for them (SL-091).
                Func<ExplorerView> view = () => { viewReads++; return new ExplorerView(false, false); };
                foreach (string path in new[] { victim, deleted, present, denied, beyondMaxPath }.Concat(reserved))
                {
                    outcomes[path] = ShellReveal.Walk(ShellReveal.Plan(path), path, ShellReveal.ParseDisplayName, ShellReveal.PathOfIdList,
                        pidl => { opened.Add(pidl); return 0; }, ShellReveal.FreeIdList, view);
                }

                // Past Plan, which refuses the name up front, to the shell itself.
                outcomes[dupDot] = ShellReveal.Walk(new RevealPlan(dupDot, null, dupDot), dupDot, ShellReveal.ParseDisplayName,
                    ShellReveal.PathOfIdList, pidl => { opened.Add(pidl); return 0; }, ShellReveal.FreeIdList, view);

                survivorParse = ShellReveal.ParseDisplayName(tree.Root, out IntPtr survivor);
                if (survivor != IntPtr.Zero)
                    ShellReveal.FreeIdList(survivor);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the reveals did not finish within 60 s");
        Assert.Null(failure);

        foreach (string gone in new[] { victim, deleted })
        {
            RevealOutcome outcome = outcomes[gone];
            Assert.Equal(RevealKind.Failed, outcome.Kind);
            Assert.Equal(RevealReason.NoLongerExists, outcome.Reason);
            Assert.Contains(outcome.HResult, new[] { NotFound, PathNotFound });
            Assert.Null(outcome.Shown);
        }

        Assert.Equal(0, survivorParse);
        foreach (string landing in new[] { present, denied, beyondMaxPath }.Concat(reserved))
            Assert.True(outcomes[landing].Kind == RevealKind.Exact, $"{landing}: {outcomes[landing]}");
        Assert.Equal(RevealKind.Failed, outcomes[dupDot].Kind);
        Assert.Equal(RevealReason.ParsedAsAnotherItem, outcomes[dupDot].Reason);
        Assert.Equal(3 + reserved.Length, opened.Count);
        Assert.All(opened, pidl => Assert.NotEqual(IntPtr.Zero, pidl));
        Assert.Equal(0, viewReads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_EmptyOrWhitespace_ThrowsArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => ShellReveal.Plan(path));
    }

    /// <summary>
    /// The shell calls Walk makes, answering with fixed HRESULTs and recording each call. The ID list stands for the path
    /// parsed unless <paramref name="pathOf"/> says otherwise, and Explorer's view is <paramref name="view"/> (everything
    /// shown when none is given), counted each time it is read.
    /// </summary>
    private sealed class FakeShell(int parse, int open, Func<IntPtr, string?>? pathOf = null, ExplorerView? view = null)
    {
        public static readonly IntPtr Pidl = new(0x1234);

        public List<string> Parsed { get; } = [];
        public List<IntPtr> Opened { get; } = [];
        public List<IntPtr> Freed { get; } = [];
        public int ViewReads { get; private set; }

        public RevealOutcome Walk(string path) =>
            ShellReveal.Walk(ShellReveal.Plan(path), path, Parse, pathOf ?? (_ => Parsed[^1]), Open, Freed.Add, ReadView);

        private ExplorerView ReadView()
        {
            ViewReads++;
            return view ?? new ExplorerView(true, true);
        }

        private int Parse(string path, out IntPtr pidl)
        {
            Parsed.Add(path);
            pidl = parse == 0 ? Pidl : IntPtr.Zero;
            return parse;
        }

        private int Open(IntPtr pidl)
        {
            Opened.Add(pidl);
            return open;
        }
    }
}
