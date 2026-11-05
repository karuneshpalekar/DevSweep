using System.Globalization;
using DevSweep.Core.Cleaning;

namespace DevSweep.Core.Projects.Git;

/// <summary>Whether a project folder may leave this PC.</summary>
public enum SafetyState
{
    /// <summary>Every check ran and nothing would be lost: everything is on a remote.</summary>
    Safe,

    /// <summary>Something would be lost (each reason is in <see cref="RemovalSafety.Blockers"/>).</summary>
    NotSafe,

    /// <summary>A check could not give an answer (git failed, timed out, LFS not verifiable). Never treated as safe.</summary>
    Unknown,

    /// <summary>Not looked at, on purpose (OneDrive files that are only in the cloud, git missing). Never treated as safe.</summary>
    NotChecked,
}

/// <summary>Commits on one local ref that no remote-tracking ref has.</summary>
public sealed record RefUnpushed(string Ref, int Commits, string? Upstream);

/// <summary>A submodule with work that is not on its remote.</summary>
public sealed record SubmoduleIssue(string Path, string Reason);

/// <summary>
/// What would be lost if a project folder left this PC, and how sure DevSweep is.
/// Built by <see cref="SafetyChecker"/>; the Mac <c>GitSafety</c> plus the Windows checks.
/// </summary>
public sealed record RemovalSafety
{
    public SafetyState State { get; init; }

    public string Path { get; init; } = string.Empty;

    /// <summary>True once the repository itself was read (false when git could not even open it).</summary>
    public bool Inspected { get; init; }

    public bool HasRemote { get; init; }

    public IReadOnlyList<string> Remotes { get; init; } = [];

    /// <summary>Every remote is a folder on this PC or a network share (not GitHub or another server).</summary>
    public bool RemotesAreLocalFolders { get; init; }

    /// <summary>Commits reachable from a local branch, tag-less HEAD included, that no remote-tracking branch has.</summary>
    public int UnpushedCommits { get; init; }

    public IReadOnlyList<RefUnpushed> UnpushedByRef { get; init; } = [];

    /// <summary>Tracked files with changes (staged or not).</summary>
    public int ChangedFiles { get; init; }

    public IReadOnlyList<string> ChangedSample { get; init; } = [];

    /// <summary>Of <see cref="ChangedFiles"/>, those whose only difference is line endings (CR at end of line).</summary>
    public int LineEndingOnlyFiles { get; init; }

    public int ConflictedFiles { get; init; }

    /// <summary>Files git does not track and does not ignore.</summary>
    public int UntrackedFiles { get; init; }

    public IReadOnlyList<string> UntrackedSample { get; init; } = [];

    /// <summary>Ignored entries that are not rebuildable artifact folders (<c>.env</c>, local settings...): not blocking, but listed.</summary>
    public int IgnoredEntries { get; init; }

    public IReadOnlyList<string> IgnoredSample { get; init; } = [];

    public int Stashes { get; init; }

    /// <summary>Other worktrees of the same repository (they share its commits and stashes).</summary>
    public IReadOnlyList<string> OtherWorktrees { get; init; } = [];

    /// <summary>This folder is a linked worktree of another repository.</summary>
    public string? LinkedWorktreeOf { get; init; }

    public IReadOnlyList<SubmoduleIssue> Submodules { get; init; } = [];

    public bool UsesLfs { get; init; }

    /// <summary>A rebase, merge, cherry-pick or revert that was started and not finished.</summary>
    public string? OperationInProgress { get; init; }

    /// <summary>Files whose name differs from the index only by letter case (git on Windows does not see this as a change).</summary>
    public IReadOnlyList<string> CaseOnlyRenames { get; init; } = [];

    /// <summary>Index entries that differ only by letter case: only one of each can exist on Windows.</summary>
    public IReadOnlyList<string> CaseCollisions { get; init; } = [];

    /// <summary>Tracked files whose full path is 260 characters or longer.</summary>
    public int LongPathFiles { get; init; }

    public bool LongPathsEnabled { get; init; }

