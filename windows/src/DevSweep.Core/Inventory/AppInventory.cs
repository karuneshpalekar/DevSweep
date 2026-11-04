using System.Diagnostics;

namespace DevSweep.Core.Inventory;

/// <summary>One app, merged across the sources that list it.</summary>
public sealed record InstalledApp
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Names { get; init; } = [];

    public IReadOnlyList<string> Publishers { get; init; } = [];

    public IReadOnlyList<string> ProductCodes { get; init; } = [];

    public IReadOnlyList<string> PackageFamilyNames { get; init; } = [];

    public IReadOnlyList<string> PackageIds { get; init; } = [];

    public IReadOnlyList<string> InstallLocations { get; init; } = [];

    public IReadOnlyList<string> ExePaths { get; init; } = [];

    public IReadOnlyList<InventorySource> Sources { get; init; } = [];
}

/// <summary>
/// Every installed app from every source (PLAN 4.3), with what each source
/// contributed. <see cref="IsInstalled"/> says "not installed" only when every
/// source was read completely and none lists the app.
/// </summary>
public sealed class AppInventory
{
    public AppInventory(IReadOnlyList<AppRecord> records, IReadOnlyList<SourceReport> sources, IReadOnlyList<InventorySource>? disabledSources = null)
    {
        Records = records ?? throw new ArgumentNullException(nameof(records));
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
        DisabledSources = disabledSources ?? [];
        Apps = AppMerger.Merge(records);
    }

    public IReadOnlyList<AppRecord> Records { get; }

    public IReadOnlyList<InstalledApp> Apps { get; }

    /// <summary>What each source contributed, in collection order.</summary>
    public IReadOnlyList<SourceReport> Sources { get; }

    /// <summary>Built-in sources turned off by a feature flag (their absence makes every "gone" unknown).</summary>
    public IReadOnlyList<InventorySource> DisabledSources { get; }

    /// <summary>Whether the app is installed, which sources were checked, and what matched.</summary>
    public InstallMatch IsInstalled(AppHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        var evidence = new List<InstallEvidence>();
        foreach (AppRecord record in Records)
        {
            if (AppMatcher.Match(hint, record) is { } matchedBy)
            {
                evidence.Add(new InstallEvidence(record.Source, record.Name, matchedBy, record.Origin));
            }
        }

        if (evidence.Count > 0)
        {
            return new InstallMatch(InstallState.Installed, Sources, evidence, null);
        }

        string? unknown = hint.IsEmpty ? "the rule gives no way to recognise the app"
            : Sources.FirstOrDefault(s => !s.Complete) is { } incomplete ? $"{incomplete.Title} could not be read completely ({incomplete.Problems[0]})"
            : DisabledSources.Count > 0 ? $"{InventorySources.Title(DisabledSources[0])} is turned off"
            : null;
        return new InstallMatch(unknown is null ? InstallState.NotInstalled : InstallState.Unknown, Sources, evidence, unknown);
    }

    /// <summary>Records whose install location or program lies inside <paramref name="folder"/> (or is it): the folder holds an installed app.</summary>
    public IReadOnlyList<AppRecord> RecordsInside(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string prefix = Path.TrimEndingDirectorySeparator(folder);
        bool Inside(string? path) => path is not null
            && (string.Equals(Path.TrimEndingDirectorySeparator(path), prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
        return Records.Where(r => r.Source != InventorySource.ProgramFiles && (Inside(r.InstallLocation) || r.ExePaths.Any(Inside))).ToList();
    }
}

/// <summary>Runs every collector (in parallel, each timed, failures recorded) and builds the <see cref="AppInventory"/>.</summary>
public static class InventoryBuilder
{
    public static AppInventory Build(
        IReadOnlyList<IInventoryCollector> collectors,
        InventoryContext context,
        IReadOnlyList<InventorySource>? disabledSources = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        var results = new (IReadOnlyList<AppRecord> Records, SourceReport Report)[collectors.Count];
        Parallel.For(0, collectors.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            results[i] = Run(collectors[i], context, cancellationToken);
        });

        var records = new List<AppRecord>();
        var reports = new List<SourceReport>();
        foreach ((IReadOnlyList<AppRecord> found, SourceReport report) in results)
        {
            records.AddRange(found);
            reports.Add(report);
        }

        return new AppInventory(records, reports, disabledSources);
    }

    private static (IReadOnlyList<AppRecord>, SourceReport) Run(IInventoryCollector collector, InventoryContext context, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var run = new CollectorRun(context, cancellationToken);
        IReadOnlyList<AppRecord> records;
        try
        {
            records = collector.Collect(run);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            run.Problem($"could not be read: {ex.Message}");
            records = [];
        }

        return (records, new SourceReport(collector.Source, run.Where, records.Count, clock.Elapsed.TotalMilliseconds, run.Problems));
    }
}
