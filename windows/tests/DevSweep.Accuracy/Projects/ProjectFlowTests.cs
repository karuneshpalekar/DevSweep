using DevSweep.Core.Cleaning;
using DevSweep.Core.Locations;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Platform.Recycling;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Safety;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy.Projects;

/// <summary>
/// Projects end to end on real repositories: discovery by origin (links never followed,
/// OneDrive placeholders never hydrated), removal through the M1 cleaner with History,
/// download back into the same folder with the account's identity in the clone only, and
/// publish behind the secret check.
/// </summary>
public sealed class ProjectFlowTests(ITestOutputHelper output) : IDisposable
{
    private readonly GitFixture _git = new("proj-flow");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _git.Dispose();

    private ProjectsService Service(IGitHubCli? gh = null)
    {
        var store = new ProjectStore(Path.Join(_git.Root, "state", "projects.json"));
        var environment = new FixedEnvironment { Folders = { ["Profile"] = Path.Join(_git.Root, "profile") } };
        return new ProjectsService(ProjectsFeatures.All, GitCli.Default, gh ?? FixedGitHubCli.None, new GitHubApi(new HttpClient()), store, environment);
    }

    [Fact]
    public void Clones_are_found_by_folder_and_links_and_dependency_folders_are_not_followed()
    {
        string code = _git.Temp.CreateDirectory("Code");
        _git.Repo(Path.Join("Code", "whatever-folder"), originUrl: "https://github.com/Octo/Widget.git");
        _git.Repo(Path.Join("Code", "sub", "ssh-clone"), originUrl: "git@github.com:octo/gadget.git");
        _git.Repo(Path.Join("Code", "no-origin"), pushed: false);
        Directory.CreateDirectory(Path.Join(code, "node_modules", "dep", ".git"));
        string outside = _git.Repo("outside-repo");
        bool linked = OperatingSystem.IsWindows();
        if (linked)
        {
            Ntfs.CreateJunction(Path.Join(code, "junction-to-repo"), outside);
        }

        FinderResult found = ProjectFinder.Find([code], cancellationToken: Ct);
        foreach (FoundRepo r in found.Repos)
        {
            output.WriteLine("found " + r.Path);
        }

        Assert.Equal(["no-origin", "ssh-clone", "whatever-folder"], found.Repos.Select(r => Path.GetFileName(r.Path)).Order(StringComparer.Ordinal));
        if (linked)
        {
            Assert.Equal(1, found.LinksNotFollowed);
        }
    }

