using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Tests;

public class ShellRevealTests
{
    [Theory]
    [InlineData(@"C:\folder\file.txt", @"C:\folder\file.txt", @"C:\folder", @"C:\")]
    [InlineData(@"C:\folder\sub", @"C:\folder\sub", @"C:\folder", @"C:\")]
    public void Plan_PlainFileAndFolder_ExactItemFirst(string path, params string[] expectedCandidates)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Null(plan.UnaddressableName);
        Assert.Equal(expectedCandidates, plan.Candidates);
    }

    [Theory]
    [InlineData(@"C:\dup.", "dup.", @"C:\")]
    [InlineData(@"C:\folder\dup.", "dup.", @"C:\folder", @"C:\")]
    [InlineData(@"C:\folder\sp ", "sp ", @"C:\folder", @"C:\")]
    [InlineData(@"C:\con", "con", @"C:\")]
    [InlineData(@"C:\con.txt", "con.txt", @"C:\")]
    [InlineData(@"C:\folder\NUL", "NUL", @"C:\folder", @"C:\")]
    [InlineData(@"C:\COM¹", "COM¹", @"C:\")]
    [InlineData(@"C:\CONIN$", "CONIN$", @"C:\")]
    [InlineData(@"C:\x\dir.\in.txt", "dir.", @"C:\x", @"C:\")]
    public void Plan_UnaddressableNames_ClimbsToParentAndRecordsName(string path, string expectedBadName, params string[] expectedCandidates)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Equal(expectedBadName, plan.UnaddressableName);
        Assert.Equal(expectedCandidates, plan.Candidates);
    }

    [Theory]
    [InlineData(@"C:\folder\dup", @"C:\folder\dup")]
    [InlineData(@"C:\folder\ lead", @"C:\folder\ lead")]
    [InlineData(@"C:\folder\a,b;c.txt", @"C:\folder\a,b;c.txt")]
    [InlineData(@"C:\folder\😀_face.txt", @"C:\folder\😀_face.txt")]
    [InlineData(@"C:\folder\%TEMP%.txt", @"C:\folder\%TEMP%.txt")]
    public void Plan_UnusualAddressableNames_StaysExact(string path, string expectedFirstCandidate)
    {
        RevealPlan plan = ShellReveal.Plan(path);
        Assert.Null(plan.UnaddressableName);
        Assert.Equal(expectedFirstCandidate, plan.Candidates[0]);
    }

    [Fact]
    public void Plan_RootShapes_CandidatesAndStopAtRoot()
    {
        // Drive root "C:\"
        RevealPlan drivePlan = ShellReveal.Plan(@"C:\");
        Assert.Null(drivePlan.UnaddressableName);
        Assert.Single(drivePlan.Candidates);
        Assert.Equal(@"C:\", drivePlan.Candidates[0]);

        // Bare drive "C:" normalises to "C:\"
        RevealPlan bareDrivePlan = ShellReveal.Plan(@"C:");
        Assert.Null(bareDrivePlan.UnaddressableName);
        Assert.Single(bareDrivePlan.Candidates);
        Assert.Equal(@"C:\", bareDrivePlan.Candidates[0]);

        // Ancestor chain never yields "C:" (only "C:\")
        RevealPlan chainPlan = ShellReveal.Plan(@"C:\a\b");
        Assert.DoesNotContain("C:", chainPlan.Candidates);
        Assert.Equal(@"C:\", chainPlan.Candidates[^1]);

        // UNC root stops at \\server\share
        RevealPlan uncRootPlan = ShellReveal.Plan(@"\\server\share");
        Assert.Null(uncRootPlan.UnaddressableName);
        Assert.Single(uncRootPlan.Candidates);
        Assert.Equal(@"\\server\share", uncRootPlan.Candidates[0]);

        // UNC child stops at \\server\share
        RevealPlan uncPlan = ShellReveal.Plan(@"\\server\share\sub\file.txt");
        Assert.Null(uncPlan.UnaddressableName);
        Assert.Equal(@"\\server\share\sub\file.txt", uncPlan.Candidates[0]);
        Assert.Equal(@"\\server\share\sub", uncPlan.Candidates[1]);
        Assert.Equal(@"\\server\share", uncPlan.Candidates[2]);
        Assert.Equal(3, uncPlan.Candidates.Count);
    }

    [Theory]
    [InlineData(@"\\?\C:\folder\file.txt", @"C:\folder\file.txt")]
    [InlineData(@"\\?\UNC\server\share\folder\file.txt", @"\\server\share\folder\file.txt")]
    public void Plan_PrefixesStripped(string extendedPath, string expectedDisplay)
    {
        RevealPlan plan = ShellReveal.Plan(extendedPath);
        Assert.Equal(expectedDisplay, plan.Candidates[0]);
    }

    [Fact]
    public void Plan_LongPaths_NotTruncated()
    {
        // 300 characters path with components <= 100
        string seg = new string('a', 50);
        string path300 = @"C:\" + string.Join(@"\", Enumerable.Repeat(seg, 5)) + @"\test.txt";
        Assert.True(path300.Length >= 260);
        RevealPlan plan300 = ShellReveal.Plan(path300);
        Assert.Equal(path300, plan300.Candidates[0]);

        // 600 characters path with components <= 100
        string path600 = @"C:\" + string.Join(@"\", Enumerable.Repeat(seg, 11)) + @"\test.txt";
        Assert.True(path600.Length >= 550);
        RevealPlan plan600 = ShellReveal.Plan(path600);
        Assert.Equal(path600, plan600.Candidates[0]);
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
    public void Walk_FirstCandidateSucceeds_ReturnsExact()
    {
        var plan = new RevealPlan([@"C:\a\b.txt", @"C:\a", @"C:\"], null, @"C:\a\b.txt");
        int calls = 0;
        RevealOutcome outcome = ShellReveal.Walk(plan, c =>
        {
            calls++;
            return 0; // S_OK
        });

        Assert.Equal(1, calls);
        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.Equal(RevealReason.None, outcome.Reason);
        Assert.Equal(@"C:\a\b.txt", outcome.Shown);
        Assert.Equal(0, outcome.HResult);
    }

    [Fact]
    public void Walk_ItemDeleted_ClimbsToParent()
    {
        var plan = new RevealPlan([@"C:\a\b.txt", @"C:\a", @"C:\"], null, @"C:\a\b.txt");
        int calls = 0;
        RevealOutcome outcome = ShellReveal.Walk(plan, c =>
        {
            calls++;
            if (c == @"C:\a\b.txt")
                return unchecked((int)0x80070002); // ERROR_FILE_NOT_FOUND
            return 0; // S_OK
        });

        Assert.Equal(2, calls);
        Assert.Equal(RevealKind.Ancestor, outcome.Kind);
        Assert.Equal(RevealReason.NoLongerExists, outcome.Reason);
        Assert.Equal(@"C:\a", outcome.Shown);
        Assert.Equal(0, outcome.HResult);
    }

    [Fact]
    public void Walk_EntireChainDeleted_ReturnsFailed()
    {
        var plan = new RevealPlan([@"C:\a\b.txt", @"C:\a", @"C:\"], null, @"C:\a\b.txt");
        int calls = 0;
        RevealOutcome outcome = ShellReveal.Walk(plan, _ =>
        {
            calls++;
            return unchecked((int)0x80070003); // ERROR_PATH_NOT_FOUND
        });

        Assert.Equal(3, calls);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(unchecked((int)0x80070003), outcome.HResult);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED
    [InlineData(unchecked((int)0x80070035))] // ERROR_BAD_NETPATH
    public void Walk_AccessDeniedOrNetworkError_StopsWithoutClimbing(int errorCode)
    {
        var plan = new RevealPlan([@"C:\a\b.txt", @"C:\a", @"C:\"], null, @"C:\a\b.txt");
        int calls = 0;
        RevealOutcome outcome = ShellReveal.Walk(plan, _ =>
        {
            calls++;
            return errorCode;
        });

        // Must stop immediately and not climb
        Assert.Equal(1, calls);
        Assert.Equal(RevealKind.Failed, outcome.Kind);
        Assert.Equal(errorCode, outcome.HResult);
    }

    [Fact]
    public void Walk_UnaddressableName_ReturnsAncestorNameUnaddressable()
    {
        var plan = new RevealPlan([@"C:\folder", @"C:\"], "dup.", @"C:\folder\dup.");
        int calls = 0;
        RevealOutcome outcome = ShellReveal.Walk(plan, _ =>
        {
            calls++;
            return 0; // S_OK on C:\folder
        });

        Assert.Equal(1, calls);
        Assert.Equal(RevealKind.Ancestor, outcome.Kind);
        Assert.Equal(RevealReason.NameUnaddressable, outcome.Reason);
        Assert.Equal("dup.", outcome.UnaddressableName);
        Assert.Equal(@"C:\folder", outcome.Shown);
    }

    [Fact]
    public void Walk_HiddenAndSystemAttributes_DetectedOnExactItem()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"hs_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, "test");
            File.SetAttributes(temp, FileAttributes.Hidden | FileAttributes.System);

            var plan = ShellReveal.Plan(temp);
            RevealOutcome outcome = ShellReveal.Walk(plan, _ => 0);

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
        RevealPlan plan = ShellReveal.Plan(@"C:\");
        RevealOutcome outcome = ShellReveal.Walk(plan, _ => 0);

        Assert.Equal(RevealKind.Exact, outcome.Kind);
        Assert.False(outcome.Hidden);
        Assert.False(outcome.System);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_EmptyOrWhitespace_ThrowsArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => ShellReveal.Plan(path));
    }
}
