using System.Globalization;

namespace DevSweep.Core.Platform;

/// <summary>
/// Identifies a file regardless of its name: every hard link to the same file
/// has the same identity. Volume serial plus the 128-bit file ID from
/// <c>FILE_ID_INFO</c> or <c>FILE_ID_EXTD_DIR_INFO</c> (unique on NTFS and ReFS).
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{VolumeSerial:X16}:{FileId:X32}");
}

/// <summary>What the file system says about one file, read without opening its contents.</summary>
/// <param name="Length">Logical size (end of file) of the main data stream in bytes.</param>
/// <param name="AllocationSize">Bytes the main data stream takes on disk: after compression, without sparse holes.</param>
/// <param name="LinkCount">Number of hard links (names) the file has across the volume.</param>
/// <param name="Identity">Volume serial plus file ID.</param>
public readonly record struct FileFacts(long Length, long AllocationSize, uint LinkCount, FileIdentity Identity)
{
    /// <summary>Named (alternate) data streams the file has, not counting the main stream.</summary>
    public int AlternateStreams { get; init; }

    /// <summary>Logical bytes of the alternate data streams.</summary>
    public long AlternateStreamLength { get; init; }

    /// <summary>Bytes the alternate data streams take on disk.</summary>
    public long AlternateStreamAllocation { get; init; }

    /// <summary>Everything the file takes on disk: main stream plus alternate streams.</summary>
    public long TotalAllocation => AllocationSize + AlternateStreamAllocation;
}
