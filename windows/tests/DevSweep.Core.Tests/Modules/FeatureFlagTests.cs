using DevSweep.Core.Detectors;
using DevSweep.Core.Inventory;
using DevSweep.Core.Modules;
using DevSweep.Core.Rules;
using DevSweep.Core.Tests.Rules;
using Xunit;

namespace DevSweep.Core.Tests.Modules;

public sealed class FeatureFlagTests
{
    [Fact]
    public void Flags_parse_on_and_off_and_compare_case_insensitively()
    {
        FeatureFlags flags = FeatureFlags.Default.With("-category:leftovers, +module:health -Detector:Paths");

        Assert.False(flags.IsEnabled("category:Leftovers"));
        Assert.False(flags.IsEnabled("detector:paths"));
        Assert.True(flags.IsEnabled("module:health", enabledByDefault: false));
        Assert.False(flags.IsEnabled("module:experiment", enabledByDefault: false));
        Assert.True(flags.IsEnabled("module:cleanup"));
        Assert.True(flags.With("+category:leftovers").IsEnabled("category:leftovers"));
    }

    [Fact]
    public void The_default_engine_registers_the_m2_modules()
    {
        DevSweepEngine engine = TestSchema.Engine();

        Assert.Equal(["locations", "inventory", "cleanup"], engine.Modules.Select(m => m.Id));
        Assert.Equal(["orphanedAppData", "paths", "staleProjectArtifacts", "versionedSiblings"], engine.Detectors.Kinds.Order(StringComparer.Ordinal));
        Assert.Equal(12, engine.InventoryCollectors.Count);
        Assert.Equal(["module:health"], engine.Disabled); // Health (M3b) is off until it passes its gate
    }

    [Fact]
    public void A_turned_off_detector_or_category_drops_its_rules_but_packs_stay_valid()
    {
        DevSweepEngine engine = TestSchema.Engine(FeatureFlags.Default.With("-detector:orphanedAppData -category:packageCaches -rule:temp-old-files"));

        RuleSet rules = engine.LoadRules(userDirectory: string.Empty);

        Assert.Empty(rules.Errors);
        Assert.DoesNotContain(rules.Rules, r => r.Detector.Kind == "orphanedAppData");
        Assert.DoesNotContain(rules.Rules, r => r.Category == Category.PackageCaches);
        Assert.DoesNotContain(rules.Rules, r => r.Id == "temp-old-files");
        Assert.Contains(rules.Rules, r => r.Id == "jetbrains-old");
        Assert.Contains("detector:orphanedAppData", engine.Disabled);
    }

    [Fact]
    public void A_turned_off_module_registers_nothing_and_a_turned_off_source_is_remembered()
    {
        DevSweepEngine engine = TestSchema.Engine(FeatureFlags.Default.With("-module:cleanup -inventory:Winget"));

        Assert.Empty(engine.Detectors.Kinds);
        Assert.Empty(engine.BundledPacks());
        Assert.Equal([InventorySource.Winget], engine.DisabledInventorySources);
        Assert.DoesNotContain(engine.InventoryCollectors, c => c.Source == InventorySource.Winget);
    }

    [Fact]
    public void Modules_can_add_detectors_locations_packs_and_services()
    {
        var builder = new EngineBuilder();
        builder.AddModule(new LocationsModule()).AddModule(new ExtraModule());
        DevSweepEngine engine = builder.Build(TestSchema.Engine().Environment, new Core.Inventory.Registry.InMemoryRegistry(), new FixedProcesses());

        Assert.True(engine.Detectors.TryGet("paths", out _));
        Assert.Equal(["hello"], engine.Services<string>());
        RuleSet rules = engine.LoadRules(userDirectory: string.Empty);
        Assert.Equal("extra-rule", Assert.Single(rules.Rules).Id);
    }

    private sealed class ExtraModule : IDevSweepModule
    {
        public string Id => "extra";

        public string Title => "Extra";

        public bool EnabledByDefault => true;

        public void Register(EngineBuilder builder)
            => builder.AddDetector(new PathsDetector())
                .AddService("hello")
                .AddRulePacks(() => [new RulePackText("extra.yml", TestSchema.Rule().Replace("thing-cache", "extra-rule", StringComparison.Ordinal), IsUser: false)]);
    }
}
