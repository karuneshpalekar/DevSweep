using DevSweep.Core.Health.Common;
using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Health.Runtimes;

/// <summary>
/// What runtime probes read: the environment (Known Folders, variables), the
/// registry, named locations, the installed-apps inventory, version resources,
/// app aliases, the Explorer PATH and, as a last resort, trusted programs.
/// Everything is replaceable, so a whole machine can be a fixture folder.
/// </summary>
public sealed class ProbeContext
{
    private readonly Lazy<AppInventory?> _inventory;
    private readonly Dictionary<string, string?> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public ProbeContext(
        ISystemEnvironment environment,
        IRegistryReader registry,
        LocationRegistry? locations = null,
        Func<AppInventory?>? inventory = null,
        IFileVersionReader? versions = null,
        IVersionCommand? commands = null,
        IAliasReader? aliases = null,
        IProcessPaths? processes = null)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Locations = locations;
        _inventory = new Lazy<AppInventory?>(inventory ?? (() => null));
        Versions = versions ?? WindowsFileVersionReader.Instance;
        Commands = commands ?? NoVersionCommand.Instance;
        Aliases = aliases ?? WindowsAliasReader.Instance;
        Processes = processes ?? NoProcessPaths.Instance;
        Path = SearchPath.FromRegistry(registry, environment);
    }

    public ISystemEnvironment Environment { get; }

    public IRegistryReader Registry { get; }

    public LocationRegistry? Locations { get; }

    public AppInventory? Inventory => _inventory.Value;

    public IFileVersionReader Versions { get; }

    public IVersionCommand Commands { get; }

    public IAliasReader Aliases { get; }

    public IProcessPaths Processes { get; }

    /// <summary>PATH as Explorer gives it to a new terminal.</summary>
    public SearchPath Path { get; }

    public string? Profile => Environment.KnownFolder("Profile");

    public string? LocalAppData => Environment.KnownFolder("LocalAppData");

    public string? RoamingAppData => Environment.KnownFolder("RoamingAppData");

    public string? ProgramData => Environment.KnownFolder("ProgramData");

    public IReadOnlyList<string> ProgramFilesFolders
        => ((string?[])[Environment.KnownFolder("ProgramFiles"), Environment.KnownFolder("ProgramFilesX86")]).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public string SystemDrive => (Variable("SystemDrive") ?? "C:") + System.IO.Path.DirectorySeparatorChar;

    /// <summary>
    /// A variable as a new program sees it: the registry environment (user, then system)
    /// first, then this process's (which may be older or changed by whoever started it).
    /// </summary>
    public string? Variable(string name)
    {
        lock (_lock)
        {
            if (!_variables.TryGetValue(name, out string? value))
            {
                value = SearchPath.RegistryVariable(Registry, Environment, name) ?? Environment.GetVariable(name);
                _variables[name] = value is { Length: > 0 } ? value.Trim().Trim('"') : null;
                value = _variables[name];
            }

            return value;
        }
    }

    /// <summary>A path under the profile ("~\.pyenv").</summary>
    public string? InProfile(params string[] parts) => Profile is null ? null : System.IO.Path.Join([Profile, .. parts]);

    public string? InLocalAppData(params string[] parts) => LocalAppData is null ? null : System.IO.Path.Join([LocalAppData, .. parts]);

    public string? InRoamingAppData(params string[] parts) => RoamingAppData is null ? null : System.IO.Path.Join([RoamingAppData, .. parts]);

    /// <summary>A named location's folder (<c>{scoop}</c>, <c>{chocolatey}</c>...), or a default when no registry is given.</summary>
    public string? Location(string name)
    {
        if (Locations?.Resolve(name).Path is { } resolved)
        {
            return resolved;
        }

        return name switch
        {
            "scoop" => Variable("SCOOP") ?? InProfile("scoop"),
            "scoopGlobal" => Variable("SCOOP_GLOBAL") ?? (ProgramData is null ? null : System.IO.Path.Join(ProgramData, "scoop")),
            "chocolatey" => Variable("ChocolateyInstall") ?? (ProgramData is null ? null : System.IO.Path.Join(ProgramData, "chocolatey")),
            "wingetPackages" => InLocalAppData("Microsoft", "WinGet", "Packages"),
            "wingetMachinePackages" => Environment.KnownFolder("ProgramFiles") is { } pf ? System.IO.Path.Join(pf, "WinGet", "Packages") : null,
            _ => null,
        };
    }

    /// <summary>A CI runner's tool cache (<c>RUNNER_TOOL_CACHE</c>, <c>AGENT_TOOLSDIRECTORY</c>).</summary>
    public string? ToolCache => Variable("RUNNER_TOOL_CACHE") ?? Variable("AGENT_TOOLSDIRECTORY");

    /// <summary>The version of a program from its version resource, else (trusted only) by running it.</summary>
    public (string Version, string From)? ProgramVersion(string? exe, params string[] arguments)
    {
        if (exe is null || !HealthFiles.Exists(exe))
        {
            return null;
        }

        if (Versions.Read(exe)?.Best is { } version)
        {
            return (version, $"version resource of {System.IO.Path.GetFileName(exe)}");
        }

        if (arguments.Length > 0 && VersionText.FirstVersion(Commands.Run(exe, arguments)) is { } ran)
        {
            return (ran, $"{System.IO.Path.GetFileName(exe)} {string.Join(' ', arguments)}");
        }

        return null;
    }
}
