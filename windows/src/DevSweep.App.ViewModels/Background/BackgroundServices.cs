using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Updates;
using DevSweep.Core.Health.Alerts;

namespace DevSweep.App.ViewModels.Background;

/// <summary>What the host (the WinUI app, or a test) provides: the login entry, the toast sender and where alert memory lives.</summary>
/// <param name="Login">Open at login (the per-user Run key in the app).</param>
/// <param name="Toasts">Sends notifications.</param>
/// <param name="AlertsFile">The file for <see cref="AlertStore"/> (<c>%LOCALAPPDATA%\DevSweep\alerts.json</c> in the app).</param>
public sealed record SystemIntegration(ILoginItem Login, IToastNotifier Toasts, string AlertsFile)
{
    /// <summary>Nothing touches Windows: login state in memory, no toasts, alert memory in a throwaway temp file (screenshots, tests).</summary>
    public static SystemIntegration Memory(LoginState login = LoginState.Off, IToastNotifier? toasts = null)
        => new(new MemoryLoginItem(login), toasts ?? new NoToasts(), Path.Join(Path.GetTempPath(), "DevSweep-alerts-" + Guid.NewGuid().ToString("N") + ".json"));
}

/// <summary>
/// Everything M5 adds behind the window: open at login, the schedule, alerts and toasts, the update check
/// and the tray menu. The host calls <see cref="Start"/> once the window is ready; screenshots and the smoke
/// test never start the timers.
/// </summary>
public sealed class BackgroundServices : IDisposable
{
    private readonly ScanScheduler _scheduler;

    public BackgroundServices(MainViewModel main, SystemIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(integration);
        Login = integration.Login;
        Toasts = integration.Toasts;
        Updates = new UpdateViewModel(main.Backend.Updates, main.Settings, () => main.Backend.Now);
        var store = new AlertStore(integration.AlertsFile);
        Alerts = new AlertCoordinator(main.Backend, main.Settings, main.Scan, main.Health, store, integration.Toasts);
        Scheduled = new ScheduledScans(main.Settings, main.Scan, main.Health, Alerts, () => main.Review.IsOpen, () => main.Backend.Now);
        _scheduler = new ScanScheduler(Scheduled);
        Tray = new TrayViewModel(main.Backend, main.Scan, main.Projects, Alerts, main.Review);
    }

    public ILoginItem Login { get; }

    public IToastNotifier Toasts { get; }

    public UpdateViewModel Updates { get; }

    public AlertCoordinator Alerts { get; }

    public ScheduledScans Scheduled { get; }

    public TrayViewModel Tray { get; }

    /// <summary>True when closing the window should leave DevSweep running in the tray (open at login is on).</summary>
    public bool KeepInTray => Login.State != LoginState.Off;

    /// <summary>Starts the scheduler (catch-up check now, then every half minute) and the daily update check.</summary>
    public void Start()
    {
        _scheduler.Start();
        Updates.Start();
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        Updates.Dispose();
    }
}
