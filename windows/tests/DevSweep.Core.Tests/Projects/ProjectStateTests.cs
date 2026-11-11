using DevSweep.Core.Modules;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using Xunit;

namespace DevSweep.Core.Tests.Projects;

/// <summary>Joining local and remote, destinations, the idle list, the store and the module flags (the Mac tests, ported).</summary>
public sealed class ProjectStateTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "devsweep-proj-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectStateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RemovalSafety Safety(int unpushed = 0, bool hasRemote = true) => new()
    {
        Inspected = true,
        HasRemote = hasRemote,
        UnpushedCommits = unpushed,
        State = unpushed == 0 && hasRemote ? SafetyState.Safe : SafetyState.NotSafe,
    };

    private static LocalClone Clone(string path, string? slug, RemovalSafety safety, long size = 1, DateTimeOffset? commit = null)
        => new() { Path = path, Slug = slug, Safety = safety, Size = size, LastCommit = commit };

    [Fact]
    public void Merge_joins_local_remote_and_removed_repositories()
    {
        RemoteRepo[] remote =
        [
            new() { Name = "Widget", NameWithOwner = "Octo/Widget", Description = "d", IsPrivate = true, DiskUsageKB = 4000, Account = "octo" },
            new() { Name = "other", NameWithOwner = "octo/other", DiskUsageKB = 100, Account = "octo" },
        ];
        LocalClone clone = Clone(@"C:\x\widget", "octo/widget", Safety(unpushed: 2), 123, DateTimeOffset.UtcNow);
        LocalClone orgClone = Clone(@"C:\x\team", "someorg/team", Safety(), 5);
        LocalClone loose = Clone(Path.Join(Path.GetTempPath(), "x", "scratch"), null, Safety(hasRemote: false));
        var state = new ProjectsState
        {
            Known = [new KnownRepo { NameWithOwner = "octo/removed", Account = "octo" }],
            LastOpened = new() { ["Octo/Widget"] = DateTimeOffset.FromUnixTimeSeconds(1000) },
        };

        IReadOnlyList<Project> projects = ProjectMerger.Merge([clone, orgClone, loose], remote, state);
        Project P(string id) => projects.Single(p => p.NameWithOwner == id);

        Assert.Equal(5, projects.Count);
        Assert.Equal(@"C:\x\widget", P("Octo/Widget").LocalPath);
        Assert.Equal("2 commits not pushed", P("Octo/Widget").StatusText);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), P("Octo/Widget").LastOpened);
        Assert.Equal("Only on GitHub", P("octo/other").StatusText);
        Assert.False(P("octo/removed").OnDisk);
        Assert.True(P("someorg/team").OnDisk);
        Assert.Null(P("someorg/team").Account);
        Assert.Equal("Only on this PC", P("scratch").StatusText);
        Assert.True(projects.Take(3).All(p => p.OnDisk));

        var stale = new ProjectsState { Known = [new KnownRepo { NameWithOwner = "oldorg/widget", Account = "oldorg" }] };
        Assert.Single(ProjectMerger.Merge([clone], remote, stale), p => p.Name.Equals("widget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_project_that_could_not_be_checked_never_reads_as_up_to_date()
    {
        var unknown = new RemovalSafety { State = SafetyState.NotChecked, NotCheckedReason = "In OneDrive, not checked: 3 files are only in the cloud" };
        IReadOnlyList<Project> projects = ProjectMerger.Merge([Clone(@"C:\od\app", "me/app", unknown)], [], new ProjectsState());
        Assert.StartsWith("In OneDrive, not checked", projects[0].StatusText, StringComparison.Ordinal);
        Assert.Equal(StatusLevel.Attention, projects[0].StatusLevel);
    }

    [Fact]
    public void Downloads_go_back_where_they_lived_else_into_the_workspace()
    {
        var p = new Project { Id = "a/b", Name = "b", NameWithOwner = "a/b", Owner = "a", OnGitHub = true, Account = "acct" };
        Assert.Equal(Path.Join(@"C:\Users\me", "Code", "acct", "b"), ProjectMerger.Destination(p, new ProjectsState(), @"C:\Users\me"));
        Assert.Equal(Path.Join(@"D:\src", "acct", "b"), ProjectMerger.Destination(p, new ProjectsState { WorkspaceRoot = @"D:\src" }, @"C:\Users\me"));
        var lived = new ProjectsState { Known = [new KnownRepo { NameWithOwner = "A/B", LastPath = @"C:\Users\me\Documents\GitHub\b" }] };
        Assert.Equal(@"C:\Users\me\Documents\GitHub\b", ProjectMerger.Destination(p, lived, @"C:\Users\me"));
    }

    [Fact]
    public void Idle_list_is_on_disk_untouched_biggest_first_with_reasons()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Project Make(string id, long size, int daysAgo, RemovalSafety? safety, bool onDisk = true) => new()
        {
            Id = id, Name = id, NameWithOwner = id, OnGitHub = true, LocalPath = onDisk ? @"C:\c\" + id : null, LocalSize = size,
            LastActivity = now.AddDays(-daysAgo), Safety = safety,
        };

        IdleCleanupResult idle = IdleCleanup.List(
            [Make("small", 10, 40, Safety()), Make("big", 1000, 40, Safety(unpushed: 1)), Make("fresh", 5000, 2, Safety()), Make("gone", 9000, 99, null, onDisk: false)],
            21, now);
        Assert.Equal(["big", "small"], idle.Projects.Select(p => p.Project.Id));
        Assert.Equal(["small"], idle.Safe.Select(p => p.Project.Id));
        Assert.Equal(["1 commit that isn't on GitHub"], idle.Projects[0].Reasons);
        Assert.Equal(1010, idle.TotalBytes);
        Assert.Equal(7, IdleCleanup.List([], 1, now).Days);
        Assert.Equal(90, IdleCleanup.Clamp(365));
        Assert.Equal(21, new ProjectsState().IdleDays);
    }

    [Fact]
    public void The_store_round_trips_caps_the_trail_and_keeps_an_unreadable_file()
    {
        string file = Path.Join(_dir, "projects.json");
        var store = new ProjectStore(file);
        store.SaveIdentity("me", new GitIdentity("Me", "me@example.com"));
        for (int i = 0; i < 505; i++)
        {
            store.Log(ActivityKind.Download, $"repo{i}", "detail");
        }

        Assert.Equal(ProjectStore.MaxActivity, store.State.Activity.Count);
        Assert.Equal("repo504", store.State.Activity[0].Subject);
        var reopened = new ProjectStore(file);
        Assert.Equal(500, reopened.State.Activity.Count);
        Assert.Equal("Me", reopened.Identity("ME")!.Name);

        store.SaveIdentity("me", new GitIdentity());
        Assert.Null(new ProjectStore(file).Identity("me"));

        File.WriteAllText(file, "{ not json");
        var broken = new ProjectStore(file);
        Assert.NotNull(broken.SetAsidePath);
        Assert.True(File.Exists(broken.SetAsidePath));
        Assert.Empty(broken.State.Activity);
    }

    [Fact]
    public void An_older_file_with_missing_fields_still_loads()
    {
        string file = Path.Join(_dir, "old.json");
        File.WriteAllText(file, """{"identities": {"me": {"name": "Me", "email": "me@example.com"}}, "strategies": {"me/app": "Shallow"}, "known": [{"nameWithOwner": "me/app", "account": "me"}]}""");
        var store = new ProjectStore(file);
        Assert.Equal("Me", store.Identity("me")!.Name);
        Assert.Equal(CloneStrategy.Shallow, store.State.Strategies["me/app"]);
        Assert.Single(store.State.Known);
        Assert.Empty(store.State.Activity);
        Assert.Equal(21, store.State.IdleDays);
    }

    [Fact]
    public void Projects_is_on_by_default_can_be_turned_off_and_its_tabs_have_flags()
    {
        Assert.Empty(DevSweepEngine.CreateDefault().Disabled);
        DevSweepEngine off = DevSweepEngine.CreateDefault(FeatureFlags.Default.With("-module:projects"));
        Assert.Contains("module:projects", off.Disabled);
        Assert.Empty(off.Services<ProjectsFeatures>());
        Assert.Null(ProjectsService.Create(off, new HttpClient()));

        DevSweepEngine on = DevSweepEngine.CreateDefault(FeatureFlags.Default.With("-module:projects.cleanup"));
        ProjectsFeatures features = Assert.Single(on.Services<ProjectsFeatures>());
        Assert.Equal(new ProjectsFeatures(Accounts: true, Cleanup: false, Activity: true), features);
        Assert.Equal([ProjectsFeatures.CleanupFlag], features.Disabled);
    }

    [Fact]
    public async Task Add_by_url_tries_each_account_and_never_asks_for_a_token_twice()
    {
        var handler = new RecordedGitHub();
        handler.Routes["GET /repos/octo/fork"] = () => RecordedGitHub.Json("""{"name":"fork","full_name":"octo/fork","private":true,"html_url":"x","size":7}""");
        var gh = new FixedGitHubCli([new GitHubAccount("me", true), new GitHubAccount("work", false)], new Dictionary<string, string> { ["me"] = "t1", ["work"] = "t2" }, program: @"C:\gh.exe");
        var store = new ProjectStore(Path.Join(_dir, "p.json"));
        var service = new ProjectsService(ProjectsFeatures.All, new GitCli(null), gh, new GitHubApi(new HttpClient(handler)), store, new DevSweep.Core.Locations.FixedEnvironment());

        (RemoteRepo? repo, string? error) = await service.AddByUrlAsync("https://github.com/octo/fork", TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Equal("me", repo!.Account);
        Assert.Equal(["me"], gh.TokensAskedFor);
        Assert.Equal(7, store.Known("octo/fork")!.RemoteKB);
        Assert.Equal(ActivityKind.AddRepo, store.State.Activity[0].Kind);

        (repo, error) = await service.AddByUrlAsync("not a repo", TestContext.Current.CancellationToken);
        Assert.Null(repo);
        Assert.Contains("owner/name", error, StringComparison.Ordinal);
    }
}
