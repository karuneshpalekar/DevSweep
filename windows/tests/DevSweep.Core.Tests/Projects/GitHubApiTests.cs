using System.Net;
using System.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.GitHub;
using Xunit;

namespace DevSweep.Core.Tests.Projects;

/// <summary>Answers requests with recorded GitHub API responses and records what was asked (no network, no token in CI).</summary>
internal sealed class RecordedGitHub : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> Bodies { get; } = [];

    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string? link = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (link is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", link);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        string key = request.Method.Method + " " + request.RequestUri!.PathAndQuery;
        return Routes.TryGetValue(key, out Func<HttpResponseMessage>? answer) ? answer() : Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
    }
}

/// <summary>The GitHub API client against recorded responses (the 0.9.8 behaviour: own, organisation and shared repositories).</summary>
public sealed class GitHubApiTests
{
    private const string Page1 = """
        [
          {"name":"DevSweep","full_name":"me/DevSweep","description":"cleaner","private":false,"pushed_at":"2026-10-16T10:00:00Z","html_url":"https://github.com/me/DevSweep","size":1200,"archived":false,"owner":{"login":"me"}},
          {"name":"site","full_name":"SomeOrg/site","description":null,"private":true,"pushed_at":"2026-10-17T10:00:00Z","html_url":"https://github.com/SomeOrg/site","size":50,"archived":false}
        ]
        """;

