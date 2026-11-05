using DevSweep.Core.Cleaning;
using DevSweep.Core.Locations;
using DevSweep.Core.Modules;
using DevSweep.Core.Platform;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Safety;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Projects;

/// <summary>What a Projects refresh should do.</summary>
public sealed record ProjectsScanOptions
{
    /// <summary>Folders to search instead of the defaults (workspace, Settings' folders, common roots).</summary>
    public IReadOnlyList<string>? Roots { get; init; }

    /// <summary>Ask GitHub for each account's repositories (needs the accounts feature).</summary>
    public bool IncludeGitHub { get; init; } = true;

    /// <summary>Clones inspected at once.</summary>
    public int Parallelism { get; init; } = 4;

    public IProgress<string>? Progress { get; init; }
}

/// <summary>Per-account numbers for the Accounts tab.</summary>
public sealed record AccountSummary(string Login, bool IsActive, int Repos, int OnDisk, long Bytes, GitIdentity? Identity)
{
    /// <summary>"Not set. Projects downloaded with this account use your global Git identity."</summary>
    public bool IdentityMissing => Identity is null || Identity.IsBlank;
}

/// <summary>Clones from an owner nobody here is signed in as.</summary>
public sealed record OutsideOwner(string Owner, int Projects, long Bytes);

/// <summary>Everything the Projects place shows after a refresh (view-model friendly: immutable, no live objects).</summary>
public sealed record ProjectsSnapshot
{
    public IReadOnlyList<Project> Projects { get; init; } = [];

    public GitHubAccounts? Accounts { get; init; }

    public FinderResult Search { get; init; } = new();

    /// <summary>Problems worth showing ("Couldn't list X's repositories: ...").</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];

    public ProjectsFeatures Features { get; init; } = ProjectsFeatures.All;

    /// <summary>The saved commit identity per account.</summary>
    public IReadOnlyDictionary<string, GitIdentity> Identities { get; init; } = new Dictionary<string, GitIdentity>();

    public TimeSpan Elapsed { get; init; }

    public DateTimeOffset Date { get; init; }

    public IReadOnlyList<AccountSummary> AccountSummaries
        => (Accounts?.Accounts ?? []).Select(a =>
        {
            List<Project> mine = Projects.Where(p => string.Equals(p.Account, a.Login, StringComparison.OrdinalIgnoreCase)).ToList();
            return new AccountSummary(a.Login, a.IsActive, mine.Count(p => p.OnGitHub), mine.Count(p => p.OnDisk), mine.Sum(p => p.LocalSize),
                Identities.FirstOrDefault(i => i.Key.Equals(a.Login, StringComparison.OrdinalIgnoreCase)).Value);
        }).ToList();

    public IReadOnlyList<OutsideOwner> OutsideOwners
    {
        get
        {
            HashSet<string> signedIn = (Accounts?.Accounts ?? []).Select(a => a.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Projects.Where(p => p.OnDisk && p.OnGitHub && p.Account is null && !signedIn.Contains(p.Owner))
                .GroupBy(p => p.Owner, StringComparer.OrdinalIgnoreCase)
                .Select(g => new OutsideOwner(g.Key, g.Count(), g.Sum(p => p.LocalSize)))
                .OrderByDescending(o => o.Bytes)
                .ToList();
        }
    }
}

/// <summary>
/// The Projects place for the CLI and the app (M4): refresh (find clones, check each, list
/// every account's repositories, join them), check one folder (optionally after a fetch),
/// the idle list, download, remove, publish, add by URL, identities, push and sign-in in a
/// visible window, and the activity trail. Each tab refuses to run when its flag is off.
/// </summary>
public sealed class ProjectsService
{
    private readonly ISystemEnvironment _environment;

    public ProjectsService(
        ProjectsFeatures features, IGit git, IGitHubCli gh, GitHubApi api, ProjectStore store, ISystemEnvironment environment,
        ISecretCheck? secrets = null, IItemMeasurer? measurer = null)
    {
        Features = features ?? throw new ArgumentNullException(nameof(features));
        Git = git ?? throw new ArgumentNullException(nameof(git));
        GitHubCli = gh ?? throw new ArgumentNullException(nameof(gh));
        Api = api ?? throw new ArgumentNullException(nameof(api));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Secrets = secrets ?? HealthSecretCheck.Instance;
        Measurer = measurer;
        Checker = new SafetyChecker(git);
    }

    public ProjectsFeatures Features { get; }

    public IGit Git { get; }

    public IGitHubCli GitHubCli { get; }

