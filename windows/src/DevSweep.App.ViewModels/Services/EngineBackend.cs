using System.Text.Json;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health;
using DevSweep.Core.Modules;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Review;
using DevSweep.Core.Rules;
using DevSweep.Core.Safety;
using DevSweep.Core.Scanning;
using DevSweep.Core.Storage;

namespace DevSweep.App.ViewModels.Services;

/// <summary>The real backend: DevSweep.Core's engine, cleaner, History and quarantine, through their public APIs.</summary>
public sealed class EngineBackend : IDevSweepBackend
{
    /// <summary>Modules that are places in the app, with their titles, whether or not this build has them.</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> KnownPlaces =
        [("cleanup", "Clean up"), ("packs", "Windows rule packs"), ("inventory", "Installed apps"), ("health", "Health"), ("projects", "Projects")];

    /// <summary>Health's tab flags and their titles in Settings.</summary>
    public static readonly IReadOnlyList<(string Flag, string Title)> HealthTabFlags =
        [(HealthFeatures.RuntimesFlag, "Health: Tools and versions"), (HealthFeatures.SecurityFlag, "Health: Security"), (HealthFeatures.PortsFlag, "Health: Ports")];

    /// <summary>Projects' tab flags and their titles in Settings.</summary>
    public static readonly IReadOnlyList<(string Flag, string Title)> ProjectsTabFlags =
        [(Core.Projects.ProjectsFeatures.CleanupFlag, "Projects: Cleanup"), (Core.Projects.ProjectsFeatures.AccountsFlag, "Projects: Accounts (GitHub)"), (Core.Projects.ProjectsFeatures.ActivityFlag, "Projects: Activity")];

    private readonly DevSweepEngine _engine;
    private readonly AppSettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly QuarantineStore _quarantine;
    private readonly ScanSnapshotStore _snapshots;
    private readonly string _featuresFile;
    private RuleSet? _rules;

    public EngineBackend(DevSweepEngine engine, AppSettingsStore settings, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        DataFolder = dataFolder;
        _history = new HistoryStore();
        _quarantine = new QuarantineStore();
        _snapshots = new ScanSnapshotStore(Path.Join(dataFolder, "scans.json"));
        _featuresFile = Path.Join(dataFolder, "features.json");
        Modules = DescribeModules(engine);
        Health = CoreHealthSource.From(engine, () => _settings.Current.ProjectFolders.Count == 0 ? null : _settings.Current.ProjectFolders);
        Projects = CoreProjectsSource.From(engine, () => _settings.Current.ProjectFolders.Count == 0 ? null : _settings.Current.ProjectFolders);
    }

    /// <summary>The engine with the user's flags (<c>features.json</c> and <c>DEVSWEEP_FEATURES</c>), as the CLI builds it.</summary>
    public static DevSweepEngine CreateEngine(string dataFolder)
        => DevSweepEngine.CreateDefault(FeatureFlags.Load(Path.Join(dataFolder, "features.json"), Environment.GetEnvironmentVariable("DEVSWEEP_FEATURES")));

    /// <summary><c>%LOCALAPPDATA%\DevSweep</c>.</summary>
    public static string DefaultDataFolder => Path.Join(KnownFolders.LocalAppData, "DevSweep");

    public IReadOnlyList<ModuleInfo> Modules { get; private set; }

    public IHealthSource? Health { get; }

    public IProjectsSource? Projects { get; }

    public IUpdateChecker Updates { get; } = new NoUpdateChecker();

    public string? ProfilePath => KnownFolders.TryGetRoot("Profile");

    public string DataFolder { get; }

    public DateTimeOffset Now => DateTimeOffset.Now;

    public static IReadOnlyList<ModuleInfo> DescribeModules(DevSweepEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var list = new List<ModuleInfo>();
        foreach (IDevSweepModule module in engine.Modules)
        {
            list.Add(new ModuleInfo(module.Id, Title(module.Id, module.Title), ModuleState.On));
        }

        foreach (string flag in engine.Disabled.Where(d => d.StartsWith("module:", StringComparison.Ordinal)))
        {
            string id = flag["module:".Length..];
            list.Add(new ModuleInfo(id, Title(id, id), ModuleState.TurnedOff));
        }

        foreach ((string id, string title) in KnownPlaces.Where(p => list.TrueForAll(m => m.Id != p.Id)))
        {
            list.Add(new ModuleInfo(id, title, ModuleState.NotInThisBuild));
        }

        // Health's and Projects' tabs have flags of their own (module:health.ports...), on by default.
        foreach ((string parent, IReadOnlyList<(string Flag, string Title)> tabs) in (ReadOnlySpan<(string, IReadOnlyList<(string, string)>)>)[("health", HealthTabFlags), ("projects", ProjectsTabFlags)])
        {
            if (list.Find(m => m.Id == parent) is { State: not ModuleState.NotInThisBuild })
            {
                foreach ((string flag, string title) in tabs)
                {
                    list.Add(new ModuleInfo(flag["module:".Length..], title, engine.Flags.IsEnabled(flag) ? ModuleState.On : ModuleState.TurnedOff));
                }
            }
        }

        return list;

        static string Title(string id, string fallback) => KnownPlaces.FirstOrDefault(p => p.Id == id).Title ?? fallback;
    }

