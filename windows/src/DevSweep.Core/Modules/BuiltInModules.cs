using DevSweep.Core.Detectors;
using DevSweep.Core.Detectors.Projects;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Modules;

/// <summary>The modules M2 ships, in registration order.</summary>
public static class BuiltInModules
{
    public static IReadOnlyList<IDevSweepModule> All { get; } = [new LocationsModule(), new InventoryModule(), new CleanUpModule(), new Packs.PacksModule()];
}

/// <summary>The named roots: Known Folders, <c>{Temp}</c> and every tool location (<c>module:locations</c>).</summary>
public sealed class LocationsModule : IDevSweepModule
{
    public string Id => "locations";

    public string Title => "Locations";

    public bool EnabledByDefault => true;

    public void Register(EngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (ILocationResolver resolver in BuiltInLocations.All())
        {
            builder.AddLocation(resolver);
        }
    }
}

/// <summary>The installed-apps inventory sources of PLAN 4.3 (<c>module:inventory</c>).</summary>
public sealed class InventoryModule : IDevSweepModule
{
    public string Id => "inventory";

    public string Title => "Installed apps";

    public bool EnabledByDefault => true;

    public void Register(EngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (UninstallCollector collector in UninstallCollector.All())
        {
            builder.AddInventoryCollector(collector);
        }

        builder.AddInventoryCollector(new StorePackagesCollector())
            .AddInventoryCollector(new ScoopCollector())
            .AddInventoryCollector(new ChocolateyCollector())
            .AddInventoryCollector(new WingetCollector())
            .AddInventoryCollector(new AppPathsCollector())
            .AddInventoryCollector(new StartMenuCollector())
            .AddInventoryCollector(new ProgramFilesCollector())
            .AddInventoryCollector(new RunningProcessesCollector());
    }
}

/// <summary>Clean up: the four M2 detector kinds and the bundled Windows rule packs (<c>module:cleanup</c>).</summary>
public sealed class CleanUpModule : IDevSweepModule
{
    public string Id => "cleanup";

    public string Title => "Clean up";

    public bool EnabledByDefault => true;

    public void Register(EngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDetector(new PathsDetector())
            .AddDetector(new VersionedSiblingsDetector())
            .AddDetector(new StaleProjectArtifactsDetector())
            .AddDetector(new OrphanedAppDataDetector())
            .AddRulePacks(() => EmbeddedRulePacks.From(typeof(CleanUpModule).Assembly));
    }
}
