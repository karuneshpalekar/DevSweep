using System.Diagnostics;
using System.Globalization;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Safety;

namespace DevSweep.Core.Cleaning;

/// <summary>
/// Carries out a reviewed plan and records every change in History (the Mac
/// Cleaner, ported). Before touching anything it re-checks blockers (named
/// processes, then Restart Manager for every file), runs every path through the
/// <see cref="SafetyGuard"/> and refuses the whole action if any path is not
/// allowed. Removal goes through the handle the guard checked. Afterwards the
/// volume's free space is compared with what was expected.
/// </summary>
public sealed class Cleaner(
    SafetyGuard guard,
    HistoryStore history,
    QuarantineStore quarantine,
    RecycleBin recycleBin,
    CleanerOptions? options = null)
{
    private readonly CleanerOptions _options = options ?? CleanerOptions.Default;

    public async Task<IReadOnlyList<ActionOutcome>> RunAsync(
        IReadOnlyList<PlannedAction> plan, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outcomes = new List<ActionOutcome>(plan.Count);
        foreach (PlannedAction item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(item.Finding.Title);
            outcomes.Add(await PerformAsync(item, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    public async Task<ActionOutcome> PerformAsync(PlannedAction item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ActionOutcome outcome = await PerformCoreAsync(item, cancellationToken).ConfigureAwait(false);
        if (outcome.Items.Count > 0 || (outcome.Succeeded && outcome.Command is not null))
        {
            HistoryEntry entry = ToHistory(item, outcome);
            history.Append(entry);
            outcome = outcome with { HistoryId = entry.Id };
        }

        return outcome;
    }

    private async Task<ActionOutcome> PerformCoreAsync(PlannedAction item, CancellationToken cancellationToken)
    {
        Finding finding = item.Finding;
        if (RunningBlockers(finding.Blockers) is { Count: > 0 } running)
        {
            return Refuse(item, $"Close {string.Join(", ", running)} first.");
        }

        return item.Action.Kind switch
        {
            CleanActionKind.Manual => Refuse(item, "Run the commands yourself; DevSweep didn't change anything."),
            CleanActionKind.Command => await RunCommandAsync(item, cancellationToken).ConfigureAwait(false),
            CleanActionKind.Delete when finding.Risk != Risk.Rebuilds => Refuse(item,
                "Delete is only for items that rebuild themselves; use Move to Recycle Bin, which can be undone."),
            _ => Remove(item),
        };
    }

    private ActionOutcome Remove(PlannedAction plan)
    {
        var items = new List<(VerifiedItem Item, WalkResult Size)>();
        try
        {
            string? refusal = CheckAll(plan.Finding, items);
            if (refusal is not null)
            {
                return Refuse(plan, refusal);
            }

            if (items.Count == 0)
            {
                return Refuse(plan, "Nothing is there any more.");
            }

            string volume = Volumes.VolumeRoot(items[0].Item.Path);
            long before = Volumes.FreeBytes(volume);
            var moved = new List<MovedItem>();
            var errors = new List<string>();
            long freed = 0;
            long expected = 0;
            foreach ((VerifiedItem item, WalkResult size) in items)
            {
                MovedItem? done = plan.Action.Kind == CleanActionKind.Delete ? Delete(item, size, errors) : Trash(item, size, errors);
                item.Dispose();
                if (done is not null)
                {
                    moved.Add(done);
                    freed += size.Bytes;
                    expected += done.Destination == ItemDestination.Deleted ? size.FreeableBytes : 0;
                }
            }

            var reconciliation = new FreedSpace
            {
                Volume = volume,
                ShownBytes = plan.Finding.Size,
                ExpectedFreedBytes = expected,
                FreeBefore = before,
                FreeAfter = Volumes.FreeBytes(volume),
            };
            return new ActionOutcome
            {
                Finding = plan.Finding,
                Action = plan.Action,
                Succeeded = errors.Count == 0,
                Message = errors.Count == 0 ? Describe(moved) : string.Join(Environment.NewLine, errors),
                Freed = freed,
                Items = moved,
                Reconciliation = reconciliation,
            };
        }
        finally
        {
            foreach ((VerifiedItem item, _) in items)
            {
                item.Dispose();
            }
        }
    }

    /// <summary>Opens, measures and lock-checks every path; a refusal message, or null when all may go.</summary>
    private string? CheckAll(Finding finding, List<(VerifiedItem Item, WalkResult Size)> items)
    {
        foreach (string path in finding.Paths)
        {
            SafetyVerdict verdict = guard.TryOpen(path, out VerifiedItem? item);
            if (verdict.Refusal == SafetyRefusal.Missing)
            {
                continue;
            }

            if (item is null)
            {
                return verdict.Reason;
            }

            WalkResult size = DirectoryWalker.Measure(item.Path);
            items.Add((item, size));
            if (size.CloudPlaceholderCount > 0 && !_options.AllowCloudPlaceholders)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"{item.Path} holds {size.CloudPlaceholderCount:N0} cloud-only files; removing it would remove them from the cloud too. Confirm that first.");
            }

            LockReport locks = FileLocks.Check(item.Path, _options.LockCheckFileLimit);
            if (locks.BlockerMessage is { } message)
            {
                return message;
            }
        }

        return null;
    }

    private static MovedItem? Delete(VerifiedItem item, WalkResult size, List<string> errors)
    {
        DeleteReport report = item.DeleteForGood();
        if (!report.Succeeded)
        {
            errors.Add(string.Join(Environment.NewLine, report.Failures.Select(f =>
                string.Create(CultureInfo.InvariantCulture, $"{f.Path}: could not delete (Win32 error {f.Win32Error})."))));
            return null;
        }

        return new MovedItem { Original = item.Path, Destination = ItemDestination.Deleted, Bytes = size.Bytes };
    }

    private MovedItem? Trash(VerifiedItem item, WalkResult size, List<string> errors)
    {
        RecycleAssessment assessment = recycleBin.Assess(item.Path);
        if (assessment.Verdict == RecycleVerdict.Recyclable)
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            item.Dispose(); // The shell works by path; nothing of ours may hold the item.
            RecycleOutcome recycled = RecycleBin.Recycle(assessment);
            if (!recycled.Recycled)
            {
                return QuarantineAfterRefusal(item.Path, size, recycled.Error, errors);
            }

            RecycledItem? landed = RecycleBinIndex.FindNewest(item.Path, started.AddSeconds(-5));
            return new MovedItem
            {
                Original = item.Path,
                Destination = ItemDestination.RecycleBin,
                Location = landed?.DataPath,
                RecycleInfo = landed?.InfoPath,
                Bytes = size.Bytes,
            };
        }

        // Would be permanent, unknown, or too long a path for the shell: DevSweep's own quarantine.
        return Quarantine(item, size, assessment.Reason, errors);
    }

    /// <summary>
    /// The shell refused to recycle (it does for some read-only files, such as the objects git
    /// writes): when the item is still there whole (same file count as measured before), it goes
    /// to the quarantine instead, so it stays restorable. A partly recycled item is reported, not moved.
    /// </summary>
    private MovedItem? QuarantineAfterRefusal(string path, WalkResult before, string? error, List<string> errors)
    {
        string reason = $"the Recycle Bin refused it ({error})";
        SafetyVerdict verdict = guard.TryOpen(path, out VerifiedItem? again);
        if (again is null)
        {
            errors.Add($"{path}: {error} It could not be opened again for the quarantine: {verdict.Reason}");
            return null;
        }

        using (again)
        {
            WalkResult after = DirectoryWalker.Measure(again.Path);
            if (after.Files != before.Files)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{path}: {error} Part of it may be in the Recycle Bin ({before.Files - after.Files:N0} of {before.Files:N0} files); the rest was left where it is."));
                return null;
            }

            return Quarantine(again, after, reason, errors);
        }
    }

    private MovedItem? Quarantine(VerifiedItem item, WalkResult size, string? reason, List<string> errors)
    {
        QuarantineResult result = quarantine.Move(item, size, reason);
        if (!result.Moved || result.Entry is null)
        {
            errors.Add($"{item.Path}: the Recycle Bin would not keep it ({reason}) and it could not be quarantined ({result.Message}). Nothing was changed.");
            return null;
        }

        return new MovedItem
        {
            Original = item.Path,
            Destination = ItemDestination.Quarantine,
            Location = result.Entry.StoredPath,
            QuarantineId = result.Entry.Id,
            Bytes = size.Bytes,
            Note = reason,
        };
    }

    private async Task<ActionOutcome> RunCommandAsync(PlannedAction plan, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> argv = plan.Action.Command ?? [];
        if (argv.Count == 0)
        {
            return Refuse(plan, "No command to run.");
        }

        string volume = Volumes.VolumeRoot(plan.Finding.Paths.Count > 0 ? plan.Finding.Paths[0] : KnownFolders.Profile);
        long before = Volumes.FreeBytes(volume);
        CommandResult result = await CommandRunner.RunAsync(argv, _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        var reconciliation = new FreedSpace
        {
            Volume = volume,
            ShownBytes = plan.Finding.Size,
            ExpectedFreedBytes = plan.Finding.Size,
            FreeBefore = before,
            FreeAfter = Volumes.FreeBytes(volume),
        };
        string commandLine = string.Join(' ', argv);
        string message = result switch
        {
            { StartError: { } start } => start,
            { TimedOut: true } => string.Create(CultureInfo.InvariantCulture, $"{commandLine} did not finish within {_options.CommandTimeout.TotalSeconds:0} seconds and was stopped."),
            { ExitCode: not 0 } => result.StandardError.Trim() is { Length: > 0 } err ? err : string.Create(CultureInfo.InvariantCulture, $"{commandLine} failed (exit {result.ExitCode})."),
            _ => $"Ran {commandLine}",
        };
        return new ActionOutcome
        {
            Finding = plan.Finding,
            Action = plan.Action,
            Succeeded = result.Succeeded,
            Message = message,
            Freed = result.Succeeded ? Math.Max(0, reconciliation.FreedBytes) : 0,
            Command = result,
            Reconciliation = reconciliation,
        };
    }

    private static List<string> RunningBlockers(IReadOnlyList<string> names)
    {
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

    private static string Describe(List<MovedItem> moved)
    {
        int bin = moved.Count(m => m.Destination == ItemDestination.RecycleBin);
        int quarantined = moved.Count(m => m.Destination == ItemDestination.Quarantine);
        int deleted = moved.Count(m => m.Destination == ItemDestination.Deleted);
        var parts = new List<string>();
        if (bin > 0)
        {
            parts.Add("Moved to the Recycle Bin");
        }

        if (quarantined > 0)
        {
            parts.Add("Moved to DevSweep's quarantine (the Recycle Bin would not have kept it); restorable from History");
        }

        if (deleted > 0)
        {
            parts.Add("Deleted");
        }

        return parts.Count == 0 ? "Nothing was there any more." : string.Join("; ", parts) + ".";
    }

    private static ActionOutcome Refuse(PlannedAction plan, string message)
        => new() { Finding = plan.Finding, Action = plan.Action, Succeeded = false, Message = message };

    private static HistoryEntry ToHistory(PlannedAction plan, ActionOutcome outcome) => new()
    {
        Title = plan.Finding.Title,
        FindingId = plan.Finding.Id,
        RuleId = plan.Finding.RuleId,
        ActionKind = plan.Action.Kind,
        ActionLabel = plan.Action.DisplayLabel,
        Size = outcome.Freed,
        Items = outcome.Items,
        Command = plan.Action.Kind == CleanActionKind.Command ? plan.Action.Command : null,
        CommandOutput = outcome.Command is { } c ? Trim(c.StandardOutput + c.StandardError) : null,
        Note = plan.Finding.UndoNote,
        Reconciliation = outcome.Reconciliation,
    };

    private static string Trim(string text) => text.Length <= 4000 ? text.Trim() : text[..4000].Trim() + " ...";
}
