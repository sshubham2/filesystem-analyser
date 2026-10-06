using System.Diagnostics;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-056's criteria 1, 3, 6 and 7 on one real temp tree, scanned once, with the files that matter held open by
/// other processes (tests/HoldRig): grow.bin, created and grown past 256 MiB with share Read, and locked.bin, held
/// with no sharing at all. Beside them plain files and folders, a hardlinked file whose stale name the scan meets
/// first, and a control folder outside the root.
/// </summary>
/// <remarks>
/// How the tree is observed is the point of the fixture (review B1). Opening a file makes Windows refresh its
/// stale directory entry (SL-045: 0 to 256 MiB), and a plain listing moves a folder's last-access time (SL-045). So
/// before the scan returns nothing here opens a held file or the stale hardlink name: the held files are observed
/// only through their holders' own handles, the hardlinked file only through the name it was written through,
/// and every other entry through HoldRig's handle-only snapshot, which lists nothing. Everything observed is
/// back-dated 48 hours first, since NTFS records an access only when the stored time is over an hour old. The
/// last thing before the scan is the listing's own reading of the held files, and "the listing still says 0 B"
/// is read as: until the scanner lists them. The scanner's own probe then refreshes the entries, which criterion 6
/// does not cover (review M1, overridden: it reads criterion 6 as each file's and folder's own metadata).
/// <para>The rule SL-056 replaced is run first, on the same tree, through ScanOptions.ProbeRule: it opens neither
/// held file, so the real scan still meets them at 0 B, and <see cref="TheRuleSl056ReplacedFailsCriteria1_3And7"/>
/// shows the checks of criteria 1, 3 and 7 fail under it.</para>
/// </remarks>
public class HeldOpenTreeTests(HeldOpenTreeTests.HeldOpenTree held, ITestOutputHelper output) : IClassFixture<HeldOpenTreeTests.HeldOpenTree>
{
    private const long HourInFileTime = 36_000_000_000; // FILETIME counts 100 ns

    /// <summary>Criterion 1: a file another process is growing, still listed at 0 B, is charged at its own size.</summary>
    [Fact]
    public void AFileAnotherProcessIsGrowingIsChargedAtItsOwnSize()
    {
        Record();
        Assert.Equal((0L, 0L), held.GrowListed);
        Assert.True(held.GrowAfter.EndOfFile > 256 * TempTree.MiB, $"grow.bin is {held.GrowAfter.EndOfFile} bytes by its own handle");
        Assert.Empty(HeldOpenTree.HeldFileFailures(held.Result, held.Grow.Path, held.GrowAfter));
    }

    /// <summary>Criterion 3: a file held with no sharing at all is sized: the probe asks for no data access.</summary>
    [Fact]
    public void AFileHeldWithNoSharingIsChargedAtItsOwnSize()
    {
        Record();
        Assert.Equal((0L, 0L), held.LockedListed);
        Assert.Empty(HeldOpenTree.HeldFileFailures(held.Result, held.Locked.Path, held.LockedAfter));
        Assert.Equal(0, held.Result.ProbeFailures);
    }

    /// <summary>
    /// Criterion 6: after the scans, no file or folder in the tree has a changed time, size or attribute, as
    /// processes other than the scanner read them through handles. Not vacuous: each was back-dated 48 hours, every
    /// folder was listed through a handle that kept its last-access time, and the control shows a plain listing
    /// moves one (<see cref="APlainListingOfTheControlFolderMovesItsLastAccessTime"/>).
    /// </summary>
    [Fact]
    public void TheScanChangesNoEntrysMetadata()
    {
        Record();
        var changed = new List<string>();
        for (int i = 0; i < held.Observed.Count; i++)
        {
            HandleMetadata before = held.Before[i], after = held.After[i];
            Assert.Equal(before.Path, after.Path);
            Assert.True(before.LastAccessTime < DateTime.UtcNow.ToFileTimeUtc() - 47 * HourInFileTime,
                $"{before.Path} was not back-dated: its last access reads {DateTime.FromFileTimeUtc(before.LastAccessTime):u}");
            if (before.Fields != after.Fields)
                changed.Add($"{before.Path}: {before} became {after}");
        }
        foreach ((HandleMetadata before, HandleMetadata after) in new[] { (held.GrowBefore, held.GrowAfter), (held.LockedBefore, held.LockedAfter) })
        {
            if (before.Fields != after.Fields)
                changed.Add($"{before.Path} (its holder's own handle): {before} became {after}");
        }
        Assert.True(changed.Count == 0, "The scan changed metadata:\n" + string.Join("\n", changed));

        // Every folder of the tree was listed through a handle that kept its last-access time, in both scans.
        int folders = held.Observed.Count(Directory.Exists);
        Assert.Equal((folders, folders), (held.Result.LastAccessAsked, held.Result.LastAccessKept));
        Assert.Equal((folders, folders), (held.OldRuleResult.LastAccessAsked, held.OldRuleResult.LastAccessKept));
    }

