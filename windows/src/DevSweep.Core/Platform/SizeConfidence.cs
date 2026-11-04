namespace DevSweep.Core.Platform;

/// <summary>
/// How far a measured size can be trusted (PLAN 4.1). When several apply, the
/// most serious wins, in the order Partial, AccessDenied, CloudPlaceholders,
/// SharedLinks, Exact.
/// </summary>
public enum SizeConfidence
{
    /// <summary>Every byte was measured and all of it is this item's own.</summary>
    Exact,

    /// <summary>Some files share data with hard links outside the item; removing it frees less.</summary>
    SharedLinks,

    /// <summary>Some files are cloud placeholders; they count as 0 bytes on this disk.</summary>
    CloudPlaceholders,

    /// <summary>Some folders or files could not be read; the size is a lower bound.</summary>
    AccessDenied,

    /// <summary>The walk stopped early (time limit, cancelled, or read errors); the size is a lower bound.</summary>
    Partial,
}

/// <summary>Picks the <see cref="SizeConfidence"/> for a set of walk counts.</summary>
public static class SizeConfidenceRules
{
    public static SizeConfidence From(bool complete, long unreadable, long accessDenied, long cloudPlaceholders, long sharedLinkBytes)
    {
        if (!complete || unreadable > 0)
        {
            return SizeConfidence.Partial;
        }

        if (accessDenied > 0)
        {
            return SizeConfidence.AccessDenied;
        }

        if (cloudPlaceholders > 0)
        {
            return SizeConfidence.CloudPlaceholders;
        }

        return sharedLinkBytes > 0 ? SizeConfidence.SharedLinks : SizeConfidence.Exact;
    }
}
