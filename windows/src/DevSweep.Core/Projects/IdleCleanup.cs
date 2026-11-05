using DevSweep.Core.Cleaning;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Projects;

/// <summary>One project in the Cleanup list.</summary>
/// <param name="Project">The project (on disk).</param>
/// <param name="FreeableBytes">What removing it frees: exact once <see cref="IdleCleanup.MeasureExactAsync"/> ran (hard links to outside excluded).</param>
/// <param name="Exact">True when <paramref name="FreeableBytes"/> is the exact re-measure (streams counted).</param>
/// <param name="Reasons">Why it can't be removed; empty when it is safe.</param>
public sealed record IdleProject(Project Project, long FreeableBytes, bool Exact, IReadOnlyList<string> Reasons)
{
    public bool IsSafe => Project.Safety?.IsSafeToRemove == true;

    public string Path => Project.LocalPath ?? string.Empty;
}

/// <summary>The Cleanup tab: what it lists, and what removing the safe ones frees.</summary>
public sealed record IdleCleanupResult(int Days, IReadOnlyList<IdleProject> Projects)
{
    public IReadOnlyList<IdleProject> Safe => Projects.Where(p => p.IsSafe).ToList();

    public long TotalBytes => Projects.Sum(p => p.FreeableBytes);

    public long SafeBytes => Safe.Sum(p => p.FreeableBytes);
}

/// <summary>
/// Projects on this PC not opened or changed for a while (1 week to 3 months, 3 weeks by
/// default), biggest first (the Mac Cleanup tab). Each is safe to remove or says what holds
/// it back. Sizes are re-measured exactly with the M2 <see cref="FindingRemeasurer"/> (the walker
/// with streams on, hard links shared with outside not counted as freeable).
/// </summary>
public static class IdleCleanup
{
    public const int DefaultDays = 21;
    public const int MinDays = 7;
    public const int MaxDays = 90;

    /// <summary>The Mac choices: 1, 2, 3 weeks, 1, 2, 3 months.</summary>
    public static IReadOnlyList<int> Choices { get; } = [7, 14, 21, 30, 60, 90];

    public static int Clamp(int days) => Math.Clamp(days, MinDays, MaxDays);

    public static IdleCleanupResult List(IEnumerable<Project> projects, int days, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(projects);
        int d = Clamp(days);
        DateTimeOffset cutoff = now.AddDays(-d);
        List<IdleProject> idle = projects
            .Where(p => p.OnDisk && (p.LastUsed ?? DateTimeOffset.MinValue) < cutoff)
            .OrderByDescending(p => p.LocalSize)
            .Select(p => new IdleProject(p, p.LocalSize, false, Reasons(p)))
            .ToList();
        return new IdleCleanupResult(d, idle);
    }

    /// <summary>The finding the cleaner removes a project with (Recycle Bin or quarantine, recorded in History).</summary>
    public static Finding ToFinding(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string path = project.LocalPath ?? throw new ArgumentException("The project is not on this PC.", nameof(project));
        return new Finding
        {
            Id = "project:" + project.Id,
            RuleId = "projects",
            Title = project.NameWithOwner,
            Subtitle = path,
            Category = Category.Projects,
            Risk = Risk.HoldsData,
            Paths = [path],
            Size = project.LocalSize,
            PathSizes = [project.LocalSize],
            FreeableBytes = project.LocalSize,
            Measurement = SizeMeasurement.Overview,
            Explanation = new Explanation(
                "A project folder.",
                "Everything in it is on a remote, and it hasn't been used for a while.",
                "Download it again from Projects; it comes back into the same folder.")
            {
                Undo = "Download it again from Projects, or restore it from History.",
            },
            Checks = project.Safety?.Checks ?? [],
            Actions = [new CleanAction(CleanActionKind.Trash)],
            UndoNote = "Download it again from Projects, or restore it from History.",
        };
    }

    /// <summary>Re-measures each listed project exactly (in parallel, each under <paramref name="timeLimit"/>).</summary>
    public static async Task<IdleCleanupResult> MeasureExactAsync(IdleCleanupResult result, FindingRemeasurer? remeasurer = null, TimeSpan? timeLimit = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        FindingRemeasurer measure = remeasurer ?? new FindingRemeasurer();
        IdleProject[] exact = await Task.WhenAll(result.Projects.Select(async p =>
        {
            Finding f = await measure.RemeasureAsync(ToFinding(p.Project), timeLimit ?? TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            return p with { FreeableBytes = f.FreeableBytes, Exact = f.Measurement == SizeMeasurement.Exact, Project = p.Project with { LocalSize = f.Size } };
        })).ConfigureAwait(false);
        return result with { Projects = exact.OrderByDescending(p => p.FreeableBytes).ToList() };
    }

    private static IReadOnlyList<string> Reasons(Project p)
        => p.Safety switch
        {
            null => ["Not checked"],
            { State: SafetyState.Safe } => [],
            { State: SafetyState.NotChecked } s => [s.Headline],
            { State: SafetyState.Unknown } s => [.. s.Blockers, .. s.Problems.Select(x => "Couldn't check: " + x)],
            { } s => s.Blockers,
        };
}
