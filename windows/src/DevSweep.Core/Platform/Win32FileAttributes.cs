namespace DevSweep.Core.Platform;

/// <summary>
/// Win32 file attribute bits, including the cloud-file bits that
/// <see cref="System.IO.FileAttributes"/> does not name.
/// </summary>
[Flags]
public enum Win32FileAttributes : uint
{
    None = 0,
    ReadOnly = 0x0000_0001,
    Hidden = 0x0000_0002,
    System = 0x0000_0004,
    Directory = 0x0000_0010,
    Archive = 0x0000_0020,
    Device = 0x0000_0040,
    Normal = 0x0000_0080,
    Temporary = 0x0000_0100,
    SparseFile = 0x0000_0200,
    ReparsePoint = 0x0000_0400,
    Compressed = 0x0000_0800,
    Offline = 0x0000_1000,
    NotContentIndexed = 0x0000_2000,
    Encrypted = 0x0000_4000,
    IntegrityStream = 0x0000_8000,
    Virtual = 0x0001_0000,
    NoScrubData = 0x0002_0000,

    /// <summary>Opening the file (not just reading it) makes the cloud provider download it.</summary>
    RecallOnOpen = 0x0004_0000,

    /// <summary>The user asked to keep the file on this device ("Always keep on this device").</summary>
    Pinned = 0x0008_0000,

    /// <summary>The user asked to free up space ("Free up space"); data may still be local.</summary>
    Unpinned = 0x0010_0000,

    /// <summary>Some or all of the data is not on disk; reading it downloads it.</summary>
    RecallOnDataAccess = 0x0040_0000,
}
