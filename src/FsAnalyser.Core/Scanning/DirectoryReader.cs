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

    public ref readonly RawEntry this[int index] => ref _entries[index];

    public ReadOnlySpan<char> NameOf(in RawEntry entry) => _names.AsSpan(entry.NameStart, entry.NameLength);

    /// <summary>Opens a directory for listing; see <see cref="SynchronousIoCancel.OpenDirectory"/>.</summary>
    /// <returns>0, or the Win32 error.</returns>
    public int Open(string win32Path, out SafeFileHandle? handle) => io.OpenDirectory(win32Path, out handle);

    /// <summary>Opens and lists one directory. See <see cref="List"/>.</summary>
    public int Read(string win32Path, bool fullClass)
    {
        _count = 0;
        _namesUsed = 0;
        int error = Open(win32Path, out SafeFileHandle? handle);
        if (error != 0)
            return error;
        try
        {
            return List(handle!, fullClass);
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
    public int List(SafeFileHandle directory, bool fullClass)
    {
        _count = 0;
        _namesUsed = 0;
        int infoClass = fullClass ? Kernel32.FileFullDirectoryRestartInfo : Kernel32.FileIdExtdDirectoryRestartInfo;
        bool first = true;

        while (true)
        {
            int error = io.Query(directory, infoClass, _buffer, BufferSize);
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
