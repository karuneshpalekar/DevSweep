namespace DevSweep.Core.Platform;

/// <summary>
/// What the directory listing says about one entry. Comes from
/// <c>FindFirstFileExW</c>, so getting it never opens the file.
/// </summary>
public readonly record struct DirectoryEntry(string Name, Win32FileAttributes Attributes, uint ReparseTag, long Length)
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
}
