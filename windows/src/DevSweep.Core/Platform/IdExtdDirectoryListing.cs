using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>
/// Lists one directory from its own handle with
/// <c>GetFileInformationByHandleEx(FileIdExtdDirectoryRestartInfo / FileIdExtdDirectoryInfo)</c>:
/// each entry carries attributes, reparse tag, end of file, allocation and the
/// 128-bit file ID, so no entry is opened. The handle is opened as the folder
/// itself (never through a reparse point), so the folder listed is the folder
/// that was checked.
/// </summary>
internal sealed unsafe class IdExtdDirectoryListing : IDirectoryListing
{
    private const int BufferSize = 64 * 1024;

    private readonly SafeFileHandle _handle;
    private byte* _buffer;
    private uint _offset;
    private bool _hasBatch;
    private bool _done;

    private IdExtdDirectoryListing(SafeFileHandle handle, byte* buffer)
    {
        _handle = handle;
        _buffer = buffer;
        _hasBatch = true;
    }

    public int Error { get; private set; }

    /// <summary>The folder's handle, for further queries (volume serial).</summary>
    public SafeFileHandle Handle => _handle;

    /// <summary>
    /// Opens a listing of <paramref name="handle"/>'s directory. Unsupported when the
    /// file system does not implement the information class; the handle is then left to the caller.
    /// </summary>
    public static ListingStatus TryCreate(SafeFileHandle handle, out IdExtdDirectoryListing? listing, out int error)
    {
        listing = null;
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 8);
        if (NativeFile.Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdExtdDirectoryRestartInfo, buffer, BufferSize, out error))
        {
            listing = new IdExtdDirectoryListing(handle, buffer);
            return ListingStatus.Opened;
        }

        NativeMemory.AlignedFree(buffer);
        if (error == Win32Errors.NoMoreFiles)
        {
            // Not even "." and "..": treat as empty.
            listing = new IdExtdDirectoryListing(handle, (byte*)NativeMemory.AlignedAlloc(8, 8)) { _done = true, _hasBatch = false };
            error = 0;
            return ListingStatus.Opened;
        }

        return Win32Errors.IsUnsupported(error) ? ListingStatus.Unsupported : ListingStatus.Failed;
    }

    public bool MoveNext(out DirectoryEntry entry)
    {
        while (true)
        {
            if (!_hasBatch && !Fill())
            {
                entry = default;
                return false;
            }

            var info = (FILE_ID_EXTD_DIR_INFO*)(_buffer + _offset);
            if (info->NextEntryOffset == 0)
            {
                _hasBatch = false;
                _offset = 0;
            }
            else
            {
                _offset += info->NextEntryOffset;
            }

            var name = new string((char*)&info->FileName, 0, (int)(info->FileNameLength / sizeof(char)));
            if (name is "." or "..")
            {
                continue;
            }

            var attributes = (Win32FileAttributes)info->FileAttributes;
            uint tag = (attributes & Win32FileAttributes.ReparsePoint) != 0 ? info->ReparsePointTag : 0;
            entry = new DirectoryEntry(name, attributes, tag, info->EndOfFile, info->AllocationSize, NativeFile.ToUInt128(&info->FileId));
            return true;
        }
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }

        _handle.Dispose();
    }

    private bool Fill()
    {
        if (_done)
        {
            return false;
        }

        if (NativeFile.Query(_handle, FILE_INFO_BY_HANDLE_CLASS.FileIdExtdDirectoryInfo, _buffer, BufferSize, out int error))
        {
            _hasBatch = true;
            _offset = 0;
            return true;
        }

        _done = true;
        if (error != Win32Errors.NoMoreFiles)
        {
            Error = error;
        }

        return false;
    }
}

/// <summary>The outcome of opening a directory listing.</summary>
internal enum ListingStatus
{
    Opened,

    /// <summary>The file system does not support this listing; use the fallback.</summary>
    Unsupported,

    /// <summary>The folder could not be opened or listed (the error says why).</summary>
    Failed,

    /// <summary>The folder turned out to be a link when opened (it changed after it was listed); not entered.</summary>
    BecameLink,
}
