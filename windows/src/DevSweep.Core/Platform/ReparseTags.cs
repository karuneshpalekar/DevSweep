namespace DevSweep.Core.Platform;

/// <summary>What a reparse point is, as far as measuring and removing goes.</summary>
public enum ReparseKind
{
    None,

    /// <summary>Junction or volume mount point (IO_REPARSE_TAG_MOUNT_POINT).</summary>
    Junction,

    /// <summary>NTFS symbolic link to a file or directory.</summary>
    SymbolicLink,

    /// <summary>Cloud Files placeholder (OneDrive and other sync providers).</summary>
    Cloud,

    /// <summary>App execution alias, for example the Store's python.exe stub.</summary>
    AppExecLink,

    /// <summary>Windows Overlay Filter compression (compact /exe, CompactOS). Data is local.</summary>
    Wof,

    /// <summary>Data Deduplication. Data is local, in the dedup store.</summary>
    Dedup,

    /// <summary>WSL symbolic link.</summary>
    WslSymlink,

    /// <summary>Projected File System (for example VFS for Git).</summary>
    ProjectedFs,

    /// <summary>AF_UNIX socket.</summary>
    UnixSocket,

    /// <summary>Any other tag. Treated as a link: never followed.</summary>
    Other,
}

/// <summary>Reparse tag values and their classification.</summary>
public static class ReparseTags
{
    public const uint MountPoint = 0xA000_0003;
    public const uint Symlink = 0xA000_000C;
    public const uint Dedup = 0x8000_0013;
    public const uint Wof = 0x8000_0017;
    public const uint AppExecLink = 0x8000_001B;
    public const uint LxSymlink = 0xA000_001D;
    public const uint ProjFs = 0x9000_001C;
    public const uint AfUnix = 0x8000_0023;
    public const uint OneDriveLegacy = 0x8000_0021;

    /// <summary>IO_REPARSE_TAG_CLOUD; CLOUD_1..CLOUD_F differ only in bits 12-15.</summary>
    public const uint Cloud = 0x9000_001A;
    private const uint CloudMask = 0xFFFF_0FFF;

    public static ReparseKind Classify(uint tag) => tag switch
    {
        0 => ReparseKind.None,
        MountPoint => ReparseKind.Junction,
        Symlink => ReparseKind.SymbolicLink,
        AppExecLink => ReparseKind.AppExecLink,
        Wof => ReparseKind.Wof,
        Dedup => ReparseKind.Dedup,
        LxSymlink => ReparseKind.WslSymlink,
        ProjFs => ReparseKind.ProjectedFs,
        AfUnix => ReparseKind.UnixSocket,
        OneDriveLegacy => ReparseKind.Cloud,
        _ when (tag & CloudMask) == Cloud => ReparseKind.Cloud,
        _ => ReparseKind.Other,
    };

    /// <summary>
    /// True when the reparse point stores the file's own data on this volume
    /// (compression or dedup), so the file is measured rather than treated as a link.
    /// </summary>
    public static bool HoldsLocalData(ReparseKind kind) => kind is ReparseKind.Wof or ReparseKind.Dedup;
}
