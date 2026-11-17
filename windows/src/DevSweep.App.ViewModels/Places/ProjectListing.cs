using System.Globalization;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels.Places;

/// <summary>The Show filter of the project list (the Mac's All, On this Mac, Only on GitHub).</summary>
public enum ProjectFilter
{
    All,
    OnThisPc,
    OnGitHub,
}

/// <summary>Whether GitHub is ready, or what is missing (the set-up card).</summary>
public enum GitHubSetup
{
    /// <summary>Signed in to at least one account.</summary>
    Ready,

    /// <summary>The GitHub CLI isn't installed.</summary>
    NotInstalled,

    /// <summary>The GitHub CLI is installed but no account is signed in.</summary>
    NotSignedIn,

    /// <summary>The accounts part of Projects is turned off, or nothing has been asked yet.</summary>
    Off,
}

/// <summary>What was searched: one line for the page, the details behind it, and what could not be looked at.</summary>
/// <param name="Line">"Searched 14 folders on C: and D: · 37 repos".</param>
/// <param name="Folders">Every folder searched, with whether the person added it (and can remove it).</param>
/// <param name="Notes">Skipped or unreadable places, OneDrive folders not checked, stopped WSL distributions.</param>
public sealed record SearchReport(string Line, IReadOnlyList<SearchedFolder> Folders, IReadOnlyList<string> Notes);

/// <summary>A folder in "what was searched".</summary>
public sealed record SearchedFolder(string Path, bool AddedByYou);

/// <summary>
/// What the project list shows, as plain functions (so the filters, counts, search and order are tested without a window):
/// the Show filter with counts, the account filter, a case-insensitive search of name, owner and description, local first
/// then most recently used, and the words for the page subtitle and the "what was searched" line.
/// </summary>
public static class ProjectListing
{
    public static string Title(ProjectFilter filter) => filter switch
    {
        ProjectFilter.OnThisPc => "On this PC",
        ProjectFilter.OnGitHub => "On GitHub",
        _ => "All",
    };

