namespace DevSweep.Core.Inventory;

/// <summary>A fixed list of running process names, for tests.</summary>
public sealed class FixedProcesses(params string[] names) : IRunningProcesses
{
    public IReadOnlyList<string> Names() => names;
}
