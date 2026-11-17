using static FsAnalyser.App.FigureText;

namespace FsAnalyser.App;

/// <summary>
/// What the coverage panel is built from (SL-001): the rows the ending already builds for its list of folders the scan could
/// not read or left out on purpose, and the files whose own size could not be read, as the scan counted them.
/// </summary>
/// <param name="NotScanned">The folders left out on purpose, in path order: <see cref="UnreadableRows.NotScanned"/>.</param>
/// <param name="Unreadable">The folders that could not be read in full, in path order: <see cref="UnreadableRows.Build"/>.</param>
/// <param name="SizeNotReadFiles">How many files refused to have their own size read: the scan's ProbeFailures.</param>
/// <param name="SizeNotReadOnDisk">What the scan charged those files, at the on-disk size the folder listing gave.</param>
internal sealed record CoverageFacts(IReadOnlyList<NotScannedRow> NotScanned, IReadOnlyList<UnreadableRow> Unreadable,
    int SizeNotReadFiles, long SizeNotReadOnDisk);

/// <summary>
/// A folder the panel names, for the report: what kind of folder and its size, which is always null, because no figure is known
/// for one. It carries no text and no path (Q-016): the on-screen lines, which name the folder by its path, are
/// <see cref="CoverageBreakdown.ScreenLines"/> and nothing in the report reads them.
/// </summary>
/// <param name="Kind">'not-scanned' (left out on purpose) or 'unreadable'.</param>
internal sealed record CoverageFolder(string Kind, long? Size = null);

/// <summary>A part of the unseen space that was measured: its stable id, its bytes and the words the panel draws for it.</summary>
internal sealed record CoveragePart(string Id, long Bytes, string Text);

/// <summary>
/// What is left of the unseen space once the measured parts are taken out: never a measured figure, and the words say so.
/// <paramref name="IncludesStore"/> is whether a folder left out on purpose is in it, and the words then say that too.
/// </summary>
internal sealed record CoverageRemainder(long Bytes, bool IncludesStore, string Text);

/// <summary>
/// The files whose own size could not be read, kept apart from the unseen space: they are counted in what the scan saw, at the
/// on-disk size the folder listing gave, so they are never part of it.
/// </summary>
internal sealed record CoverageSizeNotRead(int Files, long Bytes, string Text);

/// <summary>
/// The panel under the whole-drive ending's coverage line: how the space the scan did not see divides by what the scan itself
/// knows it missed. The report's fields are copied from this record; <see cref="ScreenLines"/> is what is drawn.
/// </summary>
/// <param name="VolumeInUse">What the volume reports in use.</param>
/// <param name="SeenOnDisk">What the scan counted on disk.</param>
/// <param name="Unseen">The volume's figure less the scan's: the space this scan did not see, never negative here.</param>
/// <param name="ScreenLines">The lines the panel draws, folder rows included, which hold paths: on screen only, never in the report.</param>
/// <param name="Folders">Every folder the scan knows it missed, the one left out on purpose first, then the unreadable ones in path
/// order: the complete list, though the screen shows only some of the unreadable ones.</param>
/// <param name="Measured">The parts of <paramref name="Unseen"/> that were measured: the $MFT, or none.</param>
/// <param name="Remainder">The rest of <paramref name="Unseen"/>: <see cref="CoverageRemainder.Bytes"/> plus the measured bytes is it.</param>
/// <param name="SizeNotRead">The files whose own size could not be read, apart from <paramref name="Unseen"/>; null when none.</param>
internal sealed record CoverageBreakdown(long VolumeInUse, long SeenOnDisk, long Unseen, IReadOnlyList<string> ScreenLines,
    IReadOnlyList<CoverageFolder> Folders, IReadOnlyList<CoveragePart> Measured, CoverageRemainder Remainder,
    CoverageSizeNotRead? SizeNotRead);

/// <summary>
/// The coverage panel's model and its words (SL-001), and the two outcome notes about files whose size was not read. Pure, no WPF,
/// every figure through FigureText, so Core.Tests compiles it as source. It words a folder left out on purpose
/// through UnreadableRows and nowhere else, and spells neither of the two words TopLevelRowText writes once (the lower
/// bound's and the unread folder's).
/// </summary>
internal static class CoveragePanelText
{
    /// <summary>
    /// How many unreadable folders the screen names. A real C:\ held 559 of them, 446 under one folder, so the panel names the
    /// shallowest ones, which are the ones a person recognises (System Volume Information, another user's profile), and counts
    /// the rest, which the Unreadable folders tab lists in full. The report's folder list is never cut.
    /// </summary>
    internal const int UnreadableShown = 20;

