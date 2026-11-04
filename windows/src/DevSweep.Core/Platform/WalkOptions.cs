namespace DevSweep.Core.Platform;

/// <summary>Limits and choices for one <see cref="DirectoryWalker"/> walk.</summary>
public sealed record WalkOptions
{
    public static WalkOptions Default { get; } = new();

    /// <summary>Stop after this long and return what was counted, with Complete = false. Null means no limit.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>At most this many reparse points are listed in the result (all are counted).</summary>
    public int MaxRecordedReparsePoints { get; init; } = 1000;

    /// <summary>At most this many unreadable paths are listed in the result (all are counted).</summary>
    public int MaxRecordedProblems { get; init; } = 100;

    /// <summary>How folders are listed. <see cref="ListingMethod.Auto"/> is walker v2.</summary>
    public ListingMethod Listing { get; init; } = ListingMethod.Auto;

    /// <summary>
    /// Count alternate data streams (true by default). Needs one metadata-only
    /// handle per file (<c>FileStreamInfo</c>). When false, files are measured by
    /// name with no handle at all (<c>NtQueryInformationByName</c>) and streams
    /// are left out.
    /// </summary>
    public bool AlternateStreams { get; init; } = true;

    /// <summary>
    /// List every file with more than one hard link in <see cref="WalkResult.LinkedFiles"/>
    /// (identity, bytes, links seen), so a scan can count bytes shared between
    /// findings once. Off by default.
    /// </summary>
    public bool RecordLinkedFiles { get; init; }
}
