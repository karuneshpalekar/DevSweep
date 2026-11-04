using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Rules;
using Xunit;

namespace DevSweep.Core.Tests.Rules;

public sealed class RulePackParserTests
{
    [Fact]
    public void A_valid_rule_is_read_with_every_field()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(extra: "minSizeMB: 25\n    blockers: [\"Thing.exe\"]",
            actions: "- kind: command\n        label: Clean\n        command: [\"thing\", \"clean\"]\n        detail: \"Runs thing clean.\"\n      - kind: delete"));

        Assert.Empty(pack.Errors);
        Rule rule = Assert.Single(pack.Rules);
        Assert.Equal("thing-cache", rule.Id);
        Assert.Equal(Category.PackageCaches, rule.Category);
        Assert.Equal(Risk.Rebuilds, rule.Risk);
        Assert.Equal(25, rule.MinSizeMB);
        Assert.Equal(["Thing"], rule.Blockers);
        Assert.Equal("paths", rule.Detector.Kind);
        var options = Assert.IsType<PathsDetector.Options>(rule.Detector.Options);
        Assert.Equal("LocalAppData", Assert.Single(options.Paths).Root);
        Assert.Equal(["Thing", "cache"], options.Paths[0].Segments);
        Assert.Equal(CleanActionKind.Command, rule.Actions[0].Kind);
        Assert.Equal(["thing", "clean"], rule.Actions[0].Command);
        Assert.Equal("{{size}} of it.", rule.Explain.Why);
        Assert.Equal("test.yml", rule.Pack);
    }

    [Fact]
    public void Unknown_detector_kind_names_the_known_ones_and_the_line()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: "kind: pathz\n      paths: ['{LocalAppData}\\x']"));

        RuleError error = Assert.Single(pack.Errors);
        Assert.Contains("unknown detector kind \"pathz\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("orphanedAppData", error.Message, StringComparison.Ordinal);
        Assert.Equal("thing-cache", error.RuleId);
        Assert.Equal(8, error.Line);
        Assert.Empty(pack.Rules);
    }

    [Fact]
    public void Missing_explain_fields_are_each_reported()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(explain: "what: \"Only this.\""));

        Assert.False(pack.IsValid);
        Assert.Contains(pack.Errors, e => e.Message.Contains("missing why", StringComparison.Ordinal));
        Assert.Contains(pack.Errors, e => e.Message.Contains("missing ifDeleted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("'{Nowhere}\\x'", "unknown root {Nowhere}")]
    [InlineData("'C:\\Users\\Dev\\x'", "must start with a named root")]
    [InlineData("'{LocalAppData}\\..\\x'", "is not allowed in a rule path")]
    [InlineData("'{LocalAppData}\\**\\x'", "is not allowed in a rule path")]
    [InlineData("'{LocalAppData}\\[ab'", "without a matching ]")]
    [InlineData("'{{LocalAppData}}\\x'", "for explanations")]
    public void Bad_paths_are_rejected(string path, string expected)
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: $"kind: paths\n      paths: [{path}]"));

        Assert.Contains(pack.Errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Root_names_are_case_insensitive_and_tilde_is_the_profile()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: "kind: paths\n      paths: ['{localappdata}\\a', '~\\.npm\\b', '{NPMCACHE}\\_cacache']"));

        Assert.Empty(pack.Errors);
        var options = (PathsDetector.Options)pack.Rules[0].Detector.Options;
        Assert.Equal(["LocalAppData", "Profile", "npmCache"], options.Paths.Select(p => p.Root));
    }

    [Theory]
    [InlineData("pathz: 1", "unknown key \"pathz\" in detector paths")]
    [InlineData("olderThanDays: soon", "olderThanDays should be a whole number")]
    [InlineData("groupEach: maybe", "groupEach should be true or false")]
    public void Unknown_and_mistyped_detector_options_are_reported(string option, string expected)
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: $"kind: paths\n      paths: ['{{Temp}}\\x']\n      {option}"));

        Assert.Contains(pack.Errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_top_level_keys_categories_and_risks_are_reported()
    {
        string yaml = TestSchema.Rule(extra: "colour: blue").Replace("packageCaches", "packageCache", StringComparison.Ordinal).Replace("risk: rebuilds", "risk: harmless", StringComparison.Ordinal);

        RulePack pack = TestSchema.Parse(yaml);

        Assert.Contains(pack.Errors, e => e.Message.Contains("unknown key \"colour\"", StringComparison.Ordinal));
        Assert.Contains(pack.Errors, e => e.Message.Contains("unknown category \"packageCache\"", StringComparison.Ordinal));
        Assert.Contains(pack.Errors, e => e.Message.Contains("unknown risk \"harmless\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_is_only_allowed_for_rebuilds_and_commands_need_a_command()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(risk: "leftover", actions: "- kind: delete\n      - kind: command"));

        Assert.Contains(pack.Errors, e => e.Message.Contains("delete is only for risk: rebuilds", StringComparison.Ordinal));
        Assert.Contains(pack.Errors, e => e.Message.Contains("needs a command list", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pack_without_rules_or_with_a_duplicate_id_is_invalid()
    {
        Assert.Contains(TestSchema.Parse("other: 1").Errors, e => e.Message.Contains("missing rules", StringComparison.Ordinal));
        string rule = TestSchema.Rule();
        string twice = rule + rule["rules:".Length..];
        Assert.Contains(TestSchema.Parse(twice).Errors, e => e.Message.Contains("used twice", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_yaml_reports_the_position()
    {
        RulePack pack = TestSchema.Parse("rules:\n  - id: [unclosed\n");

        RuleError error = Assert.Single(pack.Errors);
        Assert.Contains("not valid YAML", error.Message, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
    }

    [Fact]
    public void Versioned_siblings_needs_a_version_group()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: "kind: versionedSiblings\n      parents: ['{RoamingAppData}\\JetBrains']\n      pattern: \"^(?<key>[A-Za-z]+)$\""));

        Assert.Contains(pack.Errors, e => e.Message.Contains("(?<version>", StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_projects_only_accepts_known_artifact_folders()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: "kind: staleProjectArtifacts\n      roots: ['~\\src']\n      names: [\"node_modules\", \"src\"]"));

        Assert.Contains(pack.Errors, e => e.Message.Contains("\"src\" is not a known artifact folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Leftover_paths_must_be_in_app_data()
    {
        string detector = "kind: orphanedAppData\n      knownApps:\n        - name: Thing\n          paths: ['{Downloads}\\Thing', '{RoamingAppData}']";
        RulePack pack = TestSchema.Parse(TestSchema.Rule(detector: detector, risk: "leftover", actions: "- kind: trash"));

        Assert.Equal(2, pack.Errors.Count(e => e.Message.Contains("leftover paths must be inside AppData", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_bundled_pack_is_valid()
    {
        var engine = TestSchema.Engine();
        var loader = new RuleLoader(engine.CreateSchema());
        var packs = engine.BundledPacks();

        Assert.NotEmpty(packs);
        foreach (RulePackText text in packs)
        {
            RulePack pack = loader.Parse(text);
            Assert.True(pack.IsValid, $"{text.Name}: {string.Join("; ", pack.Errors)}");
        }

        RuleSet set = engine.LoadRules(userDirectory: string.Empty);
        Assert.Equal(
            ["npm-cache", "pip-cache", "gradle-caches", "temp-old-files", "nuget-http-cache", "jetbrains-old", "stale-project-artifacts", "orphaned-app-data"],
            set.Rules.Select(r => r.Id));
    }
}
