using DevSweep.Core.Detectors;
using DevSweep.Core.Detectors.Projects;
using DevSweep.Core.Locations;
using DevSweep.Core.Platform;

namespace DevSweep.Core.Projects.Discovery;

/// <summary>A git working copy found on disk.</summary>
/// <param name="Path">The folder holding <c>.git</c>.</param>
/// <param name="GitIsCloudOnly"><c>.git</c> itself is a cloud placeholder: running git would download it.</param>
/// <param name="ThroughCloudFolder">Reached through a OneDrive (cloud-provider) folder.</param>
public sealed record FoundRepo(string Path, bool GitIsCloudOnly = false, bool ThroughCloudFolder = false);

/// <summary>What a search for clones found, and what it could not look at.</summary>
public sealed record FinderResult
{
    public IReadOnlyList<FoundRepo> Repos { get; init; } = [];

    /// <summary>Roots that exist but could not be listed.</summary>
    public IReadOnlyList<string> UnreadableRoots { get; init; } = [];

    /// <summary>Junctions, symlinks and other links seen and not followed.</summary>
    public int LinksNotFollowed { get; init; }

    /// <summary>Cloud folders whose contents are only in the cloud (not listed: listing would download them).</summary>
    public IReadOnlyList<string> CloudOnlyFolders { get; init; } = [];

    /// <summary>Roots that were searched (existing ones).</summary>
    public IReadOnlyList<string> Searched { get; init; } = [];
}

/// <summary>
/// Finds git working copies under the project roots (the Mac <c>scanLocal</c> walk): a folder
/// with <c>.git</c> is a project and is not searched further; up to <see cref="MaxDepth"/>
/// folders deep; dot-folders, dependency and build folders (<see cref="ArtifactKinds"/> and
/// the Mac list) are skipped. Listing is metadata only. Junctions, symlinks and mount points
/// are never entered; OneDrive folders are entered only when their contents are on this disk.
/// </summary>
public static class ProjectFinder
{
    public const int MaxDepth = 4;

    private static readonly HashSet<string> SkipNames = new(
        ArtifactKinds.FolderNames.Concat(["Library", "Pods", "Carthage", "vendor", "DerivedData", "dist", "build", "__pycache__", "AppData",
            "$Recycle.Bin", "System Volume Information", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "scoop", "go"]),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folders searched: the workspace root, the user's own project roots, and the
    /// common ones (the <c>staleProjectArtifacts</c> roots of M2: source, repos, Projects,
    /// code, src, dev, Documents\GitHub, the JetBrains and Android Studio folders).
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(ISystemEnvironment environment, IEnumerable<string>? configured = null, string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var roots = new List<string>();
        if (workspaceRoot is not null)
        {
            roots.Add(workspaceRoot);
        }

        roots.AddRange(configured ?? []);
        if (environment.KnownFolder("Profile") is { } profile)
        {
            roots.Add(System.IO.Path.Join(profile, "Code"));
            foreach (string name in (string[])["source", "Projects", "repos", "code", "src", "dev", "git", "workspace", "IdeaProjects", "AndroidStudioProjects", "RiderProjects"])
            {
                roots.Add(System.IO.Path.Join(profile, name));
            }
        }

        if (environment.KnownFolder("Documents") is { } documents)
        {
            roots.Add(System.IO.Path.Join(documents, "GitHub"));
            roots.Add(System.IO.Path.Join(documents, "Projects"));
        }

        return roots.Select(Normalise).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Searches <paramref name="roots"/>, plus <paramref name="knownRepos"/> (folders found earlier, checked directly).</summary>
    public static FinderResult Find(IEnumerable<string> roots, IEnumerable<string>? knownRepos = null, int maxDepth = MaxDepth, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var found = new Dictionary<string, FoundRepo>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        var cloudOnly = new List<string>();
        var searched = new List<string>();
        int links = 0;
        var pending = new Stack<(string Folder, int Depth, bool Cloud)>();
        foreach (string root in roots.Select(Normalise).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            if (IsLinked(root))
            {
                links++;
                continue; // A root that is itself reached through a junction: not followed.
            }

            searched.Add(root);
            pending.Push((root, 0, false));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string folder, int depth, bool cloud) = pending.Pop();
                ProbeListing listing = FileProbe.List(folder);
                if (listing.Error is ProbeError.AccessDenied or ProbeError.Other)
                {
                    if (depth == 0)
                    {
                        unreadable.Add(folder);
                    }

                    continue;
                }

                if (listing.Entries.FirstOrDefault(e => e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) is { Path: not null } git && git.Name.Length > 0)
                {
                    found.TryAdd(folder, new FoundRepo(folder, git.IsCloudPlaceholder, cloud));
                    continue;
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (ProbeEntry entry in listing.Entries.Where(e => e.IsDirectory).Reverse())
                {
                    if (entry.Name.StartsWith('.') || SkipNames.Contains(entry.Name))
                    {
                        continue;
                    }

                    if (entry.IsCloudPlaceholder)
                    {
                        cloudOnly.Add(entry.Path);
                        continue;
                    }

                    if (entry.IsReparsePoint)
                    {
                        if (EnterableLink(entry.Path, out bool placeholder))
                        {
                            pending.Push((entry.Path, depth + 1, true));
                        }
                        else if (placeholder)
                        {
                            cloudOnly.Add(entry.Path);
                        }
                        else
                        {
                            links++;
                        }

                        continue;
                    }

                    pending.Push((entry.Path, depth + 1, cloud));
                }
            }
        }

        foreach (string known in knownRepos ?? [])
        {
            string path = Normalise(known);
            if (!found.ContainsKey(path) && FileProbe.Stat(System.IO.Path.Join(path, ".git")) is { } git && !IsLinked(path))
            {
                found[path] = new FoundRepo(path, git.IsCloudPlaceholder);
            }
        }

        return new FinderResult
        {
            Repos = found.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            UnreadableRoots = unreadable,
            LinksNotFollowed = links,
            CloudOnlyFolders = cloudOnly,
            Searched = searched,
        };
    }

    /// <summary>The folder is a link, or is reached through one (cloud folders and volume mount points are not links).</summary>
    private static bool IsLinked(string folder)
        => (OperatingSystem.IsWindows() && ReparseGuard.FindReparseAncestor(folder) is not null)
            || (FileProbe.Stat(folder) is { IsReparsePoint: true } && !EnterableLink(folder, out _));

    /// <summary>A reparse point that is a OneDrive (cloud-provider) folder whose contents are on this disk; every other kind is a link.</summary>
    private static bool EnterableLink(string path, out bool placeholder)
    {
        placeholder = false;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DirectoryEntry entry = FileInspector.ReadEntry(path);
            placeholder = entry.CloudState == CloudFileState.Placeholder;
            return entry.IsReparsePoint ? entry.IsTraversableDirectory : !placeholder;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string Normalise(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
}
