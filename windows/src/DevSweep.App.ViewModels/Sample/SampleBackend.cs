using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Updates;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Editors;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
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
        new("projects.cleanup", "Projects: Cleanup", ModuleState.On),
        new("health", "Health", ModuleState.On),
    ];

    public IHealthSource? Health => this;

    public IProjectsSource? Projects => this;

    public IUpdateChecker Updates { get; } = new SampleUpdateChecker();

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

    Task<DevSweep.Core.Health.Packages.PackagesResult?> IHealthSource.PackagesAsync(VersionsResult? versions, CancellationToken cancellationToken)
        => Task.FromResult<DevSweep.Core.Health.Packages.PackagesResult?>(SampleData.Packages);

    Task<DevSweep.Core.Health.Ports.PortsResult?> IHealthSource.PortsAsync(CancellationToken cancellationToken)
        => Task.FromResult(SampleData.Health.Ports);

    DevSweep.Core.Health.Ports.StopResult? IHealthSource.StopRefusal(DevSweep.Core.Health.Ports.ListeningPort port)
        => port.OwnedByYou == false
            ? new DevSweep.Core.Health.Ports.StopResult(DevSweep.Core.Health.Ports.StopOutcome.NotYours, $"{port.ProcessName} is part of Windows or runs as another user, so DevSweep leaves it alone.")
            : null;

    string? IHealthSource.TrashBlockedReason(DevSweep.Core.Health.Security.SecurityFinding finding)
        => finding.Path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)
            ? "It's online-only in OneDrive. Moving it would first download it, so DevSweep leaves it alone. Make it available on this PC, or delete it from the cloud folder yourself."
            : null;

    ProjectsFeatures IProjectsSource.Features { get; } = ProjectsFeatures.All;

    Task<ProjectsReport> IProjectsSource.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new ProjectsReport(SampleData.Projects, SampleData.Activity));

    Task<IdleCleanupResult> IProjectsSource.IdleAsync(ProjectsSnapshot snapshot, int days, CancellationToken cancellationToken)
        => Task.FromResult(IdleCleanup.List(snapshot.Projects, days, SampleData.Now));

    void IProjectsSource.Push(Project project, GitHubAccounts? accounts)
    {
    }

    void IProjectsSource.SignIn(string? activeBefore)
    {
    }

    void IProjectsSource.SignIn(string? login, GitIdentity? identity, string? activeBefore)
    {
    }

    void IProjectsSource.InstallGitHubCli()
    {
    }

    /// <summary>Asks the first-run question again (the screenshot tour shows it in each theme).</summary>
    public void ResetProjectsConsent() => _preferences = _preferences with { DeepSearchConsent = null };

    private ProjectsPreferences _preferences = new(null, false, null, @"C:\Users\alex\Code", [@"D:\clients"]);

    /// <summary>Sample "what the person chose" (the first-run question is still open until it is answered).</summary>
    ProjectsPreferences IProjectsSource.Preferences => _preferences;

    IReadOnlyList<EditorInfo> IProjectsSource.Editors { get; } =
    [
        new("vscode", "Visual Studio Code", @"C:\Users\alex\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
        new("rider", "JetBrains Rider", @"C:\Program Files\JetBrains\JetBrains Rider 2025.2\bin\rider64.exe"),
    ];

    void IProjectsSource.SetDefaultEditor(string? id) => _preferences = _preferences with { DefaultEditor = id };

    void IProjectsSource.SetDeepSearchConsent(bool allowed) => _preferences = _preferences with { DeepSearchConsent = allowed };

    void IProjectsSource.SetSearchRemovable(bool include) => _preferences = _preferences with { SearchRemovable = include };

    void IProjectsSource.SetWorkspaceRoot(string folder) => _preferences = _preferences with { WorkspaceRoot = folder };

    void IProjectsSource.AddSearchFolder(string folder) => _preferences = _preferences with { Folders = [.. _preferences.Folders, folder] };

    void IProjectsSource.RemoveSearchFolder(string folder) => _preferences = _preferences with { Folders = [.. _preferences.Folders.Where(f => f != folder)] };

    async Task<DeepSearchOutcome> IProjectsSource.DeepSearchAsync(IProgress<DeepSearchProgress>? progress, CancellationToken cancellationToken)
    {
        for (int i = 1; i <= 3; i++)
        {
            progress?.Report(new DeepSearchProgress(i * 1_000, i, @"C:\Users\alex\Documents"));
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        DeepSearchSummary summary = SampleData.Projects.DeepSearch!;
        return new DeepSearchOutcome(new DeepSearchResult { Roots = summary.Roots, FoldersVisited = summary.Folders, Elapsed = TimeSpan.FromSeconds(summary.Seconds) }, null, summary);
    }

    Task<RemovalSafety> IProjectsSource.CheckAsync(Project project, bool fetch, CancellationToken cancellationToken)
        => Task.FromResult((project.Safety ?? new RemovalSafety()) with { Fetched = fetch, CheckedAt = SampleData.Now });

    string IProjectsSource.OpenInEditor(Project project) => "Visual Studio Code";

    Task<RemoveResult> IProjectsSource.RemoveAsync(Project project, CancellationToken cancellationToken)
        => Task.FromResult(new RemoveResult(project.Safety?.IsSafeToRemove == true, project.Safety?.IsSafeToRemove == true ? "Removed." : project.Name + " was not removed: " + project.Safety?.Headline + ".", project.Safety));

    DownloadPlan IProjectsSource.PlanDownload(Project project, CloneStrategy strategy, string? destination, string? account)
        => Downloader.Plan(new DownloadRequest
        {
            NameWithOwner = project.NameWithOwner,
            Destination = destination ?? Path.Join(_preferences.WorkspaceRoot, account ?? project.Owner, project.Name),
            Strategy = strategy,
            Account = account,
            RemoteKB = project.RemoteKB,
        });

    Task<DownloadResult> IProjectsSource.DownloadAsync(Project project, CloneStrategy strategy, string? destination, string? account, CancellationToken cancellationToken)
        => Task.FromResult(new DownloadResult(true, "Downloaded.", destination));

    Task<PublishPreview> IProjectsSource.PreviewPublishAsync(PublishRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new PublishPreview([Path.Join(request.Folder, "app.py"), Path.Join(request.Folder, "deploy_key.pem")], [new SecretFile(Path.Join(request.Folder, "deploy_key.pem"), "Private key without a passphrase")], null));

    Task<PublishResult> IProjectsSource.PublishAsync(PublishRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new PublishResult(true, "Published."));

    Task<(RemoteRepo? Repo, string? Error)> IProjectsSource.AddByUrlAsync(string text, CancellationToken cancellationToken)
        => Task.FromResult<(RemoteRepo?, string?)>((new RemoteRepo { Name = "x", NameWithOwner = "o/x", Account = "alex" }, null));

    void IProjectsSource.SaveIdentity(string account, GitIdentity identity)
    {
    }

    IReadOnlyList<ActivityEntry> IProjectsSource.Activity() => SampleData.Activity;

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

/// <summary>Answers "version 0.2.0 is available" without any network (screenshots).</summary>
public sealed class SampleUpdateChecker : IUpdateChecker
{
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        => Task.FromResult(new UpdateCheckResult(
            UpdateOutcome.Available,
            new UpdateInfo("0.2.0", "windows-v0.2.0", new Uri("https://github.com/karuneshpalekar/DevSweep/releases/tag/windows-v0.2.0"), "Tray icon, scheduled scans and alerts.", SampleData.Now),
            "Version 0.2.0 is available."));
}

/// <summary>Says toasts work and swallows them (screenshots).</summary>
public sealed class SampleToasts : IToastNotifier
{
    public ToastAvailability Availability => ToastAvailability.Available;

    public bool Show(ToastMessage message) => true;
}
