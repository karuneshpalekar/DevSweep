using System.Globalization;
using DevSweep.Core.Detectors.Projects;
using DevSweep.Core.Platform;

namespace DevSweep.Core.Projects.Git;

/// <summary>What a safety check does beyond the local checks.</summary>
public sealed record SafetyCheckOptions
{
    public static SafetyCheckOptions Default { get; } = new();

    /// <summary>
    /// Run <c>git fetch --all --prune</c> first (only when the user asks: it uses the network
    /// and the account's credentials, given in <see cref="FetchCall"/>).
    /// </summary>
    public bool Fetch { get; init; }

    /// <summary>Credentials and time limit for the fetch.</summary>
    public GitCall? FetchCall { get; init; }

    /// <summary>Ask Restart Manager whether a program holds files in the folder open (Windows; before a removal).</summary>
    public bool CheckLocks { get; init; }

    /// <summary>How deep submodules of submodules are checked.</summary>
    public int SubmoduleDepth { get; init; } = 3;

    /// <summary>File names shown per list.</summary>
    public int SampleSize { get; init; } = 5;

    /// <summary>Above this many tracked files the letter-case check is skipped (and the result says so).</summary>
    public int CaseCheckFileLimit { get; init; } = 300_000;

    /// <summary>Time limit for <c>git status</c> (the slowest call on a big repository).</summary>
    public TimeSpan StatusTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>
/// Decides whether a project folder is fully backed up (the core accuracy feature of
/// Projects). Everything comes from git itself, run read-only with no network unless the
/// user asked for a fetch:
/// <list type="bullet">
/// <item>commits on any local branch or a detached HEAD that no remote-tracking ref has
/// (<c>rev-list --branches HEAD --not --remotes</c>), per branch; commits only on tags are "unknown";</item>
/// <item>uncommitted, conflicted and untracked files from <c>git status --porcelain=v2</c>
/// (.gitignore respected; line-ending settings respected, so pure autocrlf churn git calls clean is
/// clean; files git calls changed whose only difference is CR at end of line are named as such);</item>
/// <item>stashes, other worktrees (or being one), an unfinished rebase/merge, submodules with work;</item>
/// <item>Git LFS (cannot be confirmed without the server: unknown);</item>
/// <item>Windows: renames by letter case only and index entries differing only by case (git with
/// <c>core.ignorecase</c> does not see them), paths of 260+ characters and <c>core.longpaths</c>,
/// files held open by a program (Restart Manager, M1).</item>
/// </list>
/// Any git failure makes the answer <see cref="SafetyState.Unknown"/>, never "safe".
/// </summary>
public sealed class SafetyChecker(IGit git)
{
    private static readonly HashSet<string> RebuildableIgnored = new(
        ArtifactKinds.FolderNames.Concat(["dist", "build", "out", ".next", ".nuxt", ".turbo", "__pycache__", ".pytest_cache", ".mypy_cache",
            ".ruff_cache", ".vs", ".idea", "coverage", ".cache", ".parcel-cache", ".angular", ".svelte-kit", "Thumbs.db", "desktop.ini"]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] IgnoredSuffixes = [".log", ".pyc", ".tmp", ".cache", ".suo", ".user"];

    public IGit Git { get; } = git ?? throw new ArgumentNullException(nameof(git));

    public Task<RemovalSafety> CheckAsync(string folder, SafetyCheckOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return CheckAsync(folder, options ?? SafetyCheckOptions.Default, depth: 0, cancellationToken);
    }

    private async Task<RemovalSafety> CheckAsync(string folder, SafetyCheckOptions options, int depth, CancellationToken ct)
    {
        string path = Path.GetFullPath(folder);
        var now = DateTimeOffset.UtcNow;
        if (Git.Program is null)
        {
            return new RemovalSafety { Path = path, State = SafetyState.NotChecked, NotCheckedReason = "git isn't installed, so nothing could be checked", CheckedAt = now };
        }

        var problems = new List<string>();
        var notes = new List<string>();
        RemovalSafety Unknown(string why) => new() { Path = path, State = SafetyState.Unknown, Problems = [why], CheckedAt = now };

        GitResult where = await Run(path, ["rev-parse", "--show-toplevel", "--git-dir", "--git-common-dir"], ct).ConfigureAwait(false);
        if (!where.Ok || where.Lines() is not [string top, string gitDirRaw, string commonRaw, ..])
        {
            return Unknown(where.Ok ? "git did not say where the repository is" : where.Error);
        }

        string topLevel = Full(path, top);
        string gitDir = Full(path, gitDirRaw);
        string commonDir = Full(path, commonRaw);
        string? linkedOf = SamePath(gitDir, commonDir) ? null : MainWorktreeOf(commonDir);

        bool fetched = false;
        if (options.Fetch)
        {
            GitCall call = (options.FetchCall ?? new GitCall()) with { AllowNetwork = true, Timeout = options.FetchCall?.Timeout ?? TimeSpan.FromMinutes(3) };
            GitResult fetch = await Git.RunAsync(path, ["fetch", "--all", "--prune", "--quiet", "--no-recurse-submodules"], call, ct).ConfigureAwait(false);
            fetched = fetch.Ok;
            if (!fetch.Ok)
            {
                notes.Add("Fetching from the remote failed, so commits are compared with what was last fetched: " + fetch.Error);
            }
        }

        // Remotes.
        GitResult remoteList = await Run(path, ["config", "--get-regexp", @"^remote\..*\.url$"], ct).ConfigureAwait(false);
        var remotes = new List<(string Name, string Url)>();
        if (remoteList.Ok)
        {
            foreach (string line in remoteList.Lines())
            {
                int space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space > "remote..url".Length - 1)
                {
                    remotes.Add((line[7..(space - 4)], line[(space + 1)..]));
                }
            }
        }
        else if (remoteList.Command.ExitCode != 1)
        {
            problems.Add("remotes: " + remoteList.Error); // exit 1 is "no such key": no remotes.
        }

        bool hasRemote = remotes.Count > 0;

        // Commits not on any remote-tracking ref.
        bool hasHead = (await Run(path, ["rev-parse", "--verify", "--quiet", "HEAD"], ct).ConfigureAwait(false)).Ok;
        bool detached = hasHead && !(await Run(path, ["symbolic-ref", "--quiet", "HEAD"], ct).ConfigureAwait(false)).Ok;
        int unpushed = 0;
        var byRef = new List<RefUnpushed>();
        string[] heads = hasHead ? ["--branches", "HEAD"] : ["--branches"];
        GitResult count = await Run(path, ["rev-list", "--count", .. heads, "--not", "--remotes"], ct).ConfigureAwait(false);
        if (!count.Ok || !int.TryParse(count.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out unpushed))
        {
            problems.Add("commits: " + count.Error);
        }
        else if (unpushed > 0)
        {
            await AttributeAsync(path, detached, byRef, ct).ConfigureAwait(false);
        }

        GitResult tagOnly = await Run(path, ["rev-list", "--count", "--tags", "--not", "--branches", "--remotes", .. hasHead ? (string[])["HEAD"] : []], ct).ConfigureAwait(false);
        if (tagOnly.Ok && int.TryParse(tagOnly.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int onTags) && onTags > 0)
        {
            problems.Add(Project.Plural(onTags, "commit") + " only reachable from tags; without the server DevSweep can't tell whether those tags were pushed");
        }

        // Working tree.
        GitCall statusCall = new() { Timeout = options.StatusTimeout };
        GitResult status = await Git.RunAsync(path, ["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"], statusCall, ct).ConfigureAwait(false);
        int changed = 0, conflicted = 0, untracked = 0, eolOnly = 0;
        var changedSample = new List<string>();
        var untrackedSample = new List<string>();
        if (!status.Ok)
        {
            problems.Add("status: " + status.Error);
        }
        else
        {
            IReadOnlyList<StatusEntry> entries = StatusParser.Parse(status.Output);
            foreach (StatusEntry e in entries)
            {
                switch (e.Kind)
                {
                    case StatusKind.Changed or StatusKind.Renamed:
                        changed++;
                        AddSample(changedSample, e.Path, options.SampleSize);
                        break;
                    case StatusKind.Unmerged:
                        conflicted++;
                        break;
                    case StatusKind.Untracked:
                        untracked++;
                        AddSample(untrackedSample, e.Path, options.SampleSize);
                        break;
                }
            }

            HashSet<string> unstaged = entries.Where(e => e.Kind == StatusKind.Changed && e.UnstagedOnly && !e.IsSubmodule).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            if (unstaged.Count > 0)
            {
                GitResult real = await Git.RunAsync(path, ["diff", "--numstat", "-z", "--ignore-cr-at-eol", "--no-renames", "--no-ext-diff"], statusCall, ct).ConfigureAwait(false);
                if (real.Ok)
                {
                    foreach (string record in real.NulSeparated())
                    {
                        string[] fields = record.Split('\t', 3);
                        if (fields.Length == 3)
                        {
                            unstaged.Remove(fields[2]);
                        }
                    }

                    eolOnly = unstaged.Count;
                }
            }
        }

        (int ignoredCount, List<string> ignoredSample) = await IgnoredAsync(path, options.SampleSize, ct).ConfigureAwait(false);

        // Stashes (shared by every worktree of the repository).
        int stashes = 0;
        GitResult stash = await Run(path, ["stash", "list", "--format=%gd"], ct).ConfigureAwait(false);
        if (stash.Ok)
        {
            stashes = stash.Lines().Count;
        }
        else
        {
            problems.Add("stashes: " + stash.Error);
        }

        // Worktrees.
        var otherWorktrees = new List<string>();
        GitResult worktrees = await Run(path, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        if (worktrees.Ok)
        {
            foreach (string block in worktrees.Output.Replace("\r", string.Empty, StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                string[] lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0 || !lines[0].StartsWith("worktree ", StringComparison.Ordinal) || lines.Any(l => l.StartsWith("prunable", StringComparison.Ordinal) || l == "bare"))
                {
                    continue;
                }

                string worktree = Full(path, lines[0]["worktree ".Length..]);
                if (!SamePath(worktree, topLevel) && (linkedOf is null || !SamePath(worktree, linkedOf)))
                {
                    otherWorktrees.Add(worktree);
                }
            }
        }
        else
        {
            problems.Add("worktrees: " + worktrees.Error);
        }

        // Tracked files: submodules, letter case, long paths.
        var submodules = new List<SubmoduleIssue>();
        var caseRenames = new List<string>();
        var caseCollisions = new List<string>();
        int longPaths = 0;
        bool ignoreCase = string.Equals((await Run(path, ["config", "--bool", "--get", "core.ignorecase"], ct).ConfigureAwait(false)).Output.Trim(), "true", StringComparison.Ordinal);
        GitResult files = await Git.RunAsync(path, ["ls-files", "--stage", "-z"], statusCall, ct).ConfigureAwait(false);
        if (!files.Ok)
        {
            problems.Add("tracked files: " + files.Error);
        }
        else
        {
            var tracked = new List<string>();
            foreach (string record in files.NulSeparated())
            {
                int tab = record.IndexOf('\t', StringComparison.Ordinal);
                if (tab < 0)
                {
                    continue;
                }

                string file = record[(tab + 1)..];
                if (record.StartsWith("160000 ", StringComparison.Ordinal))
                {
                    if (depth < options.SubmoduleDepth && await SubmoduleAsync(topLevel, file, options, depth, ct).ConfigureAwait(false) is { } issue)
                    {
                        submodules.Add(issue);
                    }

                    continue;
                }

                tracked.Add(file);
                if (topLevel.Length + 1 + file.Length >= 260)
                {
                    longPaths++;
                }
            }

            if (ignoreCase)
            {
                if (tracked.Count > options.CaseCheckFileLimit)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"Renames by letter case were not checked ({tracked.Count:N0} tracked files)."));
                }
                else
                {
                    CaseCheck.Find(topLevel, tracked, caseRenames, caseCollisions);
                }
            }
        }

