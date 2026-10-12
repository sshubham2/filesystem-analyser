using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FsAnalyser.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Scanning;

/// <summary>One listed entry, exactly as the directory listing gave it.</summary>
internal struct RawEntry
{
    public int NameStart;
    public int NameLength;
    public long EndOfFile;
    public long AllocationSize;
    public uint Attributes;

    /// <summary>ReparsePointTag, or on the fallback class the EaSize field (the tag only for reparse points).</summary>
    public uint ReparseTag;

    /// <summary>The 128-bit FileId, or 0 on the fallback class.</summary>
    public UInt128 FileId;
}

/// <summary>
/// Lists one directory through a directory handle, with no file opened: GetFileInformationByHandleEx
/// paged through a reused 64 KiB buffer. The extended class (FILE_ID_EXTD_DIR_INFO) carries FileId,
/// EndOfFile, AllocationSize and the reparse tag in one pass. The fallback class (FILE_FULL_DIR_INFO)
/// has no FileId and carries the tag in EaSize. Records are yielded raw; nothing here interprets them.
/// Every call goes through the scan's <see cref="SynchronousIoCancel"/>, one per buffer, so Cancel is
/// honoured between the buffers of a large folder as well as inside a blocked call.
/// </summary>
internal sealed unsafe class DirectoryReader(SynchronousIoCancel io) : IDisposable
{
    internal const int BufferSize = 64 * 1024;

    // FILE_ID_EXTD_DIR_INFO and FILE_FULL_DIR_INFO share their first 68 bytes.
    private const int NextEntryOffset = 0;
    private const int EndOfFileOffset = 40;
    private const int AllocationSizeOffset = 48;
    private const int AttributesOffset = 56;
    private const int NameLengthOffset = 60;
    private const int FullEaSizeOffset = 64;
    private const int FullNameOffset = 68;
    private const int ExtdReparseTagOffset = 68;
    private const int ExtdFileIdOffset = 72;
    private const int ExtdNameOffset = 88;

