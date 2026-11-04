using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Modules;

/// <summary>What one scan should do, beyond <see cref="ScanOptions"/>.</summary>
public sealed record ScanRequest
{
    public ScanOptions Options { get; init; } = new();

    /// <summary>Project folders chosen in Settings; null means the rules' own roots.</summary>
    public IReadOnlyList<string>? ProjectRoots { get; init; }

    /// <summary>The user's rules folder; null means <c>%LOCALAPPDATA%\DevSweep\Rules</c>, empty means none.</summary>
    public string? UserRulesDirectory { get; init; }

    /// <summary>The safety guard's path rules (path to refusal reason); null means none.</summary>
    public Func<string, string?>? SafetyCheck { get; init; }

    /// <summary>Overrides "now" (tests).</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>Overrides the measurer (tests).</summary>
    public IItemMeasurer? Measurer { get; init; }
}

/// <summary>
/// The engine the CLI and the app use: the registered modules, detectors,
/// locations, inventory sources and rule packs, with feature flags applied.
/// Create one per process (<see cref="CreateDefault"/>); it is immutable.
/// </summary>
public sealed class DevSweepEngine
{
    private readonly EngineParts _parts;

    internal DevSweepEngine(EngineParts parts) => _parts = parts;

    public FeatureFlags Flags => _parts.Flags;

    public IReadOnlyList<IDevSweepModule> Modules => _parts.Modules;

    /// <summary>Every registered detector (including turned-off kinds, which still validate their rules).</summary>
    public DetectorRegistry Detectors => _parts.Detectors;

    public IReadOnlySet<string> DisabledDetectorKinds => _parts.DisabledKinds;

    public IReadOnlyList<IInventoryCollector> InventoryCollectors => _parts.Collectors;

    public IReadOnlyList<InventorySource> DisabledInventorySources => _parts.DisabledSources;

    /// <summary>Flags that turned something off ("module:x", "detector:y"...).</summary>
    public IReadOnlyList<string> Disabled => _parts.Disabled;

    public ISystemEnvironment Environment => _parts.Environment;

    /// <summary>The standard engine: core locations, the inventory and Clean up.</summary>
    public static DevSweepEngine CreateDefault(
        FeatureFlags? flags = null, ISystemEnvironment? environment = null, IRegistryReader? registry = null, IRunningProcesses? processes = null)
    {
        var builder = new EngineBuilder(flags);
        foreach (IDevSweepModule module in BuiltInModules.All)
        {
            builder.AddModule(module);
        }

        return builder.Build(environment, registry, processes);
    }

    /// <summary>Services a module registered.</summary>
    public IReadOnlyList<T> Services<T>()
        where T : class
        => _parts.Services.TryGetValue(typeof(T), out IReadOnlyList<object>? list) ? list.Cast<T>().ToList() : [];

    /// <summary>A fresh set of named roots (resolve once per scan; tools' settings may change between scans).</summary>
    public LocationRegistry CreateLocations() => new(_parts.Environment, _parts.Locations);

    /// <summary>What packs are checked against.</summary>
    public RuleSchema CreateSchema(LocationRegistry? locations = null)
    {
        LocationRegistry registry = locations ?? CreateLocations();
        return new RuleSchema(_parts.Detectors, registry.CanonicalName);
    }

    /// <summary>The bundled packs of every module.</summary>
    /// <remarks>Sorted by file name across modules: file-name order is claim order (leftovers, <c>90-</c>, last).</remarks>
    public IReadOnlyList<RulePackText> BundledPacks() => _parts.Packs.SelectMany(p => p()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary><c>%LOCALAPPDATA%\DevSweep\Rules</c>.</summary>
    public string? DefaultUserRulesDirectory
        => _parts.Environment.KnownFolder("LocalAppData") is { } local ? Path.Join(local, "DevSweep", "Rules") : null;

    /// <summary>
    /// Loads bundled and user packs (bad packs reported and skipped), then drops
    /// rules turned off by flags (<c>detector:</c>, <c>category:</c>, <c>rule:</c>).
    /// </summary>
    public RuleSet LoadRules(string? userDirectory = null, LocationRegistry? locations = null)
    {
        var loader = new RuleLoader(CreateSchema(locations));
        string? directory = userDirectory is null ? DefaultUserRulesDirectory : userDirectory.Length == 0 ? null : userDirectory;
        RuleSet all = loader.Load(BundledPacks(), directory);
        List<Rule> enabled = all.Rules.Where(IsEnabled).ToList();
        return all with { Rules = enabled };
    }

    /// <summary>Builds the installed-apps inventory now (every registered source, in parallel).</summary>
    public AppInventory BuildInventory(LocationRegistry? locations = null, CancellationToken cancellationToken = default)
        => InventoryBuilder.Build(
            _parts.Collectors,
            new InventoryContext(_parts.Registry, locations ?? CreateLocations(), _parts.Processes),
            _parts.DisabledSources,
            cancellationToken);

    /// <summary>A scan context whose inventory is built the first time a check needs it.</summary>
    public ScanContext CreateScanContext(LocationRegistry locations, ScanRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        Func<AppInventory>? inventory = _parts.Collectors.Count == 0 && _parts.DisabledSources.Count == 0
            ? null
            : () => BuildInventory(locations);
        return new ScanContext(locations, inventory, _parts.Processes, request?.Now)
        {
            ProjectRoots = request?.ProjectRoots,
            SafetyCheck = request?.SafetyCheck,
            Registry = _parts.Registry,
        };
    }

    /// <summary>Loads the rules and runs a scan.</summary>
    public async Task<ScanResult> ScanAsync(ScanRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new ScanRequest();
        LocationRegistry locations = CreateLocations();
        RuleSet rules = LoadRules(request.UserRulesDirectory, locations);
        ScanContext context = CreateScanContext(locations, request);
        var scanner = new Scanner(rules.Rules, _parts.Detectors, context, request.Measurer);
        ScanOptions options = request.Options with
        {
            RuleErrors = [.. request.Options.RuleErrors, .. rules.Errors],
            Disabled = [.. request.Options.Disabled, .. _parts.Disabled],
        };
        return await scanner.ScanAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private bool IsEnabled(Rule rule)
        => !_parts.DisabledKinds.Contains(rule.Detector.Kind)
            && _parts.Flags.IsEnabled($"category:{rule.Category.YamlName()}")
            && _parts.Flags.IsEnabled($"rule:{rule.Id}");
}
