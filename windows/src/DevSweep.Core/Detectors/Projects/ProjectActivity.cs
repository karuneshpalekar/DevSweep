namespace DevSweep.Core.Detectors.Projects;

/// <summary>When a project was last worked on, and what showed it.</summary>
/// <param name="LastUtc">The newest sign of work.</param>
/// <param name="What">What it was ("the last commit or checkout", a file's path).</param>
/// <param name="GitUtc">The git HEAD/index time, when the project is in a repository.</param>
public sealed record ProjectActivityResult(DateTime LastUtc, string What, DateTime? GitUtc);

/// <summary>
/// Works out a project's last activity from git (<c>HEAD</c> and <c>index</c> of
/// the nearest repository, worktrees included) and the newest file in the
/// project, ignoring every artifact folder (<c>node_modules</c>, <c>bin</c>,
/// <c>obj</c>...) and <c>.git</c>: installing dependencies or building is not
/// working on the project. Links are not entered.
/// </summary>
public static class ProjectActivity
{
    /// <summary>
    /// The last activity; null when it cannot be told (a folder could not be read,
    /// or the project is too large to read within <paramref name="maxEntries"/> and
    /// nothing seen so far is recent), so the caller leaves the project alone.
    /// </summary>
    public static ProjectActivityResult? Find(string project, DetectorRun run, DateTime cutoffUtc, int maxEntries = 50_000)
    {
        ArgumentNullException.ThrowIfNull(run);
        DateTime? git = GitTime(project);
        DateTime newest = git ?? DateTime.MinValue;
        string what = git is null ? "nothing" : "the last commit or checkout";
        var pending = new Stack<string>();
        pending.Push(project);
        int seen = 0;
        while (pending.Count > 0)
        {
            run.ThrowIfCancelled();
            string folder = pending.Pop();
            ProbeListing listing = FileProbe.List(folder);
            if (listing.Error is ProbeError.AccessDenied or ProbeError.Other)
            {
                if (listing.Error == ProbeError.AccessDenied)
                {
                    run.Denied(folder);
                }

                return newest >= cutoffUtc ? new ProjectActivityResult(newest, what, git) : null;
            }

            foreach (ProbeEntry entry in listing.Entries)
            {
                if (++seen > maxEntries)
                {
                    return newest >= cutoffUtc ? new ProjectActivityResult(newest, what, git) : null;
                }

                if (entry.IsDirectory)
                {
                    if (entry.CanEnter && !ArtifactKinds.FolderNames.Contains(entry.Name) && !entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(entry.Path);
                    }
                }
                else if (entry.ChangedUtc > newest)
                {
                    newest = entry.ChangedUtc;
                    what = Path.GetRelativePath(project, entry.Path);
                }
            }
        }

        return new ProjectActivityResult(newest, what, git);
    }

    /// <summary>The newest of HEAD and index in the repository holding <paramref name="project"/> (up to 8 folders up).</summary>
    public static DateTime? GitTime(string project)
    {
        string? folder = project;
        for (int up = 0; up < 8 && folder is not null; up++, folder = Path.GetDirectoryName(folder))
        {
            string dotGit = Path.Join(folder, ".git");
            string? gitDir = Directory.Exists(dotGit) ? dotGit : File.Exists(dotGit) ? WorktreeGitDir(dotGit, folder) : null;
            if (gitDir is null)
            {
                continue;
            }

            DateTime? newest = null;
            foreach (string name in (ReadOnlySpan<string>)["HEAD", "index"])
            {
                if (FileProbe.Stat(Path.Join(gitDir, name)) is { } entry && (newest is null || entry.LastWriteUtc > newest))
                {
                    newest = entry.LastWriteUtc;
                }
            }

            return newest;
        }

        return null;
    }

    /// <summary>A <c>.git</c> file holds <c>gitdir: &lt;path&gt;</c> (worktrees and submodules).</summary>
    private static string? WorktreeGitDir(string dotGitFile, string folder)
    {
        try
        {
            string text = File.ReadAllText(dotGitFile).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return null;
            }

            string path = text["gitdir:".Length..].Trim();
            return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Join(folder, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
