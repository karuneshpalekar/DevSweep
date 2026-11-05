using System.Diagnostics;
using System.Globalization;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Safety;

namespace DevSweep.Core.Review;

/// <summary>What will actually happen to an item when the plan runs (the Review sheet's groups).</summary>
public enum ReviewOutcome
{
    /// <summary>Every path goes to the Recycle Bin and can be restored until the bin is emptied.</summary>
    RecycleBin,

    /// <summary>At least one path goes to DevSweep's quarantine because the Recycle Bin would not keep it; restorable from History.</summary>
    Quarantine,

    /// <summary>Deleted for good right away (rebuilds itself). Cannot be undone.</summary>
    DeleteForGood,

    /// <summary>The owning tool's command runs. Cannot be undone by DevSweep.</summary>
    RunCommand,

    /// <summary>DevSweep changes nothing; the user runs the commands.</summary>
    Manual,

    /// <summary>It cannot be cleaned now (a program must close first, the safety guard refuses it, or nothing could keep it).</summary>
    Blocked,
}

/// <summary>One planned item as Review shows it.</summary>
public sealed record ReviewItem
{
    public required Finding Finding { get; init; }

    public required CleanAction Action { get; init; }

    public required ReviewOutcome Outcome { get; init; }

    /// <summary>One sentence: what happens, or why it cannot.</summary>
    public required string Summary { get; init; }

    /// <summary>The exact operations or commands, one per line (what the "Commands that will run" panel shows).</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>"Close Visual Studio Code (Code.exe, PID 4120) first." and the like.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Per-path notes (why a path goes to the quarantine, paths that are gone).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when DevSweep can put it back from History.</summary>
    public bool CanBeUndone => Outcome is ReviewOutcome.RecycleBin or ReviewOutcome.Quarantine;
}

/// <summary>A whole reviewed plan.</summary>
public sealed record ReviewPlan(IReadOnlyList<ReviewItem> Items)
{
    /// <summary>True when anything in the plan cannot be undone (a delete or a command).</summary>
    public bool HasPermanentChanges => Items.Any(i => i.Outcome is ReviewOutcome.DeleteForGood or ReviewOutcome.RunCommand);

    /// <summary>The items the cleaner will act on (not manual, not blocked).</summary>
    public IReadOnlyList<PlannedAction> Runnable
        => [.. Items.Where(i => i.Outcome is not (ReviewOutcome.Manual or ReviewOutcome.Blocked)).Select(i => new PlannedAction(i.Finding, i.Action))];

    public IReadOnlyList<ReviewItem> In(ReviewOutcome outcome) => [.. Items.Where(i => i.Outcome == outcome)];
}

/// <summary>What the planner asks the machine; <see cref="SystemReviewProbe"/> asks the real one.</summary>
public interface IReviewProbe
{
    /// <summary>The safety guard's verdict (opens the item, checks its final path, closes it).</summary>
    SafetyVerdict Check(string path);

    RecycleAssessment AssessRecycle(string path);

    /// <summary>The quarantine folder for an item's volume, or null with a reason.</summary>
    string? QuarantineRoot(string path, out string reason);

    /// <summary>Programs holding files of the item open (Restart Manager).</summary>
    LockReport Locks(string path);

    /// <summary>Those of <paramref name="names"/> (process names without .exe) running now.</summary>
    IReadOnlyList<string> Running(IReadOnlyList<string> names);
}

/// <summary>The real machine: the guard, the Recycle Bin policy, the quarantine and Restart Manager.</summary>
public sealed class SystemReviewProbe(SafetyGuard guard, RecycleBin recycleBin, QuarantineStore quarantine, int lockFileLimit = FileLocks.DefaultFileLimit) : IReviewProbe
{
    public SafetyVerdict Check(string path) => guard.Check(path);

    public RecycleAssessment AssessRecycle(string path) => recycleBin.Assess(path);

    public string? QuarantineRoot(string path, out string reason) => quarantine.Locations.RootFor(path, out reason);

    public LockReport Locks(string path) => FileLocks.Check(path, lockFileLimit);

    public IReadOnlyList<string> Running(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var running = new List<string>();
        foreach (string name in names)
        {
            string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            Process[] processes = Process.GetProcessesByName(bare);
            if (processes.Length > 0)
            {
                running.Add(bare);
            }

            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return running;
    }
}

/// <summary>
/// Works out, before anything changes, what the <see cref="Cleaner"/> will do with each planned
/// item: the same checks in the same order (named blockers, the safety guard, the Recycle Bin
/// verdict and the quarantine fallback, Restart Manager), without changing anything. Review
/// groups items by the answer and shows the exact operations.
/// </summary>
public sealed class ReviewPlanner(IReviewProbe probe)
{
    public ReviewPlan Prepare(IReadOnlyList<PlannedAction> plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var items = new List<ReviewItem>(plan.Count);
        foreach (PlannedAction action in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(PrepareOne(action));
        }

        return new ReviewPlan(items);
    }