    /// <summary>Restart Manager's "Close X first" message, when a program holds files in the folder open.</summary>
    public string? LockMessage { get; init; }

    /// <summary>When the remote-tracking refs were last updated (FETCH_HEAD), the reference "pushed" is measured against.</summary>
    public DateTimeOffset? LastFetchUtc { get; init; }

    /// <summary>True when this check ran <c>git fetch</c> first (the user asked).</summary>
    public bool Fetched { get; init; }

    /// <summary>Why a check gave no answer; any entry makes the state <see cref="SafetyState.Unknown"/> at best.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Things worth knowing that do not block removal (a failed fetch, a check skipped on a huge repository).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Why nothing was checked (<see cref="SafetyState.NotChecked"/>).</summary>
    public string? NotCheckedReason { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    public bool IsSafeToRemove => State == SafetyState.Safe;

    /// <summary>What would be lost with the folder, one line each (the Mac <c>lossSummary</c> plus the Windows checks). Empty when safe.</summary>
    public IReadOnlyList<string> Blockers
    {
        get
        {
            var lines = new List<string>();
            if (Inspected && !HasRemote)
            {
                lines.Add("There's no copy on GitHub or any other remote, so this folder is the only copy");
            }

            if (UnpushedCommits > 0)
            {
                string where = UnpushedByRef.Count > 0 ? " (" + string.Join(", ", UnpushedByRef.Select(r => r.Ref)) + ")" : string.Empty;
                lines.Add((UnpushedCommits == 1 ? "1 commit that isn't on GitHub" : $"{UnpushedCommits} commits that aren't on GitHub") + where);
            }

            if (ChangedFiles > 0)
            {
                string eol = LineEndingOnlyFiles > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" ({LineEndingOnlyFiles} differ only in line endings)")
                    : string.Empty;
                lines.Add(Project.Plural(ChangedFiles, "file") + " with changes that aren't committed" + eol + Sample(ChangedSample, ChangedFiles));
            }

            if (ConflictedFiles > 0)
            {
                lines.Add(Project.Plural(ConflictedFiles, "file") + " with unresolved merge conflicts");
            }

            if (UntrackedFiles > 0)
            {
                lines.Add(Project.Plural(UntrackedFiles, "file") + " git doesn't track" + Sample(UntrackedSample, UntrackedFiles));
            }

            if (Stashes > 0)
            {
                lines.Add(Stashes == 1 ? "1 stash" : $"{Stashes} stashes");
            }

            if (OtherWorktrees.Count > 0)
            {
                lines.Add(Project.Plural(OtherWorktrees.Count, "other worktree") + (OtherWorktrees.Count == 1 ? " uses" : " use") + " this repository: " + string.Join(", ", OtherWorktrees));
            }

            if (LinkedWorktreeOf is { } main)
            {
                lines.Add($"This is a worktree of {main}; remove it with git worktree remove");
            }

            lines.AddRange(Submodules.Select(s => $"Submodule {s.Path}: {s.Reason}"));
            if (OperationInProgress is { } op)
            {
                lines.Add($"A {op} was started and not finished");
            }

            if (CaseOnlyRenames.Count > 0)
            {
                lines.Add(Project.Plural(CaseOnlyRenames.Count, "file") + " renamed only by letter case, which git on Windows doesn't record: "
                    + string.Join(", ", CaseOnlyRenames.Take(3)) + (CaseOnlyRenames.Count > 3 ? " and more" : string.Empty));
            }

            if (CaseCollisions.Count > 0)
            {
                lines.Add(Project.Plural(CaseCollisions.Count, "file") + " in the repository differ only by letter case, so only one of each exists here: "
                    + string.Join(", ", CaseCollisions.Take(3)));
            }

            if (LockMessage is { } locked)
            {
                lines.Add(locked);
            }

            return lines;
        }
    }

