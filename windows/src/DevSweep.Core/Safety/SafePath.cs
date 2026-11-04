using System.Diagnostics.CodeAnalysis;

namespace DevSweep.Core.Safety;

/// <summary>
/// Lexical path rules for the safety guard. A path is accepted only in one
/// unambiguous, fully qualified form; anything Windows would quietly reinterpret
/// is refused rather than "fixed", so the path checked is the path acted on.
/// </summary>
public static class SafePath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const int MaxSegment = 255;

    /// <summary>
    /// The canonical form of <paramref name="path"/>: <c>C:\a\b</c> (drive letter
    /// upper case, single backslashes, no trailing separator except on a drive
    /// root) or <c>\\server\share\a</c>. <c>\\?\</c> prefixes and forward slashes
    /// are accepted. Refused: relative and drive-relative paths, device and NT
    /// paths, <c>.</c> and <c>..</c> segments, empty segments, segments ending in
    /// a dot or space (Windows strips those), wildcards, stream syntax (<c>:</c>)
    /// and control characters. Short (8.3) names are resolved separately, against the disk.
    /// </summary>
    public static bool TryCanonicalize(string? path, [NotNullWhen(true)] out string? canonical, out string rejection)
    {
        canonical = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            rejection = "The path is empty.";
            return false;
        }

        string p = path.Replace('/', '\\');
        if (p.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            p = string.Concat(@"\\", p.AsSpan(ExtendedUncPrefix.Length));
        }
        else if (p.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            p = p[ExtendedPrefix.Length..];
            if (!IsDriveRooted(p))
            {
                rejection = "Device and volume-GUID paths are not accepted.";
                return false;
            }
        }
        else if (p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            rejection = "Device and NT paths are not accepted.";
            return false;
        }

        if (!TrySplitRoot(p, out string root, out string rest, out rejection))
        {
            return false;
        }

        string[] segments = rest.Split('\\');
        int count = segments.Length;
        while (count > 0 && segments[count - 1].Length == 0)
        {
            count--; // trailing separators
        }

        for (int i = 0; i < count; i++)
        {
            if (!IsValidSegment(segments[i], out rejection))
            {
                return false;
            }
        }

        canonical = count == 0
            ? root
            : root + string.Join('\\', segments, 0, count);
        rejection = string.Empty;
        return true;
    }

    /// <summary>Case-insensitive equality of two canonical paths.</summary>
    public static bool AreSame(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when canonical <paramref name="path"/> is strictly inside canonical <paramref name="ancestor"/>.</summary>
    public static bool IsStrictlyUnder(string path, string ancestor)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ancestor);
        if (path.Length <= ancestor.Length || !path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ancestor.EndsWith('\\') || path[ancestor.Length] == '\\';
    }

    /// <summary>True when canonical <paramref name="path"/> is <paramref name="other"/> or inside it.</summary>
    public static bool IsSameOrUnder(string path, string other) => AreSame(path, other) || IsStrictlyUnder(path, other);

    /// <summary>The segments of a canonical path below its root.</summary>
    public static IReadOnlyList<string> RelativeSegments(string path, string ancestor)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ancestor);
        if (!IsStrictlyUnder(path, ancestor))
        {
            return [];
        }

        int start = ancestor.EndsWith('\\') ? ancestor.Length : ancestor.Length + 1;
        return path[start..].Split('\\');
    }

    private static bool IsDriveRooted(string p)
        => p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    private static bool TrySplitRoot(string p, out string root, out string rest, out string rejection)
    {
        root = rest = string.Empty;
        if (IsDriveRooted(p))
        {
            root = string.Concat(char.ToUpperInvariant(p[0]).ToString(), @":\");
            rest = p[3..];
            rejection = string.Empty;
            return true;
        }

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] parts = p[2..].Split('\\', 3);
            if (parts.Length < 2 || !IsValidSegment(parts[0], out rejection) || !IsValidSegment(parts[1], out rejection))
            {
                rejection = "A network path needs a server and a share.";
                return false;
            }

            root = string.Concat(@"\\", parts[0], @"\", parts[1], @"\");
            rest = parts.Length == 3 ? parts[2] : string.Empty;
            if (rest.Length == 0)
            {
                root = root.TrimEnd('\\');
            }

            rejection = string.Empty;
            return true;
        }

        rejection = "Only full paths (C:\\... or \\\\server\\share\\...) are accepted.";
        return false;
    }

    private static bool IsValidSegment(string segment, out string rejection)
    {
        if (segment.Length == 0)
        {
            rejection = "The path has an empty segment (a doubled separator).";
            return false;
        }

        if (segment is "." or "..")
        {
            rejection = "The path contains '.' or '..'; only direct paths are accepted.";
            return false;
        }

        if (segment.Length > MaxSegment)
        {
            rejection = "A name in the path is longer than 255 characters.";
            return false;
        }

        if (segment[^1] is '.' or ' ')
        {
            rejection = $"'{segment}' ends with a dot or space, which Windows would silently drop.";
            return false;
        }

        foreach (char c in segment)
        {
            if (c < ' ' || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
            {
                rejection = $"'{segment}' contains a character that is not allowed in a name (wildcards, ':' streams, control characters).";
                return false;
            }
        }

        rejection = string.Empty;
        return true;
    }
}
