using System.ComponentModel;
using System.Diagnostics;
using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-008: pressing 'Rescan as administrator' (RescanAsAdministrator.Start), with the start of the process handed in, so that
/// nothing here raises a prompt and nothing starts. Real prompts cannot be driven from a test (the consent dialog is on the
/// secure desktop): what is proved is what Start asks Windows for, and what it says when Windows answers each way.
/// The program is the same exe started again with the drive as its one argument, quoted so that it reaches the new copy as written
/// (SL-007's class: a backslash before the closing quote escapes it); the report option never appears.
/// </summary>
public class RescanAsAdministratorStartTests
{
    private const string SpacedExe = @"C:\Program Files\Some Folder\Filesystem Analyser\FsAnalyser.App.exe";

    // Records what Start asked for, and answers as handed (null: Windows took it).
    private sealed class Recorder(Func<ProcessStartInfo, Process?>? answer = null)
    {
        public List<ProcessStartInfo> Asked { get; } = [];

        public Process? Start(ProcessStartInfo info)
        {
            Asked.Add(info);
            return answer?.Invoke(info);
        }
    }

    public static IEnumerable<object[]> EveryDriveRoot() =>
        Enumerable.Range('A', 26).SelectMany(c => new[] { $@"{(char)c}:\", $@"{char.ToLowerInvariant((char)c)}:\" }).Select(root => new object[] { root });

    // O-011: every root A:\ to Z:\ and a:\ to z:\, with a program path that holds spaces.
    [Theory]
    [MemberData(nameof(EveryDriveRoot))]
    public void EveryDriveRootReachesTheNewCopyAsItsOneArgumentAndNothingElse(string root)
    {
        var recorder = new Recorder();
        Assert.Null(RescanAsAdministrator.Start(SpacedExe, root, recorder.Start));

        ProcessStartInfo info = Assert.Single(recorder.Asked);
        Assert.Equal(SpacedExe, info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Empty(info.ArgumentList);

        // The new copy's argv, split as Windows splits a command line, is exactly the root.
        string[] argv = TestNative.SplitArguments(info.Arguments);
        Assert.Equal([root], argv);
        Assert.DoesNotContain("--report", info.Arguments);
        // And the launch route takes it from there as it is: no repair, and the root the scan will take is the root.
        Assert.Equal(argv, CommandLine.Arguments(argv, "FsAnalyser.App.exe " + info.Arguments));
        Assert.Equal(root, CommandLine.RootArgument(argv[0]));
    }

    // Lesson #914: crafted roots through the same path. The extended spelling of a drive is a drive (its display form); a folder, a
    // share, a bare letter and a root that tries to carry a second argument or the report option are not started at all.
    [Theory]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\?\z:\")]
    public void AnExtendedDriveRootIsStartedAndSplitAsItWasWritten(string root)
    {
        var recorder = new Recorder();
        Assert.Null(RescanAsAdministrator.Start(SpacedExe, root, recorder.Start));
        Assert.Equal([root], TestNative.SplitArguments(Assert.Single(recorder.Asked).Arguments));
    }

    [Theory]
    [InlineData(@"C:\dir")]
    [InlineData(@"C:\dir\")]
    [InlineData(@"C:\a folder\")]
    [InlineData(@"C:")]
    [InlineData(@"C:\\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\UNC\server\share")]
    [InlineData(@"C:\"" --report out.json")]
    [InlineData(@"C:\"" D:\")]
    [InlineData(@"--report")]
    [InlineData("")]
    public void ARootThatIsNotAWholeDriveIsNeverStarted(string root)
    {
        var recorder = new Recorder();
        Assert.Equal(RescanAsAdministrator.NotADriveLine, RescanAsAdministrator.Start(SpacedExe, root, recorder.Start));
        Assert.Empty(recorder.Asked);
    }

    // O-012: a declined prompt is Win32's ERROR_CANCELLED. Uncaught, the click would reach the crash handler.
    [Fact]
    public void ADeclinedPromptIsTheDeclinedLineAndWindowsIsCalledOnce()
    {
        var recorder = new Recorder(_ => throw new Win32Exception(1223));
        Assert.Equal(RescanAsAdministrator.DeclinedLine, RescanAsAdministrator.Start(SpacedExe, @"C:\", recorder.Start));
        Assert.Single(recorder.Asked);
        Assert.Contains("declined", RescanAsAdministrator.DeclinedLine);
        Assert.Matches(@"not .*started", RescanAsAdministrator.DeclinedLine);
        // The words say it is the prompt, not an error, and carry no code.
        Assert.DoesNotMatch(@"\d", RescanAsAdministrator.DeclinedLine);
    }

    // O-013: any other refusal is a branch apart from the decline; the line says it was not started, with the code and no path.
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(1155)]
    [InlineData(740)]
    [InlineData(1)]
    [InlineData(1222)] // next to the decline, not the decline
    [InlineData(1224)]
    [InlineData(0)]
    public void AnyOtherRefusalIsTheRefusedLineWithItsCodeAndNoPath(int code)
    {
        var recorder = new Recorder(_ => throw new Win32Exception(code));
        string? line = RescanAsAdministrator.Start(SpacedExe, @"C:\", recorder.Start);
        Assert.Equal(RescanAsAdministrator.RefusedLine(code), line);
        Assert.NotEqual(RescanAsAdministrator.DeclinedLine, line);
        Assert.Single(recorder.Asked);

        Assert.Matches(@"was not started", line);
        Assert.Contains($"(error {code})", line);
        Assert.All(line!, c => Assert.InRange(c, ' ', '~'));
        Assert.DoesNotContain("FsAnalyser", line);
        Assert.DoesNotContain("Program Files", line);
        Assert.DoesNotContain(@"C:\", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoProgramFileIsTheNoProgramLineAndWindowsIsNeverCalled(string? exe)
    {
        var recorder = new Recorder();
        Assert.Equal(RescanAsAdministrator.NoProgramLine, RescanAsAdministrator.Start(exe, @"C:\", recorder.Start));
        Assert.Empty(recorder.Asked);
    }

    // Anything but Windows' refusal is a bug in the app and is not swallowed into a line.
    [Fact]
    public void AnExceptionThatIsNotWindowsRefusalIsNotCaught()
    {
        var recorder = new Recorder(_ => throw new InvalidOperationException("a bug"));
        Assert.Throws<InvalidOperationException>(() => RescanAsAdministrator.Start(SpacedExe, @"C:\", recorder.Start));
        Assert.Single(recorder.Asked);
    }

    // A process object that says whether it was let go.
    private sealed class TrackedProcess : Process
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    // Started: the process Windows gives back is not held, and the answer is null.
    [Fact]
    public void AStartedCopyIsNullAndItsProcessIsLetGo()
    {
        var process = new TrackedProcess();
        var recorder = new Recorder(_ => process);
        Assert.Null(RescanAsAdministrator.Start(SpacedExe, @"C:\", recorder.Start));
        Assert.True(process.WasDisposed);
        // And a start that gave no process (the request was taken) is just as well.
        Assert.Null(RescanAsAdministrator.Start(SpacedExe, @"C:\", new Recorder().Start));
    }

    // The words are all ASCII, and the refusal's carries the code and nothing of the machine.
    [Fact]
    public void EveryLineStartGivesIsAsciiAndHoldsNeitherTheProgramNorTheRoot()
    {
        var lines = new List<string?>
        {
            RescanAsAdministrator.Start(SpacedExe, @"C:\", new Recorder(_ => throw new Win32Exception(1223)).Start),
            RescanAsAdministrator.Start(SpacedExe, @"C:\", new Recorder(_ => throw new Win32Exception(5)).Start),
            RescanAsAdministrator.Start(null, @"C:\", new Recorder().Start),
            RescanAsAdministrator.Start(SpacedExe, @"C:\dir", new Recorder().Start),
        };
        foreach (string? line in lines)
        {
            Assert.NotNull(line);
            Assert.All(line!, c => Assert.InRange(c, ' ', '~'));
            Assert.DoesNotContain("Program Files", line);
            Assert.DoesNotContain(@"C:\", line);
        }
    }
}
