using System.IO;

namespace FsAnalyser.App;

/// <summary>
/// The report's file, from the check that its folder takes a new file to the move that puts it in place (SL-007). No
/// WPF in it, and compiled into FsAnalyser.Core.Tests as source, so the deny, link, rename and held-open behaviours
/// run in the default suite without launching the exe.
/// <para>
/// Every file this class creates is made with FileMode.CreateNew at a name drawn at the moment of opening, in the report's
/// own folder: the probe, <c>.fsa-&lt;32 hex&gt;.probe</c>, and the temporary, <c>.fsa-&lt;32 hex&gt;.tmp</c>, which is then moved
/// over the report. The only predictable name written is the report itself, and a move replaces whatever entry is there
/// (a hardlink or a symbolic link is replaced as an entry; nothing is written through it).
/// </para>
/// <para>
/// What CreateNew refuses: a file, a hardlink, a symbolic link to a file that exists, a folder and a junction already at
/// the name. What it does NOT refuse: a symbolic link to a file that does not exist, which it follows, creating the file at
/// the link's target. .NET cannot open the link itself, and reading a name's reparse attribute is the scanner's classifier's
/// alone, so that one kind is covered only by the name: each stem is drawn on the line just before its own open, and the
/// probe's is a different draw from the temporary's, so nothing on disk ever shows the temporary's name before it exists.
/// A stem held from the constructor would show it through the probe for the whole scan.
/// </para>
/// </summary>
internal sealed class ReportFile
{
    private readonly string _path;
    private readonly Func<string> _freshStem;
    private string? _temporary;

    /// <summary>The report at <paramref name="path"/>, a full path. Nothing is drawn or created.</summary>
    public ReportFile(string path)
        : this(path, FreshStem)
    {
    }

    /// <summary>
    /// The same with a source of stems. It is called once for each file this creates, on the line just before the open,
    /// and never in this constructor. The app calls only the one-argument constructor; this one exists so that a test can
    /// plant an entry at the exact name the next open will use.
    /// </summary>
    internal ReportFile(string path, Func<string> freshStem)
    {
        _path = path;
        _freshStem = freshStem;
    }

    private static string FreshStem() => ".fsa-" + Guid.NewGuid().ToString("N");

    private string Folder => Path.GetDirectoryName(_path)!;

    /// <summary>
    /// Creates a file in the report's folder and removes it again, so that a folder that takes no new file is found before a
    /// scan and not after it. The file is opened to be deleted when its handle closes, so a killed process leaves nothing.
    /// </summary>
    /// <exception cref="IOException">The file could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder takes no new file from this account.</exception>
    public void Probe()
    {
        string probe = Path.Combine(Folder, _freshStem() + ".probe");
        using var stream = new FileStream(probe, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose,
            BufferSize = 0,
        });
    }

    /// <summary>
    /// Writes <paramref name="json"/> to a new temporary file beside the report and moves it over the report, so the report
    /// is either whole or absent. A temporary that was made is removed when anything after that fails.
    /// </summary>
    /// <exception cref="IOException">The report could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The report could not be written.</exception>
    public void Write(byte[] json)
    {
        try
        {
            string temporary = Path.Combine(Folder, _freshStem() + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _temporary = temporary; // from here the file is this class's own, and only then is it deleted on failure
                stream.Write(json);
            }
            File.Move(temporary, _path, overwrite: true);
            _temporary = null;
        }
        catch
        {
            DeleteQuietly(_temporary);
            _temporary = null;
            throw;
        }
    }

    /// <summary>
    /// Deletes the report, the temporary this has made if it is still there, and the fixed <c>&lt;report&gt;.tmp</c> that
    /// earlier versions wrote; never throws.
    /// </summary>
    public void Discard()
    {
        DeleteQuietly(_path);
        DeleteQuietly(_path + ".tmp");
        DeleteQuietly(_temporary);
        _temporary = null;
    }

    private static void DeleteQuietly(string? file)
    {
        if (file is null)
            return;
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