    /// <summary>One line for the list: "Safe to remove", the first blocker, or why it is unknown.</summary>
    public string Headline => State switch
    {
        SafetyState.Safe => "Safe to remove",
        SafetyState.NotChecked => NotCheckedReason ?? "Not checked",
        SafetyState.Unknown => "Couldn't check: " + (Problems.Count > 0 ? Problems[0] : "unknown"),
        _ => Blockers.Count > 0 ? Blockers[0] : "Not safe to remove",
    };

    /// <summary>"What was checked" lines for the inspector: a passed line per clean check, a warning per problem.</summary>
    public IReadOnlyList<Check> Checks
    {
        get
        {
            var checks = new List<Check>();
            if (NotCheckedReason is { } why)
            {
                checks.Add(Check.Warning(why));
                return checks;
            }

            string against = Fetched ? "after fetching from the remote just now"
                : LastFetchUtc is { } t ? string.Create(CultureInfo.InvariantCulture, $"as of the last fetch ({t:yyyy-MM-dd HH:mm} UTC)")
                : "as of the last fetch or push";
            checks.Add(HasRemote ? Check.Passed($"Has a remote ({string.Join(", ", Remotes)})") : Check.Warning("No remote: this folder is the only copy"));
            if (RemotesAreLocalFolders)
            {
                checks.Add(Check.Warning("The remote is a folder on this PC or a network share, not GitHub"));
            }

            checks.Add(UnpushedCommits == 0 ? Check.Passed($"Every commit on every branch is on a remote ({against})")
                : Check.Warning($"{Project.Plural(UnpushedCommits, "commit")} not on any remote ({against})"));
            checks.Add(ChangedFiles + ConflictedFiles == 0 ? Check.Passed("No uncommitted changes (git status, line-ending settings respected)")
                : Check.Warning(Project.Plural(ChangedFiles + ConflictedFiles, "file") + " with uncommitted changes"));
            checks.Add(UntrackedFiles == 0 ? Check.Passed("No untracked files (.gitignore respected)") : Check.Warning(Project.Plural(UntrackedFiles, "untracked file")));
            checks.Add(Stashes == 0 ? Check.Passed("No stashes") : Check.Warning(Project.Plural(Stashes, "stash")));
            checks.Add(OtherWorktrees.Count == 0 && LinkedWorktreeOf is null ? Check.Passed("No other worktrees") : Check.Warning("Shares its repository with another worktree"));
            checks.Add(Submodules.Count == 0 ? Check.Passed("Submodules (if any) have nothing unpushed") : Check.Warning(Project.Plural(Submodules.Count, "submodule") + " with unpushed work"));
            checks.Add(CaseOnlyRenames.Count + CaseCollisions.Count == 0 ? Check.Passed("No renames by letter case only") : Check.Warning("Letter-case differences git can't see"));
            if (UsesLfs)
            {
                checks.Add(Check.Warning("Uses Git LFS: DevSweep can't confirm the LFS file contents are on the server without contacting it"));
            }

            if (LongPathFiles > 0)
            {
                checks.Add(LongPathsEnabled
                    ? Check.Passed(Project.Plural(LongPathFiles, "file") + " with paths of 260+ characters; core.longpaths is on")
                    : Check.Warning(Project.Plural(LongPathFiles, "file") + " with paths of 260+ characters and core.longpaths off; DevSweep downloads with core.longpaths=true"));
            }

            if (IgnoredEntries > 0)
            {
                checks.Add(Check.Warning(Project.Plural(IgnoredEntries, "ignored item") + " that GitHub doesn't have and would go with the folder: "
                    + string.Join(", ", IgnoredSample) + (IgnoredEntries > IgnoredSample.Count ? " and more" : string.Empty)));
            }

            checks.Add(LockMessage is null ? Check.Passed("No program has files in it open") : Check.Warning(LockMessage));
            checks.AddRange(Notes.Select(Check.Warning));
            checks.AddRange(Problems.Select(p => Check.Warning("Couldn't check: " + p)));
            return checks;
        }
    }

    private static string Sample(IReadOnlyList<string> sample, int total)
        => sample.Count == 0 ? string.Empty : ": " + string.Join(", ", sample) + (total > sample.Count ? " and more" : string.Empty);
}
