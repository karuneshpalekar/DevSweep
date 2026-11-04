namespace DevSweep.Core.Platform;

/// <summary>Limits for one <see cref="DirectoryWalker"/> walk.</summary>
public sealed record WalkOptions
{
    public static WalkOptions Default { get; } = new();

    /// <summary>Stop after this long and return what was counted, with Complete = false. Null means no limit.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>At most this many reparse points are listed in the result (all are counted).</summary>
    public int MaxRecordedReparsePoints { get; init; } = 1000;

    /// <summary>At most this many unreadable paths are listed in the result (all are counted).</summary>
    public int MaxRecordedProblems { get; init; } = 100;
}
