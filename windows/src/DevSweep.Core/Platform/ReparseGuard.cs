namespace DevSweep.Core.Platform;

/// <summary>Finds junctions and links between a volume root and an item.</summary>
public static class ReparseGuard
{
    /// <summary>
    /// The first ancestor of <paramref name="path"/> (below the volume root, not
    /// the item itself) that is a reparse point, or that cannot be read and so
    /// cannot be shown not to be one. Null when the path is direct.
    /// </summary>
    public static string? FindReparseAncestor(string path)
    {
        string full = LongPath.Normalize(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string? parent = Path.GetDirectoryName(full);
        var ancestors = new Stack<string>();
        while (parent is not null && parent.Length > root.Length)
        {
            ancestors.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }

        foreach (string ancestor in ancestors)
        {
            if (!FindFileEnumerator.TryReadEntry(LongPath.ToExtended(ancestor), out DirectoryEntry entry, out _)
                || entry.IsReparsePoint)
            {
                return ancestor;
            }
        }

        return null;
    }
}
