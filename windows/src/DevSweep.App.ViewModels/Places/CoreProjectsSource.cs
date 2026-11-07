using DevSweep.Core.Modules;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.GitHub;

namespace DevSweep.App.ViewModels.Places;

/// <summary>The real Projects source: M3c's <see cref="ProjectsService"/>, each tab only when its flag is on.</summary>
public sealed class CoreProjectsSource(ProjectsService service, Func<IReadOnlyList<string>?> roots) : IProjectsSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ProjectsFeatures Features => service.Features;

    /// <summary>The Projects source when <c>module:projects</c> is on; null otherwise.</summary>
    public static CoreProjectsSource? From(DevSweepEngine engine, Func<IReadOnlyList<string>?> roots)
        => ProjectsService.Create(engine, Http) is { } service ? new CoreProjectsSource(service, roots) : null;

    public async Task<ProjectsReport> LoadAsync(CancellationToken cancellationToken)
    {
        ProjectsSnapshot snapshot = await Task.Run(
            () => service.ScanAsync(new ProjectsScanOptions { Roots = roots(), IncludeGitHub = Features.Accounts }, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new ProjectsReport(snapshot, Features.Activity ? service.Activity() : []);
    }

    public Task<IdleCleanupResult> IdleAsync(ProjectsSnapshot snapshot, int days, CancellationToken cancellationToken)
        => Task.Run(() => service.IdleAsync(snapshot, days, exact: true, cancellationToken), cancellationToken);

    public void Push(Project project, GitHubAccounts? accounts) => service.Push(project, accounts);

    public void SignIn(string? activeBefore) => service.SignIn(null, null, activeBefore);
}
