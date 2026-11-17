using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.GitHub;
using Xunit;

namespace DevSweep.App.Tests;

/// <summary>The Projects list as a view model: the Show filter with counts, account filter, search, order, selection, the words, the searched report, and the actions.</summary>
public sealed class ProjectsViewModelTests
{
    private static async Task<ProjectsViewModel> LoadedAsync()
    {
        var vm = new ProjectsViewModel(new SampleBackend().Projects);
        await vm.LoadAsync();
        return vm;
    }

    private static IReadOnlyList<Project> Sample => SampleData.Projects.Projects;

    [Fact]
    public async Task Counts_follow_the_Show_filter_and_labels_carry_them()
    {
        ProjectsViewModel vm = await LoadedAsync();

        int local = Sample.Count(p => p.OnDisk);
        int remote = Sample.Count(p => !p.OnDisk);
        Assert.Equal($"All · {Sample.Count}", vm.FilterLabel(ProjectFilter.All));
        Assert.Equal($"On this PC · {local}", vm.FilterLabel(ProjectFilter.OnThisPc));
        Assert.Equal($"On GitHub · {remote}", vm.FilterLabel(ProjectFilter.OnGitHub));
        Assert.Equal(Sample.Count, vm.Visible.Count);
        vm.Filter = ProjectFilter.OnThisPc;
        Assert.Equal(local, vm.Visible.Count);
        Assert.All(vm.Visible, p => Assert.True(p.OnDisk));
        vm.Filter = ProjectFilter.OnGitHub;
        Assert.Equal(remote, vm.Visible.Count);
        Assert.All(vm.Visible, p => Assert.False(p.OnDisk));
        Assert.Equal("On GitHub", ProjectListing.Label([], ProjectFilter.OnGitHub));
    }

    [Fact]
    public async Task The_account_filter_shows_only_with_two_accounts_and_filters_by_account()
    {
        ProjectsViewModel vm = await LoadedAsync();

        Assert.True(vm.ShowAccountFilter);
        Assert.Equal(["alex", "alex-acme"], vm.Accounts);
        vm.AccountFilter = "alex-acme";
        Assert.All(vm.Visible, p => Assert.Equal("alex-acme", p.Account));
        Assert.Contains(vm.Visible, p => p.Name == "payments");
        vm.AccountFilter = null;
        Assert.Equal(Sample.Count, vm.Visible.Count);

        var one = new ProjectsViewModel(new SampleBackend().Projects);
        await one.LoadAsync();
        one.Report = one.Report! with { Snapshot = one.Report.Snapshot with { Accounts = new GitHubAccounts(GitHubStatus.SignedIn, [new GitHubAccount("alex", true)]) } };
        Assert.False(one.ShowAccountFilter);
    }

    [Theory]
    [InlineData("pay", "payments")]
    [InlineData("PAYMENTS", "payments")]
    [InlineData("acme/design", "design-system")]
    [InlineData("storefront", "web-shop")]
    [InlineData("STOREFRONT", "web-shop")]
    [InlineData("  rest api ", "inventory-api")]
    [InlineData("handbook", "docs")]
    public async Task Search_matches_name_owner_and_description_without_regard_to_case(string text, string expected)
    {
        ProjectsViewModel vm = await LoadedAsync();

        vm.Search = text;

        Assert.Equal(expected, Assert.Single(vm.Visible).Name);
    }

