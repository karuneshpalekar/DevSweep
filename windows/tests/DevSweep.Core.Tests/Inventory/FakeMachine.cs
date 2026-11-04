using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Inventory.Registry;
using DevSweep.Core.Locations;
using Microsoft.Win32;

namespace DevSweep.Core.Tests.Inventory;

/// <summary>Fake hives with one app per registry source, for inventory tests.</summary>
internal static class FakeMachine
{
    public const string Uninstall = UninstallCollector.KeyPath;
    public const string Packages = StorePackagesCollector.KeyPath;
    public const string AppPaths = AppPathsCollector.KeyPath;

    public static InMemoryRegistry Registry()
    {
        var registry = new InMemoryRegistry();

        // Every real HKLM has the Uninstall key in both views, and every user has packages.
        registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, Uninstall);
        registry.Set(RegistryHive.LocalMachine, RegistryView.Registry32, Uninstall);
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, Packages);

        registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, Uninstall + @"\{11111111-2222-3333-4444-555555555555}",
            ("DisplayName", "Contoso Studio 2024 (x64)"), ("Publisher", "Contoso Ltd"), ("DisplayVersion", "24.1.0"),
            ("WindowsInstaller", 1), ("InstallLocation", @"C:\Program Files\Contoso\Studio\"), ("DisplayIcon", "\"C:\\Program Files\\Contoso\\Studio\\studio.exe\",0"));
        registry.Set(RegistryHive.LocalMachine, RegistryView.Registry32, Uninstall + @"\Fabrikam Tool_is1",
            ("DisplayName", "Fabrikam Tool 3.2"), ("Publisher", "Fabrikam"), ("UninstallString", "\"C:\\Program Files (x86)\\Fabrikam\\unins000.exe\""));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, Uninstall + @"\Postman",
            ("DisplayName", "Postman x64 11.2.0"), ("Publisher", "Postman"), ("InstallLocation", @"C:\Users\Dev\AppData\Local\Postman"));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, Uninstall + @"\NoName", ("Publisher", "nobody"));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, Uninstall + @"\WingetPortable",
            ("DisplayName", "ripgrep"), ("WinGetPackageIdentifier", "BurntSushi.ripgrep.MSVC"));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, Packages + @"\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe",
            ("DisplayName", "@{Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe?ms-resource://Microsoft.WindowsTerminal/Resources/AppName}"),
            ("PackageRootFolder", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe"));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, Packages + @"\NotAPackageName");
        registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, AppPaths + @"\litware.exe", (string.Empty, @"C:\Tools\Litware\litware.exe"));
        registry.Set(RegistryHive.CurrentUser, RegistryView.Registry64, AppPaths + @"\gone.exe", (string.Empty, @"C:\Gone\gone.exe"));
        return registry;
    }

    /// <summary>The registry sources plus App Paths (with only litware.exe existing) and running processes.</summary>
    public static AppInventory Inventory(InMemoryRegistry? registry = null, IReadOnlyList<InventorySource>? disabled = null, params string[] processes)
    {
        var environment = FixedEnvironment.UnderRoot(Path.Join(Path.GetTempPath(), "devsweep-inventory-unit"), readRealFiles: false);
        var context = new InventoryContext(registry ?? Registry(), new LocationRegistry(environment, BuiltInLocations.All()), new FixedProcesses(processes));
        List<IInventoryCollector> collectors = [.. UninstallCollector.All(), new StorePackagesCollector(),
            new AppPathsCollector(path => path.EndsWith("litware.exe", StringComparison.Ordinal)), new RunningProcessesCollector()];
        return InventoryBuilder.Build(collectors, context, disabled);
    }
}
