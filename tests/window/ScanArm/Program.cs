// SL-095's scan arms for the pool protocol (tests/window/PoolWatch.ps1). Each arm is one process, which exits before
// PoolWatch takes its after-reading. It never relocates the container layer store: the scanner leaves out this PC's own
// store as the app does, so no arm opens a layer entry (architecture rule "the measurement rigs never relocate the store").
//
//   ScanArm listing <root> --list-out <file> [--progress <file>] [--progress-every <n>]
//       The app's Scanner, listing only: the probe rule is set to probe nothing, the one difference from the app. Writes
//       every file the walk charged (a first name, not cloud-only), as the probe would be given it, to <file>, and the
//       two halves of that list by a deterministic hash of the path (FNV-1a 64 of the path in upper case, even = A,
//       odd = B) to <file>.A and <file>.B. Appends "<utc>\t<folders>\t<folder>" to the progress log every n folders
//       (1000), so a pool burst can be lined up with the subtree being walked.
//   ScanArm probe <list> [--skip <k>] [--max <n>]
//       The app's SizeProbe.Probe, the per-file metadata open of SL-056, on each path, one at a time.
//   ScanArm plain <list> [--skip <k>] [--max <n>]
//       A plain open that is not the app's: the runtime's own attribute query (GetFileAttributesEx through FileInfo),
//       which opens each file for its attributes and closes it, as Explorer and dir do.
//
// Every arm prints one JSON line: what it did and how it ended. A file's open failing is counted, never fatal; the exit
// code is 0 unless the rig itself is at fault (2: bad arguments, 3: a list that cannot be read or written).

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace ScanArm;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return args.Length >= 2 ? args[0] switch
            {
                "listing" => Listing(args[1], Option(args, "--list-out"), Option(args, "--progress"), Number(args, "--progress-every") ?? 1000),
                "probe" => Probe(args[1], Number(args, "--skip") ?? 0, Number(args, "--max") ?? int.MaxValue),
                "plain" => Plain(args[1], Number(args, "--skip") ?? 0, Number(args, "--max") ?? int.MaxValue),
                _ => Usage($"unknown arm '{args[0]}'"),
            } : Usage("an arm and its root or list are needed");
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"ScanArm: {e.GetType().Name}: {e.Message}");
            return 3;
        }
    }

    private static int Listing(string root, string? listOut, string? progressLog, int every)
    {
        if (listOut is null)
            return Usage("listing needs --list-out <file>");
        var progress = new ScanProgress();
        var clock = Stopwatch.StartNew();
        using StreamWriter? log = progressLog is null ? null : new StreamWriter(progressLog, append: true, Encoding.UTF8) { AutoFlush = true };
        var options = new ScanOptions
        {
            ProbeRule = static (_, _, _, _, _) => false, // listing only: the one thing this arm changes
            AfterFolder = n =>
            {
                if (log is not null && n % every == 0)
                    log.WriteLine($"{DateTime.UtcNow:O}\t{n}\t{progress.CurrentFolder}");
            },
        };
        ScanResult result = Scanner.Scan(root, progress, CancellationToken.None, options);
        long listingMs = clock.ElapsedMilliseconds;

        int all = 0, a = 0, b = 0;
        using (var whole = new StreamWriter(listOut, append: false, new UTF8Encoding(false)))
        using (var halfA = new StreamWriter(listOut + ".A", append: false, new UTF8Encoding(false)))
        using (var halfB = new StreamWriter(listOut + ".B", append: false, new UTF8Encoding(false)))
        {
            ScanStoreFiles(result, path =>
            {
                whole.WriteLine(path);
                all++;
                if ((Fnv1a64(path.ToUpperInvariant()) & 1) == 0)
                {
                    halfA.WriteLine(path);
                    a++;
                }
                else
                {
                    halfB.WriteLine(path);
                    b++;
                }
            });
        }
        Print(new
        {
            arm = "listing",
            root,
            status = result.Status.ToString(),
            files = result.Totals.Files,
            folders = result.Totals.Folders,
            unreadable = result.Unreadable.Count,
            skippedStores = result.SkippedStores.Count,
            skippedStorePaths = result.SkippedStores.Select(s => result.Store.GetPath(s.Entry)).ToArray(),
            opensUnderExcludedStore = result.OpensUnderExcludedStore,
            probes = 0,
            listed = all,
            halfA = a,
            halfB = b,
            listingMs,
        });
        return 0;
    }

    // Every file the walk charged that the app's probe would size: a first name, not a later hardlink name and not
    // cloud-only, in the \\?\ form the probe is given.
    private static void ScanStoreFiles(ScanResult result, Action<string> file)
    {
        var store = result.Store;
        int parent = -1;
        string parentPath = "";
        for (int e = 1; e < store.Count; e++)
        {
            if (!SizeProbe.IsEligible(store.Kind(e), store.Flags(e)))
                continue;
            int p = store.Parent(e);
            if (p != parent)
            {
                parent = p;
                parentPath = LongPath.ToExtended(store.GetPath(p));
            }
            file(LongPath.Child(parentPath, store.Name(e)));
        }
    }

    private static int Probe(string list, int skip, int max)
    {
        using var io = new SynchronousIoCancel(CancellationToken.None);
        int opened = 0, failed = 0, gone = 0;
        var errors = new Dictionary<int, int>();
        var clock = Stopwatch.StartNew();
        foreach (string path in Paths(list, skip, max))
        {
            int error = SizeProbe.Probe(io, path, out _, out _);
            if (error == 0)
                opened++;
            else if (SizeProbe.IsGone(error))
                gone++;
            else
                failed++;
            if (error != 0)
                errors[error] = errors.GetValueOrDefault(error) + 1;
        }
        Print(new { arm = "probe", list, skip, opened, failed, gone, errors = errors.ToDictionary(k => k.Key.ToString(), k => k.Value), ms = clock.ElapsedMilliseconds });
        return 0;
    }

    private static int Plain(string list, int skip, int max)
    {
        int opened = 0, failed = 0;
        var errors = new Dictionary<string, int>();
        var clock = Stopwatch.StartNew();
        foreach (string path in Paths(list, skip, max))
        {
            try
            {
                var info = new FileInfo(path);
                info.Refresh();
                _ = info.Attributes;
                if (!info.Exists)
                    throw new FileNotFoundException(path);
                opened++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed++;
                errors[e.GetType().Name] = errors.GetValueOrDefault(e.GetType().Name) + 1;
            }
        }
        Print(new { arm = "plain", list, skip, opened, failed, errors, ms = clock.ElapsedMilliseconds });
        return 0;
    }

    private static IEnumerable<string> Paths(string list, int skip, int max) =>
        File.ReadLines(list).Where(line => line.Length > 0).Skip(skip).Take(max);

    // FNV-1a, 64 bits, over the UTF-16 code units of the path: the split PoolWatch's own check recomputes.
    internal static ulong Fnv1a64(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int? Number(string[] args, string name) => Option(args, name) is { } text && int.TryParse(text, out int n) ? n : null;

    private static int Usage(string why)
    {
        Console.Error.WriteLine($"ScanArm: {why}. Usage: ScanArm listing <root> --list-out <file> [--progress <file>] [--progress-every <n>] | probe <list> [--skip k] [--max n] | plain <list> [--skip k] [--max n]");
        return 2;
    }
}