    [Fact]
    public async Task Remove_then_download_comes_back_into_the_same_folder_with_the_accounts_identity()
    {
        string code = _git.Temp.CreateDirectory("Code");
        string work = _git.Repo(Path.Join("Code", "widget"));
        string bare = Path.Join(_git.Root, "remote-Code-widget.git");
        GitFixture.Git(work, "remote", "set-url", "origin", "https://github.com/octo/widget.git");
        GitFixture.Git(work, "remote", "set-url", "--push", "origin", bare);
        GitFixture.Git(work, "update-ref", "refs/remotes/origin/main", "HEAD");
        ProjectsService service = Service();
        service.SaveIdentity("octo", new GitIdentity("Octo Cat", "octo@example.com"));
        string globalBefore = GitFixture.Run(null, true, "config", "--global", "--list").Output;

        ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = [code], IncludeGitHub = false }, Ct);
        Project project = Assert.Single(snapshot.Projects);
        Assert.Equal("octo/widget", project.NameWithOwner);
        Assert.True(project.Safety!.IsSafeToRemove, project.Safety.Headline);
        project = project with { Account = "octo" };

        var history = new HistoryStore(Path.Join(_git.Root, "history.json"));
        var cleaner = new Cleaner(SafetyGuard.ForCurrentUser(devRoots: [code]), history, new QuarantineStore(new FixedQuarantineLocations(Path.Join(_git.Root, "quarantine"))), new RecycleBin());
        RemoveResult removed = await service.RemoveAsync(project, cleaner, Ct);
        output.WriteLine("remove: " + removed.Message);
        Assert.True(removed.Succeeded, removed.Message);
        Assert.False(Directory.Exists(work));
        HistoryEntry entry = Assert.Single(history.Entries);
        Assert.Equal(work, entry.Items[0].Original);
        output.WriteLine($"went to {entry.Items[0].Destination}: {entry.Items[0].Location}");

        // Still listed (remembered), with Download into the folder it lived in.
        snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = [code], IncludeGitHub = false }, Ct);
        Project gone = Assert.Single(snapshot.Projects);
        Assert.False(gone.OnDisk);
        DownloadPlan plan = service.PlanDownload(gone, CloneStrategy.Blobless);
        Assert.Equal(work, plan.Destination);
        Assert.Contains("--filter=blob:none", plan.Arguments);

        DownloadResult back = await service.DownloadAsync(gone, CloneStrategy.Blobless, account: "octo", cloneUrl: bare, cancellationToken: Ct);
        output.WriteLine("download: " + back.Message);
        Assert.True(back.Succeeded, back.Message);
        Assert.Equal(work, back.Path);
        Assert.True(File.Exists(Path.Join(work, "a.txt")));
        Assert.Equal("Octo Cat", GitFixture.Git(work, "config", "--local", "user.name").Trim());
        Assert.Equal("octo@example.com", GitFixture.Git(work, "config", "--local", "user.email").Trim());
        Assert.Equal("true", GitFixture.Git(work, "config", "--local", "core.longpaths").Trim());
        string local = GitFixture.Git(work, "config", "--local", "--list");
        Assert.DoesNotContain("credential", local, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(globalBefore, GitFixture.Run(null, true, "config", "--global", "--list").Output);
        Assert.Equal(
            [ActivityKind.Download, ActivityKind.Remove, ActivityKind.Identity],
            service.Activity().Select(a => a.Kind));
    }

    [Fact]
    public async Task An_unsafe_project_is_never_removed()
    {
        string code = _git.Temp.CreateDirectory("Code");
        string work = _git.Repo(Path.Join("Code", "busy"));
        File.WriteAllText(Path.Join(work, "notes.txt"), "mine");
        ProjectsService service = Service();
        ProjectsSnapshot snapshot = await service.ScanAsync(new ProjectsScanOptions { Roots = [code], IncludeGitHub = false }, Ct);
        var history = new HistoryStore(Path.Join(_git.Root, "history.json"));
        var cleaner = new Cleaner(SafetyGuard.ForCurrentUser(devRoots: [code]), history, new QuarantineStore(new FixedQuarantineLocations(Path.Join(_git.Root, "quarantine"))), new RecycleBin());
        RemoveResult result = await service.RemoveAsync(Assert.Single(snapshot.Projects), cleaner, Ct);
        Assert.False(result.Succeeded);
        Assert.Contains("git doesn't track", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(work, "notes.txt")));
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task A_repository_with_onedrive_placeholders_is_not_checked_and_not_hydrated()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Cloud Files placeholders are Windows only.");
        }

        CloudSyncRoot? root = CloudSyncRoot.TryCreate(_git.Temp.PathOf("OneDrive"), out string reason);
        if (root is null)
        {
            Assert.Skip(reason);
        }

        using (root)
        {
            string repo = Path.Join(root.Root, "repo");
            GitFixture.Git(null, "init", "-q", "-b", "main", repo);
            File.WriteAllText(Path.Join(repo, "local.txt"), "x");
            root.CreateFilePlaceholders("repo", [("big-asset.bin", 8 * 1024 * 1024)]);

            FinderResult found = ProjectFinder.Find([root.Root], cancellationToken: Ct);
            FoundRepo repoFound = Assert.Single(found.Repos);
            LocalClone clone = await new CloneInspector(GitCli.Default).InspectAsync(repoFound, cancellationToken: Ct);
            output.WriteLine($"{clone.Safety.State}: {clone.Safety.Headline}; size {clone.Size}");
            Assert.Equal(SafetyState.NotChecked, clone.Safety.State);
            Assert.StartsWith("In OneDrive, not checked", clone.Safety.Headline, StringComparison.Ordinal);
            Assert.True(clone.InCloudFolder);
            Assert.Equal(0, root.FetchDataRequests);
            Assert.Equal(0, CloudSyncRoot.GetInfo(Path.Join(repo, "big-asset.bin")).OnDiskDataSize);
        }
    }

    [Fact]
    public async Task Publish_lists_secret_files_and_needs_confirmation_then_pushes_as_the_account()
    {
        string folder = _git.Temp.CreateDirectory("new-app");
        File.WriteAllText(Path.Join(folder, "app.py"), "print('hi')\n");
        File.WriteAllText(Path.Join(folder, "deploy_key.pem"), "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----\n");
        File.WriteAllText(Path.Join(folder, ".gitignore"), "ignored.pem\n");
        File.WriteAllText(Path.Join(folder, "ignored.pem"), "-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----\n");
        string bare = _git.Bare("published");
        ProjectsService service = Service();
        service.SaveIdentity("octo", new GitIdentity("Octo Cat", "octo@example.com"));
        var request = new PublishRequest { Folder = folder, Account = "octo", Name = "new-app", PushUrl = bare };

        PublishPreview preview = await service.PreviewPublishAsync(request, Ct);
        output.WriteLine("would publish: " + string.Join(", ", preview.Files.Select(Path.GetFileName)));
        Assert.Equal([".gitignore", "app.py", "deploy_key.pem"], preview.Files.Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));
        SecretFile secret = Assert.Single(preview.Secrets);
        Assert.Equal("deploy_key.pem", Path.GetFileName(secret.Path));
        Assert.False(Directory.Exists(Path.Join(folder, ".git")), "the preview changes nothing");

        PublishResult refused = await service.PublishAsync(request, Ct);
        Assert.False(refused.Succeeded);
        Assert.Contains("look like secrets", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(folder, ".git")));

        PublishResult published = await service.PublishAsync(request with { AcknowledgeSecrets = true }, Ct);
        output.WriteLine("publish: " + published.Message);
        Assert.True(published.Succeeded, published.Message);
        Assert.Equal("Octo Cat <octo@example.com>", GitFixture.Git(bare, "log", "-1", "--format=%an <%ae>").Trim());
        Assert.Equal(SafetyState.Safe, (await GitFixture.Check(folder)).State);
    }
}
