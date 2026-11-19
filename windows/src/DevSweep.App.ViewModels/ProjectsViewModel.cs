using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Actions;
using DevSweep.Core.Projects.Discovery;
using DevSweep.Core.Projects.Git;
using DevSweep.Core.Projects.GitHub;
using DevSweep.Core.Projects.Storage;
using DevSweep.Core.Scanning;

namespace DevSweep.App.ViewModels;

/// <summary>How an action's result is shown (the banner above the list).</summary>
public enum NoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A line about what just happened, in plain words.</summary>
public sealed record Notice(NoticeKind Kind, string Text);

/// <summary>Which sheet is open over the Projects page.</summary>
public enum ProjectSheet
{
    None,
    Clone,
    Publish,
    AddByUrl,
    AddAccount,
}

/// <summary>"Find all repos on this PC": not run, asking first, running, or finished.</summary>
public enum DeepSearchState
{
    Idle,
    Running,
    Done,
}

/// <summary>
/// Projects: Projects, Cleanup, Accounts, Activity. The list (Show filter with counts, account filter, search, order), the selected
/// project's panel, the actions (open, fetch, remove, download, publish, add by URL, add account), the deep search and its progress.
/// Everything goes through <see cref="IProjectsSource"/>; results come back as plain notices.
/// </summary>
public sealed partial class ProjectsViewModel(IProjectsSource? source, Action<string, bool>? setModule = null) : ObservableObject
{
    public static readonly IReadOnlyList<(int Days, string Title)> IdleChoices = [(7, "1 week"), (14, "2 weeks"), (21, "3 weeks"), (30, "1 month"), (60, "2 months"), (90, "3 months")];

    private Action? _cancelDeep;

    [ObservableProperty]
    public partial PlaceState State { get; set; } = source is null ? PlaceState.Unavailable : PlaceState.NotChecked;

    [ObservableProperty]
    public partial ProjectsTab Tab { get; set; }

    [ObservableProperty]
    public partial ProjectsReport? Report { get; set; }

    [ObservableProperty]
    public partial IdleCleanupResult? Idle { get; set; }

    [ObservableProperty]
    public partial bool IsMeasuringIdle { get; set; }

    [ObservableProperty]
    public partial int IdleDays { get; set; } = 21;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial ProjectFilter Filter { get; set; }

