using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>
/// Reads a file's on-disk size, link count and identity through a handle opened
/// for <c>FILE_READ_ATTRIBUTES</c> only, never following a link and never
/// recalling cloud data.
/// </summary>
public static unsafe class FileInspector
{
    private const Win32FileAttributes CompressedOrSparse = Win32FileAttributes.Compressed | Win32FileAttributes.SparseFile;
    private const uint InvalidFileSize = 0xFFFF_FFFF;

    /// <summary>Reads the directory entry for <paramref name="path"/> without opening it.</summary>
    /// <exception cref="Win32Exception">The entry cannot be read.</exception>
    public static DirectoryEntry ReadEntry(string path)
    {
        if (!FindFileEnumerator.TryReadEntry(LongPath.ToExtended(path), out DirectoryEntry entry, out int error))
        {
            throw new Win32Exception(error, $"Cannot read the directory entry for {path}");
        }

        return entry;
    }

    /// <summary>Inspects one file. Cloud placeholders are refused rather than opened.</summary>
    /// <exception cref="Win32Exception">The file cannot be opened or queried.</exception>
    /// <exception cref="InvalidOperationException">The file is a cloud placeholder.</exception>
    public static FileFacts Inspect(string path)
    {
        DirectoryEntry entry = ReadEntry(path);
        if (entry.CloudState == CloudFileState.Placeholder)
        {
            throw new InvalidOperationException($"{path} is a cloud placeholder; opening it would download it.");
        }

        if (!TryInspect(LongPath.ToExtended(path), in entry, out FileFacts facts, out int error))
        {
            throw new Win32Exception(error, $"Cannot inspect {path}");
        }

        return facts;
    }

    /// <summary>Inspects <paramref name="extendedPath"/>, whose directory entry the caller already has.</summary>
    internal static bool TryInspect(string extendedPath, in DirectoryEntry entry, out FileFacts facts, out int error)
    {
        facts = default;
        ReparseKind kind = entry.ReparseKind;

        // A WOF or dedup file is opened through its reparse point so the filter
        // reports the real data; every other reparse point is opened as itself.
        FILE_FLAGS_AND_ATTRIBUTES flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS
            | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_NO_RECALL;
        if (!ReparseTags.HoldsLocalData(kind))
        {
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;
        }

        HANDLE handle = OpenForAttributes(extendedPath, flags);
        if (handle == HANDLE.INVALID_HANDLE_VALUE)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            FILE_STANDARD_INFO standard;
            FILE_ID_INFO id;
            if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO))
                || !PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }

            long allocation = standard.AllocationSize;
            if (kind == ReparseKind.Wof)
            {
                allocation = CompressedSizeByPath(extendedPath, allocation);
            }
            else if ((entry.Attributes & CompressedOrSparse) != 0)
            {
                FILE_COMPRESSION_INFO compression;
                if (PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileCompressionInfo, &compression, (uint)sizeof(FILE_COMPRESSION_INFO)))
                {
                    allocation = compression.CompressedFileSize;
                }
            }

            facts = new FileFacts(standard.EndOfFile, allocation, standard.NumberOfLinks, ToIdentity(id));
            error = 0;
            return true;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    /// <summary>Opens a metadata-only handle (FILE_READ_ATTRIBUTES, share everything).</summary>
    internal static HANDLE OpenForAttributes(string extendedPath, FILE_FLAGS_AND_ATTRIBUTES flags)
    {
        fixed (char* p = extendedPath)
        {
            return PInvoke.CreateFile(
                p,
                (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                flags,
                HANDLE.Null);
        }
    }

    private static long CompressedSizeByPath(string extendedPath, long fallback)
    {
        uint high;
        uint low;
        Marshal.SetLastPInvokeError(0);
        fixed (char* p = extendedPath)
        {
            low = PInvoke.GetCompressedFileSize(p, &high);
        }

        if (low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
        {
            return fallback;
        }

        return ((long)high << 32) | low;
    }

    private static FileIdentity ToIdentity(FILE_ID_INFO info)
    {
        ulong* words = (ulong*)&info.FileId;
        return new FileIdentity(info.VolumeSerialNumber, new UInt128(words[1], words[0]));
    }
}