    /// <summary>
    /// Criterion 6's positive control: the same observer sees a plain listing, with no last-access suppression,
    /// move a back-dated folder's last-access time. Without it a diff that finds nothing could mean the method
    /// cannot see a change, or that this machine records no access.
    /// </summary>
    [Fact]
    public void APlainListingOfTheControlFolderMovesItsLastAccessTime()
    {
        Record();
        Assert.Equal(1, held.ControlListed);
        Assert.True(held.ControlBefore.LastAccessTime < DateTime.UtcNow.ToFileTimeUtc() - 47 * HourInFileTime,
            $"the control was not back-dated: its last access reads {DateTime.FromFileTimeUtc(held.ControlBefore.LastAccessTime):u}");
        Assert.True(held.ControlAfter.LastAccessTime - held.ControlBefore.LastAccessTime > HourInFileTime,
            $"a plain listing did not move the control's last-access time ({DateTime.FromFileTimeUtc(held.ControlBefore.LastAccessTime):u} to " +
            $"{DateTime.FromFileTimeUtc(held.ControlAfter.LastAccessTime):u}); fsutil says: {held.DisableLastAccess}");
    }

    /// <summary>
    /// Criterion 7: each folder's size is the current on-disk bytes below it, held-open files and the hardlinked
    /// file included, against totals computed without the scan (<see cref="HeldOpenTree.FolderFailures"/>).
    /// </summary>
    [Fact]
    public void EachFolderIsTheOnDiskBytesBelowIt()
    {
        Record();
        Assert.Equal(ScanStatus.Completed, held.Result.Status);
        Assert.Empty(held.FolderFailures(held.Result));
    }

    /// <summary>
    /// The checks of criteria 1, 3 and 7 fail under the rule SL-056 replaced, run on the same tree before the real
    /// scan (review B1's baseline): it trusts the 0 B listing of both held files and never opens them.
    /// </summary>
    [Fact]
    public void TheRuleSl056ReplacedFailsCriteria1_3And7()
    {
        Record();
        ScanResult old = held.OldRuleResult;
        Assert.Equal(ScanStatus.Completed, old.Status);
        Assert.NotEmpty(HeldOpenTree.HeldFileFailures(old, held.Grow.Path, held.GrowAfter));
        Assert.NotEmpty(HeldOpenTree.HeldFileFailures(old, held.Locked.Path, held.LockedAfter));
        List<string> folders = held.FolderFailures(old);
        Assert.Contains(folders, f => f.StartsWith(held.HeldFolder + ":", StringComparison.Ordinal));
        Assert.Equal(0, old.Store.Size(Scans.Find(old, held.Grow.Path))); // charged its 0 B listing
    }

    // What was measured, in the test's output: the listing's readings before the scan, fsutil's setting, the timings.
    private void Record()
    {
        output.WriteLine($"fsutil behavior query disablelastaccess: {held.DisableLastAccess}");
        output.WriteLine($"listed before the scan: grow.bin {held.GrowListed}, locked.bin {held.LockedListed}, stale.bin {held.StaleListed} (EndOfFile, AllocationSize)");
        output.WriteLine($"own handles: grow.bin {held.GrowAfter.EndOfFile}/{held.GrowAfter.AllocationSize}, locked.bin {held.LockedAfter.EndOfFile}/{held.LockedAfter.AllocationSize}");
        ScanResult r = held.Result;
        output.WriteLine($"scan: {r.Probes} probes, {r.ProbeCorrections} corrected, {r.ProbeFailures} failed; listing {r.ListingTime.TotalMilliseconds:F0} ms, " +
            $"sizing {r.SizingTime.TotalMilliseconds:F0} ms, longest probe {r.LongestProbe.TotalMilliseconds:F1} ms; last access kept {r.LastAccessKept} of {r.LastAccessAsked}");
    }

    /// <summary>The tree, the holders, the two scans and every reading, made once for the class.</summary>
    public sealed class HeldOpenTree : IDisposable
    {
        internal const long GrowBytes = 257 * TempTree.MiB; // past 256 MiB (criterion 1)
        internal const long LockedBytes = 24 * TempTree.MiB;

        private readonly TempTree _tree = new();
        private readonly List<HoldRigProcess.HeldFile> _holders = [];

