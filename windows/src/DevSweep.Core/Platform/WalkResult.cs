namespace DevSweep.Core.Platform;

/// <summary>A reparse point the walker found and did not follow.</summary>
public sealed record ReparsePointRecord(string Path, uint Tag, ReparseKind Kind, bool IsDirectory);

/// <summary>A path the walker could not read, and the Win32 error.</summary>
public sealed record WalkProblem(string Path, int Win32Error);

/// <summary>What one walk measured. Sizes are on-disk allocation, each file counted once.</summary>
public sealed record WalkResult
{
    /// <summary>The walked path, normalised, without the <c>\\?\</c> prefix.</summary>
    public required string Path { get; init; }

    /// <summary>Bytes on disk: allocation of each distinct local file (alternate streams included), hard links counted once.</summary>
    public long Bytes { get; init; }

    /// <summary>Logical (end-of-file) bytes of the same files, alternate streams included.</summary>
    public long LogicalBytes { get; init; }

    /// <summary>Distinct local files measured.</summary>
    public long Files { get; init; }

    /// <summary>Directories entered (the root included).</summary>
    public long Directories { get; init; }

    /// <summary>False when the walk stopped early (time limit or cancellation).</summary>
    public bool Complete { get; init; }

    /// <summary>Extra names (hard links) seen for files already counted.</summary>
    public long HardLinkDuplicates { get; init; }

    /// <summary>
    /// Bytes of counted files that also have hard links outside this walk.
    /// Removing the walked item would not free them.
    /// </summary>
    public long SharedLinkBytes { get; init; }

    /// <summary>Files counted in <see cref="SharedLinkBytes"/>.</summary>
    public long SharedLinkFiles { get; init; }

    /// <summary>Cloud placeholders found (files, and cloud folders whose contents are not on this disk); 0 bytes, never opened.</summary>
    public long CloudPlaceholderCount { get; init; }

    /// <summary>Logical size of the placeholders (what is in the cloud, not on this disk).</summary>
    public long CloudPlaceholderLogicalBytes { get; init; }

    /// <summary>Cloud-provider folders (OneDrive and the like) that were entered, reading attributes only.</summary>
    public long CloudDirectories { get; init; }

    /// <summary>Alternate data streams found on counted files (not the main stream).</summary>
    public long AlternateStreamCount { get; init; }

    /// <summary>On-disk bytes of those streams; already included in <see cref="Bytes"/>.</summary>
    public long AlternateStreamBytes { get; init; }

    /// <summary>Folders or files that could not be read because access was denied.</summary>
    public long AccessDenied { get; init; }

    /// <summary>Folders or files that could not be read for another reason (sharing violation, I/O error...).</summary>
    public long Unreadable { get; init; }

    /// <summary>Reparse points found (all of them; <see cref="ReparsePoints"/> may list fewer).</summary>
    public long ReparsePointCount { get; init; }

    /// <summary>Reparse points found and not followed, up to <see cref="WalkOptions.MaxRecordedReparsePoints"/>.</summary>
    public IReadOnlyList<ReparsePointRecord> ReparsePoints { get; init; } = [];

    /// <summary>Paths that could not be read, up to <see cref="WalkOptions.MaxRecordedProblems"/>.</summary>
    public IReadOnlyList<WalkProblem> Problems { get; init; } = [];

    /// <summary>How folders were listed.</summary>
    public ListingUsed Listing { get; init; }

    /// <summary>
    /// Files whose allocation in the folder listing differed from the file's own
    /// (fresh) value. NTFS updates the directory copy lazily, and for hard links
    /// only under the name that was used, so the walker always takes the fresh one.
    /// </summary>
    public long StaleDirectoryEntries { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Bytes that removing the item would free: <see cref="Bytes"/> minus <see cref="SharedLinkBytes"/>.</summary>
    public long FreeableBytes => Bytes - SharedLinkBytes;

    public SizeConfidence Confidence
        => SizeConfidenceRules.From(Complete, Unreadable, AccessDenied, CloudPlaceholderCount, SharedLinkBytes);
}
