using System.Text.RegularExpressions;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Detectors;
using DevSweep.Core.Rules.Yaml;

namespace DevSweep.Core.Rules;

/// <summary>What a pack must be checked against: the detector kinds and root names this engine has.</summary>
/// <param name="Detectors">Detector kinds, which also read their own options.</param>
/// <param name="KnownRoot">Resolves a root name (<c>LocalAppData</c>, <c>npmCache</c>) to its registered spelling, or null.</param>
public sealed record RuleSchema(DetectorRegistry Detectors, Func<string, string?> KnownRoot);

/// <summary>One parsed pack: its rules and every problem found. A pack with any error is not used.</summary>
public sealed record RulePack(string Name, IReadOnlyList<Rule> Rules, IReadOnlyList<RuleError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reads a rule pack (<c>rules:</c> list, the Mac schema) and checks it: known
/// keys only, a known category, risk and detector kind, the detector's own
/// options, the required explanation fields, valid actions and named roots.
/// </summary>
public static partial class RulePackParser
{
    private static readonly Dictionary<string, Risk> Risks = new(StringComparer.Ordinal)
    {
        ["rebuilds"] = Risk.Rebuilds,
        ["oldVersion"] = Risk.OldVersion,
        ["leftover"] = Risk.Leftover,
        ["holdsData"] = Risk.HoldsData,
        ["needsAdmin"] = Risk.NeedsAdmin,
    };

    private static readonly Dictionary<string, CleanActionKind> ActionKinds = new(StringComparer.Ordinal)
    {
        ["delete"] = CleanActionKind.Delete,
        ["trash"] = CleanActionKind.Trash,
        ["command"] = CleanActionKind.Command,
        ["manual"] = CleanActionKind.Manual,
    };

    public static RulePack Parse(string text, string packName, RuleSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new RuleErrorSink(packName);
        YamlNode? root;
        try
        {
            root = YamlReader.Parse(text);
        }
        catch (RuleSyntaxException ex)
        {
            errors.Add(ex.Line, ex.Column, $"not valid YAML: {ex.Message}");
            return new RulePack(packName, [], errors.Errors);
        }

        if (root is not YamlMapping mapping)
        {
            errors.Add(root, "a rule pack is a mapping with a rules: list");
            return new RulePack(packName, [], errors.Errors);
        }

        var pack = new OptionReader(mapping, errors, schema.KnownRoot, "the pack");
        var rules = new List<Rule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OptionReader entry in pack.Mappings("rules", required: true))
        {
            if (ReadRule(entry, schema, packName) is { } rule)
            {
                if (ids.Add(rule.Id))
                {
                    rules.Add(rule);
                }
                else
                {
                    errors.RuleId = rule.Id;
                    errors.Add(entry.Node, "the id is used twice in this pack");
                }
            }

            errors.RuleId = null;
        }

        pack.Finish();
        return new RulePack(packName, rules, errors.Errors);
    }

    private static Rule? ReadRule(OptionReader rule, RuleSchema schema, string packName)
    {
        RuleErrorSink errors = rule.Errors;
        string? id = rule.Text("id", required: true);
        if (id is not null && !IdPattern().IsMatch(id))
        {
            errors.Add(rule.Node.Get("id"), $"id \"{id}\" should be lowercase letters, digits and dashes");
        }

        errors.RuleId = id;
        string? title = rule.Text("title", required: true);
        Category? category = ReadCategory(rule);
        Risk? risk = ReadEnum(rule, "risk", Risks);
        DetectorSpec? detector = ReadDetector(rule, schema);
        Explanation? explain = ReadExplanation(rule.Mapping("explain", required: true));
        IReadOnlyList<CleanAction> actions = ReadActions(rule, risk);
        IReadOnlyList<string> blockers = rule.TextList("blockers").Select(StripExe).ToList();
        int minSize = rule.Number("minSizeMB", 0, 1_000_000) ?? 1;
        string? arch = rule.Text("arch");
        if (arch is not (null or "x64" or "arm64"))
        {
            errors.Add(rule.Node.Get("arch"), "arch should be x64 or arm64");
        }

        rule.Finish();
        if (rule.Failed || id is null || title is null || category is null || risk is null || detector is null || explain is null)
        {
            return null;
        }

        return new Rule
        {
            Id = id,
            Title = title,
            Category = category.Value,
            Risk = risk.Value,
            Detector = detector,
            Explain = explain,
            Actions = actions,
            Blockers = blockers,
            MinSizeMB = minSize,
            Pack = packName,
        };
    }

    private static Category? ReadCategory(OptionReader rule)
    {
        string? name = rule.Text("category", required: true);
        if (name is null)
        {
            return null;
        }

        if (Categories.TryParse(name, out Category category) && name == category.YamlName())
        {
            return category;
        }

        rule.Errors.Add(rule.Node.Get("category"), $"unknown category \"{name}\" (known: {Categories.AllYamlNames})");
        return null;
    }

    private static T? ReadEnum<T>(OptionReader rule, string key, Dictionary<string, T> values)
        where T : struct
    {
        string? name = rule.Text(key, required: true);
        if (name is null)
        {
            return null;
        }

        if (values.TryGetValue(name, out T value))
        {
            return value;
        }

        rule.Errors.Add(rule.Node.Get(key), $"unknown {key} \"{name}\" (known: {string.Join(", ", values.Keys)})");
        return null;
    }

    private static DetectorSpec? ReadDetector(OptionReader rule, RuleSchema schema)
    {
        OptionReader? detector = rule.Mapping("detector", required: true);
        string? kind = detector?.Text("kind", required: true);
        if (detector is null || kind is null)
        {
            return null;
        }

        if (!schema.Detectors.TryGet(kind, out IDetector implementation))
        {
            rule.Errors.Add(detector.Node.Get("kind"), $"unknown detector kind \"{kind}\" (known: {string.Join(", ", schema.Detectors.Kinds.Order(StringComparer.Ordinal))})");
            return null;
        }

        var options = new OptionReader(detector.Node, rule.Errors, schema.KnownRoot, $"detector {kind}");
        options.Skip("kind");
        object? bound = implementation.Bind(options);
        return bound is null ? null : new DetectorSpec(kind, detector.Node, bound);
    }

    private static Explanation? ReadExplanation(OptionReader? explain)
    {
        if (explain is null)
        {
            return null;
        }

        string? what = explain.Text("what", required: true);
        string? why = explain.Text("why", required: true);
        string? ifDeleted = explain.Text("ifDeleted", required: true);
        var explanation = new Explanation(what ?? string.Empty, why ?? string.Empty, ifDeleted ?? string.Empty)
        {
            WontLose = explain.Text("wontLose"),
            Before = explain.Text("before"),
            Undo = explain.Text("undo"),
            Better = explain.Text("better"),
        };
        explain.Finish();
        return what is null || why is null || ifDeleted is null ? null : explanation;
    }

    private static List<CleanAction> ReadActions(OptionReader rule, Risk? risk)
    {
        var actions = new List<CleanAction>();
        foreach (OptionReader action in rule.Mappings("actions", required: true))
        {
            CleanActionKind? kind = ReadEnum(action, "kind", ActionKinds);
            string? label = action.Text("label");
            IReadOnlyList<string> command = action.TextList("command");
            string? detail = action.Text("detail");
            action.Finish();
            if (kind is null)
            {
                continue;
            }

            if (kind is CleanActionKind.Command or CleanActionKind.Manual && command.Count == 0)
            {
                action.Errors.Add(action.Node, $"a {kind.Value.ToString().ToLowerInvariant()} action needs a command list");
            }
            else if (kind is CleanActionKind.Delete && risk is not (null or Risk.Rebuilds))
            {
                action.Errors.Add(action.Node, "delete is only for risk: rebuilds (everything else goes to the Recycle Bin)");
            }

            actions.Add(new CleanAction(kind.Value, label, command.Count == 0 ? null : command, detail));
        }

        return actions;
    }

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
