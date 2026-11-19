using DevSweep.Core.Health;
using DevSweep.Core.Health.Packages;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Editors;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;

namespace DevSweep.App.ViewModels.Places;

// Health binds to M3b's Core types (VersionsResult, SecurityResult, PortsResult) through
// IHealthSource, Projects to M3c's (ProjectsSnapshot, IdleCleanupResult, ActivityEntry) through
// IProjectsSource; CoreHealthSource and CoreProjectsSource are the real ones, SampleBackend the
// screenshot ones. A module turned off by a flag keeps its place out of the navigation.

/// <summary>What the Health place shows: M3b's own result types, one per tab (null when the tab is off).</summary>
public sealed record HealthReport(VersionsResult? Versions, SecurityResult? Security, PortsResult? Ports);

/// <summary>What the Health page needs from the Health module (M3b).</summary>
public interface IHealthSource
{
    /// <summary>Which tabs are on (<c>module:health.runtimes/.security/.ports</c>).</summary>
    HealthFeatures Features { get; }

    Task<HealthReport> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Writes the fix to a script and opens it in a visible PowerShell window that waits for Enter.</summary>
    void OpenFix(RuntimeStep fix);

    /// <summary>The package-manager report (winget, Scoop, Chocolatey); null when Tools and versions is off.</summary>
    Task<PackagesResult?> PackagesAsync(VersionsResult? versions, CancellationToken cancellationToken) => Task.FromResult<PackagesResult?>(null);

    /// <summary>Just the listeners again, for the Ports tab's auto-refresh.</summary>
    Task<PortsResult?> PortsAsync(CancellationToken cancellationToken) => Task.FromResult<PortsResult?>(null);

    /// <summary>Why this process may not be stopped (Windows' own, another user's), or null when it may.</summary>
    StopResult? StopRefusal(ListeningPort port) => null;

    /// <summary>Asks the program to stop (Ctrl+C, or its window to close); with <paramref name="force"/>, ends it (only after the user confirmed).</summary>
    Task<StopResult> StopAsync(ListeningPort port, bool force, CancellationToken cancellationToken)
        => Task.FromResult(new StopResult(StopOutcome.Failed, "Stopping programs isn't available here."));

    /// <summary>Why the finding can't be moved to the Recycle Bin (online-only in OneDrive, a link, already gone), or null when it can.</summary>
    string? TrashBlockedReason(SecurityFinding finding) => null;

    /// <summary>Moves the finding's file to the Recycle Bin through the cleaner, so it is in History and can be restored.</summary>
    Task<HealthActionResult> TrashAsync(SecurityFinding finding, CancellationToken cancellationToken)
        => Task.FromResult(new HealthActionResult(false, "Moving files isn't available here."));
}

/// <summary>What the Projects place shows: M3c's snapshot and its activity trail (empty when that tab is off).</summary>
public sealed record ProjectsReport(ProjectsSnapshot Snapshot, IReadOnlyList<ActivityEntry> Activity);

/// <summary>The Projects choices the page shows and changes (the "Downloads folder", the default editor, the folders searched).</summary>
/// <param name="DeepSearchConsent">null until the person has been asked "Look for projects everywhere on this PC?".</param>
/// <param name="Folders">Folders the person added (searched in addition to the common ones).</param>
public sealed record ProjectsPreferences(bool? DeepSearchConsent, bool SearchRemovable, string? DefaultEditor, string WorkspaceRoot, IReadOnlyList<string> Folders);

/// <summary>What the Projects page needs from the Projects module (M3c and P1).</summary>
public interface IProjectsSource
{
    /// <summary>Which tabs are on (<c>module:projects.accounts/.cleanup/.activity</c>).</summary>
    ProjectsFeatures Features { get; }

    Task<ProjectsReport> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Projects untouched for <paramref name="days"/>, sizes re-measured exactly.</summary>
    Task<IdleCleanupResult> IdleAsync(ProjectsSnapshot snapshot, int days, CancellationToken cancellationToken);

    /// <summary>Opens a visible window that pushes the project as its account (Core's terminal script).</summary>
    void Push(Project project, GitHubAccounts? accounts);

    /// <summary>Opens GitHub's sign-in in a visible window.</summary>
    void SignIn(string? activeBefore);

    /// <summary>Opens GitHub's sign-in after saving the commit identity for the account (the Add account sheet).</summary>
    void SignIn(string? login, GitIdentity? identity, string? activeBefore);

    /// <summary>Opens a visible window that installs the GitHub CLI with winget (nothing runs until Enter).</summary>
    void InstallGitHubCli();

    ProjectsPreferences Preferences { get; }

    /// <summary>The editors found on this PC.</summary>
    IReadOnlyList<EditorInfo> Editors { get; }

    void SetDefaultEditor(string? id);

    void SetDeepSearchConsent(bool allowed);

    void SetSearchRemovable(bool include);

    void SetWorkspaceRoot(string folder);

    void AddSearchFolder(string folder);

    void RemoveSearchFolder(string folder);

    /// <summary>"Find all repos on this PC": every fixed drive (and removable ones when chosen) plus running WSL distributions.</summary>
    Task<DeepSearchOutcome> DeepSearchAsync(IProgress<DeepSearchProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Checks one folder now; with <paramref name="fetch"/>, after <c>git fetch</c> as the project's account.</summary>
    Task<RemovalSafety> CheckAsync(Project project, bool fetch, CancellationToken cancellationToken);

    /// <summary>Opens the folder in the chosen editor (else File Explorer); returns what was used.</summary>
    string OpenInEditor(Project project);

    Task<RemoveResult> RemoveAsync(Project project, CancellationToken cancellationToken, bool acknowledgeLoss = false);

    DownloadPlan PlanDownload(Project project, CloneStrategy strategy, string? destination, string? account);

    Task<DownloadResult> DownloadAsync(Project project, CloneStrategy strategy, string? destination, string? account, CancellationToken cancellationToken);

    Task<PublishPreview> PreviewPublishAsync(PublishRequest request, CancellationToken cancellationToken);

    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken);

    Task<(RemoteRepo? Repo, string? Error)> AddByUrlAsync(string text, CancellationToken cancellationToken);

    void SaveIdentity(string account, GitIdentity identity);

    /// <summary>The activity trail, newest first.</summary>
    IReadOnlyList<ActivityEntry> Activity();
}
