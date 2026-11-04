using Windows.Win32;

namespace DevSweep.Core.Platform;

/// <summary>Finds junctions and links between a volume root and an item.</summary>
public static class ReparseGuard
{
    /// <summary>
    /// The first ancestor of <paramref name="path"/> (below the drive or share
    /// root, not the item itself) that is a link, or that cannot be read and so
    /// cannot be shown not to be one. Null when the path is direct.
    /// </summary>
    /// <remarks>
    /// A cloud-provider folder is not a link either (it is the user's own folder).
    /// A volume mounted in a folder (a mount point whose target is a volume, not
    /// a folder) is not a link in this sense: the path below it is the direct path
    /// to items on that volume, so it does not block them. It is still never
    /// crossed: the walker does not enter it, and moves (quarantine, restore) are
    /// same-volume renames that fail rather than copy.
    /// </remarks>
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
            if (!FindFileEnumerator.TryReadEntry(LongPath.ToExtended(ancestor), out DirectoryEntry entry, out _))
            {
                return ancestor;
            }

            if (entry.IsReparsePoint && !IsDirectPathFolder(ancestor, in entry))
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>
    /// Reparse folders that are the direct path to what is below them: a volume
    /// mount point, and a cloud-provider folder (OneDrive and the like), which is
    /// the user's real folder, not a link to somewhere else.
    /// </summary>
    private static bool IsDirectPathFolder(string path, in DirectoryEntry entry)
        => entry.ReparseKind == ReparseKind.Cloud
            || (entry.ReparseTag == ReparseTags.MountPoint && IsVolumeMountPoint(path));

    /// <summary>
    /// True when <paramref name="directory"/> is where a volume is mounted (as
    /// opposed to a junction to a folder, which uses the same reparse tag).
    /// </summary>
    public static bool IsVolumeMountPoint(string directory)
    {
        string full = LongPath.Normalize(directory);
        string withSlash = (full.Length < 250 ? full : LongPath.ToExtended(full)) + "\\";
        Span<char> buffer = stackalloc char[64];
        return PInvoke.GetVolumeNameForVolumeMountPoint(withSlash, buffer);
    }
}
