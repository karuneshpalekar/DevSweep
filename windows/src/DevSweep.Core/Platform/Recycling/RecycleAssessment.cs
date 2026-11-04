namespace DevSweep.Core.Platform.Recycling;

/// <summary>What would happen if an item were sent to the Recycle Bin.</summary>
public enum RecycleVerdict
{
    /// <summary>It goes to the Recycle Bin and can be restored.</summary>
    Recyclable,

    /// <summary>Windows would delete it permanently (no bin on the drive, bin turned off, or too big).</summary>
    WouldBePermanent,

    /// <summary>We cannot tell (bin size limit not found, or the item could not be measured), so we never claim it is reversible.</summary>
    Unknown,

    /// <summary>DevSweep will not recycle it this way (inside a junction or link, or path too long for the shell).</summary>
    NotAllowed,
}

/// <summary>The verdict for one item and why.</summary>
/// <param name="Path">The item, normalised.</param>
/// <param name="Verdict">What would happen.</param>
/// <param name="Reason">A sentence for the user.</param>
/// <param name="ItemBytes">Size compared with the bin limit: the larger of logical and on-disk size.</param>
/// <param name="QuotaBytes">The Recycle Bin size limit for the drive, when known.</param>
/// <param name="DriveKind">Kind of drive the item is on.</param>
public sealed record RecycleAssessment(
    string Path,
    RecycleVerdict Verdict,
    string Reason,
    long ItemBytes,
    long? QuotaBytes,
    DriveKind DriveKind)
{
    /// <summary>True only when we are sure the item can be restored from the Recycle Bin.</summary>
    public bool IsReversible => Verdict == RecycleVerdict.Recyclable;
}

/// <summary>The result of a recycle request.</summary>
/// <param name="Recycled">True when the item was sent to the Recycle Bin and is gone from its place.</param>
/// <param name="Assessment">The check made before acting.</param>
/// <param name="Error">Why nothing was done, or what went wrong.</param>
public sealed record RecycleOutcome(bool Recycled, RecycleAssessment Assessment, string? Error);

/// <summary>What is in a drive's Recycle Bin now (SHQueryRecycleBin).</summary>
public readonly record struct RecycleBinUsage(long Bytes, long Items);
