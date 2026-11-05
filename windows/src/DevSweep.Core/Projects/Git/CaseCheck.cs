using DevSweep.Core.Detectors;

namespace DevSweep.Core.Projects.Git;

/// <summary>
/// Letter-case differences git cannot see on a case-insensitive file system
/// (<c>core.ignorecase=true</c>, the Windows default):
/// <list type="bullet">
/// <item>a file or folder renamed only by case (<c>Readme.md</c> to <c>README.md</c>): git status is clean,
/// but a fresh download brings back the old name, so the rename is lost;</item>
/// <item>two index entries that differ only by case: only one of them can exist in the folder.</item>
/// </list>
/// Names on disk come from folder listings (metadata only); links are never entered.
/// </summary>
public static class CaseCheck
{
    public static void Find(string topLevel, IReadOnlyList<string> trackedPaths, List<string> renames, List<string> collisions)
    {
        ArgumentNullException.ThrowIfNull(trackedPaths);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(collisions);
        collisions.AddRange(trackedPaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => string.Join(" / ", g)));

        var listings = new Dictionary<string, Dictionary<string, ProbeEntry>?>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tracked in trackedPaths)
        {
            string[] parts = tracked.Split('/');
            string folder = topLevel;
            string indexPrefix = string.Empty;
            string diskPrefix = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                Dictionary<string, ProbeEntry>? names = Listing(listings, folder);
                if (names is null || !names.TryGetValue(parts[i], out ProbeEntry entry))
                {
                    break; // Gone or unreadable: git status reports a deleted file.
                }

                indexPrefix = i == 0 ? parts[i] : indexPrefix + "/" + parts[i];
                diskPrefix = i == 0 ? entry.Name : diskPrefix + "/" + entry.Name;
                if (!string.Equals(entry.Name, parts[i], StringComparison.Ordinal))
                {
                    if (reported.Add(indexPrefix))
                    {
                        renames.Add($"{indexPrefix} is now {diskPrefix}");
                    }

                    break; // Everything below a renamed folder is the same rename.
                }

                if (i < parts.Length - 1 && !entry.CanEnter)
                {
                    break;
                }

                folder = entry.Path;
            }
        }
    }

    private static Dictionary<string, ProbeEntry>? Listing(Dictionary<string, Dictionary<string, ProbeEntry>?> cache, string folder)
    {
        if (cache.TryGetValue(folder, out Dictionary<string, ProbeEntry>? known))
        {
            return known;
        }

        ProbeListing listing = FileProbe.List(folder);
        Dictionary<string, ProbeEntry>? names = null;
        if (listing.Error == ProbeError.None)
        {
            names = new Dictionary<string, ProbeEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ProbeEntry entry in listing.Entries)
            {
                names.TryAdd(entry.Name, entry);
            }
        }

        cache[folder] = names;
        return names;
    }
}
