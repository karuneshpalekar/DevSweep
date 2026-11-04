namespace DevSweep.Core.Platform;

/// <summary>
/// Converts between display paths and the extended-length <c>\\?\</c> form that
/// Win32 file APIs need for paths longer than 260 characters.
/// </summary>
public static class LongPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>Full path without the extended prefix and without a trailing separator (roots keep theirs).</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(StripPrefix(path).Replace('/', '\\'));
        return TrimTrailingSeparator(full);
    }

    /// <summary>The extended-length form of <paramref name="path"/>: <c>\\?\C:\...</c> or <c>\\?\UNC\server\share\...</c>.</summary>
    public static string ToExtended(string path)
    {
        string full = Normalize(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(ExtendedUncPrefix, full.AsSpan(2))
            : ExtendedPrefix + full;
    }

    /// <summary>Removes a <c>\\?\</c> or <c>\\?\UNC\</c> prefix, for display.</summary>
    public static string StripPrefix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(@"\\", path.AsSpan(ExtendedUncPrefix.Length));
        }

        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path[ExtendedPrefix.Length..] : path;
    }

    /// <summary>Joins a directory and a child name with exactly one separator.</summary>
    public static string Join(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return directory.EndsWith('\\') ? directory + name : string.Concat(directory, "\\", name);
    }

    /// <summary>True when <paramref name="path"/> is a volume root such as <c>C:\</c>.</summary>
    public static bool IsRoot(string path)
    {
        string normal = Normalize(path);
        return string.Equals(Path.GetPathRoot(normal), normal, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailingSeparator(string full)
    {
        string? root = Path.GetPathRoot(full);
        if (root is not null && full.Length <= root.Length)
        {
            return full;
        }

        return full.TrimEnd('\\');
    }
}
