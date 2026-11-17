using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Storage;
using Xunit;
using Xunit.Sdk;
using ActivityKind = DevSweep.Core.Projects.Storage.ActivityKind;

namespace DevSweep.Core.Tests.Projects;

/// <summary>
/// "Find all repos on this PC": fixture trees on the real file system (NTFS on the CI runner). Repositories in Downloads, in a
/// drive-root-style folder, nested, behind <c>node_modules</c> (skipped), with <c>.git</c> files, bare, behind junction loops,
/// with long paths; plus the numbers (folders per second).
/// </summary>
public sealed class DeepSearchTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "devsweep-deep-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _output;

    public DeepSearchTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    private string Repo(params string[] parts)
    {
        string path = Path.Join([_root, .. parts]);
        Directory.CreateDirectory(Path.Join(path, ".git"));
        File.WriteAllText(Path.Join(path, ".git", "HEAD"), "ref: refs/heads/main\n");
        return path;
    }

    private DeepSearchResult Search(int nested = 2) => DeepSearch.Run(new DeepSearchOptions { Roots = [_root], NestedDepth = nested }, TestContext.Current.CancellationToken);

    private static List<string> Names(DeepSearchResult result, string root)
        => [.. result.Repos.Select(r => Path.GetRelativePath(root, r.Path).Replace('\\', '/')).OrderBy(n => n, StringComparer.Ordinal)];

    [Fact]
    public void Finds_repositories_wherever_they_are_and_not_inside_dependency_folders()
    {
        Repo("Downloads", "some-project-main");
        Repo("Work", "client", "app");
        Repo("a", "b", "c", "d", "e", "f", "deep");
        Repo("Program Files", "Tool", "src");
        Repo("node_modules", "pkg");
        Repo("proj", ".venv", "lib", "x");
        Repo("proj2", "target", "debug", "y");
        Repo("go", "pkg", "mod", "z");
        Repo("proj3", "site-packages", "w");

        DeepSearchResult result = Search();

        Assert.Equal(["Downloads/some-project-main", "Program Files/Tool/src", "Work/client/app", "a/b/c/d/e/f/deep"], Names(result, _root));
        Assert.True(result.Complete);
        Assert.True(result.Pruned >= 5, $"pruned {result.Pruned}");
        _output.WriteLine($"{result.FoldersVisited} folders, {result.Pruned} pruned");
    }

    [Fact]
    public void Does_not_enter_a_repository_beyond_the_nested_depth_but_finds_nested_ones()
    {
        string outer = Repo("outer");
        Repo("outer", "vendor", "inner");
        Directory.CreateDirectory(Path.Join(outer, "a", "b", "c", "d"));
        Directory.CreateDirectory(Path.Join(outer, "a", "b", "c", "d", ".git"));

        Assert.Equal(["outer", "outer/vendor/inner"], Names(Search(), _root));
        Assert.Equal(["outer"], Names(Search(nested: 0), _root));
    }

    [Fact]
    public void A_git_file_is_a_worktree_unless_it_points_into_modules()
    {
        string main = Repo("main");
        string worktree = Path.Join(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Join(worktree, ".git"), "gitdir: " + Path.Join(main, ".git", "worktrees", "wt") + "\n");
        string submodule = Path.Join(main, "libs", "sub");
        Directory.CreateDirectory(submodule);
        File.WriteAllText(Path.Join(submodule, ".git"), "gitdir: ../../.git/modules/libs/sub\n");

        Assert.Equal(["main", "wt"], Names(Search(), _root));
    }

    [Fact]
    public void Bare_repositories_are_counted_not_listed()
    {
        string bare = Path.Join(_root, "server", "thing.git");
        Directory.CreateDirectory(Path.Join(bare, "objects"));
        Directory.CreateDirectory(Path.Join(bare, "refs"));
        File.WriteAllText(Path.Join(bare, "HEAD"), "ref: refs/heads/main\n");
        Repo("server", "real");

        DeepSearchResult result = Search();

        Assert.Equal(["server/real"], Names(result, _root));
        Assert.Equal(1, result.BareRepos);
    }

    [Fact]
    public void Links_are_not_followed_so_a_loop_ends()
    {
        string real = Repo("real");
        string loop = Path.Join(_root, "loop");
        try
        {
            Directory.CreateSymbolicLink(loop, _root);
            Directory.CreateSymbolicLink(Path.Join(real, "..", "other-link"), real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This account can't create symbolic links: " + ex.Message);
        }

        DeepSearchResult result = Search();

        Assert.Equal(["real"], Names(result, _root));
        Assert.True(result.LinksNotFollowed >= 2);
        Assert.True(result.Complete);
    }

    [Fact]
    public void A_path_longer_than_260_characters_is_walked()
    {
        string folder = _root;
        for (int i = 0; i < 12; i++)
        {
            folder = Path.Join(folder, new string((char)('a' + i), 24));
        }

        Directory.CreateDirectory(Path.Join(folder, ".git"));

        DeepSearchResult result = Search();

        Assert.Single(result.Repos);
        Assert.True(result.Repos[0].Path.Length > 260);
    }

    [Fact]
    public void Cancelling_returns_what_was_found_flagged_partial()
    {
        for (int i = 0; i < 40; i++)
        {
            Repo("many", "r" + i);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        DeepSearchResult result = DeepSearch.Run(new DeepSearchOptions { Roots = [_root] }, cts.Token);

        Assert.Equal(DeepSearchStop.Cancelled, result.Stopped);
        Assert.False(result.Complete);
    }

    [Fact]
    public void A_time_budget_stops_the_walk_and_says_so()
    {
        for (int i = 0; i < 200; i++)
        {
            Directory.CreateDirectory(Path.Join(_root, "d" + i, "x", "y"));
        }

        DeepSearchResult result = DeepSearch.Run(new DeepSearchOptions { Roots = [_root], TimeBudget = TimeSpan.Zero, Parallelism = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(DeepSearchStop.TimeBudget, result.Stopped);
        Assert.False(result.Complete);
    }

    [Fact]
    public void Progress_reports_folders_and_repositories()
    {
        for (int i = 0; i < 600; i++)
        {
            Directory.CreateDirectory(Path.Join(_root, "p" + (i % 30), "q" + i));
        }

        Repo("found-me");
        var reports = new List<DeepSearchProgress>();
        DeepSearch.Run(new DeepSearchOptions { Roots = [_root], Progress = new Progress<DeepSearchProgress>(reports.Add) }, TestContext.Current.CancellationToken);
        Thread.Sleep(200);

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.Folders > 0);
    }

    [Theory]
    [InlineData("node_modules", false, true)]
    [InlineData("Node_Modules", false, true)]
    [InlineData(".venv", false, true)]
    [InlineData("venv", false, true)]
    [InlineData("target", false, true)]
    [InlineData("bin", false, true)]
    [InlineData("obj", false, true)]
    [InlineData(".gradle", false, true)]
    [InlineData(".next", false, true)]
    [InlineData(".cache", false, true)]
    [InlineData("__pycache__", false, true)]
    [InlineData("site-packages", false, true)]
    [InlineData(".nuget", false, true)]
    [InlineData(".m2", false, true)]
    [InlineData(".cargo", false, true)]
    [InlineData("WinSxS", false, true)]
    [InlineData("Windows.old", false, true)]
    [InlineData("$Recycle.Bin", false, true)]
    [InlineData("System Volume Information", false, true)]
    [InlineData(".git", false, true)]
    [InlineData("Windows", true, true)]
    [InlineData("Windows", false, false)]
    [InlineData("Downloads", true, false)]
    [InlineData("Program Files", true, false)]
    [InlineData("Program Files (x86)", true, false)]
    [InlineData("ProgramData", true, false)]
    [InlineData("Users", true, false)]
    [InlineData("src", false, false)]
    public void Folder_names_that_are_passed_over(string name, bool parentIsRoot, bool pruned)
        => Assert.Equal(pruned, DeepSearch.IsPruned(name, "x", parentIsRoot));

    [Fact]
    public void Only_go_pkg_is_pruned_not_every_pkg()
    {
        Assert.True(DeepSearch.IsPruned("pkg", "go", false));
        Assert.False(DeepSearch.IsPruned("pkg", "mylib", false));
    }

    [Fact]
    public void Speed_on_a_wide_tree_with_huge_dependency_folders()
    {
        // 30 projects, each with a node_modules of 400 folders that must never be listed, plus 3000 ordinary folders.
        for (int p = 0; p < 30; p++)
        {
            string project = Repo("big", "p" + p);
            for (int i = 0; i < 400; i++)
            {
                Directory.CreateDirectory(Path.Join(project, "node_modules", "pkg" + i, "lib"));
            }
        }

        for (int i = 0; i < 3000; i++)
        {
            Directory.CreateDirectory(Path.Join(_root, "wide", "w" + (i % 50), "n" + i));
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        DeepSearchResult result = Search();
        clock.Stop();

        Assert.Equal(30, result.Repos.Count);
        Assert.True(result.FoldersVisited < 4500, $"visited {result.FoldersVisited} folders: node_modules was entered");
        _output.WriteLine($"deep search: {result.FoldersVisited} folders in {clock.Elapsed.TotalMilliseconds:0} ms = {result.FoldersPerSecond:0} folders/s, {result.Repos.Count} repos, {result.Pruned} pruned");
    }

    [Fact]
    public void The_store_remembers_what_a_search_found_and_how_it_went()
    {
        string store = Path.Join(_root, "state", "projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        string repo = Repo("remembered");
        var projects = new ProjectStore(store);
        projects.Update(s => s with
        {
            DeepSearchConsent = true,
            DeepRepos = [repo],
            DefaultEditor = "rider",
            SearchRemovableDrives = true,
            LastDeepSearch = new DeepSearchSummary { Roots = [_root], Folders = 12, Repos = 1, Complete = false, CloudOnlySample = ["x"] },
        });

        ProjectsState again = new ProjectStore(store).State;

        Assert.True(again.DeepSearchConsent);
        Assert.Equal([repo], again.DeepRepos);
        Assert.Equal("rider", again.DefaultEditor);
        Assert.True(again.SearchRemovableDrives);
        Assert.Equal(12, again.LastDeepSearch!.Folders);
        Assert.False(again.LastDeepSearch.Complete);
        Assert.Equal(["x"], again.LastDeepSearch.CloudOnlySample);
    }

    [Fact]
    public void An_older_state_file_without_the_new_fields_still_loads()
    {
        string store = Path.Join(_root, "old.json");
        File.WriteAllText(store, "{\"idleDays\": 30}");

        ProjectsState state = new ProjectStore(store).State;

        Assert.Null(state.DeepSearchConsent);
        Assert.Empty(state.DeepRepos);
        Assert.Null(state.LastDeepSearch);
        Assert.Equal(30, state.IdleDays);
    }

    [Fact]
    public async Task The_service_search_remembers_repositories_and_logs_it()
    {
        string repo = Repo("Downloads", "thing");
        var projects = new ProjectStore(Path.Join(_root, "svc.json"));
        var service = new ProjectsService(ProjectsFeatures.All, new DevSweep.Core.Projects.Git.GitCli(null), DevSweep.Core.Projects.GitHub.FixedGitHubCli.None, new DevSweep.Core.Projects.GitHub.GitHubApi(new HttpClient()),
            projects, new DevSweep.Core.Locations.FixedEnvironment(), wsl: new NoWsl(), driveRoots: _ => [_root]);
        var progress = new List<DeepSearchProgress>();

        DeepSearchOutcome outcome = await service.DeepSearchAsync(new Progress<DeepSearchProgress>(progress.Add), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([repo], projects.State.DeepRepos);
        Assert.True(projects.State.DeepSearchConsent);
        Assert.True(outcome.Summary.Complete);
        Assert.Equal(1, outcome.Summary.Repos);
        Assert.Contains(projects.State.Activity, a => a.Kind == ActivityKind.Search);
    }
}

/// <summary>No WSL on this machine.</summary>
internal sealed class NoWsl : IWslSource
{
    public IReadOnlyList<string> RegisteredDistros() => [];

    public Task<IReadOnlySet<string>?> RunningAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>?>(null);
}