    /// <summary>The account filter; null means all accounts.</summary>
    [ObservableProperty]
    public partial string? AccountFilter { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>The project the side panel is open on (its <see cref="Project.Id"/>), or null.</summary>
    [ObservableProperty]
    public partial string? SelectedId { get; set; }

    /// <summary>The project a download or removal is running for ("all" while the Cleanup tab removes everything safe).</summary>
    [ObservableProperty]
    public partial string? BusyId { get; set; }

    /// <summary>Projects being removed now.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<string> Removing { get; set; } = new HashSet<string>();

    [ObservableProperty]
    public partial Notice? Notice { get; set; }

    [ObservableProperty]
    public partial ProjectSheet Sheet { get; set; }

    /// <summary>The project the open sheet is about (the Clone sheet).</summary>
    [ObservableProperty]
    public partial Project? SheetProject { get; set; }

    [ObservableProperty]
    public partial DeepSearchState DeepState { get; set; }

    [ObservableProperty]
    public partial DeepSearchProgress? DeepProgress { get; set; }

    /// <summary>What the last deep search did, in one or two sentences (the result summary).</summary>
    [ObservableProperty]
    public partial string? DeepResult { get; set; }

    /// <summary>Whether a fetch ("Compare with GitHub now") is running for the selected project.</summary>
    [ObservableProperty]
    public partial bool IsFetching { get; set; }

    /// <summary>Cleanup's "Remove all safe" progress ("Removing 2 of 5…"), empty when idle.</summary>
    [ObservableProperty]
    public partial string RemoveAllStatus { get; set; } = string.Empty;

    /// <summary>The Activity tab's filter; null shows everything.</summary>
    [ObservableProperty]
    public partial ActivityKind? ActivityFilter { get; set; }

    public IProjectsSource? Source => source;

    /// <summary>The tabs whose flags are on (Projects is always there).</summary>
    public IReadOnlyList<ProjectsTab> Tabs => source is null ? [ProjectsTab.Projects]
        : [.. ((ProjectsTab?[])[ProjectsTab.Projects, source.Features.Cleanup ? ProjectsTab.Cleanup : null, source.Features.Accounts ? ProjectsTab.Accounts : null, source.Features.Activity ? ProjectsTab.Activity : null]).OfType<ProjectsTab>()];

    public static string TabTitle(ProjectsTab tab) => tab switch
    {
        ProjectsTab.Projects => "Projects",
        ProjectsTab.Cleanup => "Cleanup",
        ProjectsTab.Accounts => "Accounts",
        _ => "Activity",
    };

    public static string UnavailableText => "Projects is turned off. It lists your repositories on this PC and on GitHub across every account you're signed in to, says whether each is safe to remove, and downloads them again on demand.";

    public bool CanEnable => setModule is not null;

    /// <summary>True after <see cref="Enable"/>: the module starts with the next launch.</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    /// <summary>Turns Projects and its tabs on in <c>features.json</c>; they apply the next time DevSweep starts.</summary>
    public void Enable()
    {
        if (setModule is null)
        {
            return;
        }

        foreach (string module in (ReadOnlySpan<string>)["projects", "projects.accounts", "projects.cleanup", "projects.activity"])
        {
            setModule(module, true);
        }

        RestartNeeded = true;
    }

    // ---- The list ----------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<Project> All => Report?.Snapshot.Projects ?? [];

    /// <summary>What the list shows now: the Show filter, the account filter and the search applied, in order.</summary>
    public IReadOnlyList<Project> Visible => ProjectListing.Apply(All, Filter, AccountFilter, Search);

    public string FilterLabel(ProjectFilter filter) => ProjectListing.Label(All, filter);

    /// <summary>The accounts for the account filter (shown only with two or more).</summary>
    public IReadOnlyList<string> Accounts => [.. (Report?.Snapshot.Accounts?.Accounts ?? []).Select(a => a.Login)];

    public bool ShowAccountFilter => Accounts.Count > 1;

    public string Subtitle => ProjectListing.Subtitle(All, Report is not null, State == PlaceState.Loading);

    public GitHubSetup Setup => Report is null ? GitHubSetup.Off : ProjectListing.Setup(Report.Snapshot, source?.Features.Accounts == true);

    public SearchReport? SearchReport => Report is null ? null : ProjectListing.Report(Report.Snapshot, source?.Preferences.Folders ?? []);

    public Project? Selected => SelectedId is null ? null : All.FirstOrDefault(p => p.Id == SelectedId);

    /// <summary>True until the person has said yes or no to searching the whole PC.</summary>
    public bool NeedsDeepSearchConsent => source is not null && Report is not null && source.Preferences.DeepSearchConsent is null && DeepState == DeepSearchState.Idle;

    public string EmptyTitle
    {
        get
        {
            if (Report is null || State == PlaceState.Loading && All.Count == 0)
            {
                return "Looking…";
            }

            if (Search.Trim().Length > 0)
            {
                return "Nothing matches";
            }

            if (Setup != GitHubSetup.Ready && Filter == ProjectFilter.OnGitHub)
            {
                return "GitHub isn't connected";
            }

            return Filter switch
            {
                ProjectFilter.OnThisPc => "No projects found",
                ProjectFilter.OnGitHub => "Everything on GitHub is here",
                _ => "No projects yet",
            };
        }
    }

    public string EmptyText
    {
        get
        {
            if (Search.Trim().Length > 0)
            {
                return $"No project's name, owner or description contains \"{Search.Trim()}\". Try another word, or clear the filters.";
            }

            if (Setup != GitHubSetup.Ready && Filter == ProjectFilter.OnGitHub)
            {
                return "Connect GitHub above to list your repositories and download them.";
            }

            return Filter switch
            {
                ProjectFilter.OnThisPc => "DevSweep looks in your usual project folders. Add a folder, or find every repo on this PC.",
                ProjectFilter.OnGitHub => "Repos you haven't downloaded appear here, ready to download.",
                _ => "Sign in to GitHub, or find the repos already on this PC, or download or publish a project.",
            };
        }
    }

    public string Count(ProjectsTab tab) => Report is null ? string.Empty : tab switch
    {
        ProjectsTab.Projects => Nonzero(Report.Snapshot.Projects.Count(p => p.OnDisk)),
        ProjectsTab.Cleanup => Nonzero(Idle?.Projects.Count ?? 0),
        ProjectsTab.Accounts => Nonzero(Report.Snapshot.Accounts?.Accounts.Count ?? 0),
        _ => string.Empty,
    };

    private static string Nonzero(int n) => n > 0 ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;

    partial void OnFilterChanged(ProjectFilter value) => ListChanged();

    partial void OnAccountFilterChanged(string? value) => ListChanged();

    partial void OnSearchChanged(string value) => ListChanged();

    partial void OnReportChanged(ProjectsReport? value)
    {
        ListChanged();
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(ShowAccountFilter));
        OnPropertyChanged(nameof(Setup));
        OnPropertyChanged(nameof(SearchReport));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(NeedsDeepSearchConsent));
        if (AccountFilter is { } account && !Accounts.Contains(account, StringComparer.OrdinalIgnoreCase))
        {
            AccountFilter = null;
        }
    }

    partial void OnStateChanged(PlaceState value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnSelectedIdChanged(string? value) => OnPropertyChanged(nameof(Selected));

    partial void OnDeepStateChanged(DeepSearchState value) => OnPropertyChanged(nameof(NeedsDeepSearchConsent));

    private void ListChanged()
    {
        OnPropertyChanged(nameof(All));
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Opens the side panel on a project, or closes it when it is already open on it.</summary>
    public void Toggle(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        SelectedId = SelectedId == project.Id ? null : project.Id;
    }

    // ---- Loading -----------------------------------------------------------------------------------------------------------------

    partial void OnIdleDaysChanged(int value) => _ = RefreshIdleAsync();

    partial void OnIdleChanged(IdleCleanupResult? value) => OnPropertyChanged(nameof(IdleSummary));

    partial void OnIsMeasuringIdleChanged(bool value) => OnPropertyChanged(nameof(IdleSummary));

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null || State == PlaceState.Loading)
        {
            return;
        }

        if (!Tabs.Contains(Tab))
        {
            Tab = ProjectsTab.Projects;
        }

        State = PlaceState.Loading;
        try
        {
            Report = await source.LoadAsync(CancellationToken.None);
            State = PlaceState.Ready;
            await RefreshIdleAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or HttpRequestException)
        {
            Error = ex.Message;
            State = PlaceState.Failed;
        }
    }

    public async Task RefreshIdleAsync()
    {
        if (source is null || Report is null || !source.Features.Cleanup)
        {
            return;
        }

        IsMeasuringIdle = true;
        try
        {
            Idle = await source.IdleAsync(Report.Snapshot, IdleDays, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsMeasuringIdle = false;
        }
    }

    /// <summary>Reloads after an action and keeps the activity trail current.</summary>
    private async Task ReloadAsync()
    {
        State = PlaceState.Ready;
        await LoadAsync();
    }

    // ---- Words for the rows and the panel ------------------------------------------------------------------------------------------

    /// <summary>"Unknown" and "not checked" are never safe.</summary>
    public static (string Text, Tone Tone, string Tooltip) Safety(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OnDisk)
        {
            return ("Only on GitHub", Tone.Neutral, "Downloads again on demand.");
        }

        if (project.IsWsl)
        {
            return ("In WSL, read-only", Tone.Neutral, "Listed from inside Linux. DevSweep doesn't measure, check or remove WSL projects from Windows.");
        }

        if (project.Safety is not { } s)
        {
            return ("Not checked", Tone.Caution, "Not checked yet, so not safe to remove.");
        }

        return s.State switch
        {
            SafetyState.Safe => (project.OtherHost is { } host ? "Up to date · " + host : "Up to date", Tone.Good, "Everything in it is on the remote, " + FetchNote(s) + "."),
            SafetyState.NotSafe => (project.StatusText, Tone.Caution, string.Join(Environment.NewLine, s.Blockers)),
            SafetyState.NotChecked => (project.InCloudFolder ? "In OneDrive, not checked" : project.StatusText, Tone.Caution,
                (s.NotCheckedReason ?? "git was not run on it.") + " Not safe to remove until it can be checked."),
            _ => ("Unknown: not safe", Tone.Caution, string.Join(Environment.NewLine, s.Problems.DefaultIfEmpty(s.Headline)) + " Treated as not safe."),
        };
    }

    /// <summary>"as of the last fetch (2 Nov 2026 09:14 UTC)": remote-tracking refs are only as fresh as the last fetch.</summary>
    public static string FetchNote(RemovalSafety safety)
    {
        ArgumentNullException.ThrowIfNull(safety);
        return safety.Fetched ? "checked against GitHub just now"
            : safety.LastFetchUtc is { } t ? "as of the last fetch (" + t.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC)"
            : "as of the last fetch or push";
    }

    /// <summary>"sample-dev · private · blobless clone" under the project's name.</summary>
    public static string PanelSubtitle(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string[] parts =
        [
            .. project.Owner.Length > 0 ? (string[])[project.Owner] : [],
            .. project.IsPrivate is { } p ? (string[])[p ? "private" : "public"] : [],
            .. project.Strategy is { } s ? (string[])[s.Title().ToLowerInvariant() + " clone"] : [],
            .. project.OtherHost is { } host ? (string[])["on " + host] : [],
        ];
        return string.Join(" · ", parts);
    }

    /// <summary>The panel's plain-language verdict and its tone.</summary>
    public static (string Text, Tone Tone) Verdict(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.IsWsl)
        {
            return ("This project lives inside WSL. Windows can open it in File Explorer, but DevSweep doesn't measure, check or remove it from here.", Tone.Neutral);
        }

        RemovalSafety s = project.Safety ?? new RemovalSafety { State = SafetyState.NotChecked, NotCheckedReason = "Not checked yet." };
        return s.State switch
        {
            SafetyState.Safe => ("Safe to remove. It stays in this list with a Download button.", Tone.Good),
            SafetyState.NotChecked => ((s.NotCheckedReason ?? "Not checked.") + " Until it can be checked, it isn't safe to remove.", Tone.Caution),
            SafetyState.Unknown => ("DevSweep couldn't be sure everything is saved on the remote, so it treats this project as not safe to remove.", Tone.Caution),
            _ when !s.HasRemote => ("Nothing on GitHub keeps a copy of this folder. Publish it first; DevSweep won't remove the only copy.", Tone.Caution),
            _ => ("Not everything is saved on the remote. Push your work first, then it can be removed; DevSweep won't remove a folder that holds anything only you have.", Tone.Caution),
        };
    }

    /// <summary>The panel's "Is it safe to remove from this PC?" lines.</summary>
    public static IReadOnlyList<(bool Ok, string Text)> SafetyLines(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        RemovalSafety s = project.Safety ?? new RemovalSafety { State = SafetyState.NotChecked, NotCheckedReason = "Not checked yet." };
        if (s.NotCheckedReason is { } why)
        {
            return [(false, why)];
        }

        var lines = new List<(bool, string)>();
        if (!s.HasRemote)
        {
            lines.Add((false, "It has no remote, so it exists only here"));
        }
        else
        {
            lines.Add(s.UnpushedCommits == 0 ? (true, "Everything is pushed") : (false, Words.Count(s.UnpushedCommits, "commit") + " aren't on the remote yet" + (s.UnpushedByRef.Count > 0 ? " (" + string.Join(", ", s.UnpushedByRef.Select(r => r.Ref)) + ")" : string.Empty)));
        }

        lines.Add(s.ChangedFiles + s.ConflictedFiles == 0 ? (true, "No uncommitted changes") : (false, Words.Count(s.ChangedFiles + s.ConflictedFiles, "file") + " with changes that aren't committed" + Sample(s.ChangedSample, s.ChangedFiles)));
        lines.Add(s.UntrackedFiles == 0 ? (true, "No files outside git") : (false, Words.Count(s.UntrackedFiles, "file") + " git doesn't track" + Sample(s.UntrackedSample, s.UntrackedFiles)));
        lines.Add(s.Stashes == 0 ? (true, "No stashes") : (false, Words.Count(s.Stashes, "stash").Replace("stashs", "stashes", StringComparison.Ordinal) + " would be lost"));
        lines.Add(s.OtherWorktrees.Count + (s.LinkedWorktreeOf is null ? 0 : 1) == 0 ? (true, "No other worktrees") : (false, "Shares its repository with another worktree"));
        lines.Add(s.Submodules.Count == 0 ? (true, "Submodules (if any) have nothing unpushed") : (false, string.Join("; ", s.Submodules.Select(m => $"Submodule {m.Path}: {m.Reason}"))));
        if (s.UsesLfs)
        {
            lines.Add((false, "Uses Git LFS: whether every LFS file is on the server can only be answered by the server"));
        }

        if (s.LongPathFiles > 0)
        {
            lines.Add((s.LongPathsEnabled, Words.Count(s.LongPathFiles, "file") + " with paths of 260+ characters" + (s.LongPathsEnabled ? "; core.longpaths is on" : "; core.longpaths is off (downloads set it on)")));
        }

        if (s.CaseOnlyRenames.Count + s.CaseCollisions.Count > 0)
        {
            lines.Add((false, "Letter-case differences git can't see on Windows: " + string.Join(", ", s.CaseOnlyRenames.Concat(s.CaseCollisions).Take(3))));
        }

        if (s.OperationInProgress is { } op)
        {
            lines.Add((false, $"A {op} was started and not finished"));
        }

        if (s.LockMessage is { } locked)
        {
            lines.Add((false, locked));
        }

        lines.AddRange(s.Problems.Select(p => (false, "Couldn't check: " + p)));
        return lines;
    }

    private static string Sample(IReadOnlyList<string> sample, int total)
        => sample.Count == 0 ? string.Empty : ": " + string.Join(", ", sample) + (total > sample.Count ? "…" : string.Empty);

    public static string GitHubUrl(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return "https://github.com/" + project.NameWithOwner;
    }

    /// <summary>The account a project's download or push uses (its own when signed in, else the active one).</summary>
    public string? CredentialAccount(Project project) => ProjectsService.CredentialAccount(project, Report?.Snapshot.Accounts);

    public GitIdentity? IdentityFor(string? account)
        => account is not null && Report?.Snapshot.Identities.FirstOrDefault(i => i.Key.Equals(account, StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit ? hit.Value : null;

    // ---- Idle (Cleanup) ----------------------------------------------------------------------------------------------------------

    public string IdleSummary
    {
        get
        {
            if (Idle is not { } idle)
            {
                return IsMeasuringIdle ? "Measuring exactly…" : string.Empty;
            }

            string span = IdleChoices.FirstOrDefault(c => c.Days == idle.Days).Title ?? idle.Days + " days";
            return idle.Projects.Count == 0 ? "Disk is tidy"
                : $"{Words.Count(idle.Projects.Count, "project")} · {SizeLabels.Size(idle.TotalBytes)} · {idle.Safe.Count} fully on GitHub and safe to remove (not opened or changed for {span})";
        }
    }

    // ---- Actions -----------------------------------------------------------------------------------------------------------------

    public void Dismiss() => Notice = null;

    public void Push(Project project) => Run(() => source?.Push(project, Report?.Snapshot.Accounts), "Opened a window that pushes " + project.Name + ". Press Enter there to start.");

    public void SignIn() => Run(() => source?.SignIn(Report?.Snapshot.Accounts?.Active?.Login), "Opened GitHub's sign-in in a window. When you're done, press Refresh.");

    public void SignIn(string? login, GitIdentity? identity)
        => Run(() => source?.SignIn(login, identity, Report?.Snapshot.Accounts?.Active?.Login), "Opened GitHub's sign-in in a window. When you're done, press Refresh and the account appears.");

    public void InstallGitHubCli() => Run(() => source?.InstallGitHubCli(), "Opened a window that installs the GitHub CLI. Press Enter there to start; when it's done, press Refresh.");

    public void OpenInEditor(Project project)
    {
        try
        {
            string used = source?.OpenInEditor(project) ?? "File Explorer";
            Notice = new Notice(NoticeKind.Info, $"Opened {project.Name} in {used}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Notice = new Notice(NoticeKind.Error, "Couldn't open it: " + ex.Message);
        }
    }

    public void SaveIdentity(string account, GitIdentity identity)
    {
        if (Settings.IdentityValidation.Check(identity) is { } problem)
        {
            Notice = new Notice(NoticeKind.Error, "Not saved. " + problem);
            return;
        }

        try
        {
            source?.SaveIdentity(account, identity);
            Notice = new Notice(NoticeKind.Success, identity.IsBlank ? $"Cleared the commit identity for {account}." : $"Commits for {account} will be made as {identity}.");
            if (Report is { } report)
            {
                var identities = report.Snapshot.Identities.Where(i => !i.Key.Equals(account, StringComparison.OrdinalIgnoreCase)).ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
                if (!identity.IsBlank)
                {
                    identities[account] = identity;
                }

                Report = report with { Snapshot = report.Snapshot with { Identities = identities }, Activity = source?.Activity() ?? report.Activity };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Notice = new Notice(NoticeKind.Error, "Couldn't save the identity: " + ex.Message);
        }
    }

    /// <summary>Compares one project with GitHub now (<c>git fetch</c> as its account) and updates its checklist.</summary>
    public async Task FetchNowAsync(Project project)
    {
        if (source is null || IsFetching)
        {
            return;
        }

        IsFetching = true;
        try
        {
            RemovalSafety safety = await source.CheckAsync(project, fetch: true, CancellationToken.None);
            ReplaceProject(project.Id, p => p with { Safety = safety });
            Notice = new Notice(NoticeKind.Info, $"Compared {project.Name} with GitHub just now.");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Notice = new Notice(NoticeKind.Error, "Couldn't compare with GitHub: " + ex.Message);
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>Removes one project from this PC (the caller has already asked "Remove from this PC?"). A project that is not fully backed up goes only with <paramref name="acknowledgeLoss"/> (the "Remove anyway" confirm, as on the Mac).</summary>
    public async Task<bool> RemoveAsync(Project project, bool acknowledgeLoss = false)
    {
        if (source is null || project.LocalPath is null || project.IsWsl)
        {
            return false;
        }

        Removing = new HashSet<string>(Removing) { project.Id };
        try
        {
            RemoveResult result = await source.RemoveAsync(project, CancellationToken.None, acknowledgeLoss);
            Notice = new Notice(result.Succeeded ? NoticeKind.Success : NoticeKind.Warning,
                result.Succeeded ? $"Removed {project.Name} from this PC. It's in History, and it stays in this list with Download." : result.Message);
            if (result.Succeeded && SelectedId == project.Id)
            {
                SelectedId = null;
            }

            await ReloadAsync();
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Notice = new Notice(NoticeKind.Error, "Couldn't remove it: " + ex.Message);
            return false;
        }
        finally
        {
            var next = new HashSet<string>(Removing);
            next.Remove(project.Id);
            Removing = next;
        }
    }

    /// <summary>Removes every safe project of the Cleanup list, one by one, with progress.</summary>
    public async Task RemoveAllSafeAsync()
    {
        if (source is null || Idle is not { } idle)
        {
            return;
        }

        List<Project> safe = [.. idle.Safe.Select(i => i.Project)];
        BusyId = "all";
        int done = 0, failed = 0;
        long freed = 0;
        try
        {
            foreach (Project project in safe)
            {
                RemoveAllStatus = $"Removing {done + failed + 1} of {safe.Count}: {project.Name}…";
                try
                {
                    RemoveResult result = await source.RemoveAsync(project, CancellationToken.None);
                    if (result.Succeeded)
                    {
                        done++;
                        freed += result.Outcome?.Freed ?? project.LocalSize;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed++;
                }
            }

            Notice = new Notice(failed == 0 ? NoticeKind.Success : NoticeKind.Warning,
                $"Removed {Words.Count(done, "project")}, freeing about {SizeLabels.Size(freed)}." + (failed > 0 ? $" {failed} couldn't be removed; they're still on this PC." : " Each stays in the list with a Download button."));
        }
        finally
        {
            BusyId = null;
            RemoveAllStatus = string.Empty;
            await ReloadAsync();
        }
    }

    /// <summary>Downloads a project (the Clone sheet's button). The result carries the message to show; on success the list refreshes.</summary>
    public async Task<DownloadResult> DownloadAsync(Project project, CloneStrategy strategy, string? destination, string? account, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return new DownloadResult(false, "Projects isn't available.");
        }

        BusyId = project.Id;
        try
        {
            DownloadResult result = await source.DownloadAsync(project, strategy, destination, account, cancellationToken);
            if (result.Succeeded)
            {
                Notice = new Notice(NoticeKind.Success, $"Downloaded {project.Name}" + (result.Path is { } p ? $" into {p}." : "."));
                await ReloadAsync();
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return new DownloadResult(false, ex is OperationCanceledException ? "Cancelled." : ex.Message);
        }
        finally
        {
            BusyId = null;
        }
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return new PublishResult(false, "Projects isn't available.");
        }

        try
        {
            PublishResult result = await source.PublishAsync(request, cancellationToken);
            if (result.Succeeded)
            {
                Notice = new Notice(NoticeKind.Success, $"Published {request.Name} to GitHub.");
                await ReloadAsync();
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return new PublishResult(false, ex is OperationCanceledException ? "Cancelled." : ex.Message);
        }
    }

    public Task<PublishPreview> PreviewPublishAsync(PublishRequest request, CancellationToken cancellationToken)
        => source is null ? Task.FromResult(new PublishPreview([], [], "Projects isn't available.")) : source.PreviewPublishAsync(request, cancellationToken);

    public async Task<(RemoteRepo? Repo, string? Error)> AddByUrlAsync(string text, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return (null, "Projects isn't available.");
        }

        try
        {
            (RemoteRepo? repo, string? error) = await source.AddByUrlAsync(text, cancellationToken);
            if (repo is not null)
            {
                Notice = new Notice(NoticeKind.Success, $"Added {repo.NameWithOwner}. It's listed under On GitHub, ready to download.");
                Filter = ProjectFilter.All;
                await ReloadAsync();
            }

            return (repo, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    // ---- Sheets ------------------------------------------------------------------------------------------------------------------

    public void OpenSheet(ProjectSheet sheet, Project? project = null)
    {
        SheetProject = project;
        Sheet = sheet;
    }

    public void CloseSheet()
    {
        Sheet = ProjectSheet.None;
        SheetProject = null;
    }

    // ---- Folders, editors, preferences -------------------------------------------------------------------------------------------

    public void AddFolder(string folder)
    {
        try
        {
            source?.AddSearchFolder(folder);
            Notice = new Notice(NoticeKind.Info, $"Added {folder} to the folders DevSweep looks in. Refreshing…");
            OnPropertyChanged(nameof(SearchReport));
            _ = LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Notice = new Notice(NoticeKind.Error, "Couldn't add that folder: " + ex.Message);
        }
    }

    public void RemoveFolder(string folder)
    {
        source?.RemoveSearchFolder(folder);
        Notice = new Notice(NoticeKind.Info, $"Stopped looking in {folder}. Projects already found there stay until you refresh.");
        OnPropertyChanged(nameof(SearchReport));
        _ = LoadAsync();
    }

    // ---- Deep search -------------------------------------------------------------------------------------------------------------

    /// <summary>The answer to the first-run question. Yes starts the search at once.</summary>
    public async Task AnswerDeepSearchAsync(bool allowed)
    {
        source?.SetDeepSearchConsent(allowed);
        OnPropertyChanged(nameof(NeedsDeepSearchConsent));
        if (allowed)
        {
            await StartDeepSearchAsync();
        }
        else
        {
            Notice = new Notice(NoticeKind.Info, "OK. You can look for every repo on this PC any time from Get a project.");
        }
    }

    /// <summary>"Find all repos on this PC": runs in the background with progress; <see cref="CancelDeepSearch"/> stops it and keeps what it found.</summary>
    public async Task StartDeepSearchAsync()
    {
        if (source is null || DeepState == DeepSearchState.Running)
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cancelDeep = cancel.Cancel;
        DeepProgress = new DeepSearchProgress(0, 0, string.Empty);
        DeepResult = null;
        DeepState = DeepSearchState.Running;
        var progress = new Progress<DeepSearchProgress>(p => DeepProgress = p);
        try
        {
            DeepSearchOutcome outcome = await source.DeepSearchAsync(progress, cancel.Token);
            DeepResult = DescribeDeepSearch(outcome);
            Notice = new Notice(outcome.Summary.Complete ? NoticeKind.Success : NoticeKind.Warning, DeepResult);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            DeepResult = "The search stopped: " + ex.Message;
            Notice = new Notice(NoticeKind.Error, DeepResult);
        }
        finally
        {
            _cancelDeep = null;
            DeepState = DeepSearchState.Done;
        }

        await ReloadAsync();
    }

    public void CancelDeepSearch() => _cancelDeep?.Invoke();

    /// <summary>"Looked through 482,113 folders on C: and D: in 2 min 14 s and found 37 repos (12 new)."</summary>
    public static string DescribeDeepSearch(DeepSearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        DeepSearchSummary s = outcome.Summary;
        string drives = ProjectListing.Drives(s.Roots);
        string took = s.Seconds >= 90 ? string.Create(CultureInfo.InvariantCulture, $"{(int)(s.Seconds / 60)} min {(int)(s.Seconds % 60)} s") : string.Create(CultureInfo.InvariantCulture, $"{s.Seconds:0} s");
        string text = string.Create(CultureInfo.InvariantCulture, $"Looked through {s.Folders:N0} folders{(drives.Length > 0 ? " on " + drives : string.Empty)} in {took} and found {(s.Repos == 1 ? "1 repo" : s.Repos + " repos")}.");
        if (!s.Complete)
        {
            text += outcome.Local.Stopped == DeepSearchStop.Cancelled ? " You stopped it early, so some folders weren't looked at." : " It ran out of time, so some folders weren't looked at; run it again to continue.";
        }

        return text;
    }

    private void ReplaceProject(string id, Func<Project, Project> change)
    {
        if (Report is not { } report)
        {
            return;
        }

        Report = report with { Snapshot = report.Snapshot with { Projects = [.. report.Snapshot.Projects.Select(p => p.Id == id ? change(p) : p)] } };
    }

    /// <summary>Tells the page the list may have changed (after an action that changed one project).</summary>
    public void Refresh() => ListChanged();

    /// <summary>A guard for actions that start a visible window or open something: runs it and turns a failure into a notice.</summary>
    private void Run(Action action, string success)
    {
        try
        {
            action();
            Notice = new Notice(NoticeKind.Info, success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Notice = new Notice(NoticeKind.Error, ex.Message);
        }
    }
}
