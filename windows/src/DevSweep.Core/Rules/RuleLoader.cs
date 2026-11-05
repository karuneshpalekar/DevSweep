using System.Reflection;
using System.Text;

namespace DevSweep.Core.Rules;

/// <summary>The text of one pack, bundled or from the user's folder.</summary>
public sealed record RulePackText(string Name, string Text, bool IsUser);

/// <summary>What was loaded from one pack.</summary>
/// <param name="Name">File or resource name.</param>
/// <param name="IsUser">From the user's rules folder.</param>
/// <param name="Loaded">False when the pack had errors and was skipped.</param>
/// <param name="Rules">Rules it contributed.</param>
/// <param name="Overrides">Ids of earlier rules it replaced.</param>
public sealed record PackSummary(string Name, bool IsUser, bool Loaded, int Rules, IReadOnlyList<string> Overrides);

/// <summary>Every rule in effect, in order (a path belongs to the first rule that claims it), and every load problem.</summary>
public sealed record RuleSet(IReadOnlyList<Rule> Rules, IReadOnlyList<RuleError> Errors, IReadOnlyList<PackSummary> Packs);

/// <summary>Packs embedded in an assembly as <c>DevSweep.Rules.&lt;file&gt;.yml</c>.</summary>
public static class EmbeddedRulePacks
{
    public const string Prefix = "DevSweep.Rules.";

    public static IReadOnlyList<RulePackText> From(Assembly assembly) => From(assembly, Prefix);

    /// <summary>Packs embedded as <c>&lt;prefix&gt;&lt;file&gt;.yml</c> (a module that keeps its packs apart from Clean up's).</summary>
    public static IReadOnlyList<RulePackText> From(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(prefix);
        var packs = new List<RulePackText>();
        foreach (string name in assembly.GetManifestResourceNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || !(name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            packs.Add(new RulePackText(name[prefix.Length..], reader.ReadToEnd(), IsUser: false));
        }

        return packs;
    }
}

/// <summary>
/// Loads the bundled packs, then the user's (<c>%LOCALAPPDATA%\DevSweep\Rules\*.yml</c>),
/// each in file-name order. A user rule with a bundled rule's id replaces it in place.
/// A pack with any error is reported and skipped; the others still load.
/// </summary>
public sealed class RuleLoader(RuleSchema schema)
{
    public RuleSet Load(IEnumerable<RulePackText> bundled, string? userDirectory)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        var errors = new List<RuleError>();
        IEnumerable<RulePackText> packs = bundled;
        if (userDirectory is not null)
        {
            packs = packs.Concat(ReadDirectory(userDirectory, errors));
        }

        return Load(packs, errors);
    }

    /// <summary>Loads packs given as text, in order (later packs override earlier rules by id).</summary>
    public RuleSet Load(IEnumerable<RulePackText> packs) => Load(packs, []);

    /// <summary>Parses one file (for <c>devsweep rules validate</c>).</summary>
    public RulePack ParseFile(string path)
    {
        string name = Path.GetFileName(path);
        try
        {
            return RulePackParser.Parse(File.ReadAllText(path), name, schema);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RulePack(name, [], [new RuleError(name, null, 0, 0, $"could not be read: {ex.Message}")]);
        }
    }

    public RulePack Parse(RulePackText pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return RulePackParser.Parse(pack.Text, pack.Name, schema);
    }

    private RuleSet Load(IEnumerable<RulePackText> packs, List<RuleError> errors)
    {
        var rules = new List<Rule>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<PackSummary>();
        foreach (RulePackText text in packs)
        {
            RulePack pack = Parse(text);
            if (!pack.IsValid)
            {
                errors.AddRange(pack.Errors);
                summaries.Add(new PackSummary(text.Name, text.IsUser, Loaded: false, 0, []));
                continue;
            }

            var overrides = new List<string>();
            foreach (Rule rule in pack.Rules)
            {
                if (index.TryGetValue(rule.Id, out int existing))
                {
                    rules[existing] = rule;
                    overrides.Add(rule.Id);
                }
                else
                {
                    index[rule.Id] = rules.Count;
                    rules.Add(rule);
                }
            }

            summaries.Add(new PackSummary(text.Name, text.IsUser, Loaded: true, pack.Rules.Count, overrides));
        }

        return new RuleSet(rules, errors, summaries);
    }

    private static List<RulePackText> ReadDirectory(string directory, List<RuleError> errors)
    {
        var packs = new List<RulePackText>();
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return packs;
            }

            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new RuleError(directory, null, 0, 0, $"the rules folder could not be read: {ex.Message}"));
            return packs;
        }

        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                packs.Add(new RulePackText(Path.GetFileName(file), File.ReadAllText(file), IsUser: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new RuleError(Path.GetFileName(file), null, 0, 0, $"could not be read: {ex.Message}"));
            }
        }

        return packs;
    }
}
