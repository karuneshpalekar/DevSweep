using DevSweep.Core.Health;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Modules;

namespace DevSweep.App.ViewModels.Places;

// Health binds to M3b's Core types (VersionsResult, SecurityResult, PortsResult) through
// IHealthSource (CoreHealthSource is the real one). The Projects engine (M3c) is built on
// another branch: its page is written against IProjectsSource, and after merging an adapter
// is registered in PlaceAdapters.Projects (one line) and the page lights up. Until then a
// place whose module is not in this build shows as "unavailable"; a module turned off by a
// flag keeps its place out of the navigation.

/// <summary>What the Health place shows: M3b's own result types, one per tab (null when the tab is off).</summary>
public sealed record HealthReport(VersionsResult? Versions, SecurityResult? Security, PortsResult? Ports);

/// <summary>What the Health page needs from the Health module (M3b).</summary>
public interface IHealthSource
{
    /// <summary>Which tabs are on (<c>module:health.runtimes/.security/.ports</c>).</summary>
    HealthFeatures Features { get; }

    Task<HealthReport> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the fix to a script and opens it in a visible PowerShell window that waits for Enter.</summary>
    void OpenFix(RuntimeStep fix);
}

/// <summary>A repository on this PC or on GitHub.</summary>
/// <param name="Name">owner/name or the folder name.</param>
/// <param name="Account">The GitHub account it belongs to, if any.</param>
/// <param name="Path">Where it is on this PC; null when only on GitHub.</param>
/// <param name="Bytes">Its size on disk.</param>
/// <param name="LastOpened">Last commit or change.</param>
/// <param name="Safety">"Fully pushed", "2 commits not pushed", "Uncommitted changes"...</param>
/// <param name="SafeToRemove">Everything in it is on GitHub.</param>
public sealed record ProjectInfo(string Name, string? Account, string? Path, long Bytes, DateTimeOffset? LastOpened, string Safety, bool SafeToRemove)
{
    public bool OnDisk => Path is not null;
}

/// <summary>A GitHub account signed in with gh.</summary>
public sealed record AccountInfo(string Login, string Name, string Email, int Repositories, int OnDisk, long Bytes, bool SignedIn);

/// <summary>One line of the Activity trail.</summary>
public sealed record ActivityInfo(DateTimeOffset Date, string Text, string Kind);

/// <summary>Everything the Projects place shows.</summary>
public sealed record ProjectsReport(IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<AccountInfo> Accounts, IReadOnlyList<ActivityInfo> Activity);

/// <summary>What the Projects page needs from the Projects module (M3c).</summary>
public interface IProjectsSource
{
    Task<ProjectsReport> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Where the coordinator plugs the Projects adapter in after merging M3c.</summary>
public static class PlaceAdapters
{
    /// <summary>Builds the Projects source from an engine that has the <c>projects</c> module; null when there is no adapter yet.</summary>
    public static Func<DevSweepEngine, IProjectsSource?> Projects { get; set; } = _ => null;
}
