namespace DevSweep.Core.Cleaning;

/// <summary>What carrying out one planned action did.</summary>
public sealed record ActionOutcome
{
    public required Finding Finding { get; init; }

    public required CleanAction Action { get; init; }

    public bool Succeeded { get; init; }

    /// <summary>A sentence for the user: what happened, or why nothing did ("Close X first.").</summary>
    public required string Message { get; init; }

    /// <summary>Bytes removed from their place (measured just before).</summary>
    public long Freed { get; init; }

    /// <summary>Where each item went.</summary>
    public IReadOnlyList<MovedItem> Items { get; init; } = [];

    /// <summary>Free space on the volume before and after, against what was expected.</summary>
    public FreedSpace? Reconciliation { get; init; }

    public CommandResult? Command { get; init; }

    /// <summary>The History entry written for it, if any.</summary>
    public Guid? HistoryId { get; init; }

    /// <summary>True when nothing on disk was changed (refused before acting).</summary>
    public bool NothingChanged => Items.Count == 0 && Command is null;
}

/// <summary>Settings for <see cref="Cleaner"/>.</summary>
public sealed record CleanerOptions
{
    public static CleanerOptions Default { get; } = new();

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Files per item checked for programs holding them open.</summary>
    public int LockCheckFileLimit { get; init; } = Platform.FileLocks.DefaultFileLimit;

    /// <summary>
    /// Allow removing items that contain cloud placeholders. Off: removing one
    /// would remove the file from the cloud too, so the user must confirm that first (PLAN 4.4).
    /// </summary>
    public bool AllowCloudPlaceholders { get; init; }
}
