using FsAnalyser.Core.Tests;

// The stalled-share rig as a process (SL-038), so the published app can scan a share that stops answering:
//
//   StallRig.exe --marker <file> [--wait-sec <n>]
//
// Builds a small tree in a folder named "tree" beside <file> (a, hung with 5 files, z), serves it from an
// SMBLibrary server on 127.0.0.1 through a non-persistent mapping, and prints the share's UNC root on
// stdout. The server stops answering at the listing of "hung", and every request after it; <file> is
// written at that moment. When stdin closes, the server answers again, the rig removes its own mapping,
// deletes the tree and exits 0.
//
// It waits up to --wait-sec (default 150) for another rig run in this sign-in session to finish (LoopbackShare,
// RigLock) and exits 1 if one holds on. A failed mapping also exits 1, with the error on stderr and nothing
// on stdout. Bad arguments exit 2. A rig that is killed cannot clean up: its mapping stays until the next
// rig run sweeps it, or until sign-out, and its tree stays beside <file>.

string? marker = null;
int waitSec = (int)LoopbackShare.WaitForOtherRun.TotalSeconds;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--marker" && i + 1 < args.Length)
        marker = Path.GetFullPath(args[++i]);
    else if (args[i] == "--wait-sec" && i + 1 < args.Length && int.TryParse(args[i + 1], out int seconds) && seconds >= 0)
    {
        waitSec = seconds;
        i++;
    }
    else
    {
        marker = null;
        break;
    }
}
if (marker is null || !Directory.Exists(Path.GetDirectoryName(marker)) || File.Exists(marker))
{
    Console.Error.WriteLine("usage: StallRig.exe --marker <file> [--wait-sec <n>]  (the file must not exist; its folder must)");
    return 2;
}

string tree = Path.Combine(Path.GetDirectoryName(marker)!, "tree");
try
{
    Directory.CreateDirectory(Path.Combine(tree, "a"));
    File.WriteAllBytes(Path.Combine(tree, "a", "one.txt"), new byte[11]);
    Directory.CreateDirectory(Path.Combine(tree, "hung"));
    for (int i = 0; i < 5; i++)
        File.WriteAllBytes(Path.Combine(tree, "hung", $"file{i}.txt"), new byte[100]);
    Directory.CreateDirectory(Path.Combine(tree, "z"));
    File.WriteAllBytes(Path.Combine(tree, "z", "last.txt"), new byte[33]);

    using (var share = new LoopbackShare(tree, TimeSpan.FromSeconds(waitSec)))
    {
        share.Store.StopAnsweringAtTheListingOf("hung");
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
            : "the stall never armed: nothing listed hung");
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
