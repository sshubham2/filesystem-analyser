// Bug (SL-095, matched to microsoft/Windows-Containers#646): scanning C: leaks kernel pool that only a reboot gives
// back, because the scan walks the live Windows container layer under
// C:\ProgramData\Microsoft\Windows\Containers\Layers\<id>\Files. Every untouched entry there is a WCI reparse point
// that wcifs.sys (Microsoft's Windows Container Isolation minifilter, which ships with Windows) resolves on first
// touch, and on each such open it never frees a FLT_STREAM_CONTEXT (WCsc), a FLT_FILE_CONTEXT (WCfc), a reparse
// buffer (WCri), a FltMgr work item (FMwi) and a file-list control (FMfc), and it leaves a kernel handle in the System
// process. The leak is in the driver and survives the scan's exit. The scanner has no path exclusion anywhere, lists
// hidden and system folders, and so walks the whole layer (about 210K entries on the reporter's kind of PC).
//
// Expected: a scan that starts above the container store (here, at Containers itself, the way a scan of C: reaches
// it) does not list any folder of a layer's Files tree: it stops at the boundary of the live layer, so the scan
// never makes wcifs.sys resolve those reparse points.
//
// Actual: the scan lists ...\Containers\Layers\<id>\Files, and would go on through every folder beneath it, each
// first-touch open leaking pool in the driver.
//
// What the test does and what it costs: Scanner.Scan over C:\ProgramData\Microsoft\Windows\Containers, stopped from
// the scan's own AfterFolder seam the moment a folder of a layer's Files tree has been listed. So the scan enters
// the layer by at most the one folder the bug is about (the Files folder, whose listing is what a fixed scan must
// not make), never its descendants, and the worst case on this machine is that one listing: a few KB of pool, and
// not one of the ~210K entries a full walk would touch. Containers, BaseImages, ContainerStorages (3 entries), Dumps
// and the layer's own folder hold a handful of entries between them, so it runs well under a second; a 5 s cancel
// bounds it whatever the machine holds. The test is skipped, with the reason, where there is no Layers\<id>\Files.
// The precondition reads only names in Layers and in a layer folder; it never touches Files itself.
//
// The assertion is about the walk, not about a pool counter, on purpose: wcifs leaks on the FIRST touch of each
// stream in a boot, so a counter delta would read 0 on every run after the first and pass for the wrong reason.

using FsAnalyser.Core.Scanning;

namespace SL095.ContainerLayerWalk.Tests;

/// <summary>A fact that skips itself, at discovery, when this PC has no container layer folder to walk into.</summary>
public sealed class LiveLayerFactAttribute : FactAttribute
{
    public LiveLayerFactAttribute()
    {
        string? missing = ContainerStore.WhyThereIsNoLayer();
        if (missing is not null)
            Skip = "SL-095 needs a container layer on this PC: " + missing;
    }
}

internal static class ContainerStore
{
    public static string Containers =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Containers");

    /// <summary>Null when some Layers\&lt;id&gt; holds a Files folder, by the names in the layer's own folder; else why not.</summary>
    public static string? WhyThereIsNoLayer()
    {
        string layers = Path.Combine(Containers, "Layers");
        try
        {
            if (!Directory.Exists(layers))
                return layers + " does not exist";
            foreach (string layer in Directory.EnumerateDirectories(layers))
            {
                foreach (string child in Directory.EnumerateDirectories(layer))
                {
                    if (string.Equals(Path.GetFileName(child), "Files", StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }
            return layers + " holds no layer with a Files folder";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return layers + " cannot be read (" + e.GetType().Name + ")";
        }
    }

    /// <summary>Whether a folder path is a layer's Files folder or below it: ...\Containers\Layers\&lt;id&gt;\Files[\...].</summary>
    public static bool IsInsideALayersFiles(string folder)
    {
        string layers = Path.Combine(Containers, "Layers") + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(layers, StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = folder[layers.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[1], "Files", StringComparison.OrdinalIgnoreCase);
    }
}

public class ContainerLayerWalkTests
{
    [LiveLayerFact]
    public void AScanOfTheContainerStoreDoesNotWalkIntoTheLiveLayersFiles()
    {
        var progress = new ScanProgress();
        var walked = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5)); // a bound on the test, not part of the check

        var options = new ScanOptions
        {
            // Called on the scan's own thread after each folder's listing has been committed, so CurrentFolder is
            // that folder. The first one inside a layer's Files tree is the bug; stop there, before any descendant
            // of it is opened.
            AfterFolder = _ =>
            {
                string? folder = progress.CurrentFolder;
                if (folder is not null && ContainerStore.IsInsideALayersFiles(folder))
                {
                    walked.Add(folder);
                    cts.Cancel();
                }
            },
        };

        ScanResult result = Scanner.Scan(ContainerStore.Containers, progress, cts.Token, options);

        Assert.NotEqual(ScanStatus.RootUnreadable, result.Status);
        Assert.True(result.Store.Count > 1, "setup: the scan of " + ContainerStore.Containers + " listed nothing");
        Assert.True(walked.Count == 0,
            "the scan walked into a live container layer, listing " + string.Join(", ", walked) + ". Every untouched entry of that "
            + "layer is a WCI reparse point that wcifs.sys resolves on first touch, leaking WCsc/WCfc/WCri/FMwi/FMfc pool and a "
            + "System-process handle that only a reboot gives back (microsoft/Windows-Containers#646). The scan must not descend into "
            + @"C:\ProgramData\Microsoft\Windows\Containers\Layers\<id>\Files.");
    }
}