    /// <summary>Count for a filter tab. Only GitHub means "not on this PC", as on the Mac.</summary>
    public static int Count(IEnumerable<Project> projects, ProjectFilter filter)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return filter switch
        {
            ProjectFilter.OnThisPc => projects.Count(p => p.OnDisk),
            ProjectFilter.OnGitHub => projects.Count(p => !p.OnDisk),
            _ => projects.Count(),
        };
    }

    /// <summary>"On this PC · 8".</summary>
    public static string Label(IEnumerable<Project> projects, ProjectFilter filter)
    {
        int n = Count(projects, filter);
        return n > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Title(filter)} · {n}") : Title(filter);
    }

    /// <summary>The list: filtered, searched, sorted (on this PC first, then last used, then name).</summary>
    public static IReadOnlyList<Project> Apply(IEnumerable<Project> projects, ProjectFilter filter, string? account, string? search)
    {
        ArgumentNullException.ThrowIfNull(projects);
        string text = (search ?? string.Empty).Trim();
        return [.. projects
            .Where(p => filter switch { ProjectFilter.OnThisPc => p.OnDisk, ProjectFilter.OnGitHub => !p.OnDisk, _ => true })
            .Where(p => account is null || string.Equals(p.Account, account, StringComparison.OrdinalIgnoreCase))
            .Where(p => text.Length == 0
                || p.NameWithOwner.Contains(text, StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || p.Owner.Contains(text, StringComparison.OrdinalIgnoreCase)
                || p.Description.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.OnDisk ? 0 : 1)
            .ThenByDescending(p => p.LastUsed ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.NameWithOwner, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"8 on this PC · 2.94 GB" under the page title (WSL projects have no measured size and are left out of the total).</summary>
    public static string Subtitle(IReadOnlyList<Project> projects, bool loaded, bool loading)
    {
        if (!loaded)
        {
            return loading ? "Looking…" : "Not loaded yet";
        }

        List<Project> local = [.. projects.Where(p => p.OnDisk)];
        string refreshing = loading ? " · refreshing" : string.Empty;
        return local.Count == 0
            ? "No projects on this PC" + refreshing
            : string.Create(CultureInfo.InvariantCulture, $"{local.Count} on this PC · {SizeText.Format(local.Sum(p => p.LocalSize))}") + refreshing;
    }

    /// <summary>"3d ago", "2mo ago": the short form the rows use.</summary>
    public static string UsedAgo(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is not { } d)
        {
            return string.Empty;
        }

        TimeSpan ago = now - d;
        return ago.TotalMinutes < 1 ? "now"
            : ago.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalMinutes}m ago")
            : ago.TotalDays < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalHours}h ago")
            : ago.TotalDays < 14 ? string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalDays}d ago")
            : ago.TotalDays < 60 ? string.Create(CultureInfo.InvariantCulture, $"{(int)(ago.TotalDays / 7)}w ago")
            : ago.TotalDays < 730 ? string.Create(CultureInfo.InvariantCulture, $"{(int)(ago.TotalDays / 30)}mo ago")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)(ago.TotalDays / 365)}y ago");
    }

    public static GitHubSetup Setup(ProjectsSnapshot snapshot, bool accountsOn)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!accountsOn || snapshot.Accounts is not { } accounts)
        {
            return GitHubSetup.Off;
        }

        return accounts.Status switch
        {
            GitHubStatus.SignedIn => GitHubSetup.Ready,
            GitHubStatus.NotInstalled => GitHubSetup.NotInstalled,
            _ => GitHubSetup.NotSignedIn,
        };
    }

    /// <summary>"C: and D:" or "C:, D: and E:" from folder paths (network and WSL paths are not drives).</summary>
    public static string Drives(IEnumerable<string> folders)
    {
        List<string> drives = [.. folders
            .Select(f => f.Length >= 2 && f[1] == ':' ? char.ToUpperInvariant(f[0]) + ":" : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return drives.Count switch
        {
            0 => string.Empty,
            1 => drives[0],
            _ => string.Join(", ", drives.Take(drives.Count - 1)) + " and " + drives[^1],
        };
    }

    /// <summary>What was searched and what was not (the page's report under the list's controls).</summary>
    public static SearchReport Report(ProjectsSnapshot snapshot, IReadOnlyList<string> addedFolders)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        FinderResult search = snapshot.Search;
        var deep = snapshot.DeepSearch;
        var folders = new List<SearchedFolder>();
        foreach (string root in search.Searched)
        {
            folders.Add(new SearchedFolder(root, addedFolders.Any(f => string.Equals(f.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))));
        }

        foreach (string added in addedFolders.Where(a => !folders.Exists(f => string.Equals(f.Path.TrimEnd('\\'), a.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))))
        {
            folders.Add(new SearchedFolder(added, true));
        }

        int repos = snapshot.Projects.Count(p => p.OnDisk);
        string repoText = repos == 1 ? "1 repo" : string.Create(CultureInfo.InvariantCulture, $"{repos} repos");
        string where = Drives(deep is { Roots.Count: > 0 } ? deep.Roots.Concat(search.Searched) : search.Searched);
        string line;
        if (deep is not null)
        {
            line = string.Create(CultureInfo.InvariantCulture, $"Searched every folder on {(where.Length > 0 ? where : "this PC")} ({deep.Folders:N0} looked at){(deep.Complete ? string.Empty : ", stopped early")} · {repoText}");
        }
        else
        {
            int n = search.Searched.Count;
            string folderText = n == 1 ? "1 folder" : string.Create(CultureInfo.InvariantCulture, $"{n} folders");
            line = $"Searched {folderText}{(where.Length > 0 ? " on " + where : string.Empty)} · {repoText}";
        }

        var notes = new List<string>();
        notes.AddRange(search.UnreadableRoots.Select(r => $"Couldn't read {r}."));
        if (deep is { Unreadable: > 0 })
        {
            notes.Add(Words.Count(deep.Unreadable, "folder") + " couldn't be read (no permission or in use), so projects inside them are missing.");
        }

        IEnumerable<string> cloud = search.CloudOnlyFolders.Concat(deep?.CloudOnlySample ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string folder in cloud.Take(5))
        {
            notes.Add($"In OneDrive, not checked: {folder} (its files are online-only; looking would download them).");
        }

        int cloudTotal = Math.Max(cloud.Count(), deep?.CloudOnly ?? 0);
        if (cloudTotal > 5)
        {
            notes.Add($"{cloudTotal - 5} more OneDrive folders are online-only and were not checked.");
        }

        if (search.LinksNotFollowed + (deep?.LinksNotFollowed ?? 0) > 0)
        {
            notes.Add(Words.Count(search.LinksNotFollowed + (deep?.LinksNotFollowed ?? 0), "link") + " (junctions and symbolic links) were not followed, so nothing is counted twice.");
        }

        if (deep is { BareRepos: > 0 })
        {
            notes.Add(Words.Count(deep.BareRepos, "bare repository").Replace("repositorys", "repositories", StringComparison.Ordinal) + " (server copies with no working files) were left out.");
        }

        foreach (WslDistro distro in snapshot.Wsl.Where(d => !d.Running))
        {
            notes.Add($"WSL {distro.Name} is stopped. Start it to include its projects (DevSweep never starts it).");
        }

        return new SearchReport(line, folders, notes);
    }
}
