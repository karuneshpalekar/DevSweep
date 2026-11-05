using DevSweep.Core.Projects.Storage;

namespace DevSweep.Core.Projects.Discovery;

/// <summary>
/// Joins what is on GitHub with what is on disk (the Mac <c>ProjectScanner.merge</c>): one
/// list of repositories on GitHub, clones matched to them by their <c>origin</c>
/// (case-insensitively, wherever the folder is), clones from owners nobody here is signed in
/// as, folders with no GitHub origin, and repositories removed earlier, which stay listed so
/// they can be downloaded again. On-disk projects first, most recently used first.
/// </summary>
public static class ProjectMerger
{
    public static IReadOnlyList<Project> Merge(IEnumerable<LocalClone> local, IEnumerable<RemoteRepo> remote, ProjectsState state)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(state);
        var projects = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteRepo r in remote)
        {
            projects[r.NameWithOwner] = new Project
            {
                Id = r.NameWithOwner,
                Name = r.Name,
                NameWithOwner = r.NameWithOwner,
                Owner = r.Owner,
                Description = r.Description,
                IsPrivate = r.IsPrivate,
                OnGitHub = true,
                Account = r.Account,
                LastActivity = r.PushedAt,
                RemoteKB = r.DiskUsageKB,
            };
        }

        var knownOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KnownRepo k in state.Known)
        {
            string[] parts = k.NameWithOwner.Split('/');
            if (parts.Length != 2 || projects.ContainsKey(k.NameWithOwner))
            {
                continue;
            }

            projects[k.NameWithOwner] = new Project
            {
                Id = k.NameWithOwner,
                Name = parts[1],
                NameWithOwner = k.NameWithOwner,
                Owner = parts[0],
                IsPrivate = k.IsPrivate,
                OnGitHub = true,
                Account = k.Account,
                RemoteKB = k.RemoteKB ?? 0,
            };
            knownOnly.Add(k.NameWithOwner);
        }

        foreach (LocalClone c in local)
        {
            if (c.Slug is { } slug)
            {
                if (projects.TryGetValue(slug, out Project? existing))
                {
                    projects[slug] = Attach(existing, c);
                }
                else
                {
                    // A clone from an organisation or account nobody here is signed in as.
                    string[] parts = slug.Split('/');
                    projects[slug] = Attach(new Project { Id = slug, Name = parts[1], NameWithOwner = slug, Owner = parts[0], OnGitHub = true }, c);
                }
            }
            else
            {
                string name = Path.GetFileName(c.Path);
                string id = "local:" + c.Path;
                projects[id] = Attach(new Project { Id = id, Name = name, NameWithOwner = name, OnGitHub = false }, c);
            }
        }

        // A repository that moved to another owner leaves its old name in the remembered list.
        // If a clone with the same repository name is on this PC, the old entry is not a second project.
        HashSet<string> onDiskNames = projects.Values.Where(p => p.OnDisk).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string k in knownOnly)
        {
            if (projects.TryGetValue(k, out Project? p) && !p.OnDisk && onDiskNames.Contains(p.Name))
            {
                projects.Remove(k);
            }
        }

        return projects.Values
            .Select(p => p with
            {
                LastOpened = Lookup(state.LastOpened, p.NameWithOwner),
                Strategy = state.Strategies.FirstOrDefault(s => s.Key.Equals(p.NameWithOwner, StringComparison.OrdinalIgnoreCase)) is { Key: not null } s ? s.Value : null,
            })
            .OrderBy(p => p.OnDisk ? 0 : 1)
            .ThenByDescending(p => p.LastUsed ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.NameWithOwner, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Where a (re)download goes: the folder it last lived in, else
    /// <c>&lt;workspace root&gt;\&lt;account or owner&gt;\&lt;name&gt;</c> (workspace root <c>%USERPROFILE%\Code</c>).
    /// </summary>
    public static string Destination(Project project, ProjectsState state, string profile)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(state);
        KnownRepo? known = state.Known.Find(k => k.NameWithOwner.Equals(project.NameWithOwner, StringComparison.OrdinalIgnoreCase));
        if (known?.LastPath is { Length: > 0 } last)
        {
            return last;
        }

        string root = state.WorkspaceRoot ?? Path.Join(profile, "Code");
        return Path.Join(root, project.Account ?? project.Owner, project.Name);
    }

    private static Project Attach(Project p, LocalClone c)
    {
        DateTimeOffset? activity = Newest(Newest(p.LastActivity, c.LastCommit), c.GitActivity);
        return p with
        {
            LocalPath = c.Path,
            LocalSize = c.Size,
            LocalSizeComplete = c.SizeComplete,
            Safety = c.Safety,
            LastActivity = activity,
            InCloudFolder = c.InCloudFolder,
        };
    }

    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    private static DateTimeOffset? Lookup(Dictionary<string, DateTimeOffset> map, string key)
        => map.TryGetValue(key, out DateTimeOffset v) ? v
            : map.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit ? hit.Value : null;
}
