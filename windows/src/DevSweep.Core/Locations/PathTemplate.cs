namespace DevSweep.Core.Locations;

/// <summary>
/// A path in a rule: a named root and then segments, for example
/// <c>{LocalAppData}\JetBrains\*</c> or <c>~\.gradle\caches</c> (<c>~</c> is <c>{Profile}</c>).
/// Rules never hold drive letters or user names. Segments may hold the
/// wildcards <c>*</c>, <c>?</c> and <c>[...]</c>, each matching within one segment.
/// </summary>
public sealed record PathTemplate(string Root, IReadOnlyList<string> Segments, string Text)
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>True when any segment has a wildcard.</summary>
    public bool HasWildcards => Segments.Any(Glob.HasWildcards);

    /// <summary>
    /// Parses <paramref name="text"/>. With <paramref name="knownRoot"/>, the root
    /// must be one it knows (it returns the registered spelling, or null).
    /// </summary>
    public static bool TryParse(string text, Func<string, string?>? knownRoot, out PathTemplate? template, out string error)
    {
        template = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the path is empty";
            return false;
        }

        string root;
        string rest;
        if (text[0] == '~')
        {
            root = "Profile";
            rest = text[1..];
        }
        else if (text[0] == '{' && text.IndexOf('}', StringComparison.Ordinal) is int close and > 1)
        {
            root = text[1..close];
            rest = text[(close + 1)..];
        }
        else
        {
            error = $"\"{text}\" must start with a named root such as {{LocalAppData}} or ~ (rules never hold drive letters or user names)";
            return false;
        }

        if (root.StartsWith('{'))
        {
            error = $"\"{text}\": {{{{...}}}} is for explanations; paths use one pair of braces, like {{LocalAppData}}";
            return false;
        }

        if (knownRoot is not null)
        {
            if (knownRoot(root) is not { } canonical)
            {
                error = $"\"{text}\": unknown root {{{root}}}";
                return false;
            }

            root = canonical;
        }

        if (rest.Length > 0 && rest[0] is not ('\\' or '/'))
        {
            error = $"\"{text}\": put a \\ after the root";
            return false;
        }

        string[] segments = rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal) || segment.Contains('{', StringComparison.Ordinal)
                || segment.Contains('}', StringComparison.Ordinal) || segment.Contains("**", StringComparison.Ordinal))
            {
                error = $"\"{text}\": \"{segment}\" is not allowed in a rule path (no ., .., :, braces or **)";
                return false;
            }

            if (!Glob.TryValidate(segment, out string globError))
            {
                error = $"\"{text}\": {globError}";
                return false;
            }
        }

        template = new PathTemplate(root, segments, text);
        error = string.Empty;
        return true;
    }

    public override string ToString() => Text;
}
