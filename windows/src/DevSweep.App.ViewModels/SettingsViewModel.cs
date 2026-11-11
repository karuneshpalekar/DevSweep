using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Updates;
using DevSweep.Core.Health.Alerts;

namespace DevSweep.App.ViewModels;

/// <summary>A module switch in Settings.</summary>
public sealed partial class ModuleToggle(ModuleInfo module, Action<string, bool> apply) : ObservableObject
{
    public ModuleInfo Module { get; } = module;

    public string Title => Module.Title;

    public bool CanChange => Module.State != ModuleState.NotInThisBuild && Module.Id is not ("locations" or "inventory");

    public string Note => Module.State == ModuleState.NotInThisBuild ? "Not in this build yet."
        : Module.Id == "inventory" ? "Needed to tell leftovers from installed apps."
        : string.Empty;

    [ObservableProperty]
    public partial bool IsOn { get; set; } = module?.IsOn ?? false;

    partial void OnIsOnChanged(bool value) => apply(Module.Id, value);
}

/// <summary>The pages of Settings.</summary>
public enum SettingsTab
{
    General,
    Modules,
    Background,
    Alerts,
    Updates,
}

/// <summary>
/// Settings: appearance, project folders, ignored items, modules, scan options; open at login and
/// scheduled scans; alerts; updates and about.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Disk alert thresholds the app offers (percent full).</summary>
    public const int DiskPercentMin = 70;
    public const int DiskPercentMax = 95;

    /// <summary>Growth alert thresholds the app offers (GB in a week).</summary>
    public const int GrowthGBMin = 1;
    public const int GrowthGBMax = 10;

    /// <summary>Reminder choices (days); 0 is the default (3 days for a full disk, 7 for the rest).</summary>
    public const int ReminderDaysMin = 3;
    public const int ReminderDaysMax = 7;

    private readonly AppSettingsStore _settings;
    private readonly IDevSweepBackend _backend;
    private readonly BackgroundServices? _background;
    private bool _loading;

    public SettingsViewModel(AppSettingsStore settings, IDevSweepBackend backend, BackgroundServices? background = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(backend);
        _settings = settings;
        _backend = backend;
        _background = background;
        _loading = true;
        LoadBackgroundSettings();
        Appearance = settings.Current.Appearance;
        ExactScan = settings.Current.ExactScan;
        CheckTimeoutSeconds = settings.Current.CheckTimeoutSeconds;
        CheckForUpdates = settings.Current.CheckForUpdates;
        foreach (ModuleInfo module in backend.Modules.Where(m => m.Id != "locations"))
        {
            Modules.Add(new ModuleToggle(module, Toggle));
        }

        Reload();
        settings.Changed += (_, _) => Reload();
        _loading = false;
    }

    public ObservableCollection<string> ProjectFolders { get; } = [];

    public ObservableCollection<IgnoredItem> Ignored { get; } = [];

    public ObservableCollection<ModuleToggle> Modules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeButtonLabel))]
    public partial AppearanceMode Appearance { get; set; }

    /// <summary>System, then Light, then Dark, then System again (the title bar's theme button).</summary>
    public static AppearanceMode NextAppearance(AppearanceMode mode) => mode switch
    {
        AppearanceMode.System => AppearanceMode.Light,
        AppearanceMode.Light => AppearanceMode.Dark,
        _ => AppearanceMode.System,
    };

    public static string AppearanceName(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => "Light",
        AppearanceMode.Dark => "Dark",
        _ => "System",
    };

    /// <summary>The theme button's tooltip and automation name.</summary>
    public string ThemeButtonLabel => $"Theme: {AppearanceName(Appearance)}, click to change";

    /// <summary>The theme button's click; Settings, General, Appearance shows the same value.</summary>
    public void CycleAppearance() => Appearance = NextAppearance(Appearance);

    [ObservableProperty]
    public partial bool ExactScan { get; set; }

    [ObservableProperty]
    public partial double CheckTimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial SettingsTab Tab { get; set; }

    /// <summary>The "Check now" sentence ("You're up to date", "Version 0.2.0 is available", why a check failed).</summary>
    public string? UpdateStatus => _background?.Updates.Status;

    public bool IsCheckingUpdate => _background?.Updates.IsChecking ?? false;

    // Open at login

    /// <summary>Open DevSweep at login and keep it in the tray when its window is closed.</summary>
    [ObservableProperty]
    public partial bool OpenAtLogin { get; set; }

    [ObservableProperty]
    public partial string? LoginMessage { get; set; }

    [ObservableProperty]
    public partial LoginState LoginState { get; set; }

    // Scheduled scans

    [ObservableProperty]
    public partial bool ScheduleEnabled { get; set; }

    [ObservableProperty]
    public partial ScanFrequency ScheduleFrequency { get; set; }

    [ObservableProperty]
    public partial DayOfWeek ScheduleDay { get; set; }

    /// <summary>Time of day as a TimeSpan (what WinUI's TimePicker uses).</summary>
    [ObservableProperty]
    public partial TimeSpan ScheduleTime { get; set; }

    // Alerts

    [ObservableProperty]
    public partial bool DiskAlert { get; set; }

    [ObservableProperty]
    public partial double DiskPercent { get; set; }

    [ObservableProperty]
    public partial bool GrowthAlert { get; set; }

    [ObservableProperty]
    public partial double GrowthGB { get; set; }

    [ObservableProperty]
    public partial bool EndOfLifeAlert { get; set; }

    [ObservableProperty]
    public partial bool SecretsAlert { get; set; }

    /// <summary>0 = default; 3 to 7 = days before a problem that is still there is announced again.</summary>
    [ObservableProperty]
    public partial int ReminderDays { get; set; }

    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    public bool UsingDefaultFolders => ProjectFolders.Count == 0;

    public static string Version => AppInfo.Version;

    public string DataFolder => _backend.DataFolder;

    public static string Privacy => "DevSweep never sends anything about your PC anywhere. The only network requests are for public support dates from endoflife.date (Health) and, unless you turn it off in Updates, the latest release from GitHub; neither carries anything about your PC. Scans read file and folder information only (names, sizes, dates), never the contents of your documents, and nothing is cleaned until you review and confirm. It runs as you, with no administrator rights; anything that needs one is shown as a command you run yourself.";

    partial void OnAppearanceChanged(AppearanceMode value) => _settings.Update(s => s with { Appearance = value });

    partial void OnExactScanChanged(bool value) => _settings.Update(s => s with { ExactScan = value });

    partial void OnCheckTimeoutSecondsChanged(double value)
        => _settings.Update(s => s with { CheckTimeoutSeconds = (int)Math.Clamp(double.IsNaN(value) ? 90 : value, 5, 600) });

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.Update(s => s with { CheckForUpdates = value });
        if (value && !_loading && _background is not null)
        {
            _ = _background.Updates.CheckIfDueAsync();
        }
    }

    partial void OnOpenAtLoginChanged(bool value)
    {
        if (_loading || _background is null)
        {
            return;
        }

        try
        {
            _background.Login.SetEnabled(value);
            LoginMessage = null;
        }
        catch (InvalidOperationException ex)
        {
            LoginMessage = ex.Message;
        }

        RefreshLogin();
    }

    partial void OnScheduleEnabledChanged(bool value) => UpdateSchedule(s => s with { Enabled = value });

    partial void OnScheduleFrequencyChanged(ScanFrequency value) => UpdateSchedule(s => s with { Frequency = value });

    partial void OnScheduleDayChanged(DayOfWeek value) => UpdateSchedule(s => s with { Day = value });

    partial void OnScheduleTimeChanged(TimeSpan value)
        => UpdateSchedule(s => s with { Hour = Math.Clamp(value.Hours, 0, 23), Minute = Math.Clamp(value.Minutes, 0, 59) });

    partial void OnDiskAlertChanged(bool value) => UpdateAlerts(a => a with { DiskEnabled = value });

    partial void OnDiskPercentChanged(double value)
        => UpdateAlerts(a => a with { DiskPercent = (int)Math.Clamp(double.IsNaN(value) ? 85 : Math.Round(value), DiskPercentMin, DiskPercentMax) });

    partial void OnGrowthAlertChanged(bool value) => UpdateAlerts(a => a with { GrowthEnabled = value });

    partial void OnGrowthGBChanged(double value)
        => UpdateAlerts(a => a with { GrowthGB = (int)Math.Clamp(double.IsNaN(value) ? 2 : Math.Round(value), GrowthGBMin, GrowthGBMax) });

    partial void OnEndOfLifeAlertChanged(bool value) => UpdateAlerts(a => a with { EndOfLifeEnabled = value });

    partial void OnSecretsAlertChanged(bool value) => UpdateAlerts(a => a with { SecretsEnabled = value });

    partial void OnReminderDaysChanged(int value)
        => UpdateAlerts(a => a with { ReminderDays = value <= 0 ? 0 : Math.Clamp(value, ReminderDaysMin, ReminderDaysMax) });

    /// <summary>"Every Monday at 09:00" while scheduled scans are on, else a sentence saying they are off.</summary>
    public string ScheduleSummary => _settings.Current.Schedule.Enabled ? _settings.Current.Schedule.Summary : "Scheduled scans are off.";

    /// <summary>When the next scheduled scan is due, from the last one (or from when the schedule was switched on).</summary>
    public string? NextScanText
    {
        get
        {
            ScanSchedule schedule = _settings.Current.Schedule;
            DateTimeOffset? from = _background?.Alerts.LastScheduledRun is { } last && (schedule.Since is not { } since || last > since) ? last : schedule.Since;
            if (!schedule.Enabled || from is not { } start)
            {
                return null;
            }

            DateTimeOffset next = schedule.NextRun(start);
            return next <= _backend.Now
                ? "Due now. It runs as soon as DevSweep is running and nothing else is scanning."
                : "Next scan: " + next.ToLocalTime().ToString("dddd d MMMM, HH:mm", CultureInfo.InvariantCulture) + ".";
        }
    }

    /// <summary>Whether Windows will show DevSweep's notifications, as a sentence for the Alerts page.</summary>
    public string NotificationsText => _background?.Toasts.Availability switch
    {
        ToastAvailability.Available => "Windows will show DevSweep's notifications.",
        ToastAvailability.DisabledByUser => "Windows has notifications turned off for DevSweep (Settings, System, Notifications). Alerts still appear in the tray menu and on Home.",
        _ => "Notifications are not available in this session. Alerts still appear in the tray menu and on Home.",
    };

    /// <summary>The start-up entry's state in words, for the Background page.</summary>
    public string LoginText => LoginState switch
    {
        LoginState.On => "DevSweep starts hidden in the tray when you sign in, and stays there when you close its window.",
        LoginState.DisabledInWindows => "Windows has DevSweep turned off in Settings, Apps, Startup, so it will not start at sign-in. Turn it on there, or switch this off and on again.",
        _ => "Off. DevSweep closes when you close its window, and scheduled scans only run while it is open.",
    };

    /// <summary>Health-based alerts need Health on; this says so when it is off.</summary>
    public string? HealthAlertsNote => _backend.Health is null
        ? "Health is off, so the end-of-life and secret-file alerts have nothing to look at. Turn Health on in Modules."
        : null;

    /// <summary>Windows' own Startup apps page (Settings, Apps, Startup).</summary>
    public static Uri StartupSettingsUri => new("ms-settings:startupapps");

    public void AddFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        _settings.Update(s => s with { ProjectFolders = [.. s.ProjectFolders.Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase)), full] });
    }

    public void RemoveFolder(string path) => _settings.Update(s => s with
    {
        ProjectFolders = [.. s.ProjectFolders.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))],
    });

    [RelayCommand]
    public void RestoreDefaultFolders() => _settings.Update(s => s with { ProjectFolders = [] });

    public void StopIgnoring(IgnoredItem item) => _settings.Update(s => s with { Ignored = [.. s.Ignored.Where(i => i.Id != item?.Id)] });

    [RelayCommand]
    public async Task CheckNowAsync()
    {
        if (_background is not null)
        {
            await _background.Updates.CheckNowAsync();
        }
    }

    /// <summary>Opens the release page of the update the last check found.</summary>
    public void OpenUpdateRelease() => _background?.Updates.OpenRelease();

    /// <summary>What the "Send a test notification" button said happened.</summary>
    [ObservableProperty]
    public partial string? TestToastText { get; set; }

    /// <summary>Shows a toast now, so the user can see Windows will display DevSweep's alerts.</summary>
    [RelayCommand]
    public void SendTestToast()
    {
        bool shown = _background?.Toasts.Show(new ToastMessage("DevSweep can send notifications", "This is a test. Click it to open DevSweep.", ToastTarget.Home)) ?? false;
        TestToastText = shown ? "Sent. If you don't see it, check Windows' notification settings (and Focus)." : NotificationsText;
    }

    /// <summary>The update the last check found, if any (the sidebar card shows it too).</summary>
    public UpdateInfo? AvailableUpdate => _background?.Updates.Available;

    private void UpdateSchedule(Func<ScanSchedule, ScanSchedule> change)
    {
        if (_loading)
        {
            return;
        }

        // Counting from now: a schedule that was just switched on or changed waits for its next run time.
        DateTimeOffset now = _backend.Now;
        _settings.Update(s => s with { Schedule = change(s.Schedule) with { Since = now } });
        OnPropertyChanged(nameof(ScheduleSummary));
        OnPropertyChanged(nameof(NextScanText));
    }

    private void UpdateAlerts(Func<AlertSettings, AlertSettings> change)
    {
        if (!_loading)
        {
            _settings.Update(s => s with { Alerts = change(s.Alerts) });
        }
    }

    private void RefreshLogin()
    {
        bool was = _loading;
        _loading = true;
        LoginState = _background?.Login.State ?? LoginState.Off;
        OpenAtLogin = LoginState != LoginState.Off;
        _loading = was;
        OnPropertyChanged(nameof(LoginText));
    }

    private void LoadBackgroundSettings()
    {
        AppSettings s = _settings.Current;
        ScheduleEnabled = s.Schedule.Enabled;
        ScheduleFrequency = s.Schedule.Frequency;
        ScheduleDay = s.Schedule.Day;
        ScheduleTime = new TimeSpan(Math.Clamp(s.Schedule.Hour, 0, 23), Math.Clamp(s.Schedule.Minute, 0, 59), 0);
        DiskAlert = s.Alerts.DiskEnabled;
        DiskPercent = Math.Clamp(s.Alerts.DiskPercent, DiskPercentMin, DiskPercentMax);
        GrowthAlert = s.Alerts.GrowthEnabled;
        GrowthGB = Math.Clamp(s.Alerts.GrowthGB, GrowthGBMin, GrowthGBMax);
        EndOfLifeAlert = s.Alerts.EndOfLifeEnabled;
        SecretsAlert = s.Alerts.SecretsEnabled;
        ReminderDays = s.Alerts.ReminderDays <= 0 ? 0 : Math.Clamp(s.Alerts.ReminderDays, ReminderDaysMin, ReminderDaysMax);
        RefreshLogin();
        if (_background is not null)
        {
            _background.Updates.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(UpdateViewModel.Status):
                        OnPropertyChanged(nameof(UpdateStatus));
                        break;
                    case nameof(UpdateViewModel.IsChecking):
                        OnPropertyChanged(nameof(IsCheckingUpdate));
                        break;
                    case nameof(UpdateViewModel.Available):
                        OnPropertyChanged(nameof(AvailableUpdate));
                        break;
                }
            };
        }
    }

    private void Toggle(string id, bool on)
    {
        _backend.SetModuleEnabled(id, on);
        RestartNeeded = true;
    }

    private void Reload()
    {
        ProjectFolders.Clear();
        foreach (string folder in _settings.Current.ProjectFolders)
        {
            ProjectFolders.Add(folder);
        }

        Ignored.Clear();
        foreach (IgnoredItem item in _settings.Current.Ignored)
        {
            Ignored.Add(item);
        }

        OnPropertyChanged(nameof(UsingDefaultFolders));
    }
}
