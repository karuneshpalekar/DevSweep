using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform.Recycling;

namespace DevSweep.Core.Packs.Tools;

/// <summary>
/// What the Windows packs ask the machine beyond files and the registry: running a
/// tool (always a real program, never a shell or a <c>.cmd</c> script, always with a time
/// limit) and the Recycle Bin's contents. Faked in tests (<see cref="FixedMachineQueries"/>).
/// </summary>
public interface IMachineQueries
{
    /// <summary>Runs <paramref name="argv"/> (program and arguments, no shell) and waits at most <paramref name="timeout"/>.</summary>
    CommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The root of every fixed drive (<c>C:\</c>...).</summary>
    IReadOnlyList<string> FixedDriveRoots();

    /// <summary>What is in a drive's Recycle Bin, or null when it cannot be read.</summary>
    RecycleBinUsage? RecycleBin(string driveRoot);
}

/// <summary>The real machine.</summary>
public sealed class WindowsMachineQueries : IMachineQueries
{
    public static WindowsMachineQueries Instance { get; } = new();

    public CommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken)
        => CommandRunner.RunAsync(argv, timeout, cancellationToken).GetAwaiter().GetResult();

    public IReadOnlyList<string> FixedDriveRoots()
    {
        var roots = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (IOException)
            {
                // A drive that went away while listing: not counted.
            }
        }

        return roots;
    }

    public RecycleBinUsage? RecycleBin(string driveRoot)
    {
        try
        {
            return Platform.Recycling.RecycleBin.QueryUsage(driveRoot);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>A machine described by hand: command outputs by command line, drives and bins. Anything not listed fails to start.</summary>
public sealed class FixedMachineQueries : IMachineQueries
{
    /// <summary>Results by the whole command line, parts joined with one space (program as given, e.g. a full path).</summary>
    public Dictionary<string, CommandResult> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Drives { get; } = [];

    public Dictionary<string, RecycleBinUsage> Bins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every command asked for, in order.</summary>
    public List<string> Ran { get; } = [];

    public static CommandResult Output(string stdout, int exitCode = 0) => new(exitCode, stdout, string.Empty, false, TimeSpan.Zero);

    public CommandResult Run(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argv);
        string line = string.Join(' ', argv);
        lock (Ran)
        {
            Ran.Add(line);
        }

        return Commands.TryGetValue(line, out CommandResult? result)
            ? result
            : new CommandResult(-1, string.Empty, string.Empty, false, TimeSpan.Zero, $"{argv[0]} could not be started (not on this test machine).");
    }

    public IReadOnlyList<string> FixedDriveRoots() => Drives;

    public RecycleBinUsage? RecycleBin(string driveRoot) => Bins.TryGetValue(driveRoot, out RecycleBinUsage usage) ? usage : null;
}
