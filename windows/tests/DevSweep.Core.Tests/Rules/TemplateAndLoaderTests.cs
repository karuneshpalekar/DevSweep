using DevSweep.Core.Modules;
using DevSweep.Core.Rules;
using DevSweep.Core.Rules.Yaml;
using Xunit;

namespace DevSweep.Core.Tests.Rules;

public sealed class TemplateAndLoaderTests
{
    private static readonly Dictionary<string, string> Facts = new() { ["size"] = "1.2 GB", ["app"] = "Postman" };

    [Theory]
    [InlineData("{{size}} of {{app}}", "1.2 GB of Postman")]
    [InlineData("{{ size }}!", "1.2 GB!")]
    [InlineData("{{missing}}x", "x")]
    [InlineData("no placeholders", "no placeholders")]
    [InlineData("open {{size and more", "open {{size and more")]
    [InlineData("{{size}}{{app}}", "1.2 GBPostman")]
    [InlineData("}} {{app}} {{", "}} Postman {{")]
    public void Templates_render_like_the_mac_app(string text, string expected)
        => Assert.Equal(expected, Template.Render(text, Facts));

    [Fact]
    public void Explanations_render_every_field()
    {
        var explanation = new Explanation("{{app}}", "{{size}}", "gone") { WontLose = "{{app}} data", Undo = "{{x}}" };

        Explanation rendered = explanation.Rendered(Facts);

        Assert.Equal("Postman", rendered.What);
        Assert.Equal("1.2 GB", rendered.Why);
        Assert.Equal("Postman data", rendered.WontLose);
        Assert.Equal(string.Empty, rendered.Undo);
        Assert.Null(rendered.Before);
    }

    [Fact]
    public void Yaml_anchors_and_positions_are_kept()
    {
        YamlNode? root = YamlReader.Parse("a: &list [x, \"y\"]\nb: *list\nc: ~\n");

        var map = Assert.IsType<YamlMapping>(root);
        var b = Assert.IsType<YamlSequence>(map.Get("b"));
        Assert.Equal(["x", "y"], b.Items.Cast<YamlScalar>().Select(s => s.Value));
        Assert.True(((YamlScalar)map.Get("c")!).IsNull);
        Assert.False(((YamlScalar)b.Items[1]).IsPlain);
        Assert.Equal(2, map.Entries[1].Key.Line);
        Assert.Equal(1, map.Get("b")!.Line);
    }

    [Fact]
    public void Yaml_duplicate_keys_are_an_error()
    {
        RuleSyntaxException error = Assert.Throws<RuleSyntaxException>(() => YamlReader.Parse("a: 1\na: 2\n"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void A_user_pack_overrides_a_bundled_rule_by_id_in_place_and_a_bad_pack_is_skipped()
    {
        DevSweepEngine engine = TestSchema.Engine();
        var loader = new RuleLoader(engine.CreateSchema());
        string bundled = TestSchema.Rule() + "\n" + TestSchema.Rule()["rules:".Length..].Replace("thing-cache", "other-cache", StringComparison.Ordinal);
        string user = TestSchema.Rule().Replace("Thing cache", "My thing cache", StringComparison.Ordinal);
        string broken = TestSchema.Rule(detector: "kind: nope");

        RuleSet set = loader.Load(
        [
            new RulePackText("10-bundled.yml", bundled, IsUser: false),
            new RulePackText("broken.yml", broken, IsUser: true),
            new RulePackText("mine.yml", user, IsUser: true),
        ]);

        Assert.Equal(["thing-cache", "other-cache"], set.Rules.Select(r => r.Id));
        Assert.Equal("My thing cache", set.Rules[0].Title);
        Assert.Equal("mine.yml", set.Rules[0].Pack);
        Assert.Contains(set.Errors, e => e.Pack == "broken.yml");
        PackSummary skipped = Assert.Single(set.Packs, p => p.Name == "broken.yml");
        Assert.False(skipped.Loaded);
        Assert.Equal(["thing-cache"], Assert.Single(set.Packs, p => p.Name == "mine.yml").Overrides);
    }

    [Fact]
    public void Display_title_of_a_templated_title_is_the_id_in_words()
    {
        RulePack pack = TestSchema.Parse(TestSchema.Rule().Replace("title: Thing cache", "title: \"{{key}} cache\"", StringComparison.Ordinal));

        Assert.Equal("Thing cache", pack.Rules[0].DisplayTitle);
    }
}
