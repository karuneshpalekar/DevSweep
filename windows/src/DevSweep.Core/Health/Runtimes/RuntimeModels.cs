using DevSweep.Core.Health.Common;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>How well a release line is still looked after upstream (worst first).</summary>
public enum SupportStatus
{
    EndOfLife,
    EndingSoon,
    Supported,

    /// <summary>No support data for it (offline with no cache, or no schedule is published).</summary>
    Unknown,
}

/// <summary>Who put an installation on the machine. Decides how it is upgraded or removed.</summary>
public enum InstallSource
{
    Unknown,
    Winget,
    Scoop,
    Chocolatey,
    Nvm,
    Fnm,
    Volta,
    Pyenv,
    Asdf,
    Mise,
    Uv,
    Conda,

    /// <summary>The Python install manager (<c>py install</c>, Python 3.14 and later).</summary>
    PythonInstallManager,
    PythonOrg,

    /// <summary>A Microsoft Store Python package.</summary>
    StorePython,
    NodejsOrg,
    GoOrg,

    /// <summary>A JDK vendor's installer (Adoptium, Microsoft, Oracle, Zulu, Corretto...).</summary>
    JdkInstaller,

    /// <summary>A JDK an IDE downloaded (<c>~\.jdks</c>).</summary>
    IdeDownloaded,

    /// <summary>Part of an IDE (JetBrains Runtime, Visual Studio's Node or Python).</summary>
    IdeBundled,
    RubyInstaller,
    PostgresInstaller,
    Rustup,
    DotnetInstaller,
    SdkFolder,
    Fvm,
    DenoInstaller,
    BunInstaller,

    /// <summary>A CI runner's tool cache (<c>RUNNER_TOOL_CACHE</c>), managed by the runner.</summary>
    ToolCache,
}

/// <summary>Titles of <see cref="InstallSource"/> values.</summary>
public static class InstallSources
{
    public static string Title(InstallSource source) => source switch
    {
        InstallSource.Winget => "winget",
        InstallSource.Scoop => "Scoop",
        InstallSource.Chocolatey => "Chocolatey",
        InstallSource.Nvm => "nvm-windows",
        InstallSource.Fnm => "fnm",
        InstallSource.Volta => "Volta",
        InstallSource.Pyenv => "pyenv-win",
        InstallSource.Asdf => "asdf",
        InstallSource.Mise => "mise",
        InstallSource.Uv => "uv",
        InstallSource.Conda => "conda",
        InstallSource.PythonInstallManager => "Python install manager",
        InstallSource.PythonOrg => "python.org installer",
        InstallSource.StorePython => "Microsoft Store",
        InstallSource.NodejsOrg => "nodejs.org installer",
        InstallSource.GoOrg => "go.dev installer",
        InstallSource.JdkInstaller => "JDK installer",
        InstallSource.IdeDownloaded => "Downloaded by an IDE",
        InstallSource.IdeBundled => "Bundled with an IDE",
        InstallSource.RubyInstaller => "RubyInstaller",
        InstallSource.PostgresInstaller => "PostgreSQL installer",
        InstallSource.Rustup => "rustup",
        InstallSource.DotnetInstaller => "Microsoft installer",
        InstallSource.SdkFolder => "SDK folder",
        InstallSource.Fvm => "FVM",
        InstallSource.DenoInstaller => "Deno installer",
        InstallSource.BunInstaller => "Bun installer",
        InstallSource.ToolCache => "CI tool cache",
        _ => "Unknown",
    };
}

/// <summary>One installation of a tool.</summary>
public sealed record Installation
{
    /// <summary>Full version, e.g. "20.11.0".</summary>
    public required string Version { get; init; }

    /// <summary>Release line used for support dates, e.g. "20", "3.12", "8.0".</summary>
    public required string Cycle { get; init; }

    /// <summary>The installation's folder.</summary>
    public required string Path { get; init; }

    /// <summary>The program (node.exe, python.exe...) when there is one.</summary>
    public string? Executable { get; init; }

    public required InstallSource Source { get; init; }

    /// <summary>Package, folder, vendor or environment name, when there is one.</summary>
    public string? SourceDetail { get; init; }

    /// <summary>Where the version came from ("release file", "version resource of node.exe", "folder name"...).</summary>
    public string VersionFrom { get; init; } = string.Empty;

    /// <summary>The one a new terminal runs (first on the PATH Explorer gives new programs).</summary>
    public bool IsDefault { get; init; }

    /// <summary>For servers such as PostgreSQL; null when it could not be told.</summary>
    public bool? IsRunning { get; init; }

    /// <summary>Data that belongs to this install (a PostgreSQL data folder).</summary>
    public string? DataPath { get; init; }

    /// <summary>endoflife.date product this install is checked against (vendor-specific for Java).</summary>
    public string? Product { get; init; }

    public SupportStatus Support { get; init; } = SupportStatus.Unknown;

