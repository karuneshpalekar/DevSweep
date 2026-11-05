using DevSweep.Core.Modules;

namespace DevSweep.App.ViewModels.Places;

// The Health (M3b) and Projects (M3c) engines are built on other branches. The app's
// pages are written against these small interfaces; after merging, an adapter from the
// module's Core service to the interface is registered in PlaceAdapters (one line each),
// and the page lights up. Until then a registered module without an adapter shows as
// "unavailable", and a module that is not registered at all keeps the page out of the
// navigation.

/// <summary>How serious a row is.</summary>
public enum Level
{
    Ok,
    Info,
    Warning,
    Critical,
}

/// <summary>Whether a tool version is still supported (endoflife.date).</summary>
public enum Support
{
    Supported,
    EndingSoon,
    EndOfLife,
    Unknown,
}

/// <summary>One install of a tool.</summary>
/// <param name="Version">The version.</param>
/// <param name="Source">Where it came from ("winget", "nvm-windows", "py launcher", "Scoop"...).</param>
/// <param name="Path">Its folder or program.</param>
/// <param name="IsActive">The one PATH (as Explorer builds it) runs.</param>
/// <param name="Support">Support status of this version.</param>
public sealed record ToolInstall(string Version, string Source, string Path, bool IsActive, Support Support);

/// <summary>A language, runtime or database, every install of it, and what projects ask for.</summary>
public sealed record ToolInfo(string Name, IReadOnlyList<ToolInstall> Installs, string? Note, IReadOnlyList<string> ProjectsAsk, IReadOnlyList<string> Fixes)
{
    public Level Level => Installs.Any(i => i.IsActive && i.Support == Support.EndOfLife) ? Level.Warning
        : Installs.Any(i => i.Support == Support.EndingSoon) ? Level.Info : Level.Ok;
}

/// <summary>A file that looks like a secret.</summary>
public sealed record SecurityItem(string Title, string Path, string Detail, Level Level);

/// <summary>A listening port.</summary>
/// <param name="Port">The port.</param>
/// <param name="Process">The program.</param>
/// <param name="ProcessId">Its PID.</param>
/// <param name="Project">The project folder it belongs to, if any.</param>
/// <param name="Address">What it listens on (127.0.0.1, 0.0.0.0, ::).</param>
/// <param name="ReachableFromNetwork">Listens on every interface and the firewall lets others in.</param>
/// <param name="IsDevelopment">A dev server or database (rather than an app or Windows itself).</param>
public sealed record PortInfo(int Port, string Process, int ProcessId, string? Project, string Address, bool ReachableFromNetwork, bool IsDevelopment);

/// <summary>Everything the Health place shows.</summary>
public sealed record HealthReport(IReadOnlyList<ToolInfo> Tools, IReadOnlyList<SecurityItem> Security, IReadOnlyList<PortInfo> Ports);

/// <summary>What the Health page needs from the Health module (M3b).</summary>
public interface IHealthSource
{
    Task<HealthReport> LoadAsync(CancellationToken cancellationToken);
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

/// <summary>Where the coordinator plugs module adapters in after merging M3b/M3c.</summary>
public static class PlaceAdapters
{
    /// <summary>Builds the Health source from an engine that has the <c>health</c> module; null when there is no adapter yet.</summary>
    public static Func<DevSweepEngine, IHealthSource?> Health { get; set; } = _ => null;

    /// <summary>Builds the Projects source from an engine that has the <c>projects</c> module.</summary>
    public static Func<DevSweepEngine, IProjectsSource?> Projects { get; set; } = _ => null;
}
