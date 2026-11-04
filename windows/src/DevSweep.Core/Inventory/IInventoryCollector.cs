using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Inventory;

/// <summary>Programs running now, by process name (no ".exe"). Faked in tests.</summary>
public interface IRunningProcesses
{
    IReadOnlyList<string> Names();
}

/// <summary>The machine's running processes (<see cref="System.Diagnostics.Process.GetProcesses()"/>), read once.</summary>
public sealed class RunningProcesses : IRunningProcesses
{
    private readonly Lazy<IReadOnlyList<string>> _names = new(Read);

    public IReadOnlyList<string> Names() => _names.Value;

    private static List<string> Read()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // Exited while we looked.
                }
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>What collectors read from: the registry, named locations, running processes.</summary>
public sealed record InventoryContext(IRegistryReader Registry, LocationRegistry Locations, IRunningProcesses Processes);

/// <summary>One collection: where to report what could not be read.</summary>
public sealed class CollectorRun(InventoryContext context, CancellationToken cancellationToken)
{
    private readonly List<string> _problems = [];
    private readonly List<string> _where = [];

    public InventoryContext Context { get; } = context;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public IReadOnlyList<string> Problems => _problems;

    public string Where => string.Join("; ", _where);

    /// <summary>Something the source holds could not be read: the source is then incomplete.</summary>
    public void Problem(string message) => _problems.Add(message);

    /// <summary>Records a key or folder that was read.</summary>
    public void Looked(string where) => _where.Add(where);
}

/// <summary>One inventory source (PLAN 4.3). Collectors are registered by modules and run in parallel.</summary>
public interface IInventoryCollector
{
    InventorySource Source { get; }

    /// <summary>Reads the source. Throws when it cannot be read at all; reports partial problems to <paramref name="run"/>.</summary>
    IReadOnlyList<AppRecord> Collect(CollectorRun run);
}
