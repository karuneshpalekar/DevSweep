using System.Globalization;

namespace DevSweep.Core.Platform;

/// <summary>
/// Identifies a file regardless of its name: every hard link to the same file
/// has the same identity. Volume serial plus the 128-bit file ID from
/// <c>FILE_ID_INFO</c> (unique on NTFS and ReFS).
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{VolumeSerial:X16}:{FileId:X32}");
}

/// <summary>What the file system says about one file, read from a metadata-only handle.</summary>
/// <param name="Length">Logical size (end of file) in bytes.</param>
/// <param name="AllocationSize">Bytes the file takes on disk: after compression, without sparse holes.</param>
/// <param name="LinkCount">Number of hard links (names) the file has across the volume.</param>
/// <param name="Identity">Volume serial plus file ID.</param>
public readonly record struct FileFacts(long Length, long AllocationSize, uint LinkCount, FileIdentity Identity);
