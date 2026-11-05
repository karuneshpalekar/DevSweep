using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;

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

/// <summary>Health: Tools and versions, Security, Ports (the M3b module, through <see cref="IHealthSource"/>).</summary>
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

    public static string TabTitle(HealthTab tab) => tab switch
    {
        HealthTab.Tools => "Tools and versions",
        HealthTab.Security => "Security",
        _ => "Ports",
    };

    public static string UnavailableText => "Health isn't part of this build yet. It will show every install of your languages and databases, which one your PATH runs and whether it's still supported, files that look like secrets, and what's listening on your ports.";

    public IReadOnlyList<SecurityItem> NeedsAction => Report is null ? [] : [.. Report.Security.Where(s => s.Level >= Level.Warning)];

    public IReadOnlyList<SecurityItem> LooksFine => Report is null ? [] : [.. Report.Security.Where(s => s.Level < Level.Warning)];

    public IReadOnlyList<PortInfo> DevelopmentPorts => Report is null ? [] : [.. Report.Ports.Where(p => p.IsDevelopment)];

    public IReadOnlyList<PortInfo> OtherPorts => Report is null ? [] : [.. Report.Ports.Where(p => !p.IsDevelopment)];

    public string Count(HealthTab tab) => Report is null ? string.Empty : tab switch
    {
        HealthTab.Tools => Nonzero(Report.Tools.Count(t => t.Level >= Level.Warning)),
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

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null || State == PlaceState.Loading)
        {
            return;
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

/// <summary>Projects: Projects, Cleanup, Accounts, Activity (the M3c module, through <see cref="IProjectsSource"/>).</summary>
public sealed partial class ProjectsViewModel(IProjectsSource? source, Func<DateTimeOffset> now) : ObservableObject
{
    public static readonly IReadOnlyList<(int Days, string Title)> IdleChoices = [(7, "1 week"), (14, "2 weeks"), (30, "1 month"), (90, "3 months")];

    [ObservableProperty]
    public partial PlaceState State { get; set; } = source is null ? PlaceState.Unavailable : PlaceState.NotChecked;

    [ObservableProperty]
    public partial ProjectsTab Tab { get; set; }

    [ObservableProperty]
    public partial ProjectsReport? Report { get; set; }

    [ObservableProperty]
    public partial int IdleDays { get; set; } = 30;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public static string TabTitle(ProjectsTab tab) => tab switch
    {
        ProjectsTab.Projects => "Projects",
        ProjectsTab.Cleanup => "Cleanup",
        ProjectsTab.Accounts => "Accounts",
        _ => "Activity",
    };

    public static string UnavailableText => "Projects isn't part of this build yet. It will list your repositories on this PC and on GitHub across every account you're signed in to, say whether each is safe to remove, and download them again on demand.";

    public IReadOnlyList<ProjectInfo> Idle => Report is null ? []
        : [.. Report.Projects.Where(p => p.OnDisk && p.LastOpened is { } last && (now() - last).TotalDays >= IdleDays).OrderByDescending(p => p.Bytes)];

    public string IdleSummary
    {
        get
        {
            IReadOnlyList<ProjectInfo> idle = Idle;
            int safe = idle.Count(p => p.SafeToRemove);
            return idle.Count == 0 ? "Disk is tidy"
                : $"{Words.Count(idle.Count, "project")} not opened or changed for {IdleChoices.FirstOrDefault(c => c.Days == IdleDays).Title ?? IdleDays + " days"} · {SizeLabels.Size(idle.Sum(p => p.Bytes))} · {safe} safe to remove";
        }
    }

    public string Count(ProjectsTab tab) => Report is null ? string.Empty : tab switch
    {
        ProjectsTab.Projects => Nonzero(Report.Projects.Count(p => p.OnDisk)),
        ProjectsTab.Cleanup => Nonzero(Idle.Count),
        ProjectsTab.Accounts => Nonzero(Report.Accounts.Count),
        _ => string.Empty,
    };

    private static string Nonzero(int n) => n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    partial void OnIdleDaysChanged(int value)
    {
        OnPropertyChanged(nameof(Idle));
        OnPropertyChanged(nameof(IdleSummary));
    }

    partial void OnReportChanged(ProjectsReport? value)
    {
        OnPropertyChanged(nameof(Idle));
        OnPropertyChanged(nameof(IdleSummary));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null || State == PlaceState.Loading)
        {
            return;
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
