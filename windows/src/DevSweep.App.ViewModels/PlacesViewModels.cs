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