    private const string Page2 = """
        [
          {"name":"shared","full_name":"friend/shared","description":"","private":true,"pushed_at":null,"html_url":"https://github.com/friend/shared","size":0,"archived":false},
          {"name":"old","full_name":"me/old","description":"","private":false,"pushed_at":"2020-01-01T00:00:00Z","html_url":"https://github.com/me/old","size":1,"archived":true}
        ]
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GitHubApi Api, RecordedGitHub Handler) Api()
    {
        var handler = new RecordedGitHub();
        return (new GitHubApi(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Lists_own_organisation_and_shared_repositories_on_every_page_but_not_archived()
    {
        (GitHubApi api, RecordedGitHub gh) = Api();
        gh.Routes["GET /user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=pushed"] =
            () => RecordedGitHub.Json(Page1, link: "<https://api.github.com/user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=pushed&page=2>; rel=\"next\", <https://api.github.com/user/repos?page=2>; rel=\"last\"");
        gh.Routes["GET /user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=pushed&page=2"] = () => RecordedGitHub.Json(Page2);

        IReadOnlyList<RemoteRepo> repos = await api.ListReposAsync(new GitHubToken("me", "gho_me"), Ct);

        Assert.Equal(["SomeOrg/site", "me/DevSweep", "friend/shared"], repos.Select(r => r.NameWithOwner));
        Assert.All(repos, r => Assert.Equal("me", r.Account));
        Assert.True(repos[0].IsPrivate);
        Assert.Equal(1200, repos[1].DiskUsageKB);
        Assert.Equal(string.Empty, repos[0].Description);
        Assert.Equal(2, gh.Requests.Count);
        Assert.All(gh.Requests, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
            Assert.Equal("gho_me", r.Headers.Authorization.Parameter);
            Assert.Equal("2022-11-28", r.Headers.GetValues("X-GitHub-Api-Version").Single());
        });
    }

    [Fact]
    public async Task One_repository_by_slug_and_not_found_is_null()
    {
        (GitHubApi api, RecordedGitHub gh) = Api();
        gh.Routes["GET /repos/octo/fork"] = () => RecordedGitHub.Json("""{"name":"fork","full_name":"octo/fork","private":false,"html_url":"https://github.com/octo/fork","size":10}""");
        RemoteRepo? repo = await api.GetRepoAsync("octo/fork", new GitHubToken("me", "t"), Ct);
        Assert.Equal("octo/fork", repo!.NameWithOwner);
        Assert.Null(await api.GetRepoAsync("octo/secret", new GitHubToken("me", "t"), Ct));
    }

    [Fact]
    public async Task Errors_say_what_github_said_and_rate_limits_are_named()
    {
        (GitHubApi api, RecordedGitHub gh) = Api();
        gh.Routes["GET /user"] = () => RecordedGitHub.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);
        GitHubApiException bad = await Assert.ThrowsAsync<GitHubApiException>(() => api.GetLoginAsync(new GitHubToken("me", "t"), Ct));
        Assert.Contains("Bad credentials", bad.Message, StringComparison.Ordinal);

        gh.Routes["GET /user"] = () =>
        {
            HttpResponseMessage r = RecordedGitHub.Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);
            r.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return r;
        };
        GitHubApiException limited = await Assert.ThrowsAsync<GitHubApiException>(() => api.GetLoginAsync(new GitHubToken("me", "t"), Ct));
        Assert.Contains("rate limit", limited.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_is_never_sent_to_another_host()
    {
        (GitHubApi api, RecordedGitHub gh) = Api();
        gh.Routes["GET /user/repos?affiliation=owner,collaborator,organization_member&per_page=100&sort=pushed"] =
            () => RecordedGitHub.Json(Page1, link: "<https://evil.example/steal>; rel=\"next\"");
        await Assert.ThrowsAsync<GitHubApiException>(() => api.ListReposAsync(new GitHubToken("me", "t"), Ct));
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Publish_creates_the_repository_for_the_account_or_its_organisation()
    {
        (GitHubApi api, RecordedGitHub gh) = Api();
        gh.Routes["POST /user/repos"] = () => RecordedGitHub.Json("""{"name":"new","full_name":"me/new","private":true,"html_url":"https://github.com/me/new"}""", HttpStatusCode.Created);
        gh.Routes["POST /orgs/acme/repos"] = () => RecordedGitHub.Json("""{"name":"new","full_name":"acme/new","private":false,"html_url":"https://github.com/acme/new"}""", HttpStatusCode.Created);
        RemoteRepo mine = await api.CreateRepoAsync("me", "new", "  ", true, new GitHubToken("me", "t"), Ct);
        RemoteRepo org = await api.CreateRepoAsync("acme", "new", "d", false, new GitHubToken("me", "t"), Ct);
        Assert.Equal("me/new", mine.NameWithOwner);
        Assert.Equal("acme/new", org.NameWithOwner);
        Assert.Equal("""{"name":"new","private":true}""", gh.Bodies[0]);
        Assert.Equal("""{"name":"new","description":"d","private":false}""", gh.Bodies[1]);
    }

    [Fact]
    public void A_repository_both_accounts_can_see_is_listed_once_under_its_owner()
    {
        static RemoteRepo R(string slug, string account) => new() { Name = slug.Split('/')[1], NameWithOwner = slug, Account = account };
        IReadOnlyList<RemoteRepo> merged = GitHubApi.MergeAccounts([R("work/app", "me"), R("Org/site", "me"), R("work/app", "work"), R("Org/site", "work")]);
        Assert.Equal(2, merged.Count);
        Assert.Equal("work", merged.Single(r => r.NameWithOwner == "work/app").Account);
        Assert.Equal("me", merged.Single(r => r.NameWithOwner == "Org/site").Account);
    }
}

/// <summary>
/// A read-only call to the real API, only when CI provides <c>GITHUB_TOKEN</c> (never required).
/// </summary>
public sealed class GitHubLiveSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_real_api_answers_a_read_only_request()
    {
        string token = Environment.GetEnvironmentVariable("DEVSWEEP_LIVE_GITHUB_TOKEN") ?? string.Empty;
        Assert.SkipWhen(token.Length == 0, "No DEVSWEEP_LIVE_GITHUB_TOKEN: the live GitHub smoke test runs only where CI provides a token.");

        using var http = new HttpClient();
        var api = new GitHubApi(http);
        RemoteRepo? repo = await api.GetRepoAsync("karuneshpalekar/DevSweep", new GitHubToken("ci", token), TestContext.Current.CancellationToken);
        output.WriteLine($"live: {repo?.NameWithOwner} private={repo?.IsPrivate} size={repo?.DiskUsageKB} KB pushed={repo?.PushedAt:u}");
        Assert.NotNull(repo);
        Assert.Equal("DevSweep", repo.Name);
    }
}