        bool longPathsOn = await LongPathsEnabledAsync(path, ct).ConfigureAwait(false);
        bool usesLfs = UsesLfs(topLevel, commonDir);
        if (usesLfs)
        {
            problems.Add("it uses Git LFS, and whether every LFS file is on the server can only be confirmed by the server");
        }

        string? lockMessage = null;
        if (options.CheckLocks && OperatingSystem.IsWindows())
        {
            lockMessage = FileLocks.Check(topLevel).BlockerMessage;
        }

        var result = new RemovalSafety
        {
            Path = topLevel,
            Inspected = true,
            HasRemote = hasRemote,
            Remotes = remotes.Select(r => r.Name).ToList(),
            RemotesAreLocalFolders = hasRemote && remotes.All(r => RemoteUrls.Parse(r.Url).IsLocalFolder),
            UnpushedCommits = hasRemote ? unpushed : 0,
            UnpushedByRef = hasRemote ? byRef : [],
            ChangedFiles = changed,
            ChangedSample = changedSample,
            LineEndingOnlyFiles = eolOnly,
            ConflictedFiles = conflicted,
            UntrackedFiles = untracked,
            UntrackedSample = untrackedSample,
            IgnoredEntries = ignoredCount,
            IgnoredSample = ignoredSample,
            Stashes = stashes,
            OtherWorktrees = otherWorktrees,
            LinkedWorktreeOf = linkedOf,
            Submodules = submodules,
            UsesLfs = usesLfs,
            OperationInProgress = depth == 0 ? OperationInProgress(gitDir) : null,
            CaseOnlyRenames = caseRenames,
            CaseCollisions = caseCollisions,
            LongPathFiles = longPaths,
            LongPathsEnabled = longPathsOn,
            LockMessage = lockMessage,
            LastFetchUtc = LastFetch(commonDir),
            Fetched = fetched,
            Problems = problems,
            Notes = notes,
            CheckedAt = now,
        };
        SafetyState state = result.Blockers.Count > 0 ? SafetyState.NotSafe : problems.Count > 0 ? SafetyState.Unknown : SafetyState.Safe;
        return result with { State = state };
    }

    private Task<GitResult> Run(string path, IReadOnlyList<string> arguments, CancellationToken ct) => Git.RunAsync(path, arguments, GitCall.ReadOnly, ct);

    /// <summary>Which branches (and a detached HEAD) hold the unpushed commits.</summary>
    private async Task AttributeAsync(string path, bool detached, List<RefUnpushed> byRef, CancellationToken ct)
    {
        GitResult branches = await Run(path, ["for-each-ref", "refs/heads", "--format=%(refname:short)%00%(upstream:short)"], ct).ConfigureAwait(false);
        if (!branches.Ok)
        {
            return;
        }

        IReadOnlyList<string> lines = branches.Lines();
        foreach (string line in lines.Take(100))
        {
            string[] parts = line.Split('\0');
            GitResult n = await Run(path, ["rev-list", "--count", "refs/heads/" + parts[0], "--not", "--remotes"], ct).ConfigureAwait(false);
            if (n.Ok && int.TryParse(n.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int commits) && commits > 0)
            {
                byRef.Add(new RefUnpushed(parts[0], commits, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
            }
        }

        if (detached)
        {
            GitResult n = await Run(path, ["rev-list", "--count", "HEAD", "--not", "--branches", "--remotes"], ct).ConfigureAwait(false);
            if (n.Ok && int.TryParse(n.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int commits) && commits > 0)
            {
                byRef.Add(new RefUnpushed("detached HEAD", commits, null));
            }
        }
    }

    /// <summary>Ignored entries that are not rebuildable (not blocking: git was told to ignore them).</summary>
    private async Task<(int, List<string>)> IgnoredAsync(string path, int sampleSize, CancellationToken ct)
    {
        GitResult ignored = await Git.RunAsync(path, ["status", "--porcelain=v2", "-z", "--untracked-files=normal", "--ignored=matching"], new GitCall { Timeout = TimeSpan.FromMinutes(2) }, ct)
            .ConfigureAwait(false);
        var sample = new List<string>();
        int count = 0;
        if (!ignored.Ok)
        {
            return (0, sample);
        }

        foreach (StatusEntry e in StatusParser.Parse(ignored.Output).Where(e => e.Kind == StatusKind.Ignored))
        {
            string name = e.Path.TrimEnd('/').Split('/')[^1];
            if (RebuildableIgnored.Contains(name) || IgnoredSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            count++;
            AddSample(sample, e.Path, sampleSize);
        }

        return (count, sample);
    }

    private async Task<SubmoduleIssue?> SubmoduleAsync(string topLevel, string relative, SafetyCheckOptions options, int depth, CancellationToken ct)
    {
        string folder = Path.GetFullPath(Path.Join(topLevel, relative));
        if (!Directory.Exists(Path.Join(folder, ".git")) && !File.Exists(Path.Join(folder, ".git")))
        {
            return null; // Not checked out here: nothing of it is on this PC.
        }

        RemovalSafety inner = await CheckAsync(folder, options with { Fetch = false, CheckLocks = false }, depth + 1, ct).ConfigureAwait(false);
        return inner.State == SafetyState.Safe ? null : new SubmoduleIssue(relative, inner.Headline);
    }

    /// <summary>core.longpaths as the repository's own config files set it (not this call's <c>-c</c>).</summary>
    private async Task<bool> LongPathsEnabledAsync(string path, CancellationToken ct)
    {
        GitResult scoped = await Run(path, ["config", "--show-scope", "--bool", "--get-all", "core.longpaths"], ct).ConfigureAwait(false);
        bool enabled = false;
        foreach (string line in scoped.Lines())
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0] != "command")
            {
                enabled = parts[1] == "true";
            }
        }

        return enabled;
    }

    private static bool UsesLfs(string topLevel, string commonDir)
    {
        if (Directory.Exists(Path.Join(commonDir, "lfs", "objects")))
        {
            return true;
        }

        try
        {
            string attributes = Path.Join(topLevel, ".gitattributes");
            return File.Exists(attributes) && File.ReadAllText(attributes).Contains("filter=lfs", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? OperationInProgress(string gitDir)
    {
        if (Directory.Exists(Path.Join(gitDir, "rebase-merge")) || Directory.Exists(Path.Join(gitDir, "rebase-apply")))
        {
            return "rebase";
        }

        return File.Exists(Path.Join(gitDir, "MERGE_HEAD")) ? "merge"
            : File.Exists(Path.Join(gitDir, "CHERRY_PICK_HEAD")) ? "cherry-pick"
            : File.Exists(Path.Join(gitDir, "REVERT_HEAD")) ? "revert"
            : null;
    }

    private static DateTimeOffset? LastFetch(string commonDir)
        => FileProbeTime(Path.Join(commonDir, "FETCH_HEAD"));

    private static DateTimeOffset? FileProbeTime(string file)
        => File.Exists(file) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) : null;

    private static string? MainWorktreeOf(string commonDir)
        => Path.GetFileName(commonDir.TrimEnd('\\', '/')).Equals(".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(commonDir.TrimEnd('\\', '/')) : commonDir;

    private static string Full(string basePath, string gitPath)
        => Path.GetFullPath(Path.IsPathFullyQualified(gitPath) ? gitPath : Path.Join(basePath, gitPath)).TrimEnd('\\', '/');

    internal static bool SamePath(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void AddSample(List<string> sample, string path, int size)
    {
        if (sample.Count < size)
        {
            sample.Add(path);
        }
    }
}
