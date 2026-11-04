using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using DevSweep.Core.Rules;

namespace DevSweep.Core.Tests.Rules;

/// <summary>The default engine's schema against a machine that exists only in memory.</summary>
internal static class TestSchema
{
    public static readonly string Root = Path.Join(Path.GetTempPath(), "devsweep-unit");

    public static DevSweepEngine Engine(FeatureFlags? flags = null)
        => DevSweepEngine.CreateDefault(flags, FixedEnvironment.UnderRoot(Root, readRealFiles: false), new Core.Inventory.Registry.InMemoryRegistry(), new Core.Inventory.FixedProcesses());

    public static RuleSchema Schema() => Engine().CreateSchema();

    public static RulePack Parse(string yaml, string name = "test.yml") => RulePackParser.Parse(yaml, name, Schema());

    /// <summary>A valid rule with one part replaced.</summary>
    public static string Rule(string detector = "kind: paths\n      paths: ['{LocalAppData}\\Thing\\cache']", string explain = Explain, string extra = "", string risk = "rebuilds", string actions = "- kind: delete")
        => $"""
            rules:
              - id: thing-cache
                title: Thing cache
                category: packageCaches
                risk: {risk}
                {extra}
                detector:
                  {detector}
                actions:
                  {actions}
                explain:
                  {explain}
            """;

    public const string Explain = "what: \"Thing's cache.\"\n      why: \"{{size}} of it.\"\n      ifDeleted: \"It comes back.\"";
}
