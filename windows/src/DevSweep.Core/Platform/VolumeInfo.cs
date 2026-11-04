using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;

namespace DevSweep.Core.Platform;

/// <summary>The kind of drive a volume is on (GetDriveType).</summary>
public enum DriveKind
{
    Unknown = 0,
    NoRootDirectory = 1,
    Removable = 2,
    Fixed = 3,
    Remote = 4,
    CdRom = 5,
    RamDisk = 6,
}

/// <summary>Facts about the volume that holds a path.</summary>
/// <param name="RootPath">Volume root as a display path, for example <c>C:\</c> or a mounted folder.</param>
/// <param name="VolumeGuidPath"><c>\\?\Volume{GUID}\</c>, or null when the volume has none (network shares).</param>
/// <param name="Kind">Drive kind.</param>
/// <param name="FileSystem">File system name, for example NTFS or ReFS.</param>
/// <param name="SerialNumber">32-bit volume serial number.</param>
/// <param name="ClusterSize">Allocation unit in bytes.</param>
/// <param name="TotalBytes">Volume capacity in bytes.</param>
/// <param name="FreeBytes">Free bytes available to the caller.</param>
public sealed record VolumeInfo(
    string RootPath,
    string? VolumeGuidPath,
    DriveKind Kind,
    string FileSystem,
    uint SerialNumber,
    long ClusterSize,
    long TotalBytes,
    long FreeBytes)
{
    /// <summary>The <c>{GUID}</c> part of <see cref="VolumeGuidPath"/>, or null.</summary>
    public string? VolumeGuid => ParseVolumeGuid(VolumeGuidPath);

    /// <summary>Extracts <c>{GUID}</c> from <c>\\?\Volume{GUID}\</c>.</summary>
    public static string? ParseVolumeGuid(string? volumeGuidPath)
    {
        if (volumeGuidPath is null)
        {
            return null;
        }

        int open = volumeGuidPath.IndexOf('{', StringComparison.Ordinal);
        int close = volumeGuidPath.IndexOf('}', StringComparison.Ordinal);
        return open >= 0 && close > open ? volumeGuidPath[open..(close + 1)] : null;
    }
}

/// <summary>Looks up <see cref="VolumeInfo"/> for a path.</summary>
public static unsafe class Volumes
{
    private const int MaxPath = 261;

    /// <exception cref="Win32Exception">The volume cannot be queried.</exception>
    public static VolumeInfo ForPath(string path)
    {
        string root = VolumeRoot(LongPath.Normalize(path));

        Span<char> fsName = stackalloc char[MaxPath];
        if (!PInvoke.GetVolumeInformation(root, default, out uint serial, out _, out _, fsName))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot read volume information for {root}");
        }

        long clusterSize = 0;
        if (PInvoke.GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
        {
            clusterSize = (long)sectorsPerCluster * bytesPerSector;
        }

        long total = 0;
        long free = 0;
        if (PInvoke.GetDiskFreeSpaceEx(root, out ulong freeToCaller, out ulong totalBytes, out _))
        {
            total = (long)totalBytes;
            free = (long)freeToCaller;
        }

        return new VolumeInfo(
            root,
            VolumeGuidPath(root),
            (DriveKind)PInvoke.GetDriveType(root),
            fsName.UpToNull().ToString(),
            serial,
            clusterSize,
            total,
            free);
    }

    /// <summary>The root of the volume holding <paramref name="fullPath"/>, mounted folders included.</summary>
    internal static string VolumeRoot(string fullPath)
    {
        Span<char> buffer = stackalloc char[MaxPath];
        if (fullPath.Length < MaxPath && PInvoke.GetVolumePathName(fullPath, buffer))
        {
            return buffer.UpToNull().ToString();
        }

        // Past MAX_PATH: fall back to the drive or share root.
        return Path.GetPathRoot(fullPath) ?? fullPath;
    }

    private static string? VolumeGuidPath(string root)
    {
        Span<char> buffer = stackalloc char[64];
        string withSlash = root.EndsWith('\\') ? root : root + "\\";
        return PInvoke.GetVolumeNameForVolumeMountPoint(withSlash, buffer) ? buffer.UpToNull().ToString() : null;
    }
}

internal static class SpanExtensions
{
    public static Span<char> UpToNull(this Span<char> value)
    {
        int end = value.IndexOf('\0');
        return end < 0 ? value : value[..end];
    }
}
