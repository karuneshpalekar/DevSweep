using System.Text.RegularExpressions;
using DevSweep.Core.Platform;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Projects.Actions;

/// <summary>A folder to publish as a new GitHub repository.</summary>
public sealed record PublishRequest
{
    public required string Folder { get; init; }

    /// <summary>The signed-in account whose token and identity are used.</summary>
    public required string Account { get; init; }

    /// <summary>The account itself or one of its organisations; null means the account.</summary>
    public string? Owner { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsPrivate { get; init; } = true;

    /// <summary>The user has seen the files that look like secrets and wants to publish anyway.</summary>
    public bool AcknowledgeSecrets { get; init; }

    /// <summary>Overrides the push URL and skips creating the repository on GitHub (tests push to a local bare repository).</summary>
    public string? PushUrl { get; init; }
}

/// <summary>What the secret check found before a publish.</summary>
public sealed record PublishPreview(IReadOnlyList<string> Files, IReadOnlyList<SecretFile> Secrets, string? Refusal);

/// <summary>What publishing did.</summary>
public sealed record PublishResult(bool Succeeded, string Message, PublishPreview? Preview = null, RemoteRepo? Repo = null);

/// <summary>
/// Publishes a folder to GitHub (the Mac Publish sheet): first the secret check on exactly the
/// files that would be uploaded (git's own list, .gitignore respected; nothing is changed until
/// the user has seen them), then <c>git init</c> if needed, the account's identity in the local
/// config, a first commit, the repository created through the API, and a push authenticated as
/// that account (<see cref="GitCredentials"/>). A folder with OneDrive files that are only in the
/// cloud is refused (adding them would download them).
/// </summary>
public sealed partial class Publisher(IGit git, IGitHubCli gh, GitHubApi api, ProjectStore store, ISecretCheck secrets, IItemMeasurer? measurer = null)
{
    public async Task<PublishPreview> PreviewAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string folder = Path.GetFullPath(request.Folder);
        string? refusal = Refusal(request, folder);
        if (refusal is not null)
        {
            return new PublishPreview([], [], refusal);
        }

        (IReadOnlyList<string>? files, string? error) = await FilesToPublishAsync(folder, cancellationToken).ConfigureAwait(false);
        if (files is null)
        {
            return new PublishPreview([], [], error);
        }

        string? profile = OperatingSystem.IsWindows() ? KnownFolders.Profile : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new PublishPreview(files, secrets.Check(files, profile), files.Count == 0 ? "The folder has no files to publish." : null);
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        PublishPreview preview = await PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        if (preview.Refusal is { } refusal)
        {
            return new PublishResult(false, refusal, preview);
        }

        if (preview.Secrets.Count > 0 && !request.AcknowledgeSecrets)
        {
            return new PublishResult(false, $"{Project.Plural(preview.Secrets.Count, "file")} look like secrets: "
                + string.Join(", ", preview.Secrets.Take(4).Select(s => $"{Path.GetFileName(s.Path)} ({s.What})"))
                + ". Add them to .gitignore, or confirm that you want to publish them.", preview);
        }

        string folder = Path.GetFullPath(request.Folder);
        string owner = request.Owner ?? request.Account;
        GitHubToken? token = null;
        if (request.PushUrl is null)
        {
            (token, string? error) = await gh.TokenAsync(request.Account, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return new PublishResult(false, error ?? "No token.", preview);
            }
        }

