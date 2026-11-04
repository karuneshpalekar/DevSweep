using DevSweep.Core.Platform;
using DevSweep.Core.Rules;

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

/// <summary>How a finding's size was measured.</summary>
public enum SizeMeasurement
{
    /// <summary>Every file measured with its alternate data streams (the size the cleaner reconciles against).</summary>
    Exact,

    /// <summary>
    /// Measured for an overview, without alternate data streams (no handle per file).
    /// Re-measure exactly before review or cleaning (<see cref="Scanning.FindingRemeasurer"/>).
    /// </summary>
    Overview,

    /// <summary>The size came from the owning tool (for example <c>docker system df</c>), not from walking files.</summary>
    Reported,
}

/// <summary>Whether a <see cref="Check"/> passed.</summary>
public enum CheckStatus
{
    Passed,
    Warning,
}

/// <summary>One line in "What was checked".</summary>
/// <param name="Status">Passed, or a warning the user should read.</param>
/// <param name="Text">What was checked and what it showed.</param>
public sealed record Check(CheckStatus Status, string Text)
{
    public static Check Passed(string text) => new(CheckStatus.Passed, text);

    public static Check Warning(string text) => new(CheckStatus.Warning, text);
}

/// <summary>Something DevSweep found and explains (the Mac model, plus Windows size confidence and measurement mode).</summary>
public sealed record Finding
{
    /// <summary><c>ruleId:key</c>, stable between scans (used for "ignore").</summary>
    public required string Id { get; init; }

    public string RuleId { get; init; } = string.Empty;

    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    public Category Category { get; init; } = Category.Windows;

    public required Risk Risk { get; init; }

    /// <summary>The items to clean, as full paths.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// The size shown to the user: on-disk bytes, each file counted once across
    /// <see cref="Paths"/> (hard links between them included once).
    /// </summary>
    public long Size { get; init; }

    /// <summary>On-disk size of each of <see cref="Paths"/>, in the same order (empty when the size was reported by a tool).</summary>
    public IReadOnlyList<long> PathSizes { get; init; } = [];

    /// <summary>Bytes removing every path would free: <see cref="Size"/> minus files that also have hard links elsewhere.</summary>
    public long FreeableBytes { get; init; }

    /// <summary>Files counted.</summary>
    public long Files { get; init; }

    public SizeConfidence Confidence { get; init; } = SizeConfidence.Exact;

    /// <summary>How <see cref="Size"/> was measured; <see cref="SizeMeasurement.Overview"/> needs an exact re-measure before cleaning.</summary>
    public SizeMeasurement Measurement { get; init; } = SizeMeasurement.Exact;

    /// <summary>Bytes of hard-linked files this finding shares with other findings of the same scan (counted once in the scan total).</summary>
    public long SharedWithOtherFindingsBytes { get; init; }

    /// <summary>The ⓘ text, rendered with this finding's facts.</summary>
    public Explanation? Explanation { get; init; }

    /// <summary>What was checked before calling it safe, and what to watch out for.</summary>
    public IReadOnlyList<Check> Checks { get; init; } = [];

    /// <summary>The facts the explanation was rendered with (size, path, project, app...).</summary>
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<CleanAction> Actions { get; init; } = [];

    /// <summary>Process names (without .exe) that must not be running when this is cleaned.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Those of <see cref="Blockers"/> that were running when the scan looked.</summary>
    public IReadOnlyList<string> BlockingApps { get; init; } = [];

    /// <summary>How to undo it, for History.</summary>
    public string? UndoNote { get; init; }
}

/// <summary>A finding and the action the user chose for it in Review.</summary>
public sealed record PlannedAction(Finding Finding, CleanAction Action);
