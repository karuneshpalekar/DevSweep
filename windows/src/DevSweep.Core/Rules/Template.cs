using System.Text;

namespace DevSweep.Core.Rules;

/// <summary>
/// Fills <c>{{key}}</c> placeholders, with the same semantics as the Mac app:
/// whitespace inside the braces is ignored, an unknown key becomes empty, and an
/// unclosed <c>{{</c> is left as written together with the rest of the text.
/// </summary>
public static class Template
{
    public static string Render(string text, IReadOnlyDictionary<string, string> facts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(facts);
        var output = new StringBuilder(text.Length);
        int position = 0;
        while (position < text.Length)
        {
            int open = text.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            output.Append(text, position, open - position);
            int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                output.Append(text, open, text.Length - open);
                return output.ToString();
            }

            string key = text[(open + 2)..close].Trim(' ', '\t');
            if (facts.TryGetValue(key, out string? value))
            {
                output.Append(value);
            }

            position = close + 2;
        }

        output.Append(text, position, text.Length - position);
        return output.ToString();
    }

    /// <summary>True when <paramref name="text"/> has at least one <c>{{</c> placeholder.</summary>
    public static bool HasPlaceholders(string text) => text?.Contains("{{", StringComparison.Ordinal) == true;
}
