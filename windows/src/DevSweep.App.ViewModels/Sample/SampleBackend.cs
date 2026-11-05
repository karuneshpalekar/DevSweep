using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Services;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Review;
using DevSweep.Core.Safety;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels.Sample;

/// <summary>
/// Serves <see cref="SampleData"/> (screenshot mode and tests). Review is prepared by the
/// real <see cref="ReviewPlanner"/> against a made-up machine; nothing on disk is touched.
/// </summary>
public sealed class SampleBackend : IDevSweepBackend, IHealthSource, IProjectsSource
{
    private readonly HashSet<Guid> _restored = [];
    private readonly List<QuarantineEntry> _quarantine = [.. SampleData.Quarantine];

    public IReadOnlyList<ModuleInfo> Modules { get; } =
    [
        new("locations", "Locations", ModuleState.On),
        new("inventory", "Installed apps", ModuleState.On),
        new("cleanup", "Clean up", ModuleState.On),
        new("packs", "Windows rule packs", ModuleState.On),
        new("projects", "Projects", ModuleState.On),
        new("health", "Health", ModuleState.On),
    ];

    public IHealthSource? Health => this;

    public IProjectsSource? Projects => this;

    public IUpdateChecker Updates { get; } = new NoUpdateChecker();

    public string? ProfilePath => SampleData.Profile;

    public string DataFolder => @"C:\Users\alex\AppData\Local\DevSweep";

    public DateTimeOffset Now => SampleData.Now;

    public void SetModuleEnabled(string id, bool enabled)
    {
    }

    public Task<ScanResult> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken)
        => Task.FromResult(new ScanResult(SampleData.Findings, SampleData.Report));

    /// <summary>The overview Gradle size becomes exact (+12 MB of alternate data streams).</summary>
    public Task<Finding> RemeasureAsync(Finding finding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Task.FromResult(finding.Measurement == SizeMeasurement.Overview
            ? finding with { Measurement = SizeMeasurement.Exact, Size = finding.Size + (12 * SampleData.MB), FreeableBytes = finding.FreeableBytes + (12 * SampleData.MB) }
            : finding);
    }

    public Task<ReviewPlan> PrepareAsync(IReadOnlyList<PlannedAction> plan, CancellationToken cancellationToken)
        => Task.FromResult(new ReviewPlanner(new SampleProbe()).Prepare(plan, cancellationToken));

    public Task<IReadOnlyList<ActionOutcome>> CleanAsync(IReadOnlyList<PlannedAction> plan, IProgress<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outcomes = new List<ActionOutcome>();
        long free = SampleData.Volumes[0].FreeBytes;
        foreach (PlannedAction item in plan)
        {
            progress?.Report(item.Finding.Title);
            bool delete = item.Action.Kind == CleanActionKind.Delete;
            bool big = item.Finding.Size > 5 * SampleData.GB;
            ItemDestination where = delete ? ItemDestination.Deleted : big ? ItemDestination.Quarantine : ItemDestination.RecycleBin;
            long freed = delete ? item.Finding.FreeableBytes - (2 * SampleData.MB) : 0;
            outcomes.Add(new ActionOutcome
            {
                Finding = item.Finding,
                Action = item.Action,
                Succeeded = true,
                Message = where switch
                {
                    ItemDestination.Deleted => "Deleted.",
                    ItemDestination.Quarantine => "Moved to DevSweep's quarantine (the Recycle Bin would not have kept it); restorable from History.",
                    _ => "Moved to the Recycle Bin.",
                },
                Freed = item.Finding.Size,
                Items = [.. item.Finding.Paths.Select(p => new MovedItem { Original = p, Destination = where, Bytes = item.Finding.Size })],
                Reconciliation = new FreedSpace
                {
                    Volume = @"C:\",
                    ShownBytes = item.Finding.Size,
                    ExpectedFreedBytes = delete ? item.Finding.FreeableBytes : 0,
                    FreeBefore = free,
                    FreeAfter = free + freed,
                },
            });
            free += freed;
        }

        return Task.FromResult<IReadOnlyList<ActionOutcome>>(outcomes);
    }

    public IReadOnlyList<HistoryEntry> History()
        => [.. SampleData.History.Select(e => _restored.Contains(e.Id) ? e with { RestoredAt = SampleData.Now } : e)];

    public bool CanRestore(HistoryEntry entry)
        => entry is not null && entry.RestoredAt is null && SampleData.Restorable.Contains(entry.Id) && !_restored.Contains(entry.Id);

    public Task<HistoryRestoreResult> RestoreAsync(Guid id)
    {
        _restored.Add(id);
        return Task.FromResult(new HistoryRestoreResult(1, ["Restored."]));
    }

    public IReadOnlyList<QuarantineEntry> Quarantine() => _quarantine;

    public Task<long> EmptyQuarantineAsync()
    {
        long bytes = _quarantine.Sum(q => q.Bytes);
        _quarantine.Clear();
        return Task.FromResult(bytes);
    }

    public IReadOnlyList<VolumeSpace> Volumes() => SampleData.Volumes;

    public void OpenInTerminal(string title, IReadOnlyList<string> commands, bool needsAdmin)
    {
    }

    public ScanChanges? CompareAndRecord(IReadOnlyList<Finding> findings, long? diskFree) => SampleData.Changes;

    HealthFeatures IHealthSource.Features { get; } = new(true, true, true);

    void IHealthSource.OpenFix(RuntimeStep fix)
    {
    }

    Task<HealthReport> IHealthSource.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(SampleData.Health);

    Task<ProjectsReport> IProjectsSource.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(SampleData.Projects);

    /// <summary>A made-up machine: everything allowed, Chrome running, the Android image too big for the Recycle Bin.</summary>
    private sealed class SampleProbe : IReviewProbe
    {
        public SafetyVerdict Check(string path) => SafetyVerdict.Allow(path);

        public RecycleAssessment AssessRecycle(string path)
            => path.Contains("system-images", StringComparison.OrdinalIgnoreCase)
                ? new RecycleAssessment(path, RecycleVerdict.WouldBePermanent, "It's bigger than the Recycle Bin's limit on C: (5.9 GB), so Windows would delete it for good.", 6 * SampleData.GB, 5_900 * SampleData.MB, DriveKind.Fixed)
                : new RecycleAssessment(path, RecycleVerdict.Recyclable, "Fits in the Recycle Bin.", 1, 9 * SampleData.GB, DriveKind.Fixed);

        public string? QuarantineRoot(string path, out string reason)
        {
            reason = string.Empty;
            return @"C:\Users\alex\AppData\Local\DevSweep\Quarantine";
        }

        public LockReport Locks(string path) => new([], 10, false);

        public IReadOnlyList<string> Running(IReadOnlyList<string> names) => [.. names.Where(n => n == "chrome")];
    }
}