        if (!Directory.Exists(Path.Join(folder, ".git")) && !File.Exists(Path.Join(folder, ".git")))
        {
            GitResult init = await git.RunAsync(folder, ["init", "-q", "-b", "main"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!init.Ok)
            {
                return new PublishResult(false, "git init failed: " + init.Error, preview);
            }
        }

        string? identityError = await Downloader.ApplyIdentityAsync(git, folder, store.Identity(request.Account), cancellationToken).ConfigureAwait(false);
        if (identityError is not null)
        {
            return new PublishResult(false, "The commit identity could not be written: " + identityError, preview);
        }

        bool hasHead = (await git.RunAsync(folder, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok;
        GitResult add = await git.RunAsync(folder, ["add", "-A"], new GitCall { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken).ConfigureAwait(false);
        if (!add.Ok)
        {
            return new PublishResult(false, "git add failed: " + add.Error, preview);
        }

        GitResult staged = await git.RunAsync(folder, ["diff", "--cached", "--name-only", "-z"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (staged.Ok && staged.NulSeparated().Count > 0)
        {
            GitResult commit = await git.RunAsync(folder, ["commit", "-q", "-m", hasHead ? "Add files" : "Initial commit"], new GitCall { Timeout = TimeSpan.FromMinutes(5) }, cancellationToken).ConfigureAwait(false);
            if (!commit.Ok)
            {
                return new PublishResult(false, "git commit failed: " + commit.Error + " (set a commit identity for this account in Accounts)", preview);
            }
        }
        else if (!hasHead)
        {
            return new PublishResult(false, "The folder has no files to commit.", preview);
        }

        RemoteRepo? repo = null;
        string pushUrl = request.PushUrl ?? $"https://{RemoteUrls.GitHubHost}/{owner}/{request.Name}.git";
        if (request.PushUrl is null)
        {
            try
            {
                repo = await api.CreateRepoAsync(owner, request.Name, request.Description, request.IsPrivate, token!, cancellationToken).ConfigureAwait(false);
                pushUrl = $"https://{RemoteUrls.GitHubHost}/{repo.NameWithOwner}.git";
            }
            catch (GitHubApiException ex)
            {
                return new PublishResult(false, $"GitHub didn't create {owner}/{request.Name}: {ex.Message}. The folder now has a local commit; nothing was uploaded.", preview);
            }
        }

        GitResult remote = await git.RunAsync(folder, ["remote", "add", "origin", pushUrl], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!remote.Ok)
        {
            return new PublishResult(false, "Adding the origin remote failed: " + remote.Error, preview, repo);
        }

        GitResult push = await git.RunAsync(folder, ["push", "-u", "origin", "HEAD"], GitCredentials.For(gh.Program, token, TimeSpan.FromMinutes(30)), cancellationToken).ConfigureAwait(false);
        if (!push.Ok)
        {
            return new PublishResult(false, "The repository was created, but pushing failed: " + push.Error + ". Use Push to try again.", preview, repo);
        }

        string slug = repo?.NameWithOwner ?? $"{owner}/{request.Name}";
        store.Remember(slug, k => (k ?? new KnownRepo { NameWithOwner = slug }) with { Account = request.Account, IsPrivate = request.IsPrivate, LastPath = folder, RemovedAt = null });
        store.Log(ActivityKind.Publish, slug, (request.IsPrivate ? "Private" : "Public") + $" repository from {folder}"
            + (preview.Secrets.Count > 0 ? $"; published with {Project.Plural(preview.Secrets.Count, "file")} that look like secrets (confirmed)" : string.Empty));
        return new PublishResult(true, $"Published {folder} as {slug}.", preview, repo);
    }

    /// <summary>The files git would add (tracked and untracked, .gitignore respected), as full paths.</summary>
    public async Task<(IReadOnlyList<string>? Files, string? Error)> FilesToPublishAsync(string folder, CancellationToken cancellationToken = default)
    {
        bool isRepo = Directory.Exists(Path.Join(folder, ".git")) || File.Exists(Path.Join(folder, ".git"));
        string? scratch = null;
        try
        {
            GitResult list;
            if (isRepo)
            {
                list = await git.RunAsync(folder, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Ask git which files it would add, without changing the folder: a scratch
                // repository elsewhere, with the folder as its work tree, reads its .gitignore files.
                scratch = Path.Join(Path.GetTempPath(), "DevSweep", "publish-" + Guid.NewGuid().ToString("N")[..8]);
                GitResult init = await git.RunAsync(null, ["init", "-q", scratch], cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!init.Ok)
                {
                    return (null, "git init failed: " + init.Error);
                }

                list = await git.RunAsync(null, ["--git-dir=" + Path.Join(scratch, ".git"), "--work-tree=" + folder, "ls-files", "--others", "--exclude-standard", "-z"], cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return list.Ok
                ? (list.NulSeparated().Select(f => Path.GetFullPath(Path.Join(folder, f))).ToList(), null)
                : (null, "git could not list the files: " + list.Error);
        }
        finally
        {
            if (scratch is not null && Directory.Exists(scratch))
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A scratch folder in %TEMP%; harmless if it stays.
                }
            }
        }
    }

    private string? Refusal(PublishRequest request, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return $"{folder} doesn't exist.";
        }

        if (OperatingSystem.IsWindows() && ReparseGuard.FindReparseAncestor(folder) is { } link)
        {
            return $"{link} is a junction or link; DevSweep doesn't publish through links.";
        }

        if (!RepoName().IsMatch(request.Name) || request.Name is "." or "..")
        {
            return $"\"{request.Name}\" can't be a repository name (letters, digits, '.', '-' and '_').";
        }

        if (!GitHubCli.IsLogin(request.Account) || (request.Owner is { } o && !GitHubCli.IsLogin(o)))
        {
            return "The account or owner isn't a GitHub name.";
        }

        if (git.Program is null)
        {
            return "git isn't installed (Git for Windows: winget install Git.Git).";
        }

        try
        {
            IItemMeasurer m = measurer ?? (OperatingSystem.IsWindows() ? WalkerMeasurer.Instance : Discovery.ManagedMeasurer.Instance);
            if (m.Measure(folder, SizeMode.Overview, TimeSpan.FromSeconds(60), CancellationToken.None).CloudPlaceholderCount is > 0 and long n)
            {
                return $"{Project.Plural(n, "file")} in this folder are only in OneDrive; publishing would download them. Make the folder \"Always keep on this device\" first.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The folder could not be read: " + ex.Message;
        }

        string config = Path.Join(folder, ".git", "config");
        if (File.Exists(config) && File.ReadAllText(config).Contains("[remote \"origin\"]", StringComparison.Ordinal))
        {
            return "This folder already has an origin remote; it is already published somewhere.";
        }

        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();
}
