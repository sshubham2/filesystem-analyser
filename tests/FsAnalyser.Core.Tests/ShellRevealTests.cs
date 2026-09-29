using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Tests;

public class ShellRevealTests
{
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

    [Fact]
    public void Walk_HiddenAndSystemAttributes_DetectedOnExactItem()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"hs_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, "test");
            File.SetAttributes(temp, FileAttributes.Hidden | FileAttributes.System);

            RevealOutcome outcome = new FakeShell(parse: 0, open: 0).Walk(temp);

            Assert.Equal(RevealKind.Exact, outcome.Kind);
            Assert.True(outcome.Hidden);
            Assert.True(outcome.System);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.SetAttributes(temp, FileAttributes.Normal);
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void Walk_VolumeRoot_HiddenAndSystemStayFalse()
    {
        RevealOutcome outcome = new FakeShell(parse: 0, open: 0).Walk(@"C:\");

        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.False(outcome.Hidden);
        Assert.False(outcome.System);
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
        var outcomes = new Dictionary<string, RevealOutcome>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (string path in new[] { victim, deleted, present, denied, beyondMaxPath }.Concat(reserved))
                {
                    outcomes[path] = ShellReveal.Walk(ShellReveal.Plan(path), path, ShellReveal.ParseDisplayName, ShellReveal.PathOfIdList,
                        pidl => { opened.Add(pidl); return 0; }, ShellReveal.FreeIdList);
                }

                // Past Plan, which refuses the name up front, to the shell itself.
                outcomes[dupDot] = ShellReveal.Walk(new RevealPlan(dupDot, null, dupDot), dupDot, ShellReveal.ParseDisplayName,
                    ShellReveal.PathOfIdList, pidl => { opened.Add(pidl); return 0; }, ShellReveal.FreeIdList);

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
    /// parsed unless <paramref name="pathOf"/> says otherwise.
    /// </summary>
    private sealed class FakeShell(int parse, int open, Func<IntPtr, string?>? pathOf = null)
    {
        public static readonly IntPtr Pidl = new(0x1234);

        public List<string> Parsed { get; } = [];
        public List<IntPtr> Opened { get; } = [];
        public List<IntPtr> Freed { get; } = [];

        public RevealOutcome Walk(string path) =>
            ShellReveal.Walk(ShellReveal.Plan(path), path, Parse, pathOf ?? (_ => Parsed[^1]), Open, Freed.Add);

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
