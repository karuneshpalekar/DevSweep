using System.Globalization;

namespace DevSweep.Core.Rules.Yaml;

/// <summary>A node of a parsed YAML document, with where it was in the text (for error messages).</summary>
public abstract class YamlNode(int line, int column)
{
    /// <summary>1-based line.</summary>
    public int Line { get; } = line;

    /// <summary>1-based column.</summary>
    public int Column { get; } = column;

    /// <summary>"line 12, column 5".</summary>
    public string Position => string.Create(CultureInfo.InvariantCulture, $"line {Line}, column {Column}");

    /// <summary>What kind of node this is, for messages ("a list", "text").</summary>
    public abstract string Describe();
}

/// <summary>A scalar: text, a number, true/false or null.</summary>
public sealed class YamlScalar(string value, bool plain, int line, int column) : YamlNode(line, column)
{
    public string Value { get; } = value;

    /// <summary>True when unquoted (only plain scalars can mean null, true or a number).</summary>
    public bool IsPlain { get; } = plain;

    /// <summary>An unquoted empty value, <c>~</c> or <c>null</c>.</summary>
    public bool IsNull => IsPlain && Value is "" or "~" or "null" or "Null" or "NULL";

    public override string Describe() => IsNull ? "nothing (null)" : $"\"{Value}\"";
}

/// <summary>A list.</summary>
public sealed class YamlSequence(IReadOnlyList<YamlNode> items, int line, int column) : YamlNode(line, column)
{
    public IReadOnlyList<YamlNode> Items { get; } = items;

    public override string Describe() => "a list";
}

/// <summary>A mapping (keys are always scalars here; keys are unique).</summary>
public sealed class YamlMapping(IReadOnlyList<KeyValuePair<YamlScalar, YamlNode>> entries, int line, int column) : YamlNode(line, column)
{
    public IReadOnlyList<KeyValuePair<YamlScalar, YamlNode>> Entries { get; } = entries;

    /// <summary>The value for <paramref name="key"/> (exact, case-sensitive like YAML), or null.</summary>
    public YamlNode? Get(string key)
    {
        foreach (KeyValuePair<YamlScalar, YamlNode> entry in Entries)
        {
            if (string.Equals(entry.Key.Value, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public override string Describe() => "a mapping";
}
