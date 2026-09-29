// Bug (SL-084, raised by the SL-056 reflection, O-028): the size probe opens every charged file with
// FILE_READ_ATTRIBUTES and without FILE_FLAG_OPEN_REPARSE_POINT (SynchronousIoCancel.OpenForAttributes), and records
// ProbeFailed when that open fails. A reparse point whose tag is neither a link (name-surrogate bit) nor a cloud
// placeholder is classified as a plain File (EntryClassifier), so it is probed, and opening it through its reparse
// tag fails with ERROR_CANT_ACCESS_FILE (1920). AF_UNIX socket files (tag 0x80000023) and app-execution aliases
// (IO_REPARSE_TAG_APPEXECLINK) are such files; a profile scan meets them at once.
//
// Expected: the probe retries a refused reparse-point open with FILE_FLAG_OPEN_REPARSE_POINT before giving up, so
// the socket file is sized from its own handle and the scan reports no file that "could not be opened":
// ScanResult.ProbeFailures is 0 and the entry does not carry EntryFlags.ProbeFailed.
//
// Actual: the open fails with 1920, Scanner.Correct flags the entry ProbeFailed and counts it, so ProbeFailures is 1
// and the window tells the person "1 files could not be opened to read their true size on disk".

using System.Net.Sockets;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace SL084.ReparseProbeRetry.Tests;

public class ReparseProbeRetryTests
{
    [Fact]
    public void AnAfUnixSocketFileIsSizedNotReportedAsAFileThatCouldNotBeOpened()
    {
        // A short path: a socket's address is limited to 107 characters.
        string root = Path.Combine(Path.GetTempPath(), "fsa-tests", "sl084-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string socketPath = Path.Combine(root, "s.sock");
        string plainPath = Path.Combine(root, "plain.bin");
        ScanResult result;
        try
        {
            File.WriteAllBytes(plainPath, new byte[100]);
            // The socket stays bound for the whole scan: disposing a bound AF_UNIX socket deletes its file.
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                socket.Bind(new UnixDomainSocketEndPoint(socketPath)); // leaves an AF_UNIX reparse point file behind
                Assert.True(File.GetAttributes(socketPath).HasFlag(FileAttributes.ReparsePoint),
                    "setup: the bound socket left no reparse point file at " + socketPath);

                result = Scanner.Scan(root, new ScanProgress(), CancellationToken.None);
            }

            Assert.Equal(ScanStatus.Completed, result.Status);
            ScanStore store = result.Store;
            int socketEntry = -1;
            for (int i = 1; i < store.Count; i++)
            {
                if (store.GetPath(i).EndsWith(@"\s.sock", StringComparison.Ordinal))
                    socketEntry = i;
            }
            Assert.True(socketEntry > 0, "setup: the scan did not list the socket file");
            Assert.Equal(EntryKind.File, store.Kind(socketEntry));

            Assert.False(store.Flags(socketEntry).HasFlag(EntryFlags.ProbeFailed),
                "the AF_UNIX socket file was flagged ProbeFailed: its metadata open was refused and never retried with FILE_FLAG_OPEN_REPARSE_POINT");
            Assert.Equal(0, result.ProbeFailures);
        }
        finally
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