    /// <summary>
    /// The panel for a scan that finished and whose volume reported <paramref name="volumeInUse"/>, or null: a scan that did not
    /// finish has no panel (its coverage line is the partial one), and neither has one that counted more than the volume
    /// reports in use, which has no unseen space to split.
    /// </summary>
    /// <param name="mft">The $MFT's valid data length, or null when it could not be read; used as a measured part only when it is
    /// above 0 and no more than the unseen space, so the remainder is never negative.</param>
    internal static CoverageBreakdown? Build(CoverageFacts facts, bool finished, long seenOnDisk, long volumeInUse, long? mft)
    {
        long unseen = volumeInUse - seenOnDisk;
        if (!finished || unseen < 0)
            return null;

        var measured = new List<CoveragePart>();
        if (mft is > 0 and long size && size <= unseen)
        {
            measured.Add(new CoveragePart("mft", size,
                $"The $MFT, the volume's file index: {FormatSize(size)}. Measured as its valid data length, not its allocated size."));
        }
        long left = unseen - measured.Sum(part => part.Bytes);

        bool includesStore = facts.NotScanned.Count > 0;
        string other = measured.Count > 0 ? " other than the $MFT" : "";
        string text = $"Space this account cannot read: {FormatSize(left)}. It is what is left of the unseen space{other}, and it is not measured.";
        if (facts.Unreadable.Count > 0)
            text += $" It includes {Count(facts.Unreadable.Count, "folder")} this scan could not read.";
        if (includesStore)
            text += " " + UnreadableRows.NotScannedInRemainder(facts.NotScanned[0].Reason);
        var remainder = new CoverageRemainder(left, includesStore, text);

        CoverageSizeNotRead? notRead = null;
        if (facts.SizeNotReadFiles > 0)
        {
            int files = facts.SizeNotReadFiles;
            notRead = new CoverageSizeNotRead(files, facts.SizeNotReadOnDisk,
                $"{Count(files, "file")} whose own size could not be read {(files == 1 ? "is" : "are")} not part of the unseen space: " +
                $"{(files == 1 ? "it is" : "they are")} counted in what the scan saw, at the on-disk size the folder listing gave " +
                $"({FormatSize(facts.SizeNotReadOnDisk)}).");
        }

        var folders = new List<CoverageFolder>();
        folders.AddRange(facts.NotScanned.Select(_ => new CoverageFolder("not-scanned")));
        folders.AddRange(facts.Unreadable.Select(_ => new CoverageFolder("unreadable")));

        // On screen the figures come first, then the folders, so that 500 folder lines never bury them: the one left out on
        // purpose, then the shallowest unreadable ones, and a count of the rest.
        var lines = new List<string> { $"The {FormatSize(unseen)} this scan did not see, by what the scan itself knows it missed:" };
        lines.AddRange(measured.Select(part => part.Text));
        lines.Add(remainder.Text);
        if (notRead is not null)
            lines.Add(notRead.Text);
        if (folders.Count > 0)
        {
            lines.Add("Folders the scan knows it missed:");
            lines.AddRange(facts.NotScanned.Select(row => "  " + row.Text));
            IReadOnlyList<UnreadableRow> shown = ShallowestFirst(facts.Unreadable, UnreadableShown);
            lines.AddRange(shown.Select(row => "  " + row.Text));
            int more = facts.Unreadable.Count - shown.Count;
            if (more > 0)
                lines.Add($"  and {Count(more, "more folder")}, in the Unreadable folders tab");
        }

        return new CoverageBreakdown(volumeInUse, seenOnDisk, unseen, lines, folders, measured, remainder, notRead);
    }

    /// <summary>
    /// The first <paramref name="limit"/> of <paramref name="rows"/>, which are in path order, by fewest path separators, ties
    /// in path order, and handed back in path order. So the shallow folders come first: 'C:\System Volume Information' (one
    /// separator) before a report folder four levels down.
    /// </summary>
    internal static IReadOnlyList<UnreadableRow> ShallowestFirst(IReadOnlyList<UnreadableRow> rows, int limit)
    {
        if (rows.Count <= limit)
            return rows;
        HashSet<int> chosen = rows
            .Select((row, index) => (Depth: Depth(row.Path), Index: index))
            .OrderBy(entry => entry.Depth)
            .ThenBy(entry => entry.Index)
            .Take(limit)
            .Select(entry => entry.Index)
            .ToHashSet();
        return rows.Where((_, index) => chosen.Contains(index)).ToList();
    }

    private static int Depth(string path) => path.TrimEnd('\\', '/').Count(c => c is '\\' or '/');

    /// <summary>The note for files that refused to have their own size read: the count is the scan's ProbeFailures.</summary>
    internal static string ProbeFailuresNote(int files) => files == 1
        ? $"{Count(files, "file")} could not be opened to read its true size on disk; its listed size was used."
        : $"{Count(files, "file")} could not be opened to read their true size on disk; their listed size was used.";

    /// <summary>The note for files deleted before their own size could be read: not a refusal, so a note of its own.</summary>
    internal static string VanishedNote(int files) => files == 1
        ? $"{Count(files, "file")} was deleted before its true size could be read; its listed size was used."
        : $"{Count(files, "file")} were deleted before their true size could be read; their listed size was used.";
}
