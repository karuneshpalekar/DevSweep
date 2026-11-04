using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using Microsoft.Win32;

namespace DevSweep.Core.Tests.Health;

/// <summary>A whole machine in a temporary folder: profile, Program Files, registry, version resources and aliases are all fakes.</summary>
internal sealed class HealthMachine : IDisposable
{
    public HealthMachine()
    {
        Root = Path.Join(Path.GetTempPath(), "devsweep-health-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Environment = FixedEnvironment.UnderRoot(Root);
        Environment.Variables["SystemDrive"] = Path.Join(Root, "C");
        Directory.CreateDirectory(Path.Join(Root, "C"));
        Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, SearchPath.MachineKey, ("PATHEXT", ".COM;.EXE;.BAT;.CMD"));
        Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, SearchPath.UserKey);
    }

    public string Root { get; }

    public FixedEnvironment Environment { get; }

    public InMemoryRegistry Registry { get; } = new();

    public FixedFileVersions Versions { get; } = new();

    public FixedAliases Aliases { get; } = new();

    public FixedVersionCommand Commands { get; } = new();

    /// <summary>Files created so far.</summary>
    public List<string> Created { get; } = [];

    public string Profile => Environment.KnownFolder("Profile")!;

    public string LocalAppData => Environment.KnownFolder("LocalAppData")!;

    public string RoamingAppData => Environment.KnownFolder("RoamingAppData")!;

    public string ProgramFiles => Environment.KnownFolder("ProgramFiles")!;

    /// <summary>Creates a file (and its folders) with the given text.</summary>
    public string File(string path, string text = "x")
    {
        Created.Add(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    /// <summary>An empty file standing in for an app alias, with the alias's reparse data held by <see cref="Aliases"/>.</summary>
    public string Alias(string path, AliasInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, []);
        Aliases.Links[path] = info;
        return path;
    }

    public void SetPath(string? machine, string? user)
    {
        if (machine is not null)
        {
            Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, SearchPath.MachineKey, ("Path", machine));
        }

        if (user is not null)
        {
            Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, SearchPath.UserKey, ("Path", user));
        }
    }

    public ProbeContext Context() => new(Environment, Registry, null, null, Versions, Commands, Aliases);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Aliases by path (tests).</summary>
internal sealed class FixedAliases : IAliasReader
{
    public Dictionary<string, AliasInfo> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AliasInfo? Read(string path) => Links.GetValueOrDefault(path);
}
