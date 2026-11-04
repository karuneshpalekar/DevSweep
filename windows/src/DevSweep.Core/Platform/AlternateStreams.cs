using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>Totals of a file's named (alternate) data streams.</summary>
internal readonly record struct StreamTotals(int Count, long Length, long Allocation);

/// <summary>
/// Reads a file's alternate data streams with <c>GetFileInformationByHandleEx(FileStreamInfo)</c>
/// on a metadata-only handle. The main stream (<c>::$DATA</c>) is measured
/// elsewhere; the Windows Overlay Filter's own <c>WofCompressedData</c> stream is
/// already what the main stream's allocation reports, so it is not counted twice.
/// </summary>
internal static unsafe class AlternateStreams
{
    private const string MainStream = "::$DATA";
    private const string WofStream = ":WofCompressedData:$DATA";
    private const int ErrorBadLength = 24;
    private const int StackBuffer = 512;
    private const int FirstBuffer = 4 * 1024;
    private const int LargestBuffer = 4 * 1024 * 1024;

    /// <summary>The totals, or zeros when the file system has no streams or does not say.</summary>
    public static StreamTotals Read(SafeFileHandle handle)
    {
        // Nearly every file has only its main stream: try a small stack buffer first.
        long* small = stackalloc long[StackBuffer / sizeof(long)];
        if (NativeFile.Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileStreamInfo, small, StackBuffer, out int first))
        {
            return Sum((byte*)small);
        }

        if (first is not (Win32Errors.MoreData or ErrorBadLength))
        {
            return default;
        }

        for (int size = FirstBuffer; size <= LargestBuffer; size *= 8)
        {
            byte* buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 8);
            try
            {
                if (NativeFile.Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileStreamInfo, buffer, (uint)size, out int error))
                {
                    return Sum(buffer);
                }

                if (error is not (Win32Errors.MoreData or ErrorBadLength))
                {
                    // ERROR_HANDLE_EOF: no streams at all; unsupported on FAT and some network file systems.
                    return default;
                }
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }

        return default;
    }

    private static StreamTotals Sum(byte* buffer)
    {
        int count = 0;
        long length = 0;
        long allocation = 0;
        uint offset = 0;
        while (true)
        {
            var info = (FILE_STREAM_INFO*)(buffer + offset);
            var name = new ReadOnlySpan<char>(&info->StreamName, (int)(info->StreamNameLength / sizeof(char)));
            if (!name.Equals(MainStream, StringComparison.OrdinalIgnoreCase)
                && !name.Equals(WofStream, StringComparison.OrdinalIgnoreCase))
            {
                count++;
                length += info->StreamSize;
                allocation += info->StreamAllocationSize;
            }

            if (info->NextEntryOffset == 0)
            {
                return new StreamTotals(count, length, allocation);
            }

            offset += info->NextEntryOffset;
        }
    }
}
