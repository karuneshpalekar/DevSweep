using DevSweep.App.ViewModels.Settings;

namespace DevSweep.App.ViewModels.Background;

/// <summary>What a scheduled scan did.</summary>
public enum ScheduledOutcome
{
    /// <summary>Nothing was due, or the schedule is off.</summary>
    NotDue,

    /// <summary>Due, but the user is scanning or reviewing right now; tried again at the next tick.</summary>
    Busy,

    /// <summary>Scanned, checked Health and told the user about new problems.</summary>
    Ran,
}

/// <summary>
/// A scheduled scan: exactly what the Scan button does (a read-only scan, then the read-only Health
/// checks the alerts need) followed by the alert rules. This class has no reference to Review, the cleaner,
/// History restore or the quarantine, and it does nothing while Review is open, so a scheduled scan cannot
/// clean, move or delete anything. It runs only while DevSweep is running.
/// </summary>
public sealed class ScheduledScans(
    AppSettingsStore settings,
    ScanViewModel scan,
    HealthViewModel health,
    AlertCoordinator alerts,
    Func<bool> reviewOpen,
    Func<DateTimeOffset> now)
{
    private bool _running;

    /// <summary>Raised when a scheduled scan starts and when it ends (the tray shows "Scanning…").</summary>
    public event EventHandler? RunningChanged;

    public bool IsRunning => _running;

    /// <summary>Runs a scan if the schedule says one is due (also the catch-up after sleep or while the app was closed).</summary>
    public async Task<ScheduledOutcome> RunIfDueAsync()
    {
        DateTimeOffset when = now();
        if (!settings.Current.Schedule.IsDue(alerts.LastScheduledRun, when))
        {
            return ScheduledOutcome.NotDue;
        }

        if (_running || scan.IsScanning || reviewOpen())
        {
            return ScheduledOutcome.Busy;
        }

        _running = true;
        RunningChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            // The same scan the Scan button runs (it also records the snapshot used for "grew since last week").
            await scan.ScanAsync();
            if (health.Source is not null && NeedsHealth())
            {
                await health.LoadAsync();
            }

            alerts.EvaluateAndAnnounce(now());
            // Counted from now, whatever the scan said: a failing scan must not retry every minute.
            alerts.RecordScheduledRun(now());
            return ScheduledOutcome.Ran;
        }
        finally
        {
            _running = false;
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool NeedsHealth() => settings.Current.Alerts is { EndOfLifeEnabled: true } or { SecretsEnabled: true };
}

/// <summary>
/// Checks the schedule about every half minute while the app runs, so a PC that was asleep at the scheduled
/// time scans within a minute of waking, and one that was off scans when DevSweep starts again.
/// </summary>
public sealed class ScanScheduler(ScheduledScans scans, TimeSpan? interval = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromSeconds(30);

    /// <summary>Starts the timer; the first check is immediate (catch-up at start).</summary>
    public void Start()
    {
        _loop ??= Loop();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task Loop()
    {
        using var timer = new PeriodicTimer(Interval);
        CancellationToken token = _stop.Token;
        try
        {
            do
            {
                try
                {
                    await scans.RunIfDueAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
                {
                    // A failed scheduled scan is not a reason to stop the timer; the next due time tries again.
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
