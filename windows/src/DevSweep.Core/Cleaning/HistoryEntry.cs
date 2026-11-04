using System.Text.Json.Serialization;

namespace DevSweep.Core.Cleaning;

/// <summary>Where a removed item went.</summary>
public enum ItemDestination
{
    /// <summary>Deleted for good.</summary>
    Deleted,

    /// <summary>In the Recycle Bin (<see cref="MovedItem.Location"/> is its <c>$R</c> path).</summary>
    RecycleBin,

    /// <summary>In DevSweep's quarantine (<see cref="MovedItem.Location"/> is where, <see cref="MovedItem.QuarantineId"/> its ID).</summary>
    Quarantine,
}

/// <summary>One item a clean removed, and where it went.</summary>
public sealed record MovedItem
{
    public required string Original { get; init; }

    public ItemDestination Destination { get; init; }

    /// <summary>The Recycle Bin <c>$R</c> path or the quarantine path; null when deleted.</summary>
    public string? Location { get; init; }

    /// <summary>The Recycle Bin <c>$I</c> record, when recycled.</summary>
    public string? RecycleInfo { get; init; }

    public string? QuarantineId { get; init; }

    /// <summary>On-disk bytes measured just before it went.</summary>
    public long Bytes { get; init; }

    /// <summary>Why it went to the quarantine, not the Recycle Bin.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Free space on the item's volume before and after a clean, against what was
/// expected to be freed (PLAN 6: within 1% or 10 MB).
/// </summary>
public sealed record FreedSpace
{
    public const long MinimumTolerance = 10L * 1024 * 1024;

    /// <summary>The volume root.</summary>
    public required string Volume { get; init; }

    /// <summary>The size shown to the user.</summary>
    public long ShownBytes { get; init; }

    /// <summary>What should come free now: the measured freeable bytes for a delete, 0 for the Recycle Bin or quarantine (they free space only when emptied).</summary>
    public long ExpectedFreedBytes { get; init; }

    public long FreeBefore { get; init; }

    public long FreeAfter { get; init; }

    public long FreedBytes => FreeAfter - FreeBefore;

    /// <summary>Freed minus expected; positive when more came free than expected.</summary>
    public long Difference => FreedBytes - ExpectedFreedBytes;

    public long Tolerance => Math.Max(MinimumTolerance, Math.Abs(ExpectedFreedBytes) / 100);

    public bool WithinTolerance => Math.Abs(Difference) <= Tolerance;
}

/// <summary>One line in History: what DevSweep changed, and how to undo it.</summary>
public sealed record HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset Date { get; init; } = DateTimeOffset.UtcNow;

    public required string Title { get; init; }

    public string FindingId { get; init; } = string.Empty;

    public string RuleId { get; init; } = string.Empty;

    public CleanActionKind ActionKind { get; init; }

    public string ActionLabel { get; init; } = string.Empty;

    /// <summary>Bytes removed from their place.</summary>
    public long Size { get; init; }

    public IReadOnlyList<MovedItem> Items { get; init; } = [];

    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>The command's output (trimmed), for <see cref="CleanActionKind.Command"/>.</summary>
    public string? CommandOutput { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset? RestoredAt { get; init; }

    public FreedSpace? Reconciliation { get; init; }

    /// <summary>True while something of this entry can still be put back (checks the disk; not stored).</summary>
    [JsonIgnore]
    public bool CanRestore => RestoredAt is null
        && Items.Any(i => i.Destination != ItemDestination.Deleted && i.Location is { } location && (File.Exists(location) || Directory.Exists(location)));
}
