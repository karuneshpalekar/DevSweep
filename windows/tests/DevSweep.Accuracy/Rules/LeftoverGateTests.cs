using DevSweep.Core.Cleaning;
using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using DevSweep.Fixtures;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Accuracy.Rules;

/// <summary>
/// The no-false-leftover gate (PLAN 6): apps "installed" through every inventory
/// source, each with data in AppData and ProgramData, must never be called leftovers;
/// an app no source lists must be, with every source named; and when a source cannot
/// be read, nothing is called a leftover.
/// </summary>
public sealed class LeftoverGateTests(ITestOutputHelper output) : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly MachineFixture _machine = new("gate");

    public void Dispose() => _machine.Dispose();

    [Fact]
    public async Task Apps_installed_through_any_source_are_never_leftovers_and_a_gone_app_is()
    {
        InstallEverywhere();

        ScanResult result = await _machine.ScanAsync([Category.Leftovers], userPack: Pack());
        foreach (SourceReport source in result.Report.Inventory)
        {
            output.WriteLine($"{source.Title}: {source.Count} ({(source.Complete ? "complete" : string.Join("; ", source.Problems))})");
        }

        foreach (Finding finding in result.Findings)
        {
            output.WriteLine($"FINDING {finding.Title}: {string.Join(", ", finding.Paths)}");
        }

        Assert.All(result.Report.Inventory, s => Assert.True(s.Complete, s.Title));
        Finding lima = Assert.Single(result.Findings);
        Assert.Equal("Lima", lima.Title);
        Assert.Equal(
            new[] { Path.Join(_machine.Local, "Lima"), Path.Join(_machine.ProgramData, "Lima"), Path.Join(_machine.Roaming, "Lima") }.Order(StringComparer.OrdinalIgnoreCase),
            lima.Paths.Order(StringComparer.OrdinalIgnoreCase));
        foreach (InventorySource source in Enum.GetValues<InventorySource>())
        {
            Assert.Contains(lima.Checks, c => c.Text.StartsWith($"Not in {InventorySources.Title(source)} (", StringComparison.Ordinal));
        }

    }

    [Theory]
    [InlineData("Alpha", InventorySource.UninstallMachine64)]
    [InlineData("Bravo", InventorySource.UninstallMachine32)]
    [InlineData("Charlie", InventorySource.UninstallUser64)]
    [InlineData("Delta", InventorySource.StorePackages)]
    [InlineData("Echo", InventorySource.Scoop)]
    [InlineData("Foxtrot", InventorySource.Chocolatey)]
    [InlineData("Golf", InventorySource.Winget)]
    [InlineData("Hotel", InventorySource.AppPaths)]
    [InlineData("India", InventorySource.StartMenu)]
    [InlineData("Juliett", InventorySource.ProgramFiles)]
    [InlineData("Kilo", InventorySource.RunningProcesses)]
    public void Each_app_is_found_by_its_own_source(string app, InventorySource source)
    {
        InstallEverywhere();
        AppInventory inventory = _machine.Engine().BuildInventory(cancellationToken: TestContext.Current.CancellationToken);
        AppHint hint = Hints()[app];

        InstallMatch match = inventory.IsInstalled(hint);

        output.WriteLine($"{app}: {match.State}; {string.Join("; ", match.Evidence.Select(e => $"{e.Source} {e.MatchedBy}"))}");
        Assert.Equal(InstallState.Installed, match.State);
        Assert.Contains(match.Evidence, e => e.Source == source);
    }

    [Fact]
    public async Task A_folder_holding_an_installed_program_is_never_a_leftover()
    {
        InstallEverywhere();
        string mikeApp = Path.Join(_machine.Roaming, "Mike", "app");
        MachineFixture.Allocate(Path.Join(_machine.Local, "Mike", "cache.dat"), 2 * Mb);
        MachineFixture.Write(Path.Join(mikeApp, "mike.dll"), "x");
        _machine.Registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallCollector.KeyPath + @"\SomethingElse",
            ("DisplayName", "Something Else Entirely"), ("InstallLocation", mikeApp));

        ScanResult result = await _machine.ScanAsync([Category.Leftovers], userPack: Pack());

        Assert.DoesNotContain(result.Findings, f => f.Title == "Mike");
        Assert.Single(result.Findings, f => f.Title == "Lima");
        Assert.Contains(result.Report.Checks.Single(c => c.Id == "gate-leftovers").Notes,
            n => n.StartsWith("Mike: left alone", StringComparison.Ordinal) && n.Contains("Something Else Entirely", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_any_source_cannot_be_read_nothing_is_a_leftover()
    {
        InstallEverywhere();
        _machine.Registry.Deny(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallCollector.KeyPath);

        ScanResult result = await _machine.ScanAsync([Category.Leftovers], userPack: Pack());

        Assert.Empty(result.Findings);
        Assert.Contains(result.Report.Checks.Single(c => c.Id == "gate-leftovers").Notes,
            n => n.StartsWith("Lima: leftovers not checked because", StringComparison.Ordinal) && n.Contains("32-bit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_a_source_is_turned_off_nothing_is_a_leftover()
    {
        InstallEverywhere();

        ScanResult result = await _machine.ScanAsync([Category.Leftovers], userPack: Pack(), flags: "-inventory:StartMenu");

        Assert.Empty(result.Findings);
        Assert.Contains("inventory:StartMenu", result.Report.Disabled);
    }

    /// <summary>One app per source, plus Lima (installed nowhere); each leaves data in three places.</summary>
    private void InstallEverywhere()
    {
        foreach (string app in (string[])["Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliett", "Kilo", "Lima"])
        {
            MachineFixture.Allocate(Path.Join(_machine.Roaming, app, "settings.dat"), 2 * Mb);
            MachineFixture.Allocate(Path.Join(_machine.Local, app, "cache.dat"), 2 * Mb);
            MachineFixture.Allocate(Path.Join(_machine.ProgramData, app, "shared.dat"), 2 * Mb);
        }

        string uninstall = UninstallCollector.KeyPath;
        _machine.Registry
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, uninstall + @"\{A1A1A1A1-0000-0000-0000-000000000001}",
                ("DisplayName", "Alpha Suite Enterprise"), ("WindowsInstaller", 1))
            .Set(RegistryHive.LocalMachine, RegistryView.Registry32, uninstall + @"\Bravo_is1", ("DisplayName", "Bravo 2.1 (32-bit)"), ("Publisher", "Bravo Inc"))
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, uninstall + @"\charlie", ("DisplayName", "Charlie"))
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, StorePackagesCollector.KeyPath + @"\Contoso.Delta_3.0.0.0_x64__abcdefghjkmnp",
                ("DisplayName", "@{Contoso.Delta?ms-resource://x}"));

        Directory.CreateDirectory(Path.Join(_machine.Profile, "scoop", "apps", "echo", "1.0"));
        MachineFixture.Write(Path.Join(_machine.ProgramData, "chocolatey", "lib", "foxtrot", "foxtrot.nuspec"),
            "<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd\"><metadata><id>foxtrot</id><version>1.0</version><title>Foxtrot Tool</title><authors>F</authors></metadata></package>");
        Directory.CreateDirectory(Path.Join(_machine.Local, "Microsoft", "WinGet", "Packages", "Golf.Golf_Microsoft.Winget.Source_8wekyb3d8bbwe"));

        string hotel = MachineFixture.Write(Path.Join(_machine.Root, "Tools", "HotelApp", "hotel.exe"), "MZ");
        _machine.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, AppPathsCollector.KeyPath + @"\hotel.exe", (string.Empty, hotel));

        string india = MachineFixture.Write(Path.Join(_machine.Root, "Tools", "IndiaApp", "india.exe"), "MZ");
        string shortcut = Path.Join(_machine.Environment.Folders["Programs"], "India", "India.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        CreateShortcut(shortcut, india);

        MachineFixture.Write(Path.Join(_machine.Environment.Folders["ProgramFiles"], "Juliett", "juliett.dll"), "x");
        _machine.Processes.Add("kilo");
    }

    private static void CreateShortcut(string path, string target)
    {
        string script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{path}'); $s.TargetPath = '{target}'; $s.Save()";
        (int exit, string text) = Ntfs.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"");
        Assert.True(exit == 0 && File.Exists(path), $"Creating the shortcut failed ({exit}): {text}");
        Assert.Equal(target, ShellLink.Read(path)?.Target, ignoreCase: true);
    }

    private static Dictionary<string, AppHint> Hints() => new()
    {
        ["Alpha"] = new AppHint { Name = "Alpha", ProductCodes = ["{A1A1A1A1-0000-0000-0000-000000000001}"] },
        ["Bravo"] = new AppHint { Name = "Bravo", Names = ["Bravo"] },
        ["Charlie"] = new AppHint { Name = "Charlie", Names = ["Charlie"] },
        ["Delta"] = new AppHint { Name = "Delta", PackageFamilies = ["Contoso.Delta"] },
        ["Echo"] = new AppHint { Name = "Echo", PackageIds = ["echo"] },
        ["Foxtrot"] = new AppHint { Name = "Foxtrot", Names = ["Foxtrot Tool"] },
        ["Golf"] = new AppHint { Name = "Golf", PackageIds = ["Golf.Golf"] },
        ["Hotel"] = new AppHint { Name = "Hotel", Exes = ["hotel.exe"] },
        ["India"] = new AppHint { Name = "India", Exes = ["india.exe"] },
        ["Juliett"] = new AppHint { Name = "Juliett", Folders = ["Juliett"] },
        ["Kilo"] = new AppHint { Name = "Kilo", Exes = ["kilo.exe"] },
    };

    /// <summary>A leftovers rule that knows every fixture app by exactly one hint.</summary>
    private static string Pack()
    {
        var apps = new List<string>
        {
            App("Alpha", "productCodes: [\"{A1A1A1A1-0000-0000-0000-000000000001}\"]"),
            App("Bravo", "names: [\"Bravo\"]"),
            App("Charlie", "names: [\"Charlie\"]"),
            App("Delta", "packageFamilies: [\"Contoso.Delta\"]"),
            App("Echo", "packageIds: [\"echo\"]"),
            App("Foxtrot", "names: [\"Foxtrot Tool\"]"),
            App("Golf", "packageIds: [\"Golf.Golf\"]"),
            App("Hotel", "exes: [\"hotel.exe\"]"),
            App("India", "exes: [\"india.exe\"]"),
            App("Juliett", "folders: [\"Juliett\"]"),
            App("Kilo", "exes: [\"kilo.exe\"]"),
            App("Lima", "names: [\"Lima\"]"),
            App("Mike", "names: [\"Mike\"]"),
        };
        return $$$"""
            rules:
              - id: gate-leftovers
                title: Leftovers
                category: leftovers
                risk: leftover
                detector:
                  kind: orphanedAppData
                  knownApps:
            {{{string.Join("\n", apps)}}}
                actions:
                  - kind: trash
                explain:
                  what: "{{app}} data"
                  why: "{{size}}"
                  ifDeleted: "Nothing."
            """;
    }

    private static string App(string name, string hint)
        => $"        - name: {name}\n          {hint}\n          paths: ['{{RoamingAppData}}\\{name}', '{{LocalAppData}}\\{name}', '{{ProgramData}}\\{name}']";
}
