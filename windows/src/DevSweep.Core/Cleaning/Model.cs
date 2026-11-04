using DevSweep.Core.Platform;

namespace DevSweep.Core.Cleaning;

/// <summary>How risky it is to remove something (same scale as the Mac app).</summary>
public enum Risk
{
    /// <summary>Comes back on its own when a tool needs it. The only risk that may be deleted outright.</summary>
    Rebuilds,

    /// <summary>A newer version is installed or nothing uses it.</summary>
    OldVersion,

    /// <summary>The app that made it is gone.</summary>
    Leftover,

    /// <summary>May contain the user's own data.</summary>
    HoldsData,

    /// <summary>Outside the profile; needs admin, so only shown as a command.</summary>
    NeedsAdmin,
}

/// <summary>What a clean action does (same four kinds as the Mac app).</summary>
public enum CleanActionKind
{
    /// <summary>Remove for good, immediately. Only for <see cref="Risk.Rebuilds"/> items.</summary>
    Delete,

    /// <summary>Recycle Bin, or DevSweep's quarantine where the bin would not keep it; restorable from History.</summary>
    Trash,

    /// <summary>Run the owning tool's own command (argv, no shell).</summary>
    Command,

    /// <summary>Show commands for the user to run (admin ones, for example). DevSweep changes nothing.</summary>
    Manual,
}

/// <summary>One way to clean a finding.</summary>
/// <param name="Kind">What it does.</param>
/// <param name="Label">Button text; a default per kind when null.</param>
/// <param name="Command">For <see cref="CleanActionKind.Command"/> and <see cref="CleanActionKind.Manual"/>: program and arguments, never a shell string.</param>
/// <param name="Detail">One line under the button.</param>
public sealed record CleanAction(CleanActionKind Kind, string? Label = null, IReadOnlyList<string>? Command = null, string? Detail = null)
{
    public string DisplayLabel => Label ?? Kind switch
    {
        CleanActionKind.Delete => "Delete",
        CleanActionKind.Trash => "Move to Recycle Bin",
        CleanActionKind.Command => $"Run {(Command is { Count: > 0 } c ? c[0] : "command")}",
        _ => "Show commands",
    };

    public bool IsRestorable => Kind == CleanActionKind.Trash;
}

/// <summary>Something DevSweep found and explains (the subset of the Mac model the cleaner needs; rules fill the rest in M2).</summary>
public sealed record Finding
{
    public required string Id { get; init; }

    public string RuleId { get; init; } = string.Empty;

    public required string Title { get; init; }

    public required Risk Risk { get; init; }

    /// <summary>The items to clean, as full paths.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>The size shown to the user (on-disk bytes).</summary>
    public long Size { get; init; }

    public SizeConfidence Confidence { get; init; } = SizeConfidence.Exact;

    public IReadOnlyList<CleanAction> Actions { get; init; } = [];

    /// <summary>Process names (without .exe) that must not be running when this is cleaned.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>How to undo it, for History.</summary>
    public string? UndoNote { get; init; }
}

/// <summary>A finding and the action the user chose for it in Review.</summary>
public sealed record PlannedAction(Finding Finding, CleanAction Action);
