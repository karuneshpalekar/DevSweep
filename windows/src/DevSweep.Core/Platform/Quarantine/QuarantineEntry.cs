namespace DevSweep.Core.Platform.Quarantine;

/// <summary>
/// One item in DevSweep's quarantine: moved (renamed, never copied) into a
/// quarantine folder on its own volume, restorable to exactly where it was.
/// Stored as <c>&lt;root&gt;\&lt;id&gt;.json</c> beside <c>&lt;root&gt;\&lt;id&gt;\&lt;name&gt;</c>.
/// </summary>
public sealed record QuarantineEntry
{
    /// <summary>Sortable and unique: <c>yyyyMMdd-HHmmss-xxxxxxxx</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Where the item was (canonical, long names).</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Where the item is now.</summary>
    public required string StoredPath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>On-disk bytes when it was quarantined (what emptying it will free).</summary>
    public long Bytes { get; init; }

    /// <summary>Logical bytes when it was quarantined.</summary>
    public long LogicalBytes { get; init; }

    public DateTimeOffset QuarantinedAt { get; init; }

    /// <summary>Why it went to the quarantine rather than the Recycle Bin.</summary>
    public string? Reason { get; init; }

    public TimeSpan AgeAt(DateTimeOffset now) => now - QuarantinedAt;
}

/// <summary>What moving an item into the quarantine did.</summary>
public sealed record QuarantineResult(bool Moved, QuarantineEntry? Entry, string Message);

/// <summary>What restoring an item did.</summary>
public sealed record QuarantineRestoreResult(bool Restored, string Path, string Message);
