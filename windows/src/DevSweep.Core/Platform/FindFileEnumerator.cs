using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>
/// Lists one directory with <c>FindFirstFileExW</c> (basic info, large fetch).
/// Metadata only: no entry is opened, so cloud placeholders are not recalled
/// and antivirus does not scan file contents.
/// </summary>
internal sealed unsafe class FindFileEnumerator : IDisposable
{
    private HANDLE _handle;
    private WIN32_FIND_DATAW _data;
    private bool _hasPending;

    private FindFileEnumerator(HANDLE handle, WIN32_FIND_DATAW first)
    {
        _handle = handle;
        _data = first;
        _hasPending = true;
    }

    /// <summary>The Win32 error that ended enumeration early, or 0.</summary>
    public int Error { get; private set; }

    /// <summary>
    /// Opens a listing of <paramref name="extendedDirectory"/> (a <c>\\?\</c> path).
    /// Returns null and the Win32 error when the directory cannot be listed.
    /// </summary>
    public static FindFileEnumerator? Open(string extendedDirectory, out int error)
        => Find(LongPath.Join(extendedDirectory, "*"), out error);

    /// <summary>Reads the directory entry for <paramref name="extendedPath"/> itself (not its children).</summary>
    public static bool TryReadEntry(string extendedPath, out DirectoryEntry entry, out int error)
    {
        using FindFileEnumerator? find = Find(extendedPath, out error);
        if (find is null)
        {
            entry = default;
            return false;
        }

        entry = ToEntry(in find._data);
        return true;
    }

    public bool MoveNext(out DirectoryEntry entry)
    {
        while (true)
        {
            if (!_hasPending && !Advance())
            {
                entry = default;
                return false;
            }

            _hasPending = false;
            entry = ToEntry(in _data);
            if (entry.Name is not ("." or ".."))
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (_handle != HANDLE.INVALID_HANDLE_VALUE && !_handle.IsNull)
        {
            PInvoke.FindClose(_handle);
            _handle = HANDLE.INVALID_HANDLE_VALUE;
        }
    }

    private static FindFileEnumerator? Find(string pattern, out int error)
    {
        WIN32_FIND_DATAW data;
        HANDLE handle;
        fixed (char* p = pattern)
        {
            handle = PInvoke.FindFirstFileEx(
                p,
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                &data,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                null,
                FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
        }

        if (handle == HANDLE.INVALID_HANDLE_VALUE)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        return new FindFileEnumerator(handle, data);
    }

    private bool Advance()
    {
        fixed (WIN32_FIND_DATAW* p = &_data)
        {
            if (PInvoke.FindNextFile(_handle, p))
            {
                return true;
            }
        }

        int error = Marshal.GetLastPInvokeError();
        if (error != (int)WIN32_ERROR.ERROR_NO_MORE_FILES)
        {
            Error = error;
        }

        return false;
    }

    private static DirectoryEntry ToEntry(in WIN32_FIND_DATAW data)
    {
        var attributes = (Win32FileAttributes)data.dwFileAttributes;
        uint tag = (attributes & Win32FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : 0;
        long length = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
        return new DirectoryEntry(data.cFileName.ToString(), attributes, tag, length);
    }
}