    public void SetModuleEnabled(string id, bool enabled)
    {
        FeatureSettings current = new();
        if (File.Exists(_featuresFile))
        {
            try
            {
                current = JsonSerializer.Deserialize(File.ReadAllBytes(_featuresFile), CoreJsonContext.Default.FeatureSettings) ?? new();
            }
            catch (JsonException)
            {
                current = new();
            }
        }

        string flag = $"module:{id}";
        var disabled = current.Disabled.Where(f => !string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)).ToList();
        var enabledFlags = current.Enabled.Where(f => !string.Equals(f, flag, StringComparison.OrdinalIgnoreCase)).ToList();
        (enabled ? enabledFlags : disabled).Add(flag);
        var updated = new FeatureSettings { Disabled = disabled, Enabled = enabledFlags };
        AtomicFile.Write(_featuresFile, JsonSerializer.SerializeToUtf8Bytes(updated, CoreJsonContext.Default.FeatureSettings));
        Modules = [.. Modules.Select(m => m.Id == id && m.State != ModuleState.NotInThisBuild ? m with { State = enabled ? ModuleState.On : ModuleState.TurnedOff } : m)];
    }

    public Task<ScanResult> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        AppSettings settings = _settings.Current;
        IReadOnlyList<string>? roots = settings.ProjectFolders.Count == 0 ? null : settings.ProjectFolders;
        SafetyGuard guard = Guard();
        var request = new ScanRequest
        {
            Options = new ScanOptions
            {
                Mode = settings.ExactScan ? SizeMode.Exact : SizeMode.Overview,
                CheckTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.CheckTimeoutSeconds, 5, 600)),
                Ignored = settings.Ignored.Select(i => i.Id).ToHashSet(StringComparer.Ordinal),
                Progress = progress,
            },
            ProjectRoots = roots,
            SafetyCheck = path => guard.CheckLexically(path) is { Allowed: false } verdict ? verdict.Reason : null,
        };
        return Task.Run(async () =>
        {
            _rules = _engine.LoadRules();
            return await _engine.ScanAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<Finding> RemeasureAsync(Finding finding, CancellationToken cancellationToken)
    {
        RuleSet? rules = _rules;
        var remeasurer = new FindingRemeasurer(rules: id => rules?.Rules.FirstOrDefault(r => r.Id == id));
        return remeasurer.RemeasureAsync(finding, TimeSpan.FromMinutes(2), cancellationToken);
    }

    public Task<ReviewPlan> PrepareAsync(IReadOnlyList<PlannedAction> plan, CancellationToken cancellationToken)
    {
        var planner = new ReviewPlanner(new SystemReviewProbe(Guard(), new RecycleBin(), _quarantine));
        return Task.Run(() => planner.Prepare(plan, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<ActionOutcome>> CleanAsync(IReadOnlyList<PlannedAction> plan, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var cleaner = new Cleaner(Guard(), _history, _quarantine, new RecycleBin());
        return Task.Run(() => cleaner.RunAsync(plan, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<HistoryEntry> History()
    {
        _history.Load();
        return _history.Entries;
    }

    public bool CanRestore(HistoryEntry entry) => entry?.CanRestore ?? false;

    public Task<HistoryRestoreResult> RestoreAsync(Guid id) => Task.Run(() => _history.Restore(id, _quarantine));

    public IReadOnlyList<QuarantineEntry> Quarantine() => _quarantine.List();

    public Task<long> EmptyQuarantineAsync() => Task.Run(() =>
    {
        long before = _quarantine.List().Sum(e => e.Bytes);
        _quarantine.Empty();
        return before - _quarantine.List().Sum(e => e.Bytes);
    });

    public IReadOnlyList<VolumeSpace> Volumes()
    {
        string? profile = ProfilePath;
        string? systemRoot = profile is null ? null : Path.GetPathRoot(profile);
        var list = new List<VolumeSpace>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                bool system = string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase);
                string label = drive.VolumeLabel is { Length: > 0 } l ? l : "Local Disk";
                list.Add(new VolumeSpace(drive.RootDirectory.FullName, label, drive.AvailableFreeSpace, drive.TotalSize, system));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away or refuses a query is left out, not guessed.
            }
        }

        return [.. list.OrderByDescending(v => v.IsSystem).ThenBy(v => v.Root, StringComparer.OrdinalIgnoreCase)];
    }

    public void OpenInTerminal(string title, IReadOnlyList<string> commands, bool needsAdmin)
    {
        var step = new Core.Health.Runtimes.RuntimeStep
        {
            Id = "cleanup",
            Kind = Core.Health.Runtimes.StepKind.Guided,
            Title = title,
            Detail = "From DevSweep's Clean up.",
            Commands = commands,
            NeedsAdmin = needsAdmin,
        };
        using System.Diagnostics.Process? process = Core.Health.Runtimes.TerminalScript.Open(Core.Health.Runtimes.TerminalScript.Write(step, Path.GetTempPath()));
    }

    public ScanChanges? CompareAndRecord(IReadOnlyList<Finding> findings, long? diskFree)
    {
        ArgumentNullException.ThrowIfNull(findings);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ScanChanges? changes = _snapshots.Changes(findings, now);
        _snapshots.Record(findings.Select(f => (f.Id, f.Title, f.Size)), diskFree, now);
        return changes;
    }

    private SafetyGuard Guard()
    {
        IReadOnlyList<string> folders = _settings.Current.ProjectFolders;
        return SafetyGuard.ForCurrentUser(devRoots: folders.Count == 0 ? null : folders);
    }
}
