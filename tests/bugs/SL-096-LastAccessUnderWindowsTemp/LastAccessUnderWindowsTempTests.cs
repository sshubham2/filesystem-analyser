// Bug (SL-096): the scan's keep-last-access counters over-report under %SystemRoot%\Temp. Folders there have their
// last-access time moved to now by the scan, and the scan still counts every one of them as kept.
//
// On this PC, in C:\Windows\Temp (and not in the user's own TEMP), opening a child file or folder with any write-class
// access (FILE_WRITE_ATTRIBUTES included) moves its PARENT folder's last-access time to now, inside the child's
// CreateFile. luafv.sys (UAC File Virtualization) is the driver that works in exactly those cases. The scanner keeps a
// folder's last access by opening it with access|FILE_WRITE_ATTRIBUTES and setting LastAccessTime = -1 through that
// handle (SynchronousIoCancel.cs, KeptLastAccess is true whenever the -1 set succeeds). That keep-open is itself a
// write-class open of each child folder, so under C:\Windows\Temp it moves the parent's last access, which the parent's
// own -1 handle cannot prevent. Measured with the real scan on nested trees: (asked, kept) = (7, 7) while 3 of the 7
// folders (those with a child folder) had moved, in 5 of 5 trees; flat folders never moved; the same trees under the
// user's TEMP moved 0 of 7. This is the real side effect behind HeldOpenTreeTests.TheScanChangesNoEntrysMetadata, whose
// precondition fails first under the engine's TEMP=C:\WINDOWS\TEMP (its fixtures back-date parent before child, which
// resets each parent) and so hides it.
//
// Expected: the scan's counters are true. A folder whose last-access time moved during the scan is not counted as kept,
// so LastAccessKept never exceeds the number of folders whose last access actually stayed where it was. (Either the
// scan really keeps every folder's time here, or it stops claiming it did.)
//
// Actual: LastAccessKept equals LastAccessAsked (7 of 7) while only 4 of the 7 folders kept their time.
//
// What the test does and what it costs: it builds a tree of 7 folders and 3 small files (about 70 KB) under
// %SystemRoot%\Temp\fsa-sl096-<guid>, back-dates every entry 48 h DEEPEST FIRST (so no child is opened after its
// parent and the fixture's own back-dating resets nothing), checks every entry reads as old, runs the real public
// Scanner.Scan, and reads every folder's last access again. It removes the tree afterwards (best effort, one retry).
// Worst case on this machine: those ~10 entries and 70 KB, gone after the test; it runs well under a second. The tree
// is made at %SystemRoot%\Temp whatever TEMP and TMP say, because that location is the trigger. The test is skipped,
// with the reason, where that folder is not writable by this user or is not on NTFS.

using FsAnalyser.Core.Scanning;

namespace SL096.LastAccessUnderWindowsTemp.Tests;

internal static class WindowsTemp
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");

    /// <summary>Null when a folder can be made, and removed, in %SystemRoot%\Temp on NTFS; else why not.</summary>
    public static string? WhyItCannotHostTheTree()
    {
        string folder = Folder;
        try
        {
            if (!Directory.Exists(folder))
                return folder + " does not exist";
            string format = new DriveInfo(Path.GetPathRoot(folder)!).DriveFormat;
            if (format != "NTFS")
                return $"{folder} is on {format}, not NTFS";
            string probe = Path.Combine(folder, "fsa-sl096-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{folder} cannot be written by this user ({e.GetType().Name})";
        }
    }
}

/// <summary>A fact that skips itself, at discovery, when %SystemRoot%\Temp cannot hold the tree.</summary>
public sealed class WindowsTempFactAttribute : FactAttribute
{
    public WindowsTempFactAttribute()
    {
        string? why = WindowsTemp.WhyItCannotHostTheTree();
        if (why is not null)
            Skip = "SL-096 needs a folder it can make in %SystemRoot%\\Temp: " + why;
    }
}

public class LastAccessUnderWindowsTempTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [WindowsTempFact]
    public void AFolderTheScanMovedIsNotCountedAsKeptUnderTheWindowsTempFolder()
    {
        string root = Path.Combine(WindowsTemp.Folder, "fsa-sl096-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] folders =
            [
                root,
                Path.Combine(root, "plain"),
                Path.Combine(root, "plain", "sub"),
                Path.Combine(root, "plain", "empty"),
                Path.Combine(root, "links"),
                Path.Combine(root, "links", "a-first"),
                Path.Combine(root, "links", "b-second"),
            ];
            foreach (string folder in folders)
                Directory.CreateDirectory(folder);
            string[] files =
            [
                Path.Combine(root, "top.bin"),
                Path.Combine(root, "plain", "small.txt"),
                Path.Combine(root, "plain", "sub", "deeper.bin"),
            ];
            File.WriteAllBytes(files[0], new byte[100]);
            File.WriteAllBytes(files[1], new byte[10_000]);
            File.WriteAllBytes(files[2], new byte[65_536]);

            // Deepest first: each back-dating opens its entry with FILE_WRITE_ATTRIBUTES, which here moves that entry's
            // parent, so a parent has to be back-dated after every one of its children.
            DateTime old = DateTime.UtcNow.AddHours(-48);
            string[] everything = [.. folders, .. files];
            foreach (string path in everything.OrderByDescending(Depth))
            {
                if (Directory.Exists(path))
                    Directory.SetLastAccessTimeUtc(path, old);
                else
                    File.SetLastAccessTimeUtc(path, old);
            }

            var before = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in everything)
            {
                DateTime read = LastAccess(path);
                Assert.True(read < DateTime.UtcNow - TimeSpan.FromHours(47),
                    $"setup: {path} was not back-dated: its last access reads {read:u}. This machine may record no access, or the back-dating itself moved it.");
                before[path] = read;
            }

            ScanResult result = Scanner.Scan(root, new ScanProgress(), CancellationToken.None);

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Empty(result.Unreadable);
            Assert.True(result.LastAccessAsked > 0, "setup: the scan asked no folder to keep its last-access time, so it counts nothing here");

            var moved = new List<string>();
            foreach (string folder in folders)
            {
                if (LastAccess(folder) - before[folder] > Hour)
                    moved.Add(Path.GetRelativePath(root, folder));
            }
            int unmoved = folders.Length - moved.Count;

            Assert.True(result.LastAccessKept <= unmoved,
                $"the scan reports it kept {result.LastAccessKept} of the {result.LastAccessAsked} folders' last-access times it asked to keep, " +
                $"but only {unmoved} of the {folders.Length} folders under {root} still read 48 h old: {moved.Count} had their last access moved to now " +
                $"({string.Join(", ", moved.Select(m => m == "." ? "<root>" : m))}). In %SystemRoot%\\Temp the scan's own FILE_WRITE_ATTRIBUTES open of " +
                "each child folder moves its parent's last access, which the parent's -1 handle cannot prevent, yet the folder is counted as kept " +
                "because the -1 set succeeded.");
        }
        finally
        {
            Remove(root);
        }
    }

    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);

    private static DateTime LastAccess(string path) =>
        Directory.Exists(path) ? Directory.GetLastAccessTimeUtc(path) : File.GetLastAccessTimeUtc(path);

    private static void Remove(string root)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
