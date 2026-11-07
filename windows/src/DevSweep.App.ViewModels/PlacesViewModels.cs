using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Git;

namespace DevSweep.App.ViewModels;

/// <summary>Where a module-backed place is.</summary>
public enum PlaceState
{
    /// <summary>The module is registered but this build of the app has no adapter for it yet.</summary>
    Unavailable,
    NotChecked,
    Loading,
    Ready,
    Failed,
}

public enum HealthTab
{
    Tools,
    Security,
    Ports,
}

public enum ProjectsTab
{
    Projects,
    Cleanup,
    Accounts,
    Activity,
}

/// <summary>Health: Tools and versions, Security, Ports (M3b's <see cref="IHealthSource"/>, its own result types).</summary>
public sealed partial class HealthViewModel(IHealthSource? source) : ObservableObject
{
    [ObservableProperty]
    public partial PlaceState State { get; set; } = source is null ? PlaceState.Unavailable : PlaceState.NotChecked;

    [ObservableProperty]
    public partial HealthTab Tab { get; set; }

    [ObservableProperty]
    public partial HealthReport? Report { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public IHealthSource? Source => source;

    /// <summary>The tabs whose flags are on.</summary>
    public IReadOnlyList<HealthTab> Tabs => source is null ? [HealthTab.Tools, HealthTab.Security, HealthTab.Ports]
        : [.. ((HealthTab?[])[source.Features.Runtimes ? HealthTab.Tools : null, source.Features.Security ? HealthTab.Security : null, source.Features.Ports ? HealthTab.Ports : null]).OfType<HealthTab>()];

    public static string TabTitle(HealthTab tab) => tab switch
    {
        HealthTab.Tools => "Tools and versions",
        HealthTab.Security => "Security",
        _ => "Ports",
    };

    public static string UnavailableText => "Health is turned off. It shows every install of your languages and databases, which one a new terminal runs and whether it's still supported, files that look like secrets, and what's listening on your ports. Turn it on in Settings, Modules.";

    public IReadOnlyList<SecurityFinding> NeedsAction => Report?.Security is { } s ? [.. s.Findings.Where(f => f.Level != SecurityLevel.Ok)] : [];

    public IReadOnlyList<SecurityFinding> LooksFine => Report?.Security is { } s ? [.. s.Findings.Where(f => f.Level == SecurityLevel.Ok)] : [];

    public IReadOnlyList<ListeningPort> DevelopmentPorts => Report?.Ports is { } p ? [.. p.Ports.Where(x => x.IsDevelopment)] : [];

    public IReadOnlyList<ListeningPort> OtherPorts => Report?.Ports is { } p ? [.. p.Ports.Where(x => !x.IsDevelopment)] : [];

    public string Count(HealthTab tab) => Report is null ? string.Empty : tab switch
    {
        HealthTab.Tools => Nonzero(Report.Versions?.AttentionCount ?? 0),
        HealthTab.Security => Nonzero(NeedsAction.Count),
        _ => Nonzero(DevelopmentPorts.Count),
    };

    private static string Nonzero(int n) => n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    partial void OnReportChanged(HealthReport? value)
    {
        OnPropertyChanged(nameof(NeedsAction));
        OnPropertyChanged(nameof(LooksFine));
        OnPropertyChanged(nameof(DevelopmentPorts));
        OnPropertyChanged(nameof(OtherPorts));
    }

    public void OpenFix(RuntimeStep step)
    {
        try
        {
            source?.OpenFix(step);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error = "Couldn't open the fix window: " + ex.Message;
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null || State == PlaceState.Loading)
        {
            return;
        }

        if (Tabs.Count > 0 && !Tabs.Contains(Tab))
        {
            Tab = Tabs[0];
        }

        State = PlaceState.Loading;
        try
        {
            Report = await source.LoadAsync(CancellationToken.None);
            State = PlaceState.Ready;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Error = ex.Message;
            State = PlaceState.Failed;
        }
    }
}

/// <summary>Projects: Projects, Cleanup, Accounts, Activity (M3c's <see cref="IProjectsSource"/>, its own result types).</summary>
public sealed partial class ProjectsViewModel(IProjectsSource? source) : ObservableObject
{
    public static readonly IReadOnlyList<(int Days, string Title)> IdleChoices = [(7, "1 week"), (14, "2 weeks"), (21, "3 weeks"), (30, "1 month"), (60, "2 months"), (90, "3 months")];

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

    public static string UnavailableText => "Projects is turned off. It lists your repositories on this PC and on GitHub across every account you're signed in to, says whether each is safe to remove, and downloads them again on demand. Turn it on in Settings, Modules.";

    /// <summary>"Unknown" and "not checked" are never safe.</summary>
    public static (string Text, Tone Tone, string Tooltip) Safety(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.OnDisk)
        {
            return ("Only on GitHub", Tone.Neutral, "Downloads again on demand.");
        }

        if (project.Safety is not { } s)
        {
            return ("Not checked", Tone.Caution, "Not checked yet, so not safe to remove.");
        }

        return s.State switch
        {
            SafetyState.Safe => ("Safe to remove", Tone.Good, "Everything in it is on GitHub, " + FetchNote(s) + "."),
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
            : safety.LastFetchUtc is { } t ? "as of the last fetch (" + t.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC)"
            : "as of the last fetch or push";
    }

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
                : $"{Words.Count(idle.Projects.Count, "project")} not opened or changed for {span} · {SizeLabels.Size(idle.TotalBytes)} · {idle.Safe.Count} safe to remove ({SizeLabels.Size(idle.SafeBytes)})";
        }
    }

    public string Count(ProjectsTab tab) => Report is null ? string.Empty : tab switch
    {
        ProjectsTab.Projects => Nonzero(Report.Snapshot.Projects.Count(p => p.OnDisk)),
        ProjectsTab.Cleanup => Nonzero(Idle?.Projects.Count ?? 0),
        ProjectsTab.Accounts => Nonzero(Report.Snapshot.Accounts?.Accounts.Count ?? 0),
        _ => string.Empty,
    };

    private static string Nonzero(int n) => n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

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

    public void Push(Project project) => Run(() => source?.Push(project, Report?.Snapshot.Accounts));

    public void SignIn() => Run(() => source?.SignIn(Report?.Snapshot.Accounts?.Active?.Login));

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Error = ex.Message;
        }
    }
}
