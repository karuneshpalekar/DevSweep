using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using DevSweep.Core.Packs;
using DevSweep.Core.Packs.Tools;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using DevSweep.Fixtures;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>
/// A whole machine inside a fixture folder on real NTFS: a profile with its
/// Known Folders, ProgramData, Program Files and Temp (all under the fixture),
/// a fake registry (with the keys every Windows has), fake running processes,
/// and the real walker. Scans run the bundled rules plus any user pack.
/// </summary>
internal sealed class MachineFixture : IDisposable
{
    private readonly TempFixture _fixture;

    public MachineFixture(string name)
    {
        _fixture = new TempFixture(name);
        Environment = FixedEnvironment.UnderRoot(_fixture.Root);
        Directory.CreateDirectory(Environment.TempPath);
        Registry = new InMemoryRegistry()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallCollector.KeyPath)
            .Set(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallCollector.KeyPath)
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, StorePackagesCollector.KeyPath);
    }

    public FixedEnvironment Environment { get; }

    public InMemoryRegistry Registry { get; }

    public List<string> Processes { get; } = [];

    public string Root => _fixture.Root;

    public string Profile => Environment.Folders["Profile"];

    public string Local => Environment.Folders["LocalAppData"];

    public string Roaming => Environment.Folders["RoamingAppData"];

    public string ProgramData => Environment.Folders["ProgramData"];

    public string Temp => Environment.TempPath;

    /// <summary>Tool output and Recycle Bins of this machine (the packs module never asks the real one).</summary>
    public FixedMachineQueries Machine { get; } = new();

    public DevSweepEngine Engine(string? flags = null)
    {
        var builder = new EngineBuilder(FeatureFlags.Default.With(flags));
        foreach (IDevSweepModule module in BuiltInModules.All)
        {
            builder.AddModule(module is PacksModule ? new PacksModule(Machine) : module);
        }

        return builder.Build(Environment, Registry, new FixedProcesses([.. Processes]));
    }

    /// <summary>A file whose clusters are allocated (end of file set, not sparse) without writing every byte.</summary>
    public static string Allocate(string path, long bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(bytes);
        stream.Write([1, 2, 3, 4]);
        return path;
    }

    public static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Sets write and creation times of a file or folder and everything inside it.</summary>
    public static void Age(string path, DateTime utc)
    {
        if (Directory.Exists(path))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                Touch(entry, utc);
            }
        }

        Touch(path, utc);
    }

    public async Task<ScanResult> ScanAsync(
        Category[]? categories = null, IReadOnlyList<string>? projectRoots = null, string? userPack = null, string? flags = null, SizeMode mode = SizeMode.Exact)
    {
        string rulesDirectory = Path.Join(Root, "user-rules");
        Directory.CreateDirectory(rulesDirectory);
        if (userPack is not null)
        {
            File.WriteAllText(Path.Join(rulesDirectory, "test.yml"), userPack);
        }

        var request = new ScanRequest
        {
            Options = new ScanOptions
            {
                Categories = categories?.ToHashSet(),
                Mode = mode,
                CheckTimeout = TimeSpan.FromMinutes(2),
            },
            ProjectRoots = projectRoots,
            UserRulesDirectory = rulesDirectory,
        };
        ScanResult result = await Engine(flags).ScanAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(result.Report.RuleErrors);
        Assert.Empty(result.Report.Failed);
        return result;
    }

    public void Dispose() => _fixture.Dispose();

    private static void Touch(string path, DateTime utc)
    {
        if (Directory.Exists(path))
        {
            Directory.SetCreationTimeUtc(path, utc);
            Directory.SetLastWriteTimeUtc(path, utc);
        }
        else
        {
            File.SetCreationTimeUtc(path, utc);
            File.SetLastWriteTimeUtc(path, utc);
        }
    }
}
