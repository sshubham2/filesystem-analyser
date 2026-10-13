using System;

/// <summary>
/// Which folder a window case stops the scan at (FSA_HOOK_TREE_STOP, SL-110): the words of the match, with no WPF in them, so
/// that Core.Tests compiles this file as source and runs tests/FsAnalyser.Core.Tests/StopTargetCorpus.json over it.
/// </summary>
/// <remarks>
/// <c>ScanProgress.CurrentFolder</c> names the folder most recently COMMITTED (Scanner.FolderDone publishes it after
/// SetChildren), and every commit overwrites it. A target is therefore seen only for as long as the scan takes to list and
/// commit the NEXT folder: 50 to 230 microseconds when that folder holds a handful of entries (lesson #85, about 53
/// microseconds a folder), against a poll that can stall for 5 ms (spike 120: worst gap 4.6 to 10 ms) and a click that lands
/// 574 microseconds after a match at the median, 745 at the worst (the tree-stop line logs both). A target written as one
/// exact folder is a sound stop only when the folder after it is a long listing (T-011 run A: j-before, then m-big's 55 ms).
/// Every other target is written with a trailing backslash, which matches the folder and everything under it, so the window
/// lasts as long as the subtree takes to walk (review M1 of SL-110).
/// </remarks>
internal static class StopTarget
{
    /// <summary>The folder the case names, as the scan names it: the root's path, one separator, the relative path.</summary>
    internal static string Full(string root, string target) =>
        root.TrimEnd('\\') + "\\" + target.TrimEnd('\\');

    /// <summary>Whether the target ends in a backslash: the folder and everything under it, not the one folder.</summary>
    internal static bool IsSubtree(string target) => target.EndsWith('\\');

    /// <summary>
    /// Whether <paramref name="seen"/>, a value of CurrentFolder, is the target: the one folder, or with a trailing backslash
    /// that folder or any folder under it. Paths compare as Windows does, ignoring case; a sibling that only shares the
    /// target's leading characters (m-mid2 for m-mid) is never under it.
    /// </summary>
    internal static bool Matches(string root, string target, string seen)
    {
        string full = Full(root, target);
        string folder = seen.TrimEnd('\\');
        if (string.Equals(folder, full, StringComparison.OrdinalIgnoreCase))
            return true;
        return IsSubtree(target)
            && folder.Length > full.Length
            && folder.StartsWith(full, StringComparison.OrdinalIgnoreCase)
            && folder[full.Length] == '\\';
    }

    /// <summary>The target is something the hook can wait for: it names a folder under the root.</summary>
    internal static bool IsUsable(string target) => target.TrimEnd('\\').Length > 0;
}
