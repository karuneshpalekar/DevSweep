using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Scanning;

/// <summary>
/// What every check of one scan shares: the named locations (resolved once),
/// the installed-apps inventory (built on first use), running processes, the
/// project folders chosen in Settings, and "now".
/// </summary>
public sealed class ScanContext
{
    private readonly Lazy<AppInventory>? _inventory;

    public ScanContext(LocationRegistry locations, Func<AppInventory>? inventory = null, IRunningProcesses? processes = null, DateTimeOffset? now = null)
    {
        Locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _inventory = inventory is null ? null : new Lazy<AppInventory>(inventory, LazyThreadSafetyMode.ExecutionAndPublication);
        Processes = processes ?? new RunningProcesses();
        Now = now ?? DateTimeOffset.UtcNow;
    }

    public LocationRegistry Locations { get; }

    public IRunningProcesses Processes { get; }

    /// <summary>The moment the scan treats as now (fixed for the whole scan; tests move it).</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Project folders chosen in Settings; null means each rule's own roots.</summary>
    public IReadOnlyList<string>? ProjectRoots { get; init; }

    /// <summary>
    /// The path rules of the safety guard (no disk access); returns why a path
    /// may not be removed, or null. Findings it refuses lose their remove actions.
    /// </summary>
    public Func<string, string?>? SafetyCheck { get; init; }

    /// <summary>The registry the engine reads (read only; a fake one in tests).</summary>
    public IRegistryReader Registry { get; init; } = WindowsRegistryReader.Instance;

    public bool HasInventory => _inventory is not null;

    /// <summary>The installed-apps inventory, built on first use and shared by every check.</summary>
    /// <exception cref="InvalidOperationException">The scan was set up without one.</exception>
    public AppInventory Inventory => _inventory?.Value ?? throw new InvalidOperationException("This scan has no installed-apps inventory.");

    /// <summary>The inventory if it was already built, for the report.</summary>
    public AppInventory? InventoryIfBuilt => _inventory is { IsValueCreated: true } ? _inventory.Value : null;
}
