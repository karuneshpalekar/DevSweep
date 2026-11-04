namespace DevSweep.Core.Locations;

/// <summary>
/// Wildcards within one path segment, case-insensitive like NTFS:
/// <c>*</c> (any run, also empty), <c>?</c> (one character), <c>[abc]</c>,
/// <c>[a-z]</c> and <c>[!abc]</c> (one character in, or not in, the set).
/// Unlike Win32's own matching, <c>*.*</c> does not match names without a dot
/// and 8.3 short names are never matched.
/// </summary>
public static class Glob
{
    public static bool HasWildcards(string segment)
        => segment is not null && segment.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    /// <summary>False with a reason when a <c>[</c> is not closed or a set is empty.</summary>
    public static bool TryValidate(string segment, out string error)
    {
        ArgumentNullException.ThrowIfNull(segment);
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] != '[')
            {
                continue;
            }

            int close = SetEnd(segment, i);
            if (close < 0)
            {
                error = $"\"{segment}\" has a [ without a matching ]";
                return false;
            }

            i = close;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsMatch(string pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);
        return Match(pattern, 0, name, 0);
    }

    private static bool Match(string pattern, int p, string name, int n)
    {
        while (p < pattern.Length)
        {
            char c = pattern[p];
            if (c == '*')
            {
                while (p < pattern.Length && pattern[p] == '*')
                {
                    p++;
                }

                if (p == pattern.Length)
                {
                    return true;
                }

                for (int start = n; start <= name.Length; start++)
                {
                    if (Match(pattern, p, name, start))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (n >= name.Length)
            {
                return false;
            }

            if (c == '?')
            {
                p++;
                n++;
                continue;
            }

            if (c == '[' && SetEnd(pattern, p) is int close and >= 0)
            {
                if (!InSet(pattern, p + 1, close, name[n]))
                {
                    return false;
                }

                p = close + 1;
                n++;
                continue;
            }

            if (char.ToUpperInvariant(c) != char.ToUpperInvariant(name[n]))
            {
                return false;
            }

            p++;
            n++;
        }

        return n == name.Length;
    }

    /// <summary>Index of the <c>]</c> closing the set opened at <paramref name="open"/>, or -1. A <c>]</c> right after <c>[</c> or <c>[!</c> is literal.</summary>
    private static int SetEnd(string pattern, int open)
    {
        int i = open + 1;
        if (i < pattern.Length && pattern[i] == '!')
        {
            i++;
        }

        if (i < pattern.Length && pattern[i] == ']')
        {
            i++;
        }

        int close = pattern.IndexOf(']', i);
        return close;
    }

    private static bool InSet(string pattern, int start, int close, char c)
    {
        bool negate = pattern[start] == '!';
        int i = negate ? start + 1 : start;
        char upper = char.ToUpperInvariant(c);
        bool found = false;
        while (i < close)
        {
            char from = char.ToUpperInvariant(pattern[i]);
            if (i + 2 < close && pattern[i + 1] == '-')
            {
                char to = char.ToUpperInvariant(pattern[i + 2]);
                found |= upper >= from && upper <= to;
                i += 3;
            }
            else
            {
                found |= upper == from;
                i++;
            }
        }

        return found != negate;
    }
}
