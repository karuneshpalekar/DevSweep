using System.Globalization;
using DevSweep.Core.Platform;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Scanning;

namespace DevSweep.Core.Projects.Actions;

/// <summary>What to download, and where.</summary>
public sealed record DownloadRequest
{
    public required string NameWithOwner { get; init; }

    /// <summary>The folder to create (it must not exist, or be empty).</summary>
    public required string Destination { get; init; }

    public CloneStrategy Strategy { get; init; } = CloneStrategy.Blobless;

    /// <summary>The signed-in account whose token and commit identity are used.</summary>
    public string? Account { get; init; }

    /// <summary>GitHub's size in KB, for the estimate.</summary>
    public long RemoteKB { get; init; }

    /// <summary>Overrides <c>https://github.com/&lt;owner&gt;/&lt;name&gt;.git</c> (tests clone from a local bare repository).</summary>
    public string? CloneUrl { get; init; }
}

/// <summary>What a download would run, shown before it runs.</summary>
/// <param name="Arguments">git's arguments (no token: credentials come from the helper).</param>
/// <param name="EstimateBytes">From GitHub's size and the strategy; null when GitHub did not say.</param>
/// <param name="Refusal">Why it can't run (the folder has files in it...), or null.</param>
public sealed record DownloadPlan(IReadOnlyList<string> Arguments, string Destination, string Url, long? EstimateBytes, string? Refusal);

/// <summary>What a download did.</summary>
public sealed record DownloadResult(bool Succeeded, string Message, string? Path = null, long? Bytes = null, GitIdentity? Identity = null);

/// <summary>
/// Downloads a repository (blobless, shallow or full) with plain git, no shell: the URL is
/// always https://github.com/&lt;slug&gt;.git, authenticated per command as the chosen account
/// (<see cref="GitCredentials"/>; global config and the active gh account untouched), with
/// <c>core.longpaths=true</c> written into the new clone's own config so paths over 260
/// characters work. The account's commit identity is then written into the clone's local
/// config (never the global one), and the download is remembered (strategy, folder) and logged.
/// </summary>
public sealed class Downloader(IGit git, IGitHubCli gh, ProjectStore store, IItemMeasurer? measurer = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public static DownloadPlan Plan(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string url = request.CloneUrl ?? $"https://{RemoteUrls.GitHubHost}/{request.NameWithOwner}.git";
        string destination = request.Destination;
        IReadOnlyList<string> args = ["clone", "--config", "core.longpaths=true", .. request.Strategy.GitArguments(), "--", url, destination];
        return new DownloadPlan(args, destination, url, request.Strategy.EstimateBytes(request.RemoteKB), WhyNot(request, destination));
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        DownloadPlan plan = Plan(request);
        if (plan.Refusal is { } refusal)
        {
            return new DownloadResult(false, refusal);
        }

        GitHubToken? token = null;
        if (request.CloneUrl is null)
        {
            if (request.Account is null)
            {
                return new DownloadResult(false, "Sign in to GitHub first (Accounts, Sign in).");
            }

            (token, string? error) = await gh.TokenAsync(request.Account, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return new DownloadResult(false, error ?? "No token.");
            }
        }

        string? parent = Path.GetDirectoryName(plan.Destination);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        GitResult clone = await git.RunAsync(null, plan.Arguments, GitCredentials.For(gh.Program, token, Timeout), cancellationToken).ConfigureAwait(false);
        if (!clone.Ok)
        {
            return new DownloadResult(false, $"Downloading {request.NameWithOwner} failed: {clone.Error}");
        }

        GitIdentity? identity = store.Identity(request.Account);
        string? identityError = await ApplyIdentityAsync(git, plan.Destination, identity, cancellationToken).ConfigureAwait(false);
        long bytes = Measure(plan.Destination, cancellationToken);
        store.Update(s =>
        {
            var strategies = new Dictionary<string, CloneStrategy>(s.Strategies, StringComparer.OrdinalIgnoreCase) { [request.NameWithOwner] = request.Strategy };
            var opened = new Dictionary<string, DateTimeOffset>(s.LastOpened, StringComparer.OrdinalIgnoreCase) { [request.NameWithOwner] = DateTimeOffset.UtcNow };
            return s with { Strategies = strategies, LastOpened = opened };
        });
        store.Remember(request.NameWithOwner, k => (k ?? new KnownRepo { NameWithOwner = request.NameWithOwner }) with
        {
            Account = request.Account ?? k?.Account,
            LastPath = plan.Destination,
            RemovedAt = null,
            RemoteKB = request.RemoteKB > 0 ? request.RemoteKB : k?.RemoteKB,
        });
        store.Log(ActivityKind.Download, request.NameWithOwner,
            string.Create(CultureInfo.InvariantCulture, $"{request.Strategy.Title()} clone · {SizeText.Format(bytes)} · {plan.Destination}"));
        string message = $"Downloaded {request.NameWithOwner} to {plan.Destination}"
            + (identity is { IsBlank: false } ? $"; commits are made as {identity}" : "; no commit identity is set for this account, so git's global one is used")
            + (identityError is null ? "." : $". The identity could not be written: {identityError}");
        return new DownloadResult(true, message, plan.Destination, bytes, identity);
    }

    /// <summary>Writes the identity into the clone's own config (<c>git config --local</c>); null when it worked or there was none.</summary>
    public static async Task<string?> ApplyIdentityAsync(IGit git, string folder, GitIdentity? identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (identity is null || identity.IsBlank)
        {
            return null;
        }

        foreach ((string key, string value) in (List<(string, string)>)[("user.name", identity.Name.Trim()), ("user.email", identity.Email.Trim())])
        {
            if (value.Length == 0)
            {
                continue;
            }

            GitResult set = await git.RunAsync(folder, ["config", "--local", key, value], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!set.Ok)
            {
                return set.Error;
            }
        }

        return null;
    }

    private static string? WhyNot(DownloadRequest request, string destination)
    {
        if (!Path.IsPathFullyQualified(destination))
        {
            return $"{destination} is not a full path.";
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            return $"A folder with files already exists at {destination}. Choose another place.";
        }

        if (File.Exists(destination))
        {
            return $"A file already exists at {destination}.";
        }

        if (LinkOnTheWay(destination) is { } link)
        {
            return $"{link} is a junction or link; DevSweep doesn't download through links.";
        }

        return RemoteUrls.ParseSlug(request.NameWithOwner) is null && request.CloneUrl is null ? $"{request.NameWithOwner} isn't owner/name." : null;
    }

    /// <summary>A junction or link on the way to <paramref name="destination"/> (folders still to be created are not looked at).</summary>
    internal static string? LinkOnTheWay(string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? existing = destination;
        while (existing is not null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        if (existing is null || Path.GetPathRoot(existing) == existing)
        {
            return null;
        }

        if (ReparseGuard.FindReparseAncestor(existing) is { } ancestor)
        {
            return ancestor;
        }

        try
        {
            return FileInspector.ReadEntry(existing).IsLink ? existing : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return existing;
        }
    }

    private long Measure(string path, CancellationToken cancellationToken)
    {
        try
        {
            IItemMeasurer m = measurer ?? (OperatingSystem.IsWindows() ? WalkerMeasurer.Instance : Discovery.ManagedMeasurer.Instance);
            return m.Measure(path, SizeMode.Overview, TimeSpan.FromSeconds(30), cancellationToken).Bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
