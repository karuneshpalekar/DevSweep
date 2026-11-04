using System.Text;
using System.Text.Json;
using System.Xml;

namespace DevSweep.Core.Locations;

/// <summary>
/// Small, forgiving readers for the config formats tools keep their folders in
/// (<c>.npmrc</c>, <c>pip.ini</c>, the Go env file, <c>uv.toml</c>, <c>.yarnrc</c>,
/// <c>NuGet.Config</c>, Maven <c>settings.xml</c>, Scoop and winget JSON). Each
/// returns the raw value or null; nothing here throws on a malformed file.
/// </summary>
public static class ConfigText
{
    /// <summary>
    /// Expands <c>%VAR%</c>, <c>${VAR}</c>, <c>${env.VAR}</c>, <c>${user.home}</c>
    /// and a leading <c>~</c>; strips surrounding quotes. An unset variable stays
    /// as written (so the value is then not a full path and is not used).
    /// </summary>
    public static string Expand(string value, ResolutionTrail trail)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(trail);
        string text = Unquote(value.Trim());
        if (text.StartsWith('~') && (text.Length == 1 || text[1] is '\\' or '/') && trail.Root("Profile") is { } profile)
        {
            text = profile + text[1..];
        }

        var output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '%' && text.IndexOf('%', i + 1) is int end and > 0 && end > i + 1
                && trail.Environment.GetVariable(text[(i + 1)..end]) is { } percent)
            {
                output.Append(percent);
                i = end + 1;
            }
            else if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{' && text.IndexOf('}', i + 2) is int close and > 0
                && Lookup(text[(i + 2)..close], trail) is { } braced)
            {
                output.Append(braced);
                i = close + 1;
            }
            else
            {
                output.Append(text[i]);
                i++;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// The value of <paramref name="key"/> in INI-style text (<c>key = value</c>,
    /// <c>;</c> and <c>#</c> comments). With a section, only that section counts;
    /// without one, only lines before the first section header. The last occurrence wins.
    /// </summary>
    public static string? Ini(string text, string? section, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? current = null;
        string? found = null;
        foreach (string rawLine in Lines(text))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string key = line[..equals].Trim();
            if (keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            {
                found = Unquote(line[(equals + 1)..].Trim());
            }
        }

        return found;
    }

    /// <summary><c>KEY=VALUE</c> lines (the Go env file); the last occurrence wins.</summary>
    public static string? KeyValue(string text, string key) => Ini(text, null, key);

    /// <summary>A top-level <c>key = "value"</c> in TOML (before any table header).</summary>
    public static string? TomlTopLevel(string text, string key)
    {
        string? value = Ini(text, null, key);
        if (value is null)
        {
            return null;
        }

        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        string raw = (comment >= 0 ? value[..comment] : value).Trim();

        // A basic string escapes backslashes; a 'literal' string does not.
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"'
            ? raw[1..^1].Replace(@"\\", @"\", StringComparison.Ordinal)
            : Unquote(raw);
    }

    /// <summary>Yarn classic <c>.yarnrc</c>: <c>key "value"</c> or <c>key value</c> per line.</summary>
    public static string? Yarnrc(string text, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? found = null;
        foreach (string rawLine in Lines(text))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(key, StringComparison.Ordinal) && line.Length > key.Length && char.IsWhiteSpace(line[key.Length]))
            {
                found = Unquote(line[key.Length..].Trim());
            }
        }

        return found;
    }

    /// <summary><c>&lt;config&gt;&lt;add key="..." value="..."/&gt;</c> in a NuGet.Config.</summary>
    public static string? NuGetConfig(string text, string key)
    {
        string? found = null;
        bool inConfig = false;
        ReadXml(text, reader =>
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "config")
            {
                inConfig = !reader.IsEmptyElement;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "config")
            {
                inConfig = false;
            }
            else if (inConfig && reader.NodeType == XmlNodeType.Element && reader.Name == "add"
                && string.Equals(reader.GetAttribute("key"), key, StringComparison.OrdinalIgnoreCase))
            {
                found = reader.GetAttribute("value");
            }
            else if (inConfig && reader.NodeType == XmlNodeType.Element && reader.Name == "clear")
            {
                found = null;
            }
        });
        return found;
    }

    /// <summary>The text of the first element named <paramref name="localName"/> (any namespace), e.g. Maven's localRepository.</summary>
    public static string? XmlElementText(string text, string localName)
    {
        string? found = null;
        ReadXml(text, reader =>
        {
            if (found is null && reader.NodeType == XmlNodeType.Element && reader.LocalName == localName && !reader.IsEmptyElement)
            {
                found = reader.ReadElementContentAsString().Trim();
            }
        });
        return found is { Length: > 0 } ? found : null;
    }

    /// <summary>A string property at <paramref name="path"/> (dotted) in JSON with comments and trailing commas allowed.</summary>
    public static string? JsonString(string text, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonElement element = document.RootElement;
            foreach (string part in path.Split('.'))
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
                {
                    return null;
                }
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Lookup(string name, ResolutionTrail trail)
    {
        if (name == "user.home")
        {
            return trail.Root("Profile");
        }

        return trail.Environment.GetVariable(name.StartsWith("env.", StringComparison.Ordinal) ? name[4..] : name);
    }

    private static string Unquote(string value)
        => value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r'));

    private static void ReadXml(string text, Action<XmlReader> visit)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                visit(reader);
            }
        }
        catch (XmlException)
        {
            // A malformed file gives what was read before the error.
        }
    }
}
