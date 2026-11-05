using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DevSweep.Core.Projects.GitHub;

/// <summary>A repository object of the GitHub REST API (only the fields DevSweep reads).</summary>
public sealed record GitHubRepoDto
{
    public string Name { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool Private { get; init; }

    public DateTimeOffset? PushedAt { get; init; }

    public string HtmlUrl { get; init; } = string.Empty;

    public long? Size { get; init; }

    public bool Archived { get; init; }
}

/// <summary>The signed-in user (<c>GET /user</c>).</summary>
public sealed record GitHubUserDto
{
    public string Login { get; init; } = string.Empty;
}

/// <summary>An API error body.</summary>
public sealed record GitHubErrorDto
{
    public string? Message { get; init; }
}

/// <summary>The body of <c>POST /user/repos</c> and <c>POST /orgs/{org}/repos</c>.</summary>
public sealed record GitHubCreateRepoDto
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Private { get; init; }
}

/// <summary>A GitHub API call that did not succeed.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException()
    {
    }

    public GitHubApiException(string message)
        : base(message)
    {
    }

    public GitHubApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public GitHubApiException(HttpStatusCode status, string message)
        : base(message) => Status = status;

    public HttpStatusCode? Status { get; }
}

/// <summary>
/// The GitHub REST API over <see cref="HttpClient"/> with source-generated JSON, one
/// account's token per call (in the Authorization header only). Lists every repository an
/// account can open: its own, its organisations' and ones shared with it
/// (<c>affiliation=owner,collaborator,organization_member</c>, the 0.9.8 behaviour), all
/// pages, archived ones left out, newest push first.
/// </summary>
public sealed class GitHubApi(HttpClient http, Uri? baseUri = null)
{
    public const int MaxPages = 50;

    public Uri BaseUri { get; } = baseUri ?? new Uri("https://api.github.com/");

    public async Task<IReadOnlyList<RemoteRepo>> ListReposAsync(GitHubToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        var repos = new List<RemoteRepo>();
        Uri? next = new(BaseUri, "user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=pushed");
        for (int page = 0; next is not null && page < MaxPages; page++)
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, next, token, null, cancellationToken).ConfigureAwait(false);
            List<GitHubRepoDto> items = await ReadAsync(response, GitHubJsonContext.Default.ListGitHubRepoDto, cancellationToken).ConfigureAwait(false) ?? [];
            repos.AddRange(items.Where(r => !r.Archived).Select(r => ToRemote(r, token.Login)));
            next = NextPage(response);
        }

        return repos.OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>One repository by <c>owner/name</c> (for Add by URL), or null when this account cannot see it.</summary>
    public async Task<RemoteRepo?> GetRepoAsync(string slug, GitHubToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(token);
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, new Uri(BaseUri, "repos/" + slug), token, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        GitHubRepoDto? repo = await ReadAsync(response, GitHubJsonContext.Default.GitHubRepoDto, cancellationToken).ConfigureAwait(false);
        return repo is null ? null : ToRemote(repo, token.Login);
    }

    /// <summary>The login a token belongs to (checks that <c>gh auth token --user</c> gave the right account's token).</summary>
    public async Task<string> GetLoginAsync(GitHubToken token, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, new Uri(BaseUri, "user"), token, null, cancellationToken).ConfigureAwait(false);
        GitHubUserDto? user = await ReadAsync(response, GitHubJsonContext.Default.GitHubUserDto, cancellationToken).ConfigureAwait(false);
        return user?.Login ?? throw new GitHubApiException("GitHub did not say which account the token belongs to.");
    }

    /// <summary>Creates an empty repository for an account or one of its organisations (Publish).</summary>
    public async Task<RemoteRepo> CreateRepoAsync(string owner, string name, string? description, bool isPrivate, GitHubToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(token);
        string path = owner.Equals(token.Login, StringComparison.OrdinalIgnoreCase) ? "user/repos" : $"orgs/{owner}/repos";
        var body = new GitHubCreateRepoDto { Name = name, Description = string.IsNullOrWhiteSpace(description) ? null : description, Private = isPrivate };
        using var content = new StringContent(JsonSerializer.Serialize(body, GitHubJsonContext.Default.GitHubCreateRepoDto), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, new Uri(BaseUri, path), token, content, cancellationToken).ConfigureAwait(false);
        GitHubRepoDto repo = await ReadAsync(response, GitHubJsonContext.Default.GitHubRepoDto, cancellationToken).ConfigureAwait(false)
            ?? throw new GitHubApiException("GitHub did not return the new repository.");
        return ToRemote(repo, token.Login);
    }

    /// <summary>
    /// A repository two signed-in accounts can both see is listed once: under the account that
    /// owns it when there is one, else the first account that saw it (the Mac <c>mergeAccounts</c>).
    /// </summary>
    public static IReadOnlyList<RemoteRepo> MergeAccounts(IEnumerable<RemoteRepo> repos)
    {
        ArgumentNullException.ThrowIfNull(repos);
        var best = new Dictionary<string, RemoteRepo>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (RemoteRepo r in repos)
        {
            if (best.TryGetValue(r.NameWithOwner, out RemoteRepo? existing))
            {
                bool existingOwns = existing.Account.Equals(existing.Owner, StringComparison.OrdinalIgnoreCase);
                if (!existingOwns && r.Account.Equals(r.Owner, StringComparison.OrdinalIgnoreCase))
                {
                    best[r.NameWithOwner] = r;
                }
            }
            else
            {
                best[r.NameWithOwner] = r;
                order.Add(r.NameWithOwner);
            }
        }

        return order.Select(k => best[k]).ToList();
    }

    /// <summary>The <c>rel="next"</c> URL of a <c>Link</c> header.</summary>
    public static Uri? NextPage(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string part in string.Join(',', values).Split(','))
        {
            string[] pieces = part.Split(';');
            if (pieces.Length >= 2 && pieces.Skip(1).Any(p => p.Trim() == "rel=\"next\""))
            {
                string url = pieces[0].Trim().TrimStart('<').TrimEnd('>');
                return Uri.TryCreate(url, UriKind.Absolute, out Uri? next) ? next : null;
            }
        }

        return null;
    }

    private static RemoteRepo ToRemote(GitHubRepoDto r, string account) => new()
    {
        Name = r.Name,
        NameWithOwner = r.FullName,
        Description = r.Description ?? string.Empty,
        IsPrivate = r.Private,
        PushedAt = r.PushedAt,
        Url = r.HtmlUrl,
        DiskUsageKB = r.Size ?? 0,
        Account = account,
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, GitHubToken token, HttpContent? content, CancellationToken cancellationToken)
    {
        if (uri.Host != BaseUri.Host)
        {
            throw new GitHubApiException($"Refusing to send a token to {uri.Host}.");
        }

        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevSweep", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException("Couldn't reach GitHub: " + ex.Message, ex);
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                message = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.GitHubErrorDto)?.Message;
            }
            catch (JsonException)
            {
                // Not JSON: say the status only.
            }

            bool rateLimited = response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? left) && left.FirstOrDefault() == "0";
            string text = rateLimited ? "GitHub's rate limit for this account is used up; try again later."
                : string.Create(CultureInfo.InvariantCulture, $"GitHub answered {(int)response.StatusCode} {response.StatusCode}{(message is null ? string.Empty : ": " + message)}");
            throw new GitHubApiException(response.StatusCode, text);
        }

        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException("GitHub's answer could not be read: " + ex.Message, ex);
        }
    }
}
