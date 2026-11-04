using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Modules;

/// <summary>
/// A part of DevSweep registered at startup (PLAN 7.6): Clean up, the inventory,
/// and in M3 Health and Projects. Each registers what it brings (detectors,
/// locations, inventory sources, rule packs, services) and can be turned off
/// with the <c>module:&lt;id&gt;</c> flag.
/// </summary>
public interface IDevSweepModule
{
    /// <summary>The flag name: <c>module:&lt;Id&gt;</c>.</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>False for an experimental module, which stays off until turned on.</summary>
    bool EnabledByDefault { get; }

    void Register(EngineBuilder builder);
}

/// <summary>Collects what modules register, honouring feature flags, and builds the <see cref="DevSweepEngine"/>.</summary>
public sealed class EngineBuilder(FeatureFlags? flags = null)
{
    private readonly List<IDevSweepModule> _modules = [];
    private readonly List<IDetector> _detectors = [];
    private readonly HashSet<string> _disabledKinds = new(StringComparer.Ordinal);
    private readonly List<ILocationResolver> _locations = [];
    private readonly List<IInventoryCollector> _collectors = [];
    private readonly List<InventorySource> _disabledSources = [];
    private readonly List<Func<IReadOnlyList<RulePackText>>> _packs = [];
    private readonly Dictionary<Type, List<object>> _services = [];
    private readonly List<string> _disabled = [];

    public FeatureFlags Flags { get; } = flags ?? FeatureFlags.Default;

    /// <summary>Registers a module unless its flag is off.</summary>
    public EngineBuilder AddModule(IDevSweepModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!Flags.IsEnabled($"module:{module.Id}", module.EnabledByDefault))
        {
            _disabled.Add($"module:{module.Id}");
            return this;
        }

        _modules.Add(module);
        module.Register(this);
        return this;
    }

    /// <summary>
    /// Registers a detector kind. Turned off (<c>detector:&lt;kind&gt;</c>), it still reads
    /// its rules' options (so packs stay valid) but its rules do not run.
    /// </summary>
    public EngineBuilder AddDetector(IDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detectors.Add(detector);
        if (!Flags.IsEnabled($"detector:{detector.Kind}"))
        {
            _disabledKinds.Add(detector.Kind);
            _disabled.Add($"detector:{detector.Kind}");
        }

        return this;
    }

    /// <summary>Registers a named root (<c>{name}</c> in rule paths). A later resolver with the same name replaces an earlier one.</summary>
    public EngineBuilder AddLocation(ILocationResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _locations.RemoveAll(r => string.Equals(r.Name, resolver.Name, StringComparison.OrdinalIgnoreCase));
        _locations.Add(resolver);
        return this;
    }

    /// <summary>Registers an inventory source unless <c>inventory:&lt;Source&gt;</c> is off.</summary>
    public EngineBuilder AddInventoryCollector(IInventoryCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        if (Flags.IsEnabled($"inventory:{collector.Source}"))
        {
            _collectors.Add(collector);
        }
        else if (!_disabledSources.Contains(collector.Source))
        {
            _disabledSources.Add(collector.Source);
            _disabled.Add($"inventory:{collector.Source}");
        }

        return this;
    }

    /// <summary>Registers bundled rule packs (read when rules are loaded).</summary>
    public EngineBuilder AddRulePacks(Func<IReadOnlyList<RulePackText>> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs.Add(packs);
        return this;
    }

    /// <summary>Registers a service other modules or the app can ask for (<see cref="DevSweepEngine.Services{T}"/>).</summary>
    public EngineBuilder AddService<T>(T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!_services.TryGetValue(typeof(T), out List<object>? list))
        {
            list = [];
            _services[typeof(T)] = list;
        }

        list.Add(service);
        return this;
    }

    /// <summary>Builds the engine against a machine (the real one by default).</summary>
    public DevSweepEngine Build(ISystemEnvironment? environment = null, IRegistryReader? registry = null, IRunningProcesses? processes = null)
        => new(new EngineParts(
            Flags,
            _modules.ToList(),
            new DetectorRegistry(_detectors),
            _disabledKinds.ToHashSet(StringComparer.Ordinal),
            _locations.ToList(),
            _collectors.ToList(),
            _disabledSources.ToList(),
            _packs.ToList(),
            _services.ToDictionary(p => p.Key, p => (IReadOnlyList<object>)p.Value.ToList()),
            _disabled.ToList(),
            environment ?? WindowsEnvironment.Instance,
            registry ?? WindowsRegistryReader.Instance,
            processes ?? new RunningProcesses()));
}

/// <summary>Everything an engine was built from.</summary>
internal sealed record EngineParts(
    FeatureFlags Flags,
    IReadOnlyList<IDevSweepModule> Modules,
    DetectorRegistry Detectors,
    IReadOnlySet<string> DisabledKinds,
    IReadOnlyList<ILocationResolver> Locations,
    IReadOnlyList<IInventoryCollector> Collectors,
    IReadOnlyList<InventorySource> DisabledSources,
    IReadOnlyList<Func<IReadOnlyList<RulePackText>>> Packs,
    IReadOnlyDictionary<Type, IReadOnlyList<object>> Services,
    IReadOnlyList<string> Disabled,
    ISystemEnvironment Environment,
    IRegistryReader Registry,
    IRunningProcesses Processes);
