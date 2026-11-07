using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DevSweep.App.ViewModels.Settings;

namespace DevSweep.App.ViewModels.Updates;

/// <summary>The running app's version (the executable sets it at start; tests and tools get the view-model assembly's).</summary>
public static class AppInfo
{
    private static string? _version;

    public static string Version
    {
        get => _version ?? FromAssembly(typeof(AppInfo).Assembly);
        set => _version = value;
    }

    public static string FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
    }
}

/// <summary>
/// The update check as the app shows it: a card in the sidebar when a newer Windows release exists, a
/// status line in Settings, one automatic check about every day (and after a failure, a retry no sooner than
/// two hours later, so a rate limit or no connection is never hammered). Off when the Settings switch is off.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    /// <summary>A successful check younger than this is not repeated.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    /// <summary>After a failed attempt, wait at least this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(2);

    private readonly IUpdateChecker _checker;
    private readonly AppSettingsStore _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public UpdateViewModel(IUpdateChecker checker, AppSettingsStore settings, Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(now);
        _checker = checker;
        _settings = settings;
        _now = now;
        settings.Changed += (_, _) => OnPropertyChanged(nameof(ShowCard));
    }

    /// <summary>The newer release found by the last check, if any.</summary>
    [ObservableProperty]
    public partial UpdateInfo? Available { get; set; }

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    /// <summary>A sentence for Settings ("You're up to date", "Version 0.2.0 is available", why a check failed).</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>The sidebar card: a newer release, checks are on, and the user has not chosen "Later" for it.</summary>
    public bool ShowCard => Available is { } info && _settings.Current.CheckForUpdates && _settings.Current.SkippedUpdate != info.Version;

    /// <summary>Asks the app to open this release page in the browser (only ever an https github.com address).</summary>
    public event EventHandler<Uri>? OpenRequested;

    partial void OnAvailableChanged(UpdateInfo? value) => OnPropertyChanged(nameof(ShowCard));

    /// <summary>The "Check now" button: ignores the daily limit and a skipped version, and says why a check failed.</summary>
    public async Task CheckNowAsync()
    {
        await CheckAsync(manual: true).ConfigureAwait(true);
    }

    /// <summary>The automatic check: only when switched on and one is due.</summary>
    public async Task CheckIfDueAsync()
    {
        AppSettings settings = _settings.Current;
        if (!settings.CheckForUpdates)
        {
            return;
        }

        DateTimeOffset now = _now();
        bool successDue = settings.LastUpdateCheck is not { } last || now - last >= CheckEvery || now < last;
        bool retryOk = settings.LastUpdateAttempt is not { } attempt || now - attempt >= RetryAfter || now < attempt;
        if (successDue && retryOk)
        {
            await CheckAsync(manual: false).ConfigureAwait(true);
        }
    }

    /// <summary>Starts the daily check: one now (if due), then every hour looks again whether one is due.</summary>
    public void Start()
    {
        _loop ??= Loop();
    }

    /// <summary>"Later": this version is not offered again; a newer one is.</summary>
    public void Later()
    {
        if (Available is { } info)
        {
            _settings.Update(s => s with { SkippedUpdate = info.Version });
            OnPropertyChanged(nameof(ShowCard));
        }
    }

    /// <summary>"See what's new": opens the release page.</summary>
    public void OpenRelease()
    {
        if (Available is { } info)
        {
            OpenRequested?.Invoke(this, info.PageUrl);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task CheckAsync(bool manual)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        if (manual)
        {
            Status = null;
        }

        try
        {
            UpdateCheckResult result = await _checker.CheckAsync(_stop.Token).ConfigureAwait(true);
            DateTimeOffset now = _now();
            switch (result.Outcome)
            {
                case UpdateOutcome.Available:
                    Available = result.Update;
                    Status = result.Message;
                    _settings.Update(s => s with { LastUpdateCheck = now, LastUpdateAttempt = now });
                    break;
                case UpdateOutcome.UpToDate:
                    Available = null;
                    Status = result.Message;
                    _settings.Update(s => s with { LastUpdateCheck = now, LastUpdateAttempt = now });
                    break;
                default:
                    // Quiet in the background; only a manual check says why. A failure keeps what was known.
                    if (manual)
                    {
                        Status = result.Message;
                    }

                    _settings.Update(s => s with { LastUpdateAttempt = now });
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task Loop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        CancellationToken token = _stop.Token;
        try
        {
            do
            {
                await CheckIfDueAsync().ConfigureAwait(true);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
