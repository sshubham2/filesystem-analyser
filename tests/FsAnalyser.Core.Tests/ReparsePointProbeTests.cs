using System.Net.Sockets;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A file whose reparse tag no filter handles, an AF_UNIX socket or an app-execution alias, is not a link, so it is
/// charged and probed; its first open fails with ERROR_CANT_ACCESS_FILE (1920), and the probe opens the reparse point
/// itself once more and sizes the file from that handle (SL-084). Nothing else is retried, and a retry that fails
/// reports its own error, so a refusal stays counted and a file gone in between stays gone.
/// </summary>
public class ReparsePointProbeTests
{
    private const uint AfUnixTag = 0x80000023; // IO_REPARSE_TAG_AF_UNIX
    private const uint Synchronize = 0x100000; // the right CreateFileW adds to every open (SL-045)
    private const int SharingViolation = 32;

    /// <summary>
    /// The socket stays bound for the whole scan: disposing a bound AF_UNIX socket deletes its file (risk spike
    /// temp-tree-af-unix-fixture-reproduces-1920), and a scan of a tree without it would pass having retried nothing.
    /// So would one where the first open no longer fails, which is checked before the scan (review m8).
    /// </summary>
    [Fact]
    public void AnAfUnixSocketBoundForTheWholeScanIsSizedFromItsOwnHandle()
    {
        using var tree = new TempTree();
        tree.File("plain.bin", 100);
        string socketPath = tree.PathOf("s.sock"); // a socket's address holds at most 107 characters
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); // disposed before the tree
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));

        RawEntry listed = Scans.Listing(tree.Root)["s.sock"];
        Assert.True((listed.Attributes & Kernel32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && listed.ReparseTag == AfUnixTag,
            $"precondition: s.sock should be listed as a reparse point with tag 0x{AfUnixTag:X8}, but shows 0x{listed.Attributes:X} with tag 0x{listed.ReparseTag:X8}");
        Assert.Equal(Kernel32.ERROR_CANT_ACCESS_FILE, FirstOpen(socketPath));

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        int entry = Scans.Find(result, socketPath);
        Assert.Equal(EntryKind.File, result.Store.Kind(entry));
        Assert.Equal(EntryFlags.SizeProbed, result.Store.Flags(entry) & (EntryFlags.SizeProbed | EntryFlags.ProbeFailed));
        Assert.Equal((2, 0), (result.Probes, result.ProbeFailures));
    }

    [Fact]
    public void AnAppExecutionAliasInAScannedTreeIsSizedFromItsOwnHandle()
    {
        using var tree = new TempTree();
        tree.File("plain.bin", 100);
        string alias = tree.AppExecAlias("app.exe", 5000);

        RawEntry listed = Scans.Listing(tree.Root)["app.exe"];
        Assert.True((listed.Attributes & Kernel32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && listed.ReparseTag == TempTree.AppExecLinkTag,
            $"precondition: app.exe should be listed as an app-execution alias, but shows 0x{listed.Attributes:X} with tag 0x{listed.ReparseTag:X8}");
        Assert.Equal(Kernel32.ERROR_CANT_ACCESS_FILE, FirstOpen(alias));

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        int entry = Scans.Find(result, alias);
        Assert.Equal(EntryKind.File, result.Store.Kind(entry));
        Assert.Equal(EntryFlags.SizeProbed, result.Store.Flags(entry)); // probed, not failed, and it agreed with its listing
        Assert.Equal(5000, result.Store.Size(entry));
        Assert.Equal((2, 0), (result.Probes, result.ProbeFailures));
        Assert.Equal(5100, result.Totals.Bytes);
    }

    /// <summary>
    /// The sizes are the retried handle's FileStandardInfo: an alias planted over data keeps it, and the probe reads
    /// exactly that, in three calls (the first open, the retry, the query). A retry that swallowed its error and
    /// returned zeros would pass both scan Facts above, whose files are listed at the size they have.
    /// </summary>
    [Fact]
    public void AProbeOfAnAliasOverNBytesReadsTheRetriedHandlesSizes()
    {
        const long N = 12345;
        using var tree = new TempTree();
        string alias = tree.AppExecAlias("app.exe", N);
        RawEntry listed = Scans.Listing(tree.Root)["app.exe"];

        using var io = new SynchronousIoCancel(CancellationToken.None);
        int error = SizeProbe.Probe(io, LongPath.ToExtended(alias), out long endOfFile, out long allocationSize);

        Assert.Equal(0, error);
        Assert.Equal(3, io.Calls);
        Assert.Equal(N, endOfFile);
        Assert.InRange(allocationSize, N, N + 64 * 1024);
        Assert.Equal(listed.AllocationSize, allocationSize);
    }

    /// <summary>
    /// The first open stays filtered, at flags 0: on an alias it still fails with 1920, and only the retry's entry point
    /// opens it. Nothing else holds the flag off OpenForAttributes, where it would skip wof.sys, cldflt and dedup for
    /// every file the scan probes.
    /// </summary>
    [Fact]
    public void TheFirstOpenOfAnAliasStillFailsAndOnlyTheRetryOpensIt()
    {
        using var tree = new TempTree();
        string alias = LongPath.ToExtended(tree.AppExecAlias("app.exe", 10));

        using var io = new SynchronousIoCancel(CancellationToken.None);
        int first = io.OpenForAttributes(alias, out SafeFileHandle? none);
        int retry = io.OpenReparsePointForAttributes(alias, out SafeFileHandle? opened);
        try
        {
            Assert.Equal((Kernel32.ERROR_CANT_ACCESS_FILE, true), (first, none is null));
            Assert.Equal((0, false), (retry, opened is null || opened.IsInvalid));
        }
        finally
        {
            if (opened is not null)
                io.Close(opened);
        }
    }

    /// <summary>
    /// A file whose first open succeeds is sized from that handle and never retried: two calls, the open and the query.
    /// A WOF-compressed file is a reparse point too, and its figures are still the filter's: less on disk than its size.
    /// </summary>
    [Fact]
    public void AFileWhoseFirstOpenSucceedsIsNeverRetried()
    {
        using var tree = new TempTree();
        string plain = tree.File("plain.bin", 10);
        string wof = tree.TextFile("wof.txt", 4 * TempTree.MiB);
        TempTree.WofCompress(wof);

        using var io = new SynchronousIoCancel(CancellationToken.None);
        Assert.Equal(0, SizeProbe.Probe(io, LongPath.ToExtended(plain), out long plainSize, out _));
        Assert.Equal((2, 10L), (io.Calls, plainSize));

        Assert.Equal(0, SizeProbe.Probe(io, LongPath.ToExtended(wof), out long wofSize, out long wofOnDisk));
        Assert.Equal((4, 4 * TempTree.MiB), (io.Calls, wofSize));
        Assert.InRange(wofOnDisk, 1, 2 * TempTree.MiB);
    }

    /// <summary>A plain file refused by a SYNCHRONIZE deny (SL-045) fails its one open with 5: no retry, and it is counted.</summary>
    [Fact]
    public void APlainFileRefusedBySynchronizeDenyIsNotRetriedAndStaysProbeFailed()
    {
        using var tree = new TempTree();
        string refused = tree.File("refused.bin", 300);
        tree.Deny(refused, Synchronize);

        using (var io = new SynchronousIoCancel(CancellationToken.None))
        {
            Assert.Equal(Kernel32.ERROR_ACCESS_DENIED, SizeProbe.Probe(io, LongPath.ToExtended(refused), out _, out _));
            Assert.Equal(1, io.Calls);
        }

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal((1, 1), (result.Probes, result.ProbeFailures));
        int entry = Scans.Find(result, refused);
        Assert.Equal(EntryFlags.ProbeFailed, result.Store.Flags(entry));
        Assert.Equal(300, result.Store.Size(entry)); // the listing's, kept
    }

    /// <summary>
    /// An alias under a SYNCHRONIZE deny: the first open still fails with 1920, since the tag is met before the access
    /// check refuses, and it is the retry that is refused, with 5 (measured, both asserted here). The probe reports the
    /// retry's 5, never the 1920, and the entry stays ProbeFailed and counted.
    /// </summary>
    [Fact]
    public void AnAliasRefusedBySynchronizeDenyFailsItsRetryAndStaysProbeFailed()
    {
        using var tree = new TempTree();
        string alias = tree.AppExecAlias("app.exe", 4096);
        tree.Deny(alias, Synchronize);

        using (var io = new SynchronousIoCancel(CancellationToken.None))
        {
            string path = LongPath.ToExtended(alias);
            int first = io.OpenForAttributes(path, out _);
            int retry = io.OpenReparsePointForAttributes(path, out _);
            Assert.True((first, retry) == (Kernel32.ERROR_CANT_ACCESS_FILE, Kernel32.ERROR_ACCESS_DENIED),
                $"the deny should refuse the retry (5), not the first open (1920), but the first open gave {first} and the retry {retry}");
            Assert.Equal(Kernel32.ERROR_ACCESS_DENIED, SizeProbe.Probe(io, path, out _, out _));
        }

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal((1, 1), (result.Probes, result.ProbeFailures));
        int entry = Scans.Find(result, alias);
        Assert.Equal(EntryFlags.ProbeFailed, result.Store.Flags(entry));
        Assert.Equal(4096, result.Store.Size(entry)); // the listing's, kept
    }

    /// <summary>
    /// A retry that fails reports its own error, fed in through the guard's seam (review M2): a file deleted between the
    /// two opens is gone, kept at its listing sizes and not counted; one refused or held on the second open is counted.
    /// Never the first open's 1920, and never 0 with zeros.
    /// </summary>
    [Theory]
    [InlineData(Kernel32.ERROR_FILE_NOT_FOUND, false)]
    [InlineData(Kernel32.ERROR_PATH_NOT_FOUND, false)]
    [InlineData(Kernel32.ERROR_ACCESS_DENIED, true)]
    [InlineData(SharingViolation, true)]
    public void ARetryThatFailsReportsItsOwnError(int retryError, bool counted)
    {
        using var tree = new TempTree();
        string plain = tree.File("plain.bin", 100);
        string alias = tree.AppExecAlias("app.exe", 7000);

        using (var io = new SynchronousIoCancel(CancellationToken.None))
        {
            var seen = new List<int>();
            io.OverrideReparseRetryError = error =>
            {
                seen.Add(error);
                return retryError;
            };
            Assert.Equal(retryError, SizeProbe.Probe(io, LongPath.ToExtended(alias), out long endOfFile, out long allocationSize));
            Assert.Equal(new[] { 0 }, seen); // the retry did open; the seam refused it after
            Assert.Equal((0L, 0L), (endOfFile, allocationSize));
            Assert.Equal(0, SizeProbe.Probe(io, LongPath.ToExtended(plain), out _, out _));
            Assert.Single(seen); // a file whose first open succeeds never reaches the retry
        }

        var options = new ScanOptions
        {
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                io.OverrideReparseRetryError = _ => retryError;
                return SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
            },
        };
        ScanResult result = Scans.Run(tree.Root, options);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal((2, counted ? 1 : 0, counted ? 0 : 1), (result.Probes, result.ProbeFailures, result.VanishedBeforeProbe));
        int entry = Scans.Find(result, alias);
        Assert.Equal(counted ? EntryFlags.ProbeFailed : EntryFlags.None, result.Store.Flags(entry));
        Assert.Equal(7000, result.Store.Size(entry)); // the listing's, kept
        Assert.Equal(EntryFlags.SizeProbed, result.Store.Flags(Scans.Find(result, plain)));
    }

    // The probe's first open, alone, as SizeProbe makes it: its error, with no handle left open.
    private static int FirstOpen(string path)
    {
        using var io = new SynchronousIoCancel(CancellationToken.None);
        int error = io.OpenForAttributes(LongPath.ToExtended(path), out SafeFileHandle? handle);
        if (handle is not null)
            io.Close(handle);
        return error;
    }
}
