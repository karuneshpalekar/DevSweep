using CommunityToolkit.Mvvm.ComponentModel;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Cleaning;

namespace DevSweep.App.ViewModels;

/// <summary>A place in the navigation.</summary>
/// <param name="Id">home, cleanup, projects, health, history, settings.</param>
/// <param name="Title">Shown in the navigation.</param>
/// <param name="Glyph">Segoe Fluent Icons code point.</param>
public sealed record Place(string Id, string Title, string Glyph);

/// <summary>
/// The shell: which places exist (from Core's registered modules and flags), which one is
/// shown, the first-launch Welcome, and the child view models.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <param name="backend">The engine (or sample data).</param>
    /// <param name="settings">App settings.</param>
    /// <param name="integration">What Windows provides (login entry, toasts); null keeps everything in memory.</param>
    public MainViewModel(IDevSweepBackend backend, AppSettingsStore settings, SystemIntegration? integration = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(settings);
        Backend = backend;
        Settings = settings;
        Scan = new ScanViewModel(backend);
        Health = new HealthViewModel(backend.Health);
        Projects = new ProjectsViewModel(backend.Projects);
        Home = new HomeViewModel(backend, Scan, Health);
        CleanUp = new CleanUpViewModel(Scan, settings, backend.ProfilePath);
        Review = new ReviewViewModel(backend);
        History = new HistoryViewModel(backend);
        Background = new BackgroundServices(this, integration ?? SystemIntegration.Memory());
        SettingsPage = new SettingsViewModel(settings, backend, Background);
        Places = BuildPlaces(backend.Modules);
        ShowWelcome = !settings.Current.WelcomeDone;
        CleanUp.ReviewRequested += async (_, plan) => await Review.OpenAsync(plan);
        Review.Cleaned += (_, ids) =>
        {
            Scan.Remove(ids);
            History.Refresh();
            Home.Refresh();
        };
    }

    public IDevSweepBackend Backend { get; }

    public AppSettingsStore Settings { get; }

    public ScanViewModel Scan { get; }

    public HomeViewModel Home { get; }

    public CleanUpViewModel CleanUp { get; }

    public ReviewViewModel Review { get; }

    public HistoryViewModel History { get; }

    public SettingsViewModel SettingsPage { get; }

    public HealthViewModel Health { get; }

    public ProjectsViewModel Projects { get; }

    /// <summary>Open at login, the schedule, alerts, the update check and the tray menu.</summary>
    public BackgroundServices Background { get; }

    /// <summary>The places shown, in order; History and Settings are always there.</summary>
    public IReadOnlyList<Place> Places { get; }

    [ObservableProperty]
    public partial string Current { get; set; } = "home";

    [ObservableProperty]
    public partial bool ShowWelcome { get; set; }

    /// <summary>
    /// A place exists when its module is registered and on. Health and Projects also show
    /// when their module is not in this build at all (as "unavailable"), but not when a flag
    /// turned them off.
    /// </summary>
    public static IReadOnlyList<Place> BuildPlaces(IReadOnlyList<ModuleInfo> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        bool Shown(string id, bool whenAbsent) => modules.FirstOrDefault(m => m.Id == id) is { } m
            ? m.State == ModuleState.On || (whenAbsent && m.State == ModuleState.NotInThisBuild)
            : whenAbsent;

        var places = new List<Place> { new("home", "Home", "") };
        if (Shown("cleanup", false) || Shown("packs", false))
        {
            places.Add(new Place("cleanup", "Clean up", ""));
        }

        if (Shown("projects", true))
        {
            places.Add(new Place("projects", "Projects", ""));
        }

        if (Shown("health", true))
        {
            places.Add(new Place("health", "Health", ""));
        }

        places.Add(new Place("history", "History", ""));
        places.Add(new Place("settings", "Settings", ""));
        return places;
    }

    /// <summary>After the window is up: the first scan (unless Welcome is showing), Health and Projects.</summary>
    public async Task StartAsync()
    {
        History.Refresh();
        Home.Refresh();
        if (ShowWelcome)
        {
            return;
        }

        await Task.WhenAll(Scan.ScanAsync(), Health.LoadAsync(), Projects.LoadAsync());
    }

    public async Task FinishWelcomeAsync()
    {
        Settings.Update(s => s with { WelcomeDone = true });
        ShowWelcome = false;
        await StartAsync();
    }

    /// <summary>Opens the screen a notification or a tray alert is about (Home when that place is not there).</summary>
    public void Open(ToastTarget target)
    {
        switch (target)
        {
            case ToastTarget.CleanUp when Places.Any(p => p.Id == "cleanup"):
                Go("cleanup");
                break;
            case ToastTarget.HealthTools when Places.Any(p => p.Id == "health"):
                Go("health");
                ShowHealthTab(HealthTab.Tools);
                break;
            case ToastTarget.HealthSecurity when Places.Any(p => p.Id == "health"):
                Go("health");
                ShowHealthTab(HealthTab.Security);
                break;
            default:
                Go("home");
                break;
        }
    }

    private void ShowHealthTab(HealthTab tab)
    {
        if (Health.Tabs.Contains(tab))
        {
            Health.Tab = tab;
        }
    }

    public void Go(string place)
    {
        if (Places.Any(p => p.Id == place))
        {
            Current = place;
        }
    }

    /// <summary>Opens Review for every selectable finding (screenshots, "Review all" from Home).</summary>
    public Task ReviewAsync(IReadOnlyList<PlannedAction> plan) => Review.OpenAsync(plan);
}