        public HeldOpenTree()
        {
            try
            {
                Build();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal HoldRigProcess.HeldFile Grow { get; private set; } = null!;

        internal HoldRigProcess.HeldFile Locked { get; private set; } = null!;

        internal string HeldFolder => _tree.PathOf("held");

        /// <summary>Every entry of the tree but the held files and the stale name, in snapshot order.</summary>
        internal List<string> Observed { get; } = [];

        internal List<HandleMetadata> Before { get; private set; } = [];

        internal List<HandleMetadata> After { get; private set; } = [];

        internal HandleMetadata GrowBefore { get; private set; } = null!;

        internal HandleMetadata GrowAfter { get; private set; } = null!;

        internal HandleMetadata LockedBefore { get; private set; } = null!;

        internal HandleMetadata LockedAfter { get; private set; } = null!;

        internal HandleMetadata ControlBefore { get; private set; } = null!;

        internal HandleMetadata ControlAfter { get; private set; } = null!;

        internal int ControlListed { get; private set; }

        /// <summary>The listing's (EndOfFile, AllocationSize) for each held file, read last before the real scan.</summary>
        internal (long, long) GrowListed { get; private set; }

        internal (long, long) LockedListed { get; private set; }

        internal long StaleListed { get; private set; }

        internal ScanResult OldRuleResult { get; private set; } = null!;

        internal ScanResult Result { get; private set; } = null!;

        internal string DisableLastAccess { get; private set; } = "";

        private string Written => _tree.PathOf(@"links\b-second\written.bin");

        private string Stale => _tree.PathOf(@"links\a-first\stale.bin");

        private void Build()
        {
            _tree.File("top.bin", 100);
            _tree.Folder(@"plain\sub");
            _tree.Folder(@"plain\empty");
            _tree.File(@"plain\small.txt", 10_000);
            _tree.File(@"plain\sub\deeper.bin", 3 * TempTree.MiB);
            // Two names in two folders. The scan meets a-first's first (name order, review m6), and that name is the
            // stale one: 1 MiB listed, 64 MiB since the append through written.bin.
            _tree.Folder(@"links\a-first");
            _tree.Folder(@"links\b-second");
            _tree.File(@"links\b-second\written.bin", 1 * TempTree.MiB);
            TempTree.Hardlink(Stale, Written);
            TempTree.Append(Written, 63 * TempTree.MiB);
            _tree.Folder("held");
            Grow = Hold(@"held\grow.bin", GrowBytes, shareNone: false);
            Locked = Hold(@"held\locked.bin", LockedBytes, shareNone: true);
            string control = _tree.Outside("control");
            TempTree.WriteFile(Path.Combine(control, "c.txt"), 10);
            string scratch = _tree.Outside("scratch");

            Observed.AddRange(new[] { "", "top.bin", "plain", @"plain\small.txt", "plain\\sub", @"plain\sub\deeper.bin", @"plain\empty",
                "links", @"links\a-first", @"links\b-second", @"links\b-second\written.bin", "held" }.Select(_tree.PathOf));

            // Back-dated, so an access would show. Never through a held file or the stale name (see the class remarks).
            // Deepest first (SL-096): where opening a child with write access moves its parent's last-access time, as it
            // does under the Windows folder, a parent back-dated before its children would be reset by them, and the test
            // would fail its 'not back-dated' checks instead of the check that names the cause.
            DateTime old = DateTime.UtcNow.AddHours(-48);
            foreach (string path in Enumerable.Reverse(Observed).Append(Path.Combine(control, "c.txt")).Append(control))
            {
                if (Directory.Exists(path))
                    Directory.SetLastAccessTimeUtc(path, old);
                else
                    File.SetLastAccessTimeUtc(path, old);
            }

            List<HandleMetadata> before = HoldRigProcess.Snapshot([.. Observed, control], scratch);
            Before = before[..^1];
            ControlBefore = before[^1];
            GrowBefore = Grow.Report();
            LockedBefore = Locked.Report();

            // The rule SL-056 replaced, first: it leaves both held files unopened, so the real scan meets them at 0 B.
            OldRuleResult = Scanner.Scan(_tree.Root, new ScanProgress(), CancellationToken.None, new ScanOptions { ProbeRule = OldRule });

            // The last step before the scan: what the listing says, read as the scan reads it, opening no file.
            Dictionary<string, RawEntry> heldListing = Scans.Listing(HeldFolder);
            GrowListed = (heldListing["grow.bin"].EndOfFile, heldListing["grow.bin"].AllocationSize);
            LockedListed = (heldListing["locked.bin"].EndOfFile, heldListing["locked.bin"].AllocationSize);
            StaleListed = Scans.Listing(_tree.PathOf(@"links\a-first"))["stale.bin"].EndOfFile;

            Result = Scans.Run(_tree.Root);

            GrowAfter = Grow.Report();
            LockedAfter = Locked.Report();
            After = HoldRigProcess.Snapshot(Observed, scratch);
            ControlListed = Directory.GetFileSystemEntries(control).Length; // a plain listing: FindFirstFile, nothing kept
            ControlAfter = HoldRigProcess.Snapshot([control], scratch)[0];
            DisableLastAccess = Fsutil("behavior", "query", "disablelastaccess");
        }

        /// <summary>
        /// The rule SL-056 replaced (SizeProbe.IsDue in SL-020's design): a WOF suspect, or a file listed at 64 MiB or
        /// more, never a cloud-only file or a later name. Its first-repeat probe of a hardlink is not reproduced, so
        /// under it the stale name stays stale too; the baseline asserts only what the held files make fail.
        /// </summary>
        internal static bool OldRule(EntryKind kind, EntryFlags flags, uint attributes, long endOfFile, long allocationSize) =>
            SizeProbe.IsEligible(kind, flags)
            && ((allocationSize == 0 && endOfFile >= 4096 && (attributes & Kernel32.FILE_ATTRIBUTE_SPARSE_FILE) == 0)
                || endOfFile >= 64 * TempTree.MiB
                || allocationSize >= 64 * TempTree.MiB);

        /// <summary>
        /// Criteria 1 and 3: the held file is charged at what its holder's own handle reports, and the probe, not
        /// the listing, supplied the figure.
        /// </summary>
        internal static List<string> HeldFileFailures(ScanResult result, string path, HandleMetadata own)
        {
            var failures = new List<string>();
            ScanStore store = result.Store;
            int entry = Scans.Find(result, path);
            if (store.Size(entry) != own.EndOfFile)
                failures.Add($"{path} is charged {store.Size(entry)} bytes; its own handle says {own.EndOfFile}");
            if (store.OnDisk(entry) != own.AllocationSize)
                failures.Add($"{path} is charged {store.OnDisk(entry)} bytes on disk; its own handle says {own.AllocationSize}");
            if (store.Flags(entry) != (EntryFlags.SizeProbed | EntryFlags.ProbeCorrected))
                failures.Add($"{path} is flagged {store.Flags(entry)}, not SizeProbed and ProbeCorrected: its listing's figure was kept");
            return failures;
        }

        /// <summary>
        /// Criterion 7: every folder of the tree against the bytes below it, computed without the scan. A plain file
        /// is what HoldRig's snapshot read through its handle; a held file is what its holder's own handle reports;
        /// the hardlinked file is read through the name it was written through and charged once, to a-first, the
        /// folder of the name the scan meets first. written.bin counts as a file there and adds no bytes.
        /// </summary>
        internal List<string> FolderFailures(ScanResult result)
        {
            HandleMetadata Snapshotted(string relative) => Before.Single(m => m.Path == _tree.PathOf(relative));
            HandleMetadata written = Snapshotted(@"links\b-second\written.bin");
            var files = new List<(string Folder, Rolled Charge)>
            {
                ("", Charge(Snapshotted("top.bin"))),
                ("plain", Charge(Snapshotted(@"plain\small.txt"))),
                (@"plain\sub", Charge(Snapshotted(@"plain\sub\deeper.bin"))),
                (@"links\a-first", Charge(written)),
                (@"links\b-second", new Rolled(0, 0, 1)),
                ("held", Charge(GrowAfter)),
                ("held", Charge(LockedAfter)),
            };
            var failures = new List<string>();
            foreach (string folder in Observed.Where(Directory.Exists))
            {
                string relative = Path.GetRelativePath(_tree.Root, folder) is "." ? "" : Path.GetRelativePath(_tree.Root, folder);
                Rolled expected = files.Where(f => f.Folder == relative || f.Folder.StartsWith(relative + @"\", StringComparison.Ordinal) || relative == "")
                    .Aggregate(new Rolled(0, 0, 0), (sum, f) => sum + f.Charge);
                Rolled actual = Rolled.Of(result.Store, Scans.Find(result, folder));
                if (actual != expected)
                    failures.Add($"{folder}: the scan holds {actual}, the bytes below it are {expected}");
            }
            return failures;
        }

        private static Rolled Charge(HandleMetadata file) => new(file.EndOfFile, file.AllocationSize, 1);

        private HoldRigProcess.HeldFile Hold(string relative, long bytes, bool shareNone)
        {
            HoldRigProcess.HeldFile holder = HoldRigProcess.Hold(_tree.PathOf(relative), bytes, shareNone);
            _holders.Add(holder);
            return holder;
        }

        private static string Fsutil(params string[] args)
        {
            var start = new ProcessStartInfo("fsutil.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args)
                start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            string said = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            process.WaitForExit();
            return $"exit {process.ExitCode}: {said}";
        }

        public void Dispose()
        {
            foreach (HoldRigProcess.HeldFile holder in _holders)
                holder.Dispose();
            _tree.Dispose();
        }
    }
}
