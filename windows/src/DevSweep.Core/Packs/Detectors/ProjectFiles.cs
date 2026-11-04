using DevSweep.Core.Detectors;
using DevSweep.Core.Locations;

namespace DevSweep.Core.Packs.Detectors;

/// <summary>
/// Finds pin files (<c>global.json</c>, <c>.python-version</c>, <c>rust-toolchain.toml</c>...)
/// in the project folders: Settings' folders when chosen, else the rule's <c>projectRoots</c>.
/// Metadata only; links, cloud folders and dependency folders are never entered.
/// </summary>
public static class ProjectFiles
{
    private static readonly HashSet<string> NeverEnter = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", ".venv", "venv", "bin", "obj", "target", ".gradle", "build", "dist", ".next", "AppData",
    };

    /// <summary>The folders searched, for "What was checked".</summary>
    public static IReadOnlyList<string> Roots(DetectorRun run, IReadOnlyList<PathTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(templates);
        IEnumerable<string> roots = run.Context.ProjectRoots
            ?? templates.SelectMany(t => run.Expand(t).Matches.Where(m => m.CanEnter).Select(m => m.Path));
        return roots.Where(r => FileProbe.Stat(r) is { CanEnter: true }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Full paths of files named one of <paramref name="names"/> at most <paramref name="maxDepth"/> folders below each root.</summary>
    public static IReadOnlyList<string> Find(DetectorRun run, IReadOnlyList<string> roots, IReadOnlyCollection<string> names, int maxDepth = 5, int maxFolders = 20_000)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(names);
        var found = new List<string>();
        var pending = new Stack<(string Folder, int Depth)>(roots.Select(r => (r, 0)));
        int folders = 0;
        while (pending.Count > 0 && folders++ < maxFolders)
        {
            run.ThrowIfCancelled();
            (string folder, int depth) = pending.Pop();
            foreach (ProbeEntry entry in FileProbe.List(folder).Entries)
            {
                if (!entry.IsDirectory && !entry.IsCloudPlaceholder && names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(entry.Path);
                }
                else if (entry.CanEnter && depth < maxDepth && !NeverEnter.Contains(entry.Name))
                {
                    pending.Push((entry.Path, depth + 1));
                }
            }
        }

        if (folders > maxFolders)
        {
            run.Note($"Stopped looking for {string.Join(", ", names)} after {maxFolders} folders");
        }

        return found;
    }
}
