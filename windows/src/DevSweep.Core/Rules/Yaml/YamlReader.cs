using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DevSweep.Core.Rules.Yaml;

/// <summary>
/// Reads YAML into a <see cref="YamlNode"/> tree with YamlDotNet's parser
/// (the event stream only). DevSweep maps the tree to rules by hand: no
/// reflection-based deserialiser, so the engine stays trim and Native AOT safe,
/// and every schema error can name its line and column.
/// </summary>
public static class YamlReader
{
    /// <summary>Parses the first document of <paramref name="text"/>; null when the text holds no document.</summary>
    /// <exception cref="RuleSyntaxException">The text is not valid YAML.</exception>
    public static YamlNode? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var parser = new Parser(new StringReader(text));
            var reader = new Reading(parser);
            return reader.ReadDocument();
        }
        catch (YamlException ex)
        {
            throw new RuleSyntaxException((int)ex.Start.Line, (int)ex.Start.Column, Clean(ex.Message));
        }
    }

    /// <summary>YamlDotNet prefixes messages with the position, which DevSweep prints itself.</summary>
    private static string Clean(string message)
    {
        int close = message.IndexOf("): ", StringComparison.Ordinal);
        return message.StartsWith("(Line:", StringComparison.Ordinal) && close > 0 ? message[(close + 3)..] : message;
    }

    private sealed class Reading(Parser parser)
    {
        private readonly Dictionary<string, YamlNode> _anchors = new(StringComparer.Ordinal);

        public YamlNode? ReadDocument()
        {
            // StreamStart, then DocumentStart or StreamEnd.
            Advance();
            Advance();
            if (parser.Current is not DocumentStart)
            {
                return null;
            }

            Advance();
            return ReadNode();
        }

        private YamlNode ReadNode()
        {
            ParsingEvent current = parser.Current ?? throw Unexpected(null);
            int line = (int)current.Start.Line;
            int column = (int)current.Start.Column;
            Advance();
            switch (current)
            {
                case Scalar scalar:
                    return Remember(scalar.Anchor, new YamlScalar(scalar.Value, scalar.Style is ScalarStyle.Plain or ScalarStyle.Any, line, column));
                case SequenceStart start:
                    {
                        var items = new List<YamlNode>();
                        while (parser.Current is not SequenceEnd)
                        {
                            items.Add(ReadNode());
                        }

                        Advance();
                        return Remember(start.Anchor, new YamlSequence(items, line, column));
                    }

                case MappingStart start:
                    return Remember(start.Anchor, ReadMapping(line, column));
                case AnchorAlias alias:
                    return _anchors.TryGetValue(alias.Value.Value, out YamlNode? target)
                        ? target
                        : throw new RuleSyntaxException(line, column, $"Unknown alias *{alias.Value.Value}.");
                default:
                    throw Unexpected(current);
            }
        }

        private YamlMapping ReadMapping(int line, int column)
        {
            var entries = new List<KeyValuePair<YamlScalar, YamlNode>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (parser.Current is not MappingEnd)
            {
                YamlNode key = ReadNode();
                if (key is not YamlScalar scalarKey)
                {
                    throw new RuleSyntaxException(key.Line, key.Column, "Mapping keys must be plain text.");
                }

                if (!keys.Add(scalarKey.Value))
                {
                    throw new RuleSyntaxException(key.Line, key.Column, $"\"{scalarKey.Value}\" appears twice in the same mapping.");
                }

                entries.Add(new KeyValuePair<YamlScalar, YamlNode>(scalarKey, ReadNode()));
            }

            Advance();
            return new YamlMapping(entries, line, column);
        }

        private YamlNode Remember(AnchorName anchor, YamlNode node)
        {
            if (!anchor.IsEmpty)
            {
                _anchors[anchor.Value] = node;
            }

            return node;
        }

        private void Advance()
        {
            if (!parser.MoveNext())
            {
                throw Unexpected(null);
            }
        }

        private static RuleSyntaxException Unexpected(ParsingEvent? at)
            => at is null
                ? new RuleSyntaxException(0, 0, "The YAML ended unexpectedly.")
                : new RuleSyntaxException((int)at.Start.Line, (int)at.Start.Column, $"Unexpected {at.GetType().Name} in the YAML.");
    }
}