    public DateOnly? ReleaseDate { get; init; }

    public DateOnly? SupportEnds { get; init; }

    public string? LatestInCycle { get; init; }

    public bool IsLts { get; init; }

    /// <summary>Uninstall details from the inventory (MSI product code), for removal commands.</summary>
    public string? ProductCode { get; init; }

    /// <summary>A winget package id, when winget knows this install.</summary>
    public string? WingetId { get; init; }

    /// <summary>Part of an IDE or managed by a CI runner: reported, never offered for removal.</summary>
    public bool IsSystem => Source is InstallSource.IdeBundled or InstallSource.ToolCache;

    public bool PatchAvailable => LatestInCycle is { } latest && VersionText.Less(Version, latest);

    public string SourceTitle => InstallSources.Title(Source);

    /// <summary>"Node.js 20.11.0 (nvm-windows)".</summary>
    public string Label(string toolName) => $"{toolName} {Version} ({SourceTitle})";
}

public enum IssueLevel
{
    Critical,
    Warning,
    Info,
}

public sealed record RuntimeIssue(IssueLevel Level, string Text);

public enum StepKind
{
    Upgrade,
    Remove,
    SwitchDefault,
    Guided,
}

/// <summary>
/// A fix DevSweep can show and run in a visible PowerShell window that lists the
/// commands and waits for Enter. Nothing runs without the user, nothing is elevated silently.
/// </summary>
public sealed record RuntimeStep
{
    /// <summary>Stable within one result ("node:remove:1").</summary>
    public required string Id { get; init; }

    public required StepKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    /// <summary>PowerShell commands, in order.</summary>
    public required IReadOnlyList<string> Commands { get; init; }

    /// <summary>The commands need an administrator (machine-wide installs, Chocolatey); the window explains and offers "Run as administrator".</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>What PATH says about one command: every match in order, the first one runs.</summary>
public sealed record CommandResolution
{
    public required string Command { get; init; }

    /// <summary>Every match on the PATH, first one first (Store aliases included and marked).</summary>
    public IReadOnlyList<PathHit> Hits { get; init; } = [];

    /// <summary>The program that really runs after following shims and links; null when nothing matches.</summary>
    public string? Runs { get; init; }

    /// <summary>How <see cref="Runs"/> was worked out ("scoop shim", "nvm link"...).</summary>
    public string? Via { get; init; }

    /// <summary>Shells whose start-up files change PATH for this command (never run, only read).</summary>
    public IReadOnlyList<string> MayDiffer { get; init; } = [];
}

/// <summary>One match for a command in one PATH folder.</summary>
public sealed record PathHit(string Path, int PathIndex, bool FromUserPath, AliasInfo? Alias);

public sealed record RuntimeReport
{
    /// <summary>"node", "python"...</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<Installation> Installs { get; init; } = [];

    public IReadOnlyList<RuntimeIssue> Issues { get; init; } = [];

    public IReadOnlyList<RuntimeStep> Steps { get; init; } = [];

    /// <summary>Newest supported release line ("Node.js 22 LTS, supported until 30 Apr 2027").</summary>
    public string? Recommended { get; init; }

    public string? DataSource { get; init; }

    public DateTimeOffset? CheckedAt { get; init; }

    public string? Note { get; init; }

    /// <summary>What a new terminal runs for the tool's command.</summary>
    public CommandResolution? Resolution { get; init; }

    /// <summary>Store aliases and other things on PATH that are not installs, and why.</summary>
    public IReadOnlyList<string> NotInstalls { get; init; } = [];

    public SupportStatus Worst => Installs.Count == 0 ? SupportStatus.Unknown : Installs.Min(i => i.Support);

    public bool NeedsAttention => Issues.Any(i => i.Level != IssueLevel.Info);
}

/// <summary>Everything Health "Tools and versions" shows.</summary>
public sealed record VersionsResult
{
    public IReadOnlyList<RuntimeReport> Runtimes { get; init; } = [];

    public IReadOnlyList<ProjectRequirement> Projects { get; init; } = [];

    /// <summary>Windows itself (feature update and support dates).</summary>
    public RuntimeReport? Windows { get; init; }

    /// <summary>The PATH folders a new program gets, system then user.</summary>
    public IReadOnlyList<PathEntry> Path { get; init; } = [];

    /// <summary>Shell start-up files that change PATH ("PowerShell profile ... runs fnm env").</summary>
    public IReadOnlyList<string> ShellNotes { get; init; } = [];

    /// <summary>Support data came from an old cache or none at all (offline).</summary>
    public bool EolOffline { get; init; }

    public DateTimeOffset Date { get; init; }

    public int AttentionCount
        => Runtimes.Count(r => r.NeedsAttention)
            + (Projects.Any(p => p.Status != RequirementStatus.Ok) ? 1 : 0)
            + (Windows?.NeedsAttention == true ? 1 : 0);
}