    public GitHubApi Api { get; }

    public ProjectStore Store { get; }

    public ISecretCheck Secrets { get; }

    public IItemMeasurer? Measurer { get; }

    public SafetyChecker Checker { get; }

    /// <summary>The service on the engine's machine, or null when <c>module:projects</c> is off.</summary>
    public static ProjectsService? Create(DevSweepEngine engine, HttpClient http, ProjectStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        IReadOnlyList<ProjectsFeatures> registered = engine.Services<ProjectsFeatures>();
        ProjectsFeatures? features = registered.Count > 0 ? registered[0] : null;
        return features is null
            ? null
            : new ProjectsService(features, GitCli.Default, GitHub.GitHubCli.Default, new GitHubApi(http), store ?? new ProjectStore(), engine.Environment);
    }

    public string Profile => _environment.KnownFolder("Profile") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The folders a refresh searches.</summary>
    public IReadOnlyList<string> Roots(ProjectsScanOptions? options = null)
        => options?.Roots ?? ProjectFinder.DefaultRoots(_environment, Store.State.ProjectRoots, Store.State.WorkspaceRoot ?? Path.Join(Profile, "Code"));

    public async Task<ProjectsSnapshot> ScanAsync(ProjectsScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ProjectsScanOptions();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var messages = new List<string>();
        IReadOnlyList<string> roots = Roots(options);
        options.Progress?.Report("Looking for projects");
        IEnumerable<string> remembered = Store.State.Known.Where(k => k.RemovedAt is null && k.LastPath is not null).Select(k => k.LastPath!);
        FinderResult search = await Task.Run(() => ProjectFinder.Find(roots, remembered, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        var inspector = new CloneInspector(Git, Measurer, RemoteUrls.SshAliasesFromProfile(Profile));
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
        LocalClone[] local = await Task.WhenAll(search.Repos.Select(async repo =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                options.Progress?.Report("Checking " + repo.Path);
                return await inspector.InspectAsync(repo, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        RememberClones(local);
        if (Git.Program is null)
        {
            messages.Add("git isn't installed, so projects could not be checked (Git for Windows: winget install Git.Git).");
        }

        GitHubAccounts? accounts = null;
        var remote = new List<RemoteRepo>();
        if (Features.Accounts && options.IncludeGitHub)
        {
            options.Progress?.Report("Asking GitHub");
            accounts = await GitHubCli.AccountsAsync(cancellationToken).ConfigureAwait(false);
            if (accounts.Message is { } m)
            {
                messages.Add(m);
            }

            foreach (GitHubAccount account in accounts.Accounts)
            {
                (GitHubToken? token, string? error) = await GitHubCli.TokenAsync(account.Login, cancellationToken).ConfigureAwait(false);
                if (token is null)
                {
                    messages.Add($"Couldn't list {account.Login}'s repositories: {error}");
                    continue;
                }

                try
                {
                    remote.AddRange(await Api.ListReposAsync(token, cancellationToken).ConfigureAwait(false));
                }
                catch (GitHubApiException ex)
                {
                    messages.Add($"Couldn't list {account.Login}'s repositories: {ex.Message}");
                }
            }

            IReadOnlyList<RemoteRepo> merged = GitHubApi.MergeAccounts(remote);
            remote = [.. merged];
            Store.Update(s => s with
            {
                Known = s.Known.Select(k => merged.FirstOrDefault(r => r.NameWithOwner.Equals(k.NameWithOwner, StringComparison.OrdinalIgnoreCase)) is { } r
                    ? k with { Account = r.Account, IsPrivate = r.IsPrivate, RemoteKB = r.DiskUsageKB }
                    : k).ToList(),
            });
        }

        IReadOnlyList<Project> projects = ProjectMerger.Merge(local, remote, Store.State);
        return new ProjectsSnapshot
        {
            Projects = projects,
            Accounts = accounts,
            Search = search,
            Messages = messages,
            Features = Features,
            Identities = Store.State.Identities,
            Elapsed = clock.Elapsed,
            Date = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Checks one folder now; with <paramref name="fetch"/>, after <c>git fetch</c> as the project's account.</summary>
    public async Task<RemovalSafety> CheckAsync(string folder, bool fetch = false, string? account = null, CancellationToken cancellationToken = default)
    {
        GitCall? call = null;
        if (fetch)
        {
            GitHubToken? token = null;
            if (Features.Accounts && account is not null)
            {
                (token, _) = await GitHubCli.TokenAsync(account, cancellationToken).ConfigureAwait(false);
            }

            call = GitCredentials.For(GitHubCli.Program, token, TimeSpan.FromMinutes(3));
            Store.Log(ActivityKind.Fetch, folder, "Fetched to compare with the remote" + (account is null ? string.Empty : " as " + account));
        }

        return await Checker.CheckAsync(folder, new SafetyCheckOptions { Fetch = fetch, FetchCall = call, CheckLocks = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Cleanup list for <paramref name="days"/> (the saved choice when null), sizes re-measured exactly when asked.</summary>
    public async Task<IdleCleanupResult> IdleAsync(ProjectsSnapshot snapshot, int? days = null, bool exact = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Require(Features.Cleanup, ProjectsFeatures.CleanupFlag);
        int d = IdleCleanup.Clamp(days ?? Store.State.IdleDays);
        if (days is not null && d != Store.State.IdleDays)
        {
            Store.Update(s => s with { IdleDays = d });
        }

        IdleCleanupResult list = IdleCleanup.List(snapshot.Projects, d, DateTimeOffset.UtcNow);
        return exact ? await IdleCleanup.MeasureExactAsync(list, cancellationToken: cancellationToken).ConfigureAwait(false) : list;
    }

    /// <summary>The signed-in account a project uses: its own when signed in, else the active one, else the first (the Mac <c>credentialAccount</c>).</summary>
    public static string? CredentialAccount(Project project, GitHubAccounts? accounts)
    {
        ArgumentNullException.ThrowIfNull(project);
        IReadOnlyList<GitHubAccount> list = accounts?.Accounts ?? [];
        if (project.Account is { } a && list.Any(x => x.Login.Equals(a, StringComparison.OrdinalIgnoreCase)))
        {
            return a;
        }

        return list.FirstOrDefault(x => x.IsActive)?.Login ?? (list.Count > 0 ? list[0].Login : null);
    }

    public DownloadPlan PlanDownload(Project project, CloneStrategy strategy, string? destination = null, string? account = null)
        => Downloader.Plan(DownloadRequestFor(project, strategy, destination, account));

    public Task<DownloadResult> DownloadAsync(Project project, CloneStrategy strategy, string? destination = null, string? account = null, string? cloneUrl = null, CancellationToken cancellationToken = default)
    {
        Require(Features.Accounts || cloneUrl is not null, ProjectsFeatures.AccountsFlag);
        return new Downloader(Git, GitHubCli, Store, Measurer).DownloadAsync(DownloadRequestFor(project, strategy, destination, account) with { CloneUrl = cloneUrl }, cancellationToken);
    }

    /// <summary>Removes a fully backed-up project with the M1 cleaner (Recycle Bin or quarantine, History). <paramref name="cleaner"/> defaults to the user's.</summary>
    public Task<RemoveResult> RemoveAsync(Project project, Cleaner? cleaner = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cleaner ??= DefaultCleaner(project.LocalPath);
        return new ProjectRemover(Checker, cleaner, Store).RemoveAsync(project, cancellationToken);
    }

    public Task<PublishPreview> PreviewPublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
        => new Publisher(Git, GitHubCli, Api, Store, Secrets, Measurer).PreviewAsync(request, cancellationToken);

    public Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        Require(Features.Accounts, ProjectsFeatures.AccountsFlag);
        return new Publisher(Git, GitHubCli, Api, Store, Secrets, Measurer).PublishAsync(request, cancellationToken);
    }

    /// <summary>Adds a repository by URL or <c>owner/name</c> (forks, other organisations), trying each signed-in account.</summary>
    public async Task<(RemoteRepo? Repo, string? Error)> AddByUrlAsync(string text, CancellationToken cancellationToken = default)
    {
        Require(Features.Accounts, ProjectsFeatures.AccountsFlag);
        if (RemoteUrls.ParseSlug(text ?? string.Empty) is not { } slug)
        {
            return (null, "That doesn't look like a GitHub repository. Use owner/name or a github.com URL.");
        }

        GitHubAccounts accounts = await GitHubCli.AccountsAsync(cancellationToken).ConfigureAwait(false);
        string lastError = "Sign in to GitHub first.";
        foreach (GitHubAccount account in accounts.Accounts.OrderByDescending(a => a.IsActive))
        {
            (GitHubToken? token, string? error) = await GitHubCli.TokenAsync(account.Login, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                lastError = error ?? lastError;
                continue;
            }

            try
            {
                if (await Api.GetRepoAsync(slug, token, cancellationToken).ConfigureAwait(false) is { } repo)
                {
                    Store.Remember(repo.NameWithOwner, k => (k ?? new KnownRepo { NameWithOwner = repo.NameWithOwner }) with
                    {
                        Account = repo.Account,
                        IsPrivate = repo.IsPrivate,
                        RemoteKB = repo.DiskUsageKB,
                    });
                    Store.Log(ActivityKind.AddRepo, repo.NameWithOwner, $"Added by URL (readable by {repo.Account})");
                    return (repo, null);
                }

                lastError = $"{account.Login} can't see {slug}.";
            }
            catch (GitHubApiException ex)
            {
                lastError = ex.Message;
            }
        }

        return (null, $"Couldn't find {slug} with your signed-in accounts. {lastError}");
    }

    public void SaveIdentity(string account, GitIdentity identity) => Store.SaveIdentity(account, identity);

    /// <summary>Opens a visible window that pushes the project's current branch as its account.</summary>
    public TerminalLaunch Push(Project project, GitHubAccounts? accounts)
    {
        ArgumentNullException.ThrowIfNull(project);
        string path = project.LocalPath ?? throw new InvalidOperationException($"{project.NameWithOwner} isn't on this PC.");
        string git = Git.Program ?? throw new InvalidOperationException("git isn't installed.");
        string? account = Features.Accounts ? CredentialAccount(project, accounts) : null;
        TerminalJob job = TerminalLauncher.Push(path, project.Name, git, GitHubCli.Program, account, project.Safety?.UnpushedCommits ?? 0);
        TerminalLaunch launch = TerminalLauncher.Open(job);
        Store.Log(ActivityKind.Push, project.NameWithOwner, $"Started pushing {Project.Plural(project.Safety?.UnpushedCommits ?? 0, "commit")}" + (account is null ? string.Empty : " as " + account));
        return launch;
    }

    /// <summary>Opens GitHub's sign-in in a visible window; the identity can be saved before the account exists.</summary>
    public TerminalLaunch SignIn(string? login, GitIdentity? identity, string? activeBefore)
    {
        Require(Features.Accounts, ProjectsFeatures.AccountsFlag);
        string gh = GitHubCli.Program ?? throw new InvalidOperationException("The GitHub CLI isn't installed. Install it with: winget install GitHub.cli");
        if (login is { Length: > 0 } && identity is { IsBlank: false })
        {
            Store.SaveIdentity(login, identity);
        }

        TerminalLaunch launch = TerminalLauncher.Open(TerminalLauncher.SignIn(gh, activeBefore));
        Store.Log(ActivityKind.SignIn, login is { Length: > 0 } ? login : "GitHub", "Started signing in");
        return launch;
    }

    public IReadOnlyList<ActivityEntry> Activity()
    {
        Require(Features.Activity, ProjectsFeatures.ActivityFlag);
        return Store.State.Activity;
    }

    /// <summary>The user's cleaner: Recycle Bin, quarantine and History, with the project roots as dev roots for the guard.</summary>
    public Cleaner DefaultCleaner(string? projectPath = null)
    {
        var roots = Roots().ToList();
        if (projectPath is not null && Path.GetDirectoryName(projectPath) is { } parent)
        {
            roots.Add(parent);
        }

        return new Cleaner(SafetyGuard.ForCurrentUser(devRoots: roots), new HistoryStore(), new QuarantineStore(), new RecycleBin());
    }

    private DownloadRequest DownloadRequestFor(Project project, CloneStrategy strategy, string? destination, string? account)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new DownloadRequest
        {
            NameWithOwner = project.NameWithOwner,
            Destination = destination ?? ProjectMerger.Destination(project, Store.State, Profile),
            Strategy = strategy,
            Account = account ?? project.Account,
            RemoteKB = project.RemoteKB,
        };
    }

    private void RememberClones(IEnumerable<LocalClone> local)
    {
        List<LocalClone> github = local.Where(c => c.Slug is not null).ToList();
        if (github.Count == 0)
        {
            return;
        }

        Store.Update(s =>
        {
            var known = s.Known.ToList();
            foreach (LocalClone c in github)
            {
                int i = known.FindIndex(k => k.NameWithOwner.Equals(c.Slug, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                {
                    known[i] = known[i] with { LastPath = c.Path, RemovedAt = null };
                }
                else
                {
                    known.Add(new KnownRepo { NameWithOwner = c.Slug!, LastPath = c.Path });
                }
            }

            return s with { Known = known };
        });
    }

    private static void Require(bool enabled, string flag)
    {
        if (!enabled)
        {
            throw new InvalidOperationException($"This part of Projects is turned off ({flag}).");
        }
    }
}
