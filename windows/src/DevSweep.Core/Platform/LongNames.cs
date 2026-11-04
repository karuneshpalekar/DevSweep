using Windows.Win32;

namespace DevSweep.Core.Platform;

/// <summary>
/// Expands 8.3 short names (<c>C:\Users\RUNNER~1</c>) to long ones with
/// <c>GetLongPathNameW</c>, so two spellings of one folder compare equal. The
/// longest part of the path that exists is expanded; the rest (not yet created)
/// is kept as given.
/// </summary>
public static class LongNames
{
    /// <summary>The path with every existing short-name segment expanded. Input and output are full paths without <c>\\?\</c>.</summary>
    public static string Resolve(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (!fullPath.Contains('~', StringComparison.Ordinal))
        {
            return fullPath; // No short-name segment can be present.
        }

        string existing = fullPath;
        string tail = string.Empty;
        while (true)
        {
            if (TryExpand(existing, out string? expanded))
            {
                return tail.Length == 0 ? expanded : LongPath.Join(expanded, tail);
            }

            string? parent = Path.GetDirectoryName(existing);
            if (parent is null || parent.Length >= existing.Length)
            {
                return fullPath;
            }

            string name = Path.GetFileName(existing);
            tail = tail.Length == 0 ? name : LongPath.Join(name, tail);
            existing = parent;
        }
    }

    private static bool TryExpand(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? expanded)
    {
        string query = LongPath.ToExtended(path);
        Span<char> buffer = new char[query.Length + 260];
        uint length = PInvoke.GetLongPathName(query, buffer);
        if (length == 0)
        {
            expanded = null;
            return false;
        }

        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = PInvoke.GetLongPathName(query, buffer);
            if (length == 0 || length > buffer.Length)
            {
                expanded = null;
                return false;
            }
        }

        expanded = LongPath.StripPrefix(buffer[..(int)length].ToString());
        return true;
    }
}
