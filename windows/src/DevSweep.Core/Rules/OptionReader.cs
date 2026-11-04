using System.Globalization;
using System.Text.RegularExpressions;
using DevSweep.Core.Locations;
using DevSweep.Core.Rules.Yaml;

namespace DevSweep.Core.Rules;

/// <summary>
/// Reads typed values out of one YAML mapping (a rule, its detector, a known
/// app), reporting every wrong or missing value to the sink with its position.
/// Call <see cref="Finish"/> last: any key nothing asked for is reported as
/// unknown, so a typo never silently changes what a rule does.
/// </summary>
public sealed class OptionReader
{
    private readonly YamlMapping _node;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly int _errorsBefore;

    public OptionReader(YamlMapping node, RuleErrorSink errors, Func<string, string?> knownRoot, string what)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        Errors = errors ?? throw new ArgumentNullException(nameof(errors));
        KnownRoot = knownRoot ?? throw new ArgumentNullException(nameof(knownRoot));
        What = what;
        _errorsBefore = errors.Errors.Count;
    }

    public RuleErrorSink Errors { get; }

    /// <summary>Resolves a root name to its registered spelling, or null when unknown.</summary>
    public Func<string, string?> KnownRoot { get; }

    /// <summary>What is being read, for messages ("detector paths", "rule npm-cache").</summary>
    public string What { get; }

    public YamlMapping Node => _node;

    /// <summary>True when this reader (or anything read through it) reported an error.</summary>
    public bool Failed => Errors.Errors.Count > _errorsBefore;

    public bool Has(string key) => _node.Get(key) is { } value && value is not YamlScalar { IsNull: true };

    public string? Text(string key, bool required = false)
    {
        YamlNode? value = Take(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is YamlScalar { IsNull: false } scalar && scalar.Value.Trim().Length > 0)
        {
            return scalar.Value;
        }

        Errors.Add(value, $"{key} should be text, not {value.Describe()}");
        return null;
    }

    public IReadOnlyList<string> TextList(string key, bool required = false)
    {
        YamlNode? value = Take(key, required);
        if (value is null)
        {
            return [];
        }

        if (value is not YamlSequence list)
        {
            Errors.Add(value, $"{key} should be a list, like [\"a\", \"b\"]");
            return [];
        }

        var items = new List<string>(list.Items.Count);
        foreach (YamlNode item in list.Items)
        {
            if (item is YamlScalar { IsNull: false } scalar && scalar.Value.Trim().Length > 0)
            {
                items.Add(scalar.Value);
            }
            else
            {
                Errors.Add(item, $"every item of {key} should be text, not {item.Describe()}");
            }
        }

        if (required && items.Count == 0 && !Failed)
        {
            Errors.Add(value, $"{key} should not be empty");
        }

        return items;
    }

    public int? Number(string key, int min, int max, bool required = false)
    {
        YamlNode? value = Take(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is YamlScalar { IsPlain: true } scalar
            && int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number >= min && number <= max)
        {
            return number;
        }

        Errors.Add(value, string.Create(CultureInfo.InvariantCulture, $"{key} should be a whole number from {min} to {max}, not {value.Describe()}"));
        return null;
    }

    public bool Flag(string key, bool fallback = false)
    {
        YamlNode? value = Take(key, required: false);
        if (value is null)
        {
            return fallback;
        }

        if (value is YamlScalar { IsPlain: true } scalar)
        {
            if (scalar.Value is "true" or "True" or "TRUE")
            {
                return true;
            }

            if (scalar.Value is "false" or "False" or "FALSE")
            {
                return false;
            }
        }

        Errors.Add(value, $"{key} should be true or false, not {value.Describe()}");
        return fallback;
    }

    /// <summary>Path templates (<c>{Root}\...</c> or <c>~\...</c>), each checked for a known root and allowed segments.</summary>
    public IReadOnlyList<PathTemplate> Paths(string key, bool required = false)
    {
        YamlNode? node = _node.Get(key);
        var templates = new List<PathTemplate>();
        IReadOnlyList<string> texts = TextList(key, required);
        IReadOnlyList<YamlNode> positions = node is YamlSequence sequence ? sequence.Items : [];
        for (int i = 0; i < texts.Count; i++)
        {
            if (PathTemplate.TryParse(texts[i], KnownRoot, out PathTemplate? template, out string error))
            {
                templates.Add(template!);
            }
            else
            {
                Errors.Add(i < positions.Count ? positions[i] : node, $"{key}: {error}");
            }
        }

        return templates;
    }

    /// <summary>A regular expression that must define the named groups in <paramref name="groups"/>.</summary>
    public Regex? Pattern(string key, bool required, params string[] groups)
    {
        string? text = Text(key, required);
        if (text is null)
        {
            return null;
        }

        try
        {
            var regex = new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            foreach (string group in groups)
            {
                if (regex.GroupNumberFromName(group) < 0)
                {
                    Errors.Add(_node.Get(key), $"{key} needs a named group (?<{group}>...)");
                    return null;
                }
            }

            return regex;
        }
        catch (ArgumentException ex)
        {
            Errors.Add(_node.Get(key), $"{key} is not a valid regular expression: {ex.Message}");
            return null;
        }
    }

    /// <summary>A list of mappings, each read by its own reader (call <see cref="Finish"/> on each).</summary>
    public IReadOnlyList<OptionReader> Mappings(string key, bool required = false)
    {
        YamlNode? value = Take(key, required);
        if (value is null)
        {
            return [];
        }

        if (value is not YamlSequence list)
        {
            Errors.Add(value, $"{key} should be a list of mappings");
            return [];
        }

        var readers = new List<OptionReader>();
        foreach (YamlNode item in list.Items)
        {
            if (item is YamlMapping mapping)
            {
                readers.Add(new OptionReader(mapping, Errors, KnownRoot, $"{key} entry"));
            }
            else
            {
                Errors.Add(item, $"every item of {key} should be a mapping, not {item.Describe()}");
            }
        }

        return readers;
    }

    /// <summary>One nested mapping, read by its own reader; null when absent (an error when required).</summary>
    public OptionReader? Mapping(string key, bool required = false)
    {
        YamlNode? value = Take(key, required);
        if (value is null)
        {
            return null;
        }

        if (value is YamlMapping mapping)
        {
            return new OptionReader(mapping, Errors, KnownRoot, key);
        }

        Errors.Add(value, $"{key} should be a mapping, not {value.Describe()}");
        return null;
    }

    /// <summary>Marks a key as handled without reading it.</summary>
    public void Skip(string key) => _read.Add(key);

    /// <summary>Reports every key nothing asked for.</summary>
    public void Finish()
    {
        var known = _read.Order(StringComparer.Ordinal).ToList();
        foreach (KeyValuePair<YamlScalar, YamlNode> entry in _node.Entries)
        {
            if (!_read.Contains(entry.Key.Value))
            {
                Errors.Add(entry.Key, $"unknown key \"{entry.Key.Value}\" in {What} (known: {string.Join(", ", known)})");
            }
        }
    }

    private YamlNode? Take(string key, bool required)
    {
        _read.Add(key);
        YamlNode? value = _node.Get(key);
        if (value is null or YamlScalar { IsNull: true })
        {
            if (required)
            {
                Errors.Add(value ?? _node, $"{What} is missing {key}");
            }

            return null;
        }

        return value;
    }
}