    private byte* _buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8);
    private RawEntry[] _entries = new RawEntry[256];
    private char[] _names = new char[16 * 1024];
    private int _count;
    private int _namesUsed;

    /// <summary>The number of entries the last listing produced (excluding "." and "..").</summary>
    public int Count => _count;

    /// <summary>
    /// Whether the last <see cref="Read"/> got a handle for its folder: true once Open returned one, the keep-last-access
    /// open or the plain open it falls back to, whether or not the listing then failed. False for a refused open and
    /// for a folder at or under <see cref="ExcludedStore"/>. It tells the scan which phase an error came from, which the
    /// int <see cref="Read"/> returns does not (SL-106). Set by Read alone: the root goes through Open and List itself.
    /// </summary>
    public bool Opened { get; private set; }

    public ref readonly RawEntry this[int index] => ref _entries[index];

    public ReadOnlySpan<char> NameOf(in RawEntry entry) => _names.AsSpan(entry.NameStart, entry.NameLength);

    /// <summary>
    /// Opens a directory for listing, keeping its last-access time when asked and when the folder allows it; see
    /// <see cref="SynchronousIoCancel.OpenDirectory"/>.
    /// </summary>
    /// <returns>0, or the Win32 error.</returns>
    public int Open(string win32Path, bool keepLastAccess, out SafeFileHandle? handle) =>
        io.OpenDirectory(win32Path, keepLastAccess, out handle);

    /// <summary>What <see cref="Read"/> returns, with no open made, for a folder at or under <see cref="ExcludedStore"/>.</summary>
    public const int InsideExcludedStore = -1;

    /// <summary>
    /// The container layer store this scan leaves out, in the walk's spelling, or null (SL-095, <see cref="ContainerLayerStore"/>).
    /// The walk never asks to list it: it stops at the store when it pops it. This is the backstop behind that check, at the
    /// one call that opens a folder to list it, so that any other way a folder at or under the store reaches a listing is
    /// refused here and counted (review m4).
    /// </summary>
    public string? ExcludedStore { get; set; }

    /// <summary>
    /// How many listings <see cref="Read"/> refused because the folder was at or under <see cref="ExcludedStore"/>. 0 in
    /// every scan whose own check held; anything else is a way into the store the walk's check missed (review M6).
    /// </summary>
    public int OpensUnderExcludedStore { get; private set; }

    /// <summary>
    /// Opens and lists one directory. See <see cref="Open"/> and <see cref="List"/>. A folder at or under
    /// <see cref="ExcludedStore"/> is never opened: <see cref="InsideExcludedStore"/>, with nothing listed.
    /// </summary>
    public int Read(string win32Path, bool fullClass, bool keepLastAccess)
    {
        _count = 0;
        _namesUsed = 0;
        Opened = false;
        if (ExcludedStore is { } store && Scanner.IsAtOrUnder(win32Path, store))
        {
            OpensUnderExcludedStore++;
            return InsideExcludedStore;
        }
        int error = Open(win32Path, keepLastAccess, out SafeFileHandle? handle);
        if (error != 0)
            return error;
        Opened = true;
        try
        {
            return List(handle!, fullClass, win32Path);
        }
        finally
        {
            io.Close(handle!);
        }
    }

    /// <summary>
    /// Lists an open directory, replacing what the reader held.
    /// </summary>
    /// <returns>0 when the listing ran to its end, or the Win32 error that stopped it. Entries read before
    /// the error are kept, except when the guard aborted the listing: that listing keeps nothing, so a
    /// cancelled folder is never committed half read, and its handle is not used again. A 995 Windows
    /// returns with no cancel issued is an error like any other and keeps what was read.</returns>
    public int List(SafeFileHandle directory, bool fullClass) => List(directory, fullClass, win32Path: null);

    // As List, for the folder at win32Path when there is one: the listing failpoint (SL-082,
    // SynchronousIoCancel.FailListingOf) goes by the folder's name and the page, and stands in for what a call
    // returned before anything below reads it, as OverrideQueryError does inside the call.
    private int List(SafeFileHandle directory, bool fullClass, string? win32Path)
    {
        _count = 0;
        _namesUsed = 0;
        int infoClass = fullClass ? Kernel32.FileFullDirectoryRestartInfo : Kernel32.FileIdExtdDirectoryRestartInfo;
        bool first = true;
        int page = 0;

        while (true)
        {
            int error = io.Query(directory, infoClass, _buffer, BufferSize);
            page++;
            if (win32Path is not null)
                error = SynchronousIoCancel.ListingError(win32Path, page, error);
            if (error != 0)
            {
                if (error == Kernel32.ERROR_NO_MORE_FILES)
                    return 0;
                if (first && error == Kernel32.ERROR_FILE_NOT_FOUND)
                    return 0; // nothing at all to list
                if (io.LastCallAborted)
                {
                    _count = 0;
                    _namesUsed = 0;
                }
                return error;
            }

            Parse(fullClass);
            first = false;
            infoClass = fullClass ? Kernel32.FileFullDirectoryInfo : Kernel32.FileIdExtdDirectoryInfo;
        }
    }

    private void Parse(bool fullClass)
    {
        byte* p = _buffer;
        while (true)
        {
            uint next = Read<uint>(p, NextEntryOffset);
            int nameLength = (int)(Read<uint>(p, NameLengthOffset) / 2);
            char* name = (char*)(p + (fullClass ? FullNameOffset : ExtdNameOffset));

            bool dots = (nameLength == 1 && name[0] == '.') || (nameLength == 2 && name[0] == '.' && name[1] == '.');
            if (!dots)
            {
                if (_count == _entries.Length)
                    Array.Resize(ref _entries, _entries.Length * 2);
                if (_namesUsed + nameLength > _names.Length)
                    Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesUsed + nameLength));

                new ReadOnlySpan<char>(name, nameLength).CopyTo(_names.AsSpan(_namesUsed));
                ref RawEntry entry = ref _entries[_count++];
                entry.NameStart = _namesUsed;
                entry.NameLength = nameLength;
                entry.EndOfFile = Read<long>(p, EndOfFileOffset);
                entry.AllocationSize = Read<long>(p, AllocationSizeOffset);
                entry.Attributes = Read<uint>(p, AttributesOffset);
                if (fullClass)
                {
                    entry.ReparseTag = Read<uint>(p, FullEaSizeOffset);
                    entry.FileId = UInt128.Zero;
                }
                else
                {
                    entry.ReparseTag = Read<uint>(p, ExtdReparseTagOffset);
                    entry.FileId = new UInt128(Read<ulong>(p, ExtdFileIdOffset + 8), Read<ulong>(p, ExtdFileIdOffset));
                }
                _namesUsed += nameLength;
            }

            if (next == 0)
                return;
            p += next;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(byte* p, int offset) where T : unmanaged => Unsafe.ReadUnaligned<T>(p + offset);

    public void Dispose()
    {
        if (_buffer != null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }
}
