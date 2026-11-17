using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Modules;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Editors;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;

namespace DevSweep.App.ViewModels.Places;

/// <summary>The real Projects source: M3c's <see cref="ProjectsService"/>, each tab only when its flag is on.</summary>
public sealed class CoreProjectsSource(ProjectsService service, Func<IReadOnlyList<string>?> roots, AppSettingsStore? settings = null) : IProjectsSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ProjectsFeatures Features => service.Features;

    /// <summary>The Projects source when <c>module:projects</c> is on; null otherwise.</summary>
    public static CoreProjectsSource? From(DevSweepEngine engine, Func<IReadOnlyList<string>?> roots, AppSettingsStore? settings = null)
        => ProjectsService.Create(engine, Http) is { } service ? new CoreProjectsSource(service, roots, settings) : null;

    public async Task<ProjectsReport> LoadAsync(CancellationToken cancellationToken)
    {
        ProjectsSnapshot snapshot = await Task.Run(
            () => service.ScanAsync(new ProjectsScanOptions { ExtraRoots = roots(), IncludeGitHub = Features.Accounts }, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new ProjectsReport(snapshot, Features.Activity ? service.Activity() : []);
    }

    public Task<IdleCleanupResult> IdleAsync(ProjectsSnapshot snapshot, int days, CancellationToken cancellationToken)
        => Task.Run(() => service.IdleAsync(snapshot, days, exact: true, cancellationToken), cancellationToken);

    public void Push(Project project, GitHubAccounts? accounts) => service.Push(project, accounts);

    public void SignIn(string? activeBefore) => service.SignIn(null, null, activeBefore);

    public void SignIn(string? login, GitIdentity? identity, string? activeBefore) => service.SignIn(login, identity, activeBefore);

    public void InstallGitHubCli() => ProjectsService.InstallGitHubCli();

    public ProjectsPreferences Preferences
        => new(service.Store.State.DeepSearchConsent, service.Store.State.SearchRemovableDrives, service.Store.State.DefaultEditor, service.WorkspaceRoot, Folders());

    public IReadOnlyList<EditorInfo> Editors => service.Editors();

    public void SetDefaultEditor(string? id) => service.SetDefaultEditor(id);

    public void SetDeepSearchConsent(bool allowed) => service.SetDeepSearchConsent(allowed);

    public void SetSearchRemovable(bool include) => service.SetSearchRemovableDrives(include);

    public void SetWorkspaceRoot(string folder) => service.SetWorkspaceRoot(folder);

    public void AddSearchFolder(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (settings is not null)
        {
            settings.Update(s => s with { ProjectFolders = [.. s.ProjectFolders.Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase)), full] });
            return;
        }

        service.Store.Update(s => s with { ProjectRoots = [.. s.ProjectRoots.Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase)), full] });
    }

    public void RemoveSearchFolder(string folder)
    {
        settings?.Update(s => s with { ProjectFolders = [.. s.ProjectFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))] });
        service.Store.Update(s => s with { ProjectRoots = [.. s.ProjectRoots.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))] });
    }

    public Task<DeepSearchOutcome> DeepSearchAsync(IProgress<DeepSearchProgress>? progress, CancellationToken cancellationToken)
        => service.DeepSearchAsync(progress, cancellationToken: cancellationToken);

    public Task<RemovalSafety> CheckAsync(Project project, bool fetch, CancellationToken cancellationToken)
        => service.CheckAsync(project.LocalPath ?? throw new InvalidOperationException($"{project.NameWithOwner} isn't on this PC."), fetch,
            project.Account, cancellationToken);

    public string OpenInEditor(Project project) => service.OpenInEditor(project);

    public Task<RemoveResult> RemoveAsync(Project project, CancellationToken cancellationToken) => service.RemoveAsync(project, cancellationToken: cancellationToken);

    public DownloadPlan PlanDownload(Project project, CloneStrategy strategy, string? destination, string? account)
        => service.PlanDownload(project, strategy, destination, account);

    public Task<DownloadResult> DownloadAsync(Project project, CloneStrategy strategy, string? destination, string? account, CancellationToken cancellationToken)
        => service.DownloadAsync(project, strategy, destination, account, cancellationToken: cancellationToken);

    public Task<PublishPreview> PreviewPublishAsync(PublishRequest request, CancellationToken cancellationToken)
        => service.PreviewPublishAsync(request, cancellationToken);

    public Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken) => service.PublishAsync(request, cancellationToken);

    public Task<(RemoteRepo? Repo, string? Error)> AddByUrlAsync(string text, CancellationToken cancellationToken) => service.AddByUrlAsync(text, cancellationToken);

    public void SaveIdentity(string account, GitIdentity identity) => service.SaveIdentity(account, identity);

    public IReadOnlyList<ActivityEntry> Activity() => Features.Activity ? service.Activity() : [];

    private IReadOnlyList<string> Folders()
        => [.. (settings?.Current.ProjectFolders ?? []).Concat(service.Store.State.ProjectRoots).Distinct(StringComparer.OrdinalIgnoreCase)];
}