    public ReviewItem PrepareOne(PlannedAction planned)
    {
        ArgumentNullException.ThrowIfNull(planned);
        Finding finding = planned.Finding;
        CleanAction action = planned.Action;
        IReadOnlyList<string> running = finding.Blockers.Count == 0 ? [] : probe.Running(finding.Blockers);
        if (running.Count > 0 && action.Kind != CleanActionKind.Manual)
        {
            return Item(planned, ReviewOutcome.Blocked, $"Close {string.Join(", ", running)} first.") with
            {
                Blockers = [.. running.Select(r => $"Close {r} first.")],
            };
        }

        return action.Kind switch
        {
            CleanActionKind.Manual => Item(planned, ReviewOutcome.Manual, action.Detail ?? "You run these yourself. DevSweep doesn't change anything.") with
            {
                Commands = action.Command is { Count: > 0 } c ? [CommandLineText.Join(c)] : [],
            },
            CleanActionKind.Command => Item(planned, ReviewOutcome.RunCommand, action.Command is { Count: > 0 } ? $"Runs {action.Command[0]}. This can't be undone." : "No command to run.") with
            {
                Outcome = action.Command is { Count: > 0 } ? ReviewOutcome.RunCommand : ReviewOutcome.Blocked,
                Commands = action.Command is { Count: > 0 } argv ? [CommandLineText.Join(argv)] : [],
            },
            CleanActionKind.Delete when finding.Risk != Risk.Rebuilds => Item(planned, ReviewOutcome.Blocked,
                "Delete is only for items that rebuild themselves; use Move to Recycle Bin, which can be undone."),
            _ => Removal(planned),
        };
    }

    private ReviewItem Removal(PlannedAction planned)
    {
        bool delete = planned.Action.Kind == CleanActionKind.Delete;
        var commands = new List<string>();
        var notes = new List<string>();
        var blockers = new List<string>();
        bool quarantined = false;
        int present = 0;
        foreach (string path in planned.Finding.Paths)
        {
            SafetyVerdict verdict = probe.Check(path);
            if (verdict.Refusal == SafetyRefusal.Missing)
            {
                notes.Add($"{path} is no longer there.");
                continue;
            }

            if (!verdict.Allowed)
            {
                return Item(planned, ReviewOutcome.Blocked, verdict.Reason) with { Notes = [$"{path}: {verdict.Reason}"] };
            }

            present++;
            if (probe.Locks(path).BlockerMessage is { } locked)
            {
                blockers.Add(locked);
            }

            if (delete)
            {
                commands.Add($"delete for good   {Quote(path)}");
                continue;
            }

            RecycleAssessment assessment = probe.AssessRecycle(path);
            if (assessment.Verdict == RecycleVerdict.Recyclable)
            {
                commands.Add($"Recycle Bin       {Quote(path)}");
                continue;
            }

            string? root = probe.QuarantineRoot(path, out string reason);
            if (root is null)
            {
                return Item(planned, ReviewOutcome.Blocked,
                    $"The Recycle Bin would not keep it ({assessment.Reason}) and there is no quarantine on its drive ({reason}).") with
                {
                    Notes = [$"{path}: {assessment.Reason}"],
                };
            }

            quarantined = true;
            commands.Add($"quarantine        {Quote(path)} -> {Quote(root)}");
            notes.Add($"{path}: {assessment.Reason} It goes to DevSweep's quarantine instead.");
        }

        if (present == 0)
        {
            return Item(planned, ReviewOutcome.Blocked, "Nothing is there any more.") with { Notes = notes };
        }

        if (blockers.Count > 0)
        {
            return Item(planned, ReviewOutcome.Blocked, blockers[0]) with { Blockers = blockers, Commands = commands, Notes = notes };
        }

        (ReviewOutcome outcome, string summary) = delete
            ? (ReviewOutcome.DeleteForGood, "Deleted right away. It rebuilds on its own, so there's nothing to restore. This can't be undone.")
            : quarantined
                ? (ReviewOutcome.Quarantine, "The Recycle Bin wouldn't keep it, so it moves to DevSweep's quarantine on the same drive. Restore it from History; it takes space until you empty the quarantine.")
                : (ReviewOutcome.RecycleBin, "Moved to the Recycle Bin. Restore it from History until you empty the Recycle Bin.");
        return Item(planned, outcome, summary) with { Commands = commands, Notes = notes };
    }

    private static ReviewItem Item(PlannedAction planned, ReviewOutcome outcome, string summary)
        => new() { Finding = planned.Finding, Action = planned.Action, Outcome = outcome, Summary = summary };

    private static string Quote(string path) => string.Create(CultureInfo.InvariantCulture, $"\"{path}\"");
}

/// <summary>Shows an argv the way Windows would parse it back (CommandLineToArgvW rules).</summary>
public static class CommandLineText
{
    public static string Join(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return string.Join(' ', argv.Select(Quote));
    }

    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var text = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            text.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            backslashes = 0;
            text.Append(c);
        }

        text.Append('\\', backslashes * 2).Append('"');
        return text.ToString();
    }
}
