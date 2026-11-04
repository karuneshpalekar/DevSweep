namespace DevSweep.Core.Platform;

/// <summary>
/// What the directory listing says about one entry. Comes from
/// <c>GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)</c> on the folder's
/// handle, or <c>FindFirstFileExW</c> where that is not supported, so getting it
/// never opens the file.
/// </summary>
/// <param name="Name">Entry name (no path).</param>
/// <param name="Attributes">Win32 attributes, including the cloud-file bits.</param>
/// <param name="ReparseTag">Reparse tag; 0 when this is not a reparse point.</param>
/// <param name="Length">Logical size (end of file) as recorded in the directory.</param>
/// <param name="AllocationSize">Allocation as recorded in the directory, or -1 when the listing does not say.</param>
/// <param name="FileId">128-bit file ID, or null when the listing does not say (FindFirstFileExW).</param>
public readonly record struct DirectoryEntry(
    string Name,
    Win32FileAttributes Attributes,
    uint ReparseTag,
    long Length,
    long AllocationSize = -1,
    UInt128? FileId = null)
{
    public bool IsDirectory => (Attributes & Win32FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & Win32FileAttributes.ReparsePoint) != 0;

    /// <summary>The reparse kind; <see cref="ReparseKind.None"/> when this is not a reparse point.</summary>
    public ReparseKind ReparseKind => IsReparsePoint ? ReparseTags.Classify(ReparseTag) : ReparseKind.None;

    public CloudFileState CloudState => CloudFiles.Classify(Attributes, IsReparsePoint ? ReparseTag : 0);

    /// <summary>
    /// A link to somewhere else (junction, symlink, alias...): recorded, never followed or measured through.
    /// Data-holding reparse points (WOF, dedup) and cloud files are not links.
    /// </summary>
    public bool IsLink => IsReparsePoint
        && ReparseKind is not (ReparseKind.Wof or ReparseKind.Dedup or ReparseKind.Cloud);

    /// <summary>
    /// A folder the walker may enter: an ordinary folder, or a cloud-provider
    /// folder (OneDrive and the like, the user's real folders) whose children are
    /// already on this disk. Every other reparse point is a hard stop.
    /// </summary>
    public bool IsTraversableDirectory => IsDirectory
        && (!IsReparsePoint || (ReparseKind == ReparseKind.Cloud && CloudState != CloudFileState.Placeholder));
}
