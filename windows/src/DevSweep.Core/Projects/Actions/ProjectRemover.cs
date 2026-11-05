using DevSweep.Core.Cleaning;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Projects.Actions;

/// <summary>What removing a project did.</summary>
/// <param name="Safety">The check made just before (why it was refused, or what was confirmed).</param>
/// <param name="Outcome">The cleaner's outcome (where it went, History ID, freed space), when it ran.</param>
public sealed record RemoveResult(bool Succeeded, string Message, RemovalSafety? Safety, ActionOutcome? Outcome = null);

/// <summary>
/// Takes a fully backed-up project off this PC. The safety check runs again right before
/// (with Restart Manager asked about open files); only <see cref="SafetyState.Safe"/> may go.
/// Removal is the M1 cleaner's Trash action: Recycle Bin when it will keep the folder, otherwise
/// DevSweep's quarantine, through the safety guard, recorded in History (restorable). The
/// repository is remembered with the folder it lived in, so Download puts it back there.
/// </summary>
public sealed class ProjectRemover(SafetyChecker checker, Cleaner cleaner, ProjectStore store)
{
    public async Task<RemoveResult> RemoveAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.LocalPath is not { } path)
        {
            return new RemoveResult(false, $"{project.NameWithOwner} isn't on this PC.", null);
        }

        RemovalSafety safety = await checker.CheckAsync(path, new SafetyCheckOptions { CheckLocks = true }, cancellationToken).ConfigureAwait(false);
        if (safety.State != SafetyState.Safe)
        {
            IEnumerable<string> why = safety.State == SafetyState.NotSafe ? safety.Blockers
                : safety.State == SafetyState.NotChecked ? [safety.Headline]
                : [.. safety.Blockers, .. safety.Problems.Select(p => "Couldn't check: " + p)];
            return new RemoveResult(false, $"{project.NameWithOwner} was not removed: " + string.Join("; ", why) + ".", safety);
        }

        Finding finding = IdleCleanup.ToFinding(project) with { Checks = safety.Checks };
        ActionOutcome outcome = await cleaner.PerformAsync(new PlannedAction(finding, new CleanAction(CleanActionKind.Trash)), cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            return new RemoveResult(false, outcome.Message, safety, outcome);
        }

        if (project.OnGitHub)
        {
            store.Remember(project.NameWithOwner, k => (k ?? new KnownRepo { NameWithOwner = project.NameWithOwner }) with
            {
                Account = project.Account ?? k?.Account,
                IsPrivate = project.IsPrivate ?? k?.IsPrivate,
                RemoteKB = project.RemoteKB > 0 ? project.RemoteKB : k?.RemoteKB,
                LastPath = path,
                RemovedAt = DateTimeOffset.UtcNow,
            });
        }

        string where = outcome.Items.Any(i => i.Destination == ItemDestination.Quarantine) ? "In DevSweep's quarantine" : "In the Recycle Bin";
        store.Log(ActivityKind.Remove, project.NameWithOwner, $"Freed {SizeText.Format(outcome.Freed)} from {path}. {where}; restore from History or download it again.");
        return new RemoveResult(true, outcome.Message, safety, outcome);
    }
}
