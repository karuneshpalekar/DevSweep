using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Health.Alerts;

namespace DevSweep.App.ViewModels.Background;

/// <summary>What one evaluation of the alert rules decided.</summary>
/// <param name="Events">The problems announced now (each new, or due a reminder).</param>
/// <param name="Toast">The one toast they became; null when there is nothing to say.</param>
/// <param name="Shown">True when the toast was handed to Windows.</param>
public sealed record AlertRun(IReadOnlyList<AlertEvent> Events, ToastMessage? Toast, bool Shown);

/// <summary>
/// Connects the scan to M3b's alert logic: turns the latest scan (disk, growth) and Health (end of life, secrets)
/// into <see cref="AlertInputs"/>, lets <see cref="AlertEvaluator"/> decide with the persistent
/// <see cref="AlertStore"/> ledger (each problem once, then a reminder after the chosen days if it is still
/// there), and shows one toast for all of them. <see cref="Active"/> is every problem present right now
/// (what the tray menu lists), whether or not it was announced already.
/// </summary>
public sealed class AlertCoordinator
{
    private readonly IDevSweepBackend _backend;
    private readonly AppSettingsStore _settings;
    private readonly ScanViewModel _scan;
    private readonly HealthViewModel _health;
    private readonly AlertStore _store;
    private readonly IToastNotifier _toasts;

    public AlertCoordinator(IDevSweepBackend backend, AppSettingsStore settings, ScanViewModel scan, HealthViewModel health, AlertStore store, IToastNotifier toasts)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(toasts);
        _backend = backend;
        _settings = settings;
        _scan = scan;
        _health = health;
        _store = store;
        _toasts = toasts;
        scan.Finished += (_, _) => RefreshActive();
        health.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HealthViewModel.Report))
            {
                RefreshActive();
            }
        };
        settings.Changed += (_, _) => RefreshActive();
    }

    public AlertStore Store => _store;

    /// <summary>Every problem present now that the user has not switched off, most important first.</summary>
    public IReadOnlyList<AlertEvent> Active { get; private set; } = [];

    public event EventHandler? ActiveChanged;

    /// <summary>When the last scheduled scan ran (<c>alerts.json</c>).</summary>
    public DateTimeOffset? LastScheduledRun => _store.Data.LastScheduledRun;

    /// <summary>The rules' inputs from the latest scan and Health result (either may be missing).</summary>
    public AlertInputs BuildInputs(HealthReport? health, IReadOnlySet<string>? knownSecretIds)
    {
        VolumeSpace? system = _backend.Volumes().FirstOrDefault(v => v.IsSystem);
        double? used = system is { TotalBytes: > 0 } ? 100.0 * (system.TotalBytes - system.FreeBytes) / system.TotalBytes : null;
        return new AlertInputs
        {
            DiskUsedPercent = used,
            Grew = [.. (_scan.Changes?.Grew ?? []).Select(c => new AlertGrowth(c.Title, c.Bytes))],
            EndOfLife = health?.Versions is { } versions ? AlertInputs.EndOfLifeFrom(versions) : [],
            Secrets = health?.Security is { } security ? AlertInputs.SecretsFrom(security) : [],
            KnownSecretIds = knownSecretIds,
        };
    }

    /// <summary>Recomputes <see cref="Active"/> from what is known now; nothing is recorded or shown.</summary>
    public void RefreshActive()
    {
        AlertInputs input = BuildInputs(_health.Report, new HashSet<string>(StringComparer.Ordinal));
        Active = AlertEvaluator.Evaluate(input, _settings.Current.Alerts, new AlertLedger(), _backend.Now);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Decides what to announce after a scheduled scan, records it (so each problem is said once), remembers
    /// the secret files seen so far (so only later ones are "new"), and shows one toast.
    /// </summary>
    public AlertRun EvaluateAndAnnounce(DateTimeOffset now)
    {
        AlertData data = _store.Data;
        HealthReport? health = _health.Report;
        HashSet<string>? known = data.KnownSecretIds is null ? null : new HashSet<string>(data.KnownSecretIds, StringComparer.Ordinal);
        AlertInputs input = BuildInputs(health, known);
        IReadOnlyList<AlertEvent> events = AlertEvaluator.Evaluate(input, _settings.Current.Alerts, data.Ledger, now);
        _store.Update(d => d with
        {
            Ledger = data.Ledger,
            KnownSecretIds = health?.Security is null ? d.KnownSecretIds : [.. input.Secrets.Select(s => s.Id)],
        });
        ToastMessage? toast = AlertToasts.Compose(events);
        bool shown = toast is not null && _toasts.Show(toast);
        RefreshActive();
        return new AlertRun(events, toast, shown);
    }

    /// <summary>Remembers that a scheduled scan ran (the schedule counts from here).</summary>
    public void RecordScheduledRun(DateTimeOffset when) => _store.Update(d => d with { LastScheduledRun = when });
}
