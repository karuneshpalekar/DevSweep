using System.Text;

namespace DevSweep.Fixtures;

/// <summary>
/// A small virtual disk (VHDX), formatted with a chosen file system and mounted
/// in an empty NTFS folder (no drive letter needed): a second volume for
/// fallback-listing, mount-point and cross-volume tests. Built with
/// <c>diskpart</c>, which needs an administrator.
/// </summary>
public sealed class VirtualDisk : IDisposable
{
    private readonly string _vhd;
    private bool _attached;

    private VirtualDisk(string vhd, string mountPath)
    {
        _vhd = vhd;
        MountPath = mountPath;
    }

    /// <summary>The folder the volume is mounted in.</summary>
    public string MountPath { get; }

    /// <summary>Creates, formats and mounts the disk; null with a precise reason when it cannot.</summary>
    public static VirtualDisk? TryCreate(string folder, string fileSystem, out string skipReason)
    {
        if (!Elevation.IsAdministrator)
        {
            skipReason = "Creating and mounting a virtual disk (diskpart) needs an administrator; this run is a standard user.";
            return null;
        }

        Directory.CreateDirectory(folder);
        string vhd = Path.Combine(folder, "volume.vhdx");
        string mount = Path.Combine(folder, "mounted");
        Directory.CreateDirectory(mount);
        var disk = new VirtualDisk(vhd, mount);
        (int exit, string output) = Diskpart(folder, $"""
            create vdisk file="{vhd}" maximum=64 type=expandable
            select vdisk file="{vhd}"
            attach vdisk
            convert mbr
            create partition primary
            format fs={fileSystem} quick label=DSFIX
            assign mount="{mount}"
            """);
        disk._attached = File.Exists(vhd);
        if (exit != 0 || !IsMounted(mount))
        {
            disk.Dispose();
            skipReason = $"diskpart could not create a {fileSystem} volume (exit {exit}): {output}";
            return null;
        }

        skipReason = string.Empty;
        return disk;
    }

    public void Dispose()
    {
        if (_attached)
        {
            Diskpart(Path.GetDirectoryName(_vhd)!, $"""
                select vdisk file="{_vhd}"
                detach vdisk
                """);
            _attached = false;
        }
    }

    private static bool IsMounted(string folder)
        => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;

    private static (int Exit, string Output) Diskpart(string folder, string script)
    {
        string file = Path.Combine(folder, $"diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script, Encoding.ASCII);
        try
        {
            return Ntfs.Run("diskpart.exe", $"/s \"{file}\"");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
