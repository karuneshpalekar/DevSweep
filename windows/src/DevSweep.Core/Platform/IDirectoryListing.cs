namespace DevSweep.Core.Platform;

/// <summary>The entries of one directory, without "." and "..".</summary>
internal interface IDirectoryListing : IDisposable
{
    /// <summary>The Win32 error that ended enumeration early, or 0.</summary>
    int Error { get; }

    bool MoveNext(out DirectoryEntry entry);
}

/// <summary>How the walker lists folders.</summary>
public enum ListingMethod
{
    /// <summary>
    /// <c>GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)</c> on each folder's
    /// handle (sizes and 128-bit file IDs with no per-file open), falling back to
    /// <c>FindFirstFileExW</c> for the rest of the walk when the file system does not support it.
    /// </summary>
    Auto,

    /// <summary><c>FindFirstFileExW</c> only, as in M0 (each file is then opened once for its ID).</summary>
    FindFirstFile,
}

/// <summary>Which listing a walk actually used.</summary>
public enum ListingUsed
{
    /// <summary>No folder was listed (a single file, a link, or nothing).</summary>
    None,

    FileIdExtdDirectoryInfo,

    FindFirstFile,

    /// <summary>Some folders each way (the walk fell back part way through).</summary>
    Both,
}
