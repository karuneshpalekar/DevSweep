using Windows.Wdk;
using Windows.Wdk.Foundation;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32.Foundation;
using Windows.Win32.System.IO;

namespace DevSweep.Core.Platform;

/// <summary>Link count and sizes of one file, read by name.</summary>
internal readonly record struct StatFacts(long Length, long AllocationSize, uint LinkCount, Win32FileAttributes Attributes);

/// <summary>
/// <c>NtQueryInformationByName(FileStatInformation)</c>: the link count and fresh
/// sizes of a file without a user-mode handle (one call instead of open, two
/// queries and close). Used by the walker when alternate streams are not counted.
/// Supported on NTFS since Windows 10 1709; elsewhere the caller opens the file.
/// </summary>
internal static unsafe class FileStat
{
    private const uint ObjCaseInsensitive = 0x40;

    /// <summary>Opened = true with facts; false with a Win32 error (unsupported errors mean "use a handle").</summary>
    public static bool TryQuery(string extendedPath, out StatFacts facts, out int error)
    {
        facts = default;
        string ntPath = ToNtPath(extendedPath);
        if (ntPath.Length > short.MaxValue / sizeof(char))
        {
            error = Win32Errors.InvalidParameter;
            return false;
        }

        FILE_STAT_INFORMATION info;
        IO_STATUS_BLOCK io;
        NTSTATUS status;
        fixed (char* p = ntPath)
        {
            var name = new UNICODE_STRING
            {
                Length = (ushort)(ntPath.Length * sizeof(char)),
                MaximumLength = (ushort)(ntPath.Length * sizeof(char)),
                Buffer = p,
            };
            var attributes = new OBJECT_ATTRIBUTES
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                ObjectName = &name,
                Attributes = (OBJECT_ATTRIBUTE_FLAGS)ObjCaseInsensitive,
            };
            status = PInvoke.NtQueryInformationByName(&attributes, &io, &info, (uint)sizeof(FILE_STAT_INFORMATION), FILE_INFORMATION_CLASS.FileStatInformation);
        }

        if ((uint)status.Value >= 0xC000_0000)
        {
            error = (int)Windows.Win32.PInvoke.RtlNtStatusToDosError(status);
            return false;
        }

        error = 0;
        facts = new StatFacts(info.EndOfFile, info.AllocationSize, info.NumberOfLinks, (Win32FileAttributes)info.FileAttributes);
        return true;
    }

    /// <summary><c>\\?\C:\x</c> becomes <c>\??\C:\x</c>; <c>\\?\UNC\s\x</c> becomes <c>\??\UNC\s\x</c>.</summary>
    internal static string ToNtPath(string extendedPath) => string.Concat(@"\??\", extendedPath.AsSpan(4));
}
