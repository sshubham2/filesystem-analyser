using System.Globalization;
using FsAnalyser.Core.Tests;

// The stalled-share rig as a process (SL-038), so the published app can scan a share that stops answering:
//
//   StallRig.exe --marker <file> [--wait-sec <n>] [--seam] [--deny <name>] [--log <file>]
//   StallRig.exe --sweep [--wait-sec <n>]
//
// Builds a small tree in a folder named "tree" beside <file> (a, hung with 5 files, z), serves it from an
// SMBLibrary server on 127.0.0.1 through a non-persistent mapping, and prints the share's UNC root on
// stdout. The server stops answering at the listing of "hung", and every request after it; <file> is
// written at that moment. When stdin closes, the server answers again, the rig removes its own mapping,
// deletes the tree and exits 0.
//
// --seam (SL-089, the window case N-005) builds the stop-seam tree instead and stops answering at the listing of "s":
// a (one file), b (SeamChildren files with names of SeamNameLength characters, so the scanner's listing of it takes
// three pages or more and the listing failpoint b|3|59 fails a full third page), s (5 files), t and u (a file each).
// --deny <name> answers every open of that top folder STATUS_ACCESS_DENIED. --log <file> appends one line per open the
// server answers, '<utc> create <name> <status>', and '<utc> hold <name>' as it stops answering, so the case can tell what
// was opened before its Stop. A rig run this way is killed by its case, not let go: that teardown is what fails the held
// listing. --sweep removes, under the lock, the rig mappings a killed run left behind, prints each on stderr and exits 0
// (review M5): the case runs it straight after the kill.
//
// It waits up to --wait-sec (default 150) for another rig run in this sign-in session to finish (LoopbackShare,
// RigLock) and exits 1 if one holds on. A failed mapping also exits 1, with the error on stderr and nothing
// on stdout. Bad arguments exit 2. A rig that is killed cannot clean up: its mapping stays until the next
// rig run sweeps it, or until sign-out, and its tree stays beside <file>.

const int SeamChildren = 1_200;
const int SeamNameLength = 46;

string? marker = null;
string? deny = null;
string? log = null;
bool seam = false;
bool sweep = false;
bool bad = false;
int waitSec = (int)LoopbackShare.WaitForOtherRun.TotalSeconds;
for (int i = 0; i < args.Length && !bad; i++)
{
    if (args[i] == "--marker" && i + 1 < args.Length)
        marker = Path.GetFullPath(args[++i]);
    else if (args[i] == "--wait-sec" && i + 1 < args.Length && int.TryParse(args[i + 1], out int seconds) && seconds >= 0)
    {
        waitSec = seconds;
        i++;
    }
    else if (args[i] == "--seam")
        seam = true;
    else if (args[i] == "--deny" && i + 1 < args.Length && args[i + 1].Length > 0 && args[i + 1].IndexOfAny(['\\', '/']) < 0)
        deny = args[++i];
    else if (args[i] == "--log" && i + 1 < args.Length)
        log = Path.GetFullPath(args[++i]);
    else if (args[i] == "--sweep")
        sweep = true;
    else
        bad = true;
}

if (sweep && !bad && marker is null && deny is null && log is null && !seam)
{
    try
    {
        foreach (string left in LoopbackShare.SweepLeftBehind(TimeSpan.FromSeconds(waitSec)))
            Console.Error.WriteLine($"removed a rig mapping a killed run left behind: {left}");
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"StallRig: {e.GetType().Name}: {e.Message}");
        return 1;
    }
}
if (bad || sweep || marker is null || !Directory.Exists(Path.GetDirectoryName(marker)) || File.Exists(marker))
{
    Console.Error.WriteLine("usage: StallRig.exe --marker <file> [--wait-sec <n>] [--seam] [--deny <name>] [--log <file>]  (the file must not exist; its folder must)");
    Console.Error.WriteLine("       StallRig.exe --sweep [--wait-sec <n>]");
    return 2;
}

string tree = Path.Combine(Path.GetDirectoryName(marker)!, "tree");
string hold = seam ? "s" : "hung";
try
{
    if (seam)
    {
        Directory.CreateDirectory(Path.Combine(tree, "a"));
        File.WriteAllBytes(Path.Combine(tree, "a", "one.txt"), new byte[11]);
        Directory.CreateDirectory(Path.Combine(tree, "b"));
        for (int i = 0; i < SeamChildren; i++)
        {
            string name = $"b-{i:D4}-";
            File.WriteAllBytes(Path.Combine(tree, "b", name + new string('n', SeamNameLength - name.Length - 4) + ".txt"), new byte[100]);
        }
        Directory.CreateDirectory(Path.Combine(tree, "s"));
        for (int i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(tree, "s", $"file{i}.txt"), new byte[100]);
        foreach (string folder in new[] { "t", "u" })
        {
            Directory.CreateDirectory(Path.Combine(tree, folder));
            File.WriteAllBytes(Path.Combine(tree, folder, "one.txt"), new byte[22]);
        }
    }
    else
    {
        Directory.CreateDirectory(Path.Combine(tree, "a"));
        File.WriteAllBytes(Path.Combine(tree, "a", "one.txt"), new byte[11]);
        Directory.CreateDirectory(Path.Combine(tree, "hung"));
        for (int i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(tree, "hung", $"file{i}.txt"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(tree, "z"));
        File.WriteAllBytes(Path.Combine(tree, "z", "last.txt"), new byte[33]);
    }
    if (deny is not null && !Directory.Exists(Path.Combine(tree, deny)))
    {
        Console.Error.WriteLine($"StallRig: --deny {deny} names no folder of the served tree");
        return 2;
    }

    var gate = new object();
    using (var share = new LoopbackShare(tree, TimeSpan.FromSeconds(waitSec)))
    {
        if (log is not null)
        {
            share.Store.Trace = line =>
            {
                lock (gate)
                    File.AppendAllText(log, $"{DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {line}\r\n");
            };
        }
        share.Store.DenyAtOpen = deny;
        share.Store.StopAnsweringAtTheListingOf(hold);
        var armed = new Thread(() =>
        {
            share.Store.Stalled.Wait();
            File.WriteAllText(marker, $"{DateTime.UtcNow:O}\r\n");
        }) { IsBackground = true, Name = "stall-armed" };
        armed.Start();

        foreach (string left in share.LeftBehind)
            Console.Error.WriteLine($"removed a rig mapping a killed run left behind: {left}");
        Console.Out.WriteLine(share.RemotePath);
        Console.Out.Flush();

        while (Console.In.ReadLine() is not null)
        {
            // until whoever started the rig closes stdin
        }
        Console.Error.WriteLine(share.Store.Stalled.IsSet
            ? $"the stall armed {share.Store.StalledAt.TotalMilliseconds:F0} ms after the server started"
            : $"the stall never armed: nothing listed {hold}");
    }
    try
    {
        Directory.Delete(tree, recursive: true);
    }
    catch (IOException e)
    {
        Console.Error.WriteLine($"the tree {tree} was left behind: {e.Message}"); // the rig itself ended cleanly
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"StallRig: {e.GetType().Name}: {e.Message}");
    return 1;
}