    [Fact]
    public async Task Search_by_owner_and_nothing_found_and_cleared()
    {
        ProjectsViewModel vm = await LoadedAsync();

        vm.Search = "acme";
        Assert.All(vm.Visible, p => Assert.Contains("acme", p.NameWithOwner + p.Description, StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.Visible.Count >= 3);
        vm.Search = "no-such-project";
        Assert.Empty(vm.Visible);
        Assert.Equal("Nothing matches", vm.EmptyTitle);
        Assert.Contains("no-such-project", vm.EmptyText, StringComparison.Ordinal);
        vm.Search = string.Empty;
        Assert.Equal(Sample.Count, vm.Visible.Count);
    }

    [Fact]
    public async Task Search_and_filters_combine()
    {
        ProjectsViewModel vm = await LoadedAsync();

        vm.Filter = ProjectFilter.OnGitHub;
        vm.Search = "alex";
        Assert.All(vm.Visible, p => Assert.False(p.OnDisk));
        Assert.Equal(["dotfiles", "game-jam"], vm.Visible.Select(p => p.Name).Order());
        vm.AccountFilter = "alex-acme";
        Assert.Empty(vm.Visible);
    }

    [Fact]
    public async Task Projects_on_this_PC_come_first_then_the_most_recently_used()
    {
        ProjectsViewModel vm = await LoadedAsync();

        IReadOnlyList<Project> list = vm.Visible;

        int firstRemote = list.ToList().FindIndex(p => !p.OnDisk);
        Assert.True(firstRemote > 0);
        Assert.All(list.Take(firstRemote), p => Assert.True(p.OnDisk));
        Assert.All(list.Skip(firstRemote), p => Assert.False(p.OnDisk));
        Assert.Equal("web-shop", list[0].Name);
        DateTimeOffset?[] used = [.. list.Take(firstRemote).Select(p => p.LastUsed)];
        Assert.Equal(used.OrderByDescending(u => u ?? DateTimeOffset.MinValue), used);
    }

    [Fact]
    public async Task Selection_opens_the_panel_on_one_project_and_toggles_off_again()
    {
        ProjectsViewModel vm = await LoadedAsync();
        Project first = vm.Visible[0];
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.Toggle(first);
        Assert.Equal(first.Id, vm.SelectedId);
        Assert.Equal(first, vm.Selected);
        vm.Toggle(vm.Visible[1]);
        Assert.Equal(vm.Visible[1].Id, vm.SelectedId);
        vm.Toggle(vm.Visible[1]);
        Assert.Null(vm.SelectedId);
        Assert.Null(vm.Selected);
        Assert.Contains(nameof(ProjectsViewModel.Selected), changes);
    }

    [Fact]
    public async Task The_subtitle_says_how_many_are_on_this_PC_and_how_big()
    {
        ProjectsViewModel vm = await LoadedAsync();

        int local = Sample.Count(p => p.OnDisk);
        Assert.StartsWith($"{local} on this PC · ", vm.Subtitle, StringComparison.Ordinal);
        Assert.Equal("Not loaded yet", ProjectListing.Subtitle([], loaded: false, loading: false));
        Assert.Equal("Looking…", ProjectListing.Subtitle([], loaded: false, loading: true));
        Assert.Equal("No projects on this PC · refreshing", ProjectListing.Subtitle([], loaded: true, loading: true));
    }

    [Fact]
    public void Relative_times_are_short()
    {
        DateTimeOffset now = SampleData.Now;
        Assert.Equal("3h ago", ProjectListing.UsedAgo(now.AddHours(-3), now));
        Assert.Equal("3d ago", ProjectListing.UsedAgo(now.AddDays(-3), now));
        Assert.Equal("4w ago", ProjectListing.UsedAgo(now.AddDays(-30), now));
        Assert.Equal("3mo ago", ProjectListing.UsedAgo(now.AddDays(-95), now));
        Assert.Equal(string.Empty, ProjectListing.UsedAgo(null, now));
    }

    [Fact]
    public async Task The_searched_report_names_drives_counts_and_what_was_skipped()
    {
        ProjectsViewModel vm = await LoadedAsync();

        SearchReport report = vm.SearchReport!;

        Assert.Contains("Searched every folder on C: and D:", report.Line, StringComparison.Ordinal);
        Assert.Contains("482,113", report.Line, StringComparison.Ordinal);
        Assert.Contains($"{Sample.Count(p => p.OnDisk)} repos", report.Line, StringComparison.Ordinal);
        Assert.Contains(report.Notes, n => n.Contains("OneDrive", StringComparison.Ordinal) && n.Contains("old-code", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("Debian is stopped", StringComparison.Ordinal) && n.Contains("Start it", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("12 folders couldn't be read", StringComparison.Ordinal));
        Assert.Contains(report.Folders, f => f.Path == @"D:\src");

        ProjectsSnapshot quick = SampleData.Projects with { DeepSearch = null };
        Assert.Equal($"Searched 6 folders on C: and D: · {Sample.Count(p => p.OnDisk)} repos", ProjectListing.Report(quick, []).Line);
        Assert.Contains(ProjectListing.Report(quick, [@"D:\clients"]).Folders, f => f.Path == @"D:\clients" && f.AddedByYou);
    }

    [Fact]
    public void The_GitHub_setup_state_comes_from_gh()
    {
        Assert.Equal(GitHubSetup.Ready, ProjectListing.Setup(SampleData.Projects, accountsOn: true));
        Assert.Equal(GitHubSetup.NotInstalled, ProjectListing.Setup(SampleData.ProjectsWith(GitHubStatus.NotInstalled), accountsOn: true));
        Assert.Equal(GitHubSetup.NotSignedIn, ProjectListing.Setup(SampleData.ProjectsWith(GitHubStatus.NotSignedIn), accountsOn: true));
        Assert.Equal(GitHubSetup.Off, ProjectListing.Setup(SampleData.Projects, accountsOn: false));
    }

    [Theory]
    [InlineData(GitHubStatus.NotInstalled)]
    [InlineData(GitHubStatus.NotSignedIn)]
    public async Task With_no_GitHub_every_local_clone_is_still_listed_and_usable(GitHubStatus status)
    {
        ProjectsViewModel vm = await LoadedAsync();

        vm.Report = new ProjectsReport(SampleData.ProjectsWith(status), []);

        Assert.Equal(Sample.Count(p => p.OnDisk), vm.Visible.Count);
        Assert.All(vm.Visible, p => Assert.NotNull(p.LocalPath));
        Assert.NotEqual(GitHubSetup.Ready, vm.Setup);
        Assert.False(vm.ShowAccountFilter);
    }

    [Fact]
    public void The_panel_words_cover_every_kind_of_repo()
    {
        Project Get(string name) => Sample.Single(p => p.Name == name);

        // (a) fully pushed, nothing uncommitted
        Assert.All(ProjectsViewModel.SafetyLines(Get("inventory-api")), l => Assert.True(l.Ok, l.Text));
        Assert.Equal(DevSweep.App.ViewModels.Text.Tone.Good, ProjectsViewModel.Verdict(Get("inventory-api")).Tone);
        // (b) unpushed commits and changed files
        Assert.Contains(ProjectsViewModel.SafetyLines(Get("web-shop")), l => !l.Ok && l.Text.Contains("3 commits aren't on the remote yet (main)", StringComparison.Ordinal));
        Assert.Contains(ProjectsViewModel.SafetyLines(Get("web-shop")), l => !l.Ok && l.Text.Contains("src/cart.ts", StringComparison.Ordinal));
        // (c) no remote
        Assert.Contains(ProjectsViewModel.SafetyLines(Get("scratch-notes")), l => !l.Ok && l.Text.Contains("only here", StringComparison.Ordinal));
        Assert.Contains("Nothing on GitHub keeps a copy", ProjectsViewModel.Verdict(Get("scratch-notes")).Text, StringComparison.Ordinal);
        // (e) OneDrive: not checked, never safe
        Assert.Contains("not checked", ProjectsViewModel.SafetyLines(Get("ml-notebooks"))[0].Text, StringComparison.Ordinal);
        Assert.NotEqual(DevSweep.App.ViewModels.Text.Tone.Good, ProjectsViewModel.Verdict(Get("ml-notebooks")).Tone);
        // (f) LFS: unknown, never safe
        Assert.Contains(ProjectsViewModel.SafetyLines(Get("lfs-assets")), l => !l.Ok && l.Text.Contains("LFS", StringComparison.Ordinal));
        Assert.Equal("Unknown: not safe", ProjectsViewModel.Safety(Get("lfs-assets")).Text);
        // (g) WSL: read-only
        Assert.True(Get("api-experiments").IsWsl);
        Assert.Equal("In WSL, read-only", ProjectsViewModel.Safety(Get("api-experiments")).Text);
        Assert.Contains("DevSweep doesn't measure, check or remove", ProjectsViewModel.Verdict(Get("api-experiments")).Text, StringComparison.Ordinal);
        // another host
        Assert.Equal("gitlab.com", Get("landing-page").OtherHost);
        Assert.Contains("gitlab.com", ProjectsViewModel.Safety(Get("landing-page")).Text, StringComparison.Ordinal);
        Assert.Contains("on gitlab.com", ProjectsViewModel.PanelSubtitle(Get("landing-page")), StringComparison.Ordinal);
        // The header: owner, privacy, clone strategy
        Assert.Equal("alex · private · blobless clone", ProjectsViewModel.PanelSubtitle(Get("inventory-api")));
    }

    [Fact]
    public async Task Fetch_now_replaces_the_projects_check_and_says_so()
    {
        ProjectsViewModel vm = await LoadedAsync();
        Project project = vm.All.Single(p => p.Name == "inventory-api");

        await vm.FetchNowAsync(project);

        Assert.True(vm.All.Single(p => p.Name == "inventory-api").Safety!.Fetched);
        Assert.Equal(NoticeKind.Info, vm.Notice!.Kind);
        Assert.Contains("just now", vm.Notice.Text, StringComparison.Ordinal);
        Assert.Contains("checked against GitHub just now", ProjectsViewModel.FetchNote(vm.All.Single(p => p.Name == "inventory-api").Safety!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_project_that_is_not_fully_saved_is_refused_and_nothing_is_dropped()
    {
        ProjectsViewModel vm = await LoadedAsync();
        Project unsafeProject = vm.All.Single(p => p.Name == "web-shop");
        Project wsl = vm.All.Single(p => p.Name == "api-experiments");

        Assert.False(await vm.RemoveAsync(unsafeProject));
        Assert.Equal(NoticeKind.Warning, vm.Notice!.Kind);
        Assert.Contains("was not removed", vm.Notice.Text, StringComparison.Ordinal);
        Assert.False(await vm.RemoveAsync(wsl));
        Assert.Empty(vm.Removing);
        Assert.Contains(vm.All, p => p.Name == "web-shop" && p.OnDisk);
    }

    [Fact]
    public async Task Removing_a_safe_project_reports_success_and_clears_the_selection()
    {
        ProjectsViewModel vm = await LoadedAsync();
        Project safe = vm.All.Single(p => p.Name == "inventory-api");
        vm.Toggle(safe);

        bool removed = await vm.RemoveAsync(safe);

        Assert.True(removed);
        Assert.Null(vm.SelectedId);
        Assert.Equal(NoticeKind.Success, vm.Notice!.Kind);
        Assert.Contains("stays in this list with Download", vm.Notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_run_question_starts_the_deep_search_on_yes_and_leaves_it_on_no()
    {
        var backend = new SampleBackend();
        var vm = new ProjectsViewModel(backend.Projects);
        await vm.LoadAsync();
        Assert.True(vm.NeedsDeepSearchConsent);

        await vm.AnswerDeepSearchAsync(false);
        Assert.False(vm.NeedsDeepSearchConsent);
        Assert.Equal(DeepSearchState.Idle, vm.DeepState);

        backend.ResetProjectsConsent();
        Assert.True(vm.NeedsDeepSearchConsent);
        await vm.AnswerDeepSearchAsync(true);
        Assert.Equal(DeepSearchState.Done, vm.DeepState);
        Assert.Contains("Looked through 482,113 folders on C: and D:", vm.DeepResult, StringComparison.Ordinal);
        Assert.Contains("found 11 repos", vm.DeepResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_across_thousands_of_projects_is_instant()
    {
        ProjectsViewModel vm = await LoadedAsync();
        var many = new List<Project>(Sample);
        for (int i = 0; i < 20_000; i++)
        {
            many.Add(new Project { Id = $"bulk/r{i}", Name = $"r{i}", NameWithOwner = $"bulk/r{i}", Owner = "bulk", OnGitHub = true, Description = "generated", LastActivity = SampleData.Now.AddDays(-i % 365) });
        }

        vm.Report = vm.Report! with { Snapshot = vm.Report.Snapshot with { Projects = many } };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.Search = "r19999";
        IReadOnlyList<Project> hits = vm.Visible;
        watch.Stop();

        Assert.Single(hits);
        Assert.True(watch.ElapsedMilliseconds < 400, $"{watch.ElapsedMilliseconds} ms to search 20,000 projects");
        Assert.Equal(many.Count(p => !p.OnDisk), ProjectListing.Count(many, ProjectFilter.OnGitHub));
    }

    [Fact]
    public async Task Activity_can_be_filtered_by_kind_and_the_trail_covers_every_kind()
    {
        ProjectsViewModel vm = await LoadedAsync();

        Assert.True(vm.Report!.Activity.Select(a => a.Kind).Distinct().Count() >= 8);
        vm.ActivityFilter = DevSweep.Core.Projects.Storage.ActivityKind.Remove;
        Assert.Equal(DevSweep.Core.Projects.Storage.ActivityKind.Remove, vm.ActivityFilter);
    }

    [Fact]
    public void The_drives_phrase_reads_naturally()
    {
        Assert.Equal("C:", ProjectListing.Drives([@"C:\Users"]));
        Assert.Equal("C: and D:", ProjectListing.Drives([@"D:\src", @"C:\x", @"c:\y"]));
        Assert.Equal("C:, D: and E:", ProjectListing.Drives([@"E:\", @"D:\", @"C:\"]));
        Assert.Equal(string.Empty, ProjectListing.Drives([@"\\wsl.localhost\Ubuntu"]));
    }
}
