using System.Globalization;

namespace DevSweep.Core.Platform.Recycling;

/// <summary>The facts a recycle verdict is made from.</summary>
/// <param name="DriveKind">Kind of drive the item is on.</param>
/// <param name="PathLength">Length of the item's full path.</param>
/// <param name="ReparseAncestor">A junction or link the path goes through, if any.</param>
/// <param name="SizeComplete">False when the item could not be measured fully.</param>
/// <param name="ItemBytes">The larger of logical and on-disk size.</param>
/// <param name="Quota">The drive's Recycle Bin settings.</param>
public sealed record RecycleInputs(
    DriveKind DriveKind,
    int PathLength,
    string? ReparseAncestor,
    bool SizeComplete,
    long ItemBytes,
    RecycleQuota Quota);

/// <summary>Decides whether recycling an item really puts it in the Recycle Bin.</summary>
public static class RecyclePolicy
{
    /// <summary>The shell's file operations take paths shorter than MAX_PATH (260) only.</summary>
    public const int MaxShellPathLength = 259;

    public static (RecycleVerdict Verdict, string Reason) Evaluate(RecycleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.ReparseAncestor is { } link)
        {
            return (RecycleVerdict.NotAllowed,
                $"The path goes through a junction or link ({link}); recycling it would act on the link's target.");
        }

        if (inputs.PathLength > MaxShellPathLength)
        {
            return (RecycleVerdict.NotAllowed, "The path is too long for the Recycle Bin.");
        }

        if (inputs.DriveKind != DriveKind.Fixed)
        {
            return (RecycleVerdict.WouldBePermanent,
                $"{inputs.DriveKind} drives have no Recycle Bin, so Windows would delete it permanently.");
        }

        if (inputs.Quota.NukeOnDelete)
        {
            return (RecycleVerdict.WouldBePermanent,
                $"The Recycle Bin is turned off for this drive ({inputs.Quota.Source}).");
        }

        if (!inputs.SizeComplete)
        {
            return (RecycleVerdict.Unknown, "The item could not be measured fully, so it may be too big for the Recycle Bin.");
        }

        if (inputs.Quota.MaxBytes is not { } max)
        {
            return (RecycleVerdict.Unknown,
                $"The Recycle Bin size limit for this drive could not be found ({inputs.Quota.Source}).");
        }

        if (inputs.ItemBytes > max)
        {
            return (RecycleVerdict.WouldBePermanent, string.Create(CultureInfo.InvariantCulture,
                $"It is bigger ({inputs.ItemBytes:N0} bytes) than the Recycle Bin limit ({max:N0} bytes), so Windows would delete it permanently."));
        }

        return (RecycleVerdict.Recyclable, "It fits in the Recycle Bin and can be restored from there.");
    }
}
