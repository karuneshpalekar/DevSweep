using Microsoft.Win32;

namespace DevSweep.Core.Platform.Recycling;

/// <summary>A drive's Recycle Bin settings.</summary>
/// <param name="MaxBytes">Largest total the bin holds; items bigger than this are deleted permanently. Null when unknown.</param>
/// <param name="NukeOnDelete">True when the bin is turned off ("Don't move files to the Recycle Bin").</param>
/// <param name="Source">Where the settings came from, for explanations.</param>
public sealed record RecycleQuota(long? MaxBytes, bool NukeOnDelete, string Source);

/// <summary>Reads a drive's Recycle Bin settings.</summary>
public interface IRecycleBinQuotaSource
{
    RecycleQuota GetQuota(VolumeInfo volume);
}

/// <summary>
/// Reads Recycle Bin settings from the registry: group policy first
/// (NoRecycleFiles, RecycleBinSize), then the per-volume BitBucket keys that
/// the Recycle Bin properties dialog writes (NukeOnDelete, MaxCapacity in MB).
/// </summary>
public sealed class RegistryRecycleBinQuotaSource : IRecycleBinQuotaSource
{
    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string BitBucketVolumeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\";
    private const long Megabyte = 1024 * 1024;

    public RecycleQuota GetQuota(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (ReadPolicy("NoRecycleFiles") == 1)
        {
            return new RecycleQuota(null, NukeOnDelete: true, "group policy NoRecycleFiles");
        }

        int? nuke = null;
        int? maxCapacityMb = null;
        if (volume.VolumeGuid is { } guid)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BitBucketVolumeKey + guid);
            nuke = ReadDword(key, "NukeOnDelete");
            maxCapacityMb = ReadDword(key, "MaxCapacity");
        }

        if (nuke == 1)
        {
            return new RecycleQuota(null, NukeOnDelete: true, "BitBucket NukeOnDelete");
        }

        if (ReadPolicy("RecycleBinSize") is int percent and > 0 and <= 100 && volume.TotalBytes > 0)
        {
            return new RecycleQuota(volume.TotalBytes / 100 * percent, NukeOnDelete: false, "group policy RecycleBinSize");
        }

        if (maxCapacityMb is int mb and > 0)
        {
            return new RecycleQuota(mb * Megabyte, NukeOnDelete: false, "BitBucket MaxCapacity");
        }

        return new RecycleQuota(null, NukeOnDelete: false, "no Recycle Bin size setting found");
    }

    private static int? ReadPolicy(string name)
    {
        using RegistryKey? machine = Registry.LocalMachine.OpenSubKey(PolicyKey);
        using RegistryKey? user = Registry.CurrentUser.OpenSubKey(PolicyKey);
        return ReadDword(machine, name) ?? ReadDword(user, name);
    }

    private static int? ReadDword(RegistryKey? key, string name) => key?.GetValue(name) is int value ? value : null;
}
