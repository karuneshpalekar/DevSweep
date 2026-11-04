using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>
/// Reads a file's on-disk size, link count, identity and alternate streams
/// through a handle opened for <c>FILE_READ_ATTRIBUTES</c> only, never following
/// a link and never recalling cloud data.
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

    /// <summary>Inspects one file, alternate streams included. Cloud placeholders are refused rather than opened.</summary>
    /// <exception cref="Win32Exception">The file cannot be opened or queried.</exception>
    /// <exception cref="InvalidOperationException">The file is a cloud placeholder.</exception>
    public static FileFacts Inspect(string path)
    {
        DirectoryEntry entry = ReadEntry(path);
        if (entry.CloudState == CloudFileState.Placeholder)
        {
            throw new InvalidOperationException($"{path} is a cloud placeholder; opening it would download it.");
        }

        if (!TryInspect(LongPath.ToExtended(path), in entry, includeStreams: true, out FileFacts facts, out int error))
        {
            throw new Win32Exception(error, $"Cannot inspect {path}");
        }

        return facts;
    }

    /// <summary>Inspects <paramref name="extendedPath"/>, whose directory entry the caller already has.</summary>
    internal static bool TryInspect(string extendedPath, in DirectoryEntry entry, bool includeStreams, out FileFacts facts, out int error)
    {
        facts = default;
        ReparseKind kind = entry.ReparseKind;

        // A WOF or dedup file is opened through its reparse point so the filter
        // reports the real data; every other reparse point is opened as itself.
        FILE_FLAGS_AND_ATTRIBUTES flags = ReparseTags.HoldsLocalData(kind)
            ? FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_NO_RECALL
            : NativeFile.NoFollow;

        using SafeFileHandle? handle = NativeFile.Open(extendedPath, NativeFile.ReadAttributes, flags, out error);
        if (handle is null)
        {
            return false;
        }

        FILE_STANDARD_INFO standard;
        if (!NativeFile.Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO), out error)
            || !TryGetIdentity(handle, out FileIdentity identity, out error))
        {
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
            if (NativeFile.Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileCompressionInfo, &compression, (uint)sizeof(FILE_COMPRESSION_INFO), out _))
            {
                allocation = compression.CompressedFileSize;
            }
        }

        StreamTotals streams = includeStreams ? AlternateStreams.Read(handle) : default;
        facts = new FileFacts(standard.EndOfFile, allocation, standard.NumberOfLinks, identity)
        {
            AlternateStreams = streams.Count,
            AlternateStreamLength = streams.Length,
            AlternateStreamAllocation = streams.Allocation,
        };
        error = 0;
        return true;
    }

    /// <summary>
    /// FILE_ID_INFO where the file system has it; otherwise (FAT, exFAT) the
    /// 32-bit serial and 64-bit index from BY_HANDLE_FILE_INFORMATION.
    /// </summary>
    private static bool TryGetIdentity(SafeFileHandle handle, out FileIdentity identity, out int error)
    {
        if (NativeFile.TryGetIdentity(handle, out identity, out error))
        {
            return true;
        }

        if (!Win32Errors.IsUnsupported(error))
        {
            return false;
        }

        if (!PInvoke.GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        identity = new FileIdentity(info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow);
        error = 0;
        return true;
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
}
