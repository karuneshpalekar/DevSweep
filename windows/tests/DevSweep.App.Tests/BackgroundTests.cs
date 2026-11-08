using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Updates;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Platform.Quarantine;
using DevSweep.Core.Review;
using DevSweep.Core.Scanning;
using Xunit;

namespace DevSweep.App.Tests;

/// <summary>Scheduled scans: when they are due, catch-up after sleep or while closed, and the fixed time zone maths.</summary>
public sealed class ScanScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_is_due_at_its_time_and_not_before()
    {
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 9 };
        DateTimeOffset last = At(2, 10);   // Monday 2 Nov 2026, 10:00
        Assert.False(schedule.IsDue(last, At(3, 8, 59), Utc));
        Assert.True(schedule.IsDue(last, At(3, 9), Utc));
        Assert.Equal(At(3, 9), schedule.NextRun(last, Utc));
    }

    [Fact]
    public void Weekly_waits_for_its_day()
    {
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Monday, Hour = 9 };
        DateTimeOffset last = At(2, 9);   // Monday 09:00 exactly: the next run is a week later
        Assert.Equal(At(9, 9), schedule.NextRun(last, Utc));
        Assert.False(schedule.IsDue(last, At(8, 23), Utc));
        Assert.True(schedule.IsDue(last, At(9, 9, 1), Utc));
    }

    [Fact]
    public void A_pc_that_was_asleep_catches_up_once()
    {
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Monday, Hour = 9 };
        DateTimeOffset last = At(2, 9, 5);          // ran Monday
        DateTimeOffset wakes = At(12, 14);          // Thursday of the week after next: two runs were missed
        Assert.True(schedule.IsDue(last, wakes, Utc));
        // It runs once, is recorded, and the next one is the following Monday.
        Assert.False(schedule.IsDue(wakes, wakes.AddMinutes(1), Utc));
        Assert.Equal(At(16, 9), schedule.NextRun(wakes, Utc));
    }

    [Fact]
    public void A_schedule_just_switched_on_waits_for_its_first_run_time()
    {
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 9, Since = At(4, 10) };
        Assert.False(schedule.IsDue(null, At(4, 23), Utc));
        Assert.False(schedule.IsDue(null, At(5, 8, 59), Utc));
        Assert.True(schedule.IsDue(null, At(5, 9), Utc));
    }

    [Fact]
    public void Changing_the_schedule_counts_from_the_change_not_from_the_last_run()
    {
        // The last scan was long ago, but the time was only just changed to 21:00 today: not due until 21:00.
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 21, Since = At(10, 15) };
        DateTimeOffset last = At(1, 9);
        Assert.False(schedule.IsDue(last, At(10, 16), Utc));
        Assert.True(schedule.IsDue(last, At(10, 21), Utc));
    }

    [Fact]
    public void Off_is_never_due_and_nothing_known_is_not_due()
    {
        Assert.False(new ScanSchedule { Enabled = false, Since = At(1, 0) }.IsDue(At(1, 0), At(30, 12), Utc));
        Assert.False(new ScanSchedule { Enabled = true }.IsDue(null, At(30, 12), Utc));
    }

    [Fact]
    public void The_time_is_the_clock_time_of_the_zone_the_schedule_is_in()
    {
        TimeZoneInfo plus530 = TimeZoneInfo.CreateCustomTimeZone("test+5:30", TimeSpan.FromHours(5.5), "test", "test");
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 9, Minute = 30 };
        DateTimeOffset next = schedule.NextRun(At(3, 0), plus530);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.FromHours(5.5)), next);
        Assert.Equal(At(3, 4), next.ToUniversalTime());
    }

    [Fact]
    public void The_summary_reads_like_the_Mac_one()
    {
        Assert.Equal("Every day at 07:05", new ScanSchedule { Frequency = ScanFrequency.Daily, Hour = 7, Minute = 5 }.Summary);
        Assert.Equal("Every Friday at 18:00", new ScanSchedule { Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Friday, Hour = 18 }.Summary);
    }
}

/// <summary>
/// A backend that runs the real sample data but records every call and refuses anything that changes files:
/// proof that a scheduled scan only reads.
/// </summary>
internal sealed class WatchedBackend(DateTimeOffset now) : IDevSweepBackend, IHealthSource
{
    private readonly SampleBackend _inner = new();

    /// <summary>Calls that would clean, move, delete, restore, run a command or write the module flags.</summary>
    public static readonly HashSet<string> Forbidden = ["RemeasureAsync", "PrepareAsync", "CleanAsync", "RestoreAsync", "EmptyQuarantineAsync", "OpenInTerminal", "SetModuleEnabled", "OpenFix"];

    public List<string> Calls { get; } = [];

    /// <summary>False while a test sets up a state that needs the user's own actions (opening Review).</summary>
    public bool Strict { get; set; } = true;

    public DateTimeOffset NowValue { get; set; } = now;

    public HealthReport? HealthOverride { get; set; }

    private void Record(string name)
    {
        Calls.Add(name);
        if (Strict && Forbidden.Contains(name))
        {
            throw new InvalidOperationException($"{name} must never run from a scheduled scan.");
        }
    }

    public IReadOnlyList<ModuleInfo> Modules => _inner.Modules;

    public IHealthSource? Health => this;

    public IProjectsSource? Projects => _inner.Projects;

    public IUpdateChecker Updates => _inner.Updates;

    public string? ProfilePath => _inner.ProfilePath;

    public string DataFolder => _inner.DataFolder;

    public DateTimeOffset Now => NowValue;

    public void SetModuleEnabled(string id, bool enabled) => Record(nameof(SetModuleEnabled));

    public Task<ScanResult> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        Record(nameof(ScanAsync));
        return _inner.ScanAsync(progress, cancellationToken);
    }

    public Task<Finding> RemeasureAsync(Finding finding, CancellationToken cancellationToken)
    {
        Record(nameof(RemeasureAsync));
        return _inner.RemeasureAsync(finding, cancellationToken);
    }

    public Task<ReviewPlan> PrepareAsync(IReadOnlyList<PlannedAction> plan, CancellationToken cancellationToken)
    {
        Record(nameof(PrepareAsync));
        return _inner.PrepareAsync(plan, cancellationToken);
    }

    public Task<IReadOnlyList<ActionOutcome>> CleanAsync(IReadOnlyList<PlannedAction> plan, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Record(nameof(CleanAsync));
        return _inner.CleanAsync(plan, progress, cancellationToken);
    }

    public IReadOnlyList<HistoryEntry> History() => _inner.History();

    public bool CanRestore(HistoryEntry entry) => _inner.CanRestore(entry);

    public Task<HistoryRestoreResult> RestoreAsync(Guid id)
    {
        Record(nameof(RestoreAsync));
        return _inner.RestoreAsync(id);
    }

    public IReadOnlyList<QuarantineEntry> Quarantine() => _inner.Quarantine();

    public Task<long> EmptyQuarantineAsync()
    {
        Record(nameof(EmptyQuarantineAsync));
        return _inner.EmptyQuarantineAsync();
    }

    public IReadOnlyList<VolumeSpace> Volumes()
    {
        Record(nameof(Volumes));
        return _inner.Volumes();
    }

    public void OpenInTerminal(string title, IReadOnlyList<string> commands, bool needsAdmin) => Record(nameof(OpenInTerminal));

    public ScanChanges? CompareAndRecord(IReadOnlyList<Finding> findings, long? diskFree)
    {
        Record(nameof(CompareAndRecord));
        return _inner.CompareAndRecord(findings, diskFree);
    }

    DevSweep.Core.Health.HealthFeatures IHealthSource.Features => new(true, true, true);

    Task<HealthReport> IHealthSource.LoadAsync(CancellationToken cancellationToken)
    {
        Record("Health.LoadAsync");
        return Task.FromResult(HealthOverride ?? SampleData.Health);
    }

    void IHealthSource.OpenFix(DevSweep.Core.Health.Runtimes.RuntimeStep fix) => Record("OpenFix");
}

/// <summary>Remembers every toast it was asked to show.</summary>
internal sealed class RecordingToasts : IToastNotifier
{
    public List<ToastMessage> Shown { get; } = [];

    public ToastAvailability Availability => ToastAvailability.Available;

    public bool Show(ToastMessage message)
    {
        Shown.Add(message);
        return true;
    }
}

public sealed class ScheduledScanTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static (MainViewModel Model, WatchedBackend Backend, RecordingToasts Toasts, AppSettingsStore Settings) Create(Func<AppSettings, AppSettings>? tweak = null)
    {
        var backend = new WatchedBackend(Monday);
        var settings = new AppSettingsStore(null);
        settings.Update(s => (tweak ?? (x => x))(s with
        {
            WelcomeDone = true,
            Schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 9, Since = Monday.AddDays(-2) },
        }));
        var toasts = new RecordingToasts();
        return (new MainViewModel(backend, settings, SystemIntegration.Memory(toasts: toasts)), backend, toasts, settings);
    }

    [Fact]
    public async Task A_scheduled_scan_only_reads_it_never_cleans_moves_or_deletes()
    {
        (MainViewModel model, WatchedBackend backend, _, _) = Create();

        ScheduledOutcome outcome = await model.Background.Scheduled.RunIfDueAsync();

        Assert.Equal(ScheduledOutcome.Ran, outcome);
        // A scan and the read-only Health checks ran, and the snapshot was recorded once (the "grew since last week" baseline).
        Assert.Single(backend.Calls, c => c == "ScanAsync");
        Assert.Single(backend.Calls, c => c == "CompareAndRecord");
        Assert.Contains("Health.LoadAsync", backend.Calls);
        Assert.DoesNotContain(backend.Calls, WatchedBackend.Forbidden.Contains);
        Assert.False(model.Review.IsOpen);
        Assert.NotEmpty(model.Scan.Findings);   // the results are in the app, waiting for the user's review
    }

    [Fact]
    public async Task Nothing_runs_while_the_review_sheet_is_open()
    {
        (MainViewModel model, WatchedBackend backend, _, _) = Create();
        await model.Scan.ScanAsync();
        backend.Strict = false;   // the user opens Review themselves
        await model.ReviewAsync([.. model.CleanUp.All.Where(f => f.CanSelect).Take(1).Select(f => new PlannedAction(f.Finding, f.ChosenAction))]);
        Assert.True(model.Review.IsOpen);
        backend.Strict = true;
        backend.Calls.Clear();

        ScheduledOutcome outcome = await model.Background.Scheduled.RunIfDueAsync();

        Assert.Equal(ScheduledOutcome.Busy, outcome);
        Assert.DoesNotContain("ScanAsync", backend.Calls);
    }

    [Fact]
    public async Task It_runs_once_then_waits_for_the_next_time_and_catches_up_after_sleep()
    {
        (MainViewModel model, WatchedBackend backend, _, _) = Create();
        DateTimeOffset tuesdayNine = Monday.AddDays(1);

        backend.NowValue = Monday;   // 09:00 Monday: the schedule was switched on two days before, so it is due
        Assert.Equal(ScheduledOutcome.Ran, await model.Background.Scheduled.RunIfDueAsync());
        Assert.Equal(Monday, model.Background.Alerts.LastScheduledRun);

        backend.NowValue = tuesdayNine.AddMinutes(-1);
        Assert.Equal(ScheduledOutcome.NotDue, await model.Background.Scheduled.RunIfDueAsync());

        backend.NowValue = tuesdayNine.AddDays(2).AddHours(5);   // the PC slept through two runs
        Assert.Equal(ScheduledOutcome.Ran, await model.Background.Scheduled.RunIfDueAsync());
        Assert.Equal(2, backend.Calls.Count(c => c == "ScanAsync"));
        Assert.Equal(ScheduledOutcome.NotDue, await model.Background.Scheduled.RunIfDueAsync());
    }

    [Fact]
    public async Task A_schedule_that_is_off_never_scans()
    {
        (MainViewModel model, WatchedBackend backend, _, AppSettingsStore settings) = Create();
        settings.Update(s => s with { Schedule = s.Schedule with { Enabled = false } });
        backend.NowValue = Monday.AddDays(30);

        Assert.Equal(ScheduledOutcome.NotDue, await model.Background.Scheduled.RunIfDueAsync());
        Assert.DoesNotContain(backend.Calls, c => c == "ScanAsync");
    }

    [Fact]
    public async Task The_timer_checks_straight_away_and_stops_when_disposed()
    {
        (MainViewModel model, WatchedBackend backend, _, _) = Create();
        using var scheduler = new ScanScheduler(model.Background.Scheduled, TimeSpan.FromMilliseconds(20));
        scheduler.Start();
        for (int i = 0; i < 200 && !backend.Calls.Contains("ScanAsync"); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Contains("ScanAsync", backend.Calls);
        Assert.DoesNotContain(backend.Calls, WatchedBackend.Forbidden.Contains);
    }
}

public sealed class AlertFlowTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static (MainViewModel Model, WatchedBackend Backend, RecordingToasts Toasts) Create(AlertSettings alerts)
    {
        var backend = new WatchedBackend(Start);
        var settings = new AppSettingsStore(null);
        settings.Update(s => s with { WelcomeDone = true, Alerts = alerts, Schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 9, Since = Start.AddDays(-1) } });
        var toasts = new RecordingToasts();
        return (new MainViewModel(backend, settings, SystemIntegration.Memory(toasts: toasts)), backend, toasts);
    }

    private static async Task RunAt(MainViewModel model, WatchedBackend backend, DateTimeOffset when)
    {
        backend.NowValue = when;
        // Each run is a scheduled one: force it due by moving the baseline back.
        model.Settings.Update(s => s with { Schedule = s.Schedule with { Since = when.AddDays(-2) } });
        model.Background.Alerts.Store.Update(d => d with { LastScheduledRun = null });
        Assert.Equal(ScheduledOutcome.Ran, await model.Background.Scheduled.RunIfDueAsync());
    }

    [Fact]
    public async Task Several_problems_become_one_toast_and_each_is_announced_once()
    {
        // Disk 83% full (sample C:), Gradle grew 1.2 GB, two end-of-life tools.
        (MainViewModel model, WatchedBackend backend, RecordingToasts toasts) = Create(new AlertSettings { DiskPercent = 70, GrowthGB = 1 });

        await RunAt(model, backend, Start);

        ToastMessage toast = Assert.Single(toasts.Shown);
        Assert.Equal("DevSweep found 3 things to look at", toast.Title);
        Assert.Contains("Your disk is 83% full", toast.Body, StringComparison.Ordinal);
        Assert.Equal(ToastTarget.Home, toast.Target);   // the problems are on different screens

        // The same problems, a day later: nothing new to say.
        await RunAt(model, backend, Start.AddDays(1));
        Assert.Single(toasts.Shown);
    }

    [Fact]
    public async Task A_problem_that_is_still_there_is_announced_again_after_the_reminder_days()
    {
        (MainViewModel model, WatchedBackend backend, RecordingToasts toasts) = Create(new AlertSettings { DiskPercent = 70, GrowthEnabled = false, EndOfLifeEnabled = false, SecretsEnabled = false, ReminderDays = 5 });

        await RunAt(model, backend, Start);
        await RunAt(model, backend, Start.AddDays(4));
        Assert.Single(toasts.Shown);

        await RunAt(model, backend, Start.AddDays(5));
        Assert.Equal(2, toasts.Shown.Count);
        Assert.Equal("Your disk is 83% full", toasts.Shown[1].Title);
        Assert.Equal(ToastTarget.CleanUp, toasts.Shown[1].Target);
    }

    [Fact]
    public async Task Without_a_reminder_choice_the_Mac_defaults_apply_three_days_for_a_full_disk_seven_for_the_rest()
    {
        (MainViewModel model, WatchedBackend backend, RecordingToasts toasts) = Create(new AlertSettings { DiskPercent = 70, GrowthGB = 1 });

        await RunAt(model, backend, Start);
        Assert.Single(toasts.Shown);
        await RunAt(model, backend, Start.AddDays(3));
        ToastMessage disk = Assert.Single(toasts.Shown.Skip(1));
        Assert.Equal("Your disk is 83% full", disk.Title);   // only the disk comes back
        await RunAt(model, backend, Start.AddDays(7));
        Assert.True(toasts.Shown.Count >= 3);
    }

    [Fact]
    public async Task Every_alert_can_be_switched_off_on_its_own()
    {
        (MainViewModel model, WatchedBackend backend, RecordingToasts toasts) = Create(new AlertSettings
        {
            DiskEnabled = false,
            GrowthEnabled = false,
            EndOfLifeEnabled = true,
            SecretsEnabled = false,
            GrowthGB = 1,
            DiskPercent = 70,
        });

        await RunAt(model, backend, Start);

        ToastMessage toast = Assert.Single(toasts.Shown);
        Assert.Equal("2 tools you use are past end of life", toast.Title);
        Assert.Equal(ToastTarget.HealthTools, toast.Target);
    }

    [Fact]
    public async Task A_new_secret_file_is_announced_only_after_a_baseline_and_opens_Security()
    {
        (MainViewModel model, WatchedBackend backend, RecordingToasts toasts) = Create(new AlertSettings { DiskEnabled = false, GrowthEnabled = false, EndOfLifeEnabled = false });

        await RunAt(model, backend, Start);   // first run: what exists is the baseline, not "new"
        Assert.Empty(toasts.Shown);

        SecurityResult sample = SampleData.Health.Security!;
        var fresh = new SecurityFinding
        {
            Kind = SecurityKind.PrivateKey,
            Level = SecurityLevel.Critical,
            Title = "Private key",
            Path = @"C:\Users\alex\Downloads\id_rsa",
            Status = "Plain text",
            Why = "x",
            WhatToDo = "y",
        };
        backend.HealthOverride = SampleData.Health with { Security = sample with { Findings = [.. sample.Findings, fresh] } };

        await RunAt(model, backend, Start.AddDays(1));

        ToastMessage toast = Assert.Single(toasts.Shown);
        Assert.Equal("A new file looks like a secret", toast.Title);
        Assert.Contains("id_rsa", toast.Body, StringComparison.Ordinal);
        Assert.Equal(ToastTarget.HealthSecurity, toast.Target);
    }

    [Fact]
    public async Task The_tray_lists_every_active_alert_even_ones_already_announced()
    {
        (MainViewModel model, WatchedBackend backend, RecordingToasts toasts) = Create(new AlertSettings { DiskPercent = 70, GrowthGB = 1 });
        await RunAt(model, backend, Start);
        await RunAt(model, backend, Start.AddDays(1));
        Assert.Single(toasts.Shown);

        IReadOnlyList<TrayItem> items = model.Background.Tray.Items();

        Assert.Contains(items, i => i.Text.StartsWith("Needs attention", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Command == TrayCommand.OpenTarget && i.Text == "Your disk is 83% full" && i.Argument == nameof(ToastTarget.CleanUp));
        Assert.Contains(items, i => i.Command == TrayCommand.OpenTarget && i.Argument == nameof(ToastTarget.HealthTools));
    }

    [Fact]
    public void One_problem_keeps_its_own_words_and_screen()
    {
        ToastMessage toast = AlertToasts.Compose([new AlertEvent("disk", "Your disk is 90% full", "DevSweep can show what's safe to clean.", AlertTarget.CleanUp)])!;
        Assert.Equal(("Your disk is 90% full", "DevSweep can show what's safe to clean.", ToastTarget.CleanUp), (toast.Title, toast.Body, toast.Target));
        Assert.Null(AlertToasts.Compose([]));
    }

    [Fact]
    public void Problems_on_one_screen_open_that_screen_and_a_long_list_is_counted()
    {
        AlertEvent[] events = [.. Enumerable.Range(1, 5).Select(i => new AlertEvent("k" + i, "Problem " + i, "b", AlertTarget.HealthTools))];
        ToastMessage toast = AlertToasts.Compose(events)!;
        Assert.Equal("DevSweep found 5 things to look at", toast.Title);
        Assert.Equal("Problem 1\nProblem 2\nProblem 3\nand 2 more", toast.Body);
        Assert.Equal(ToastTarget.HealthTools, toast.Target);
    }

    [Theory]
    [InlineData(ToastTarget.CleanUp, "cleanup")]
    [InlineData(ToastTarget.HealthTools, "health")]
    [InlineData(ToastTarget.HealthSecurity, "health")]
    [InlineData(ToastTarget.Home, "home")]
    public void A_click_on_a_toast_opens_the_right_screen(ToastTarget target, string place)
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));

        model.Open(target);

        Assert.Equal(place, model.Current);
        if (target == ToastTarget.HealthSecurity)
        {
            Assert.Equal(HealthTab.Security, model.Health.Tab);
        }

        if (target == ToastTarget.HealthTools)
        {
            Assert.Equal(HealthTab.Tools, model.Health.Tab);
        }
    }

    [Fact]
    public void A_toast_target_survives_the_round_trip_through_its_launch_argument()
    {
        foreach (ToastTarget target in Enum.GetValues<ToastTarget>())
        {
            var message = new ToastMessage("t", "b", target);
            Assert.Equal(target, ToastMessage.ParseTarget(message.Argument));
            Assert.Equal(target, ToastMessage.ParseTarget(new Dictionary<string, string> { ["target"] = target.ToString() }));
        }

        Assert.Equal(ToastTarget.Home, ToastMessage.ParseTarget("target=Nonsense"));
        Assert.Equal(ToastTarget.Home, ToastMessage.ParseTarget((string?)null));
        Assert.Equal(ToastTarget.Home, ToastMessage.ParseTarget("target=99"));
    }

    [Fact]
    public void Reminder_days_change_the_core_rules_without_touching_the_defaults()
    {
        var ledger = new AlertLedger();
        var input = new AlertInputs { DiskUsedPercent = 90 };
        DateTimeOffset now = Start;
        Assert.Single(AlertEvaluator.Evaluate(input, new AlertSettings { ReminderDays = 6 }, ledger, now));
        Assert.Empty(AlertEvaluator.Evaluate(input, new AlertSettings { ReminderDays = 6 }, ledger, now.AddDays(5)));
        Assert.Single(AlertEvaluator.Evaluate(input, new AlertSettings { ReminderDays = 6 }, ledger, now.AddDays(6)));

        var defaults = new AlertLedger();
        Assert.Single(AlertEvaluator.Evaluate(input, new AlertSettings(), defaults, now));
        Assert.Single(AlertEvaluator.Evaluate(input, new AlertSettings(), defaults, now.AddDays(3)));
    }
}

public sealed class TrayMenuTests
{
    [Fact]
    public void The_menu_has_free_space_open_scan_now_and_quit_in_order()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));

        IReadOnlyList<TrayItem> items = model.Background.Tray.Items();

        string free = DevSweep.App.ViewModels.Text.SizeLabels.Size(SampleData.Volumes[0].FreeBytes);
        Assert.Equal(free + " free on Windows (C:)", items[0].Text);
        Assert.False(items[0].Enabled);
        string[] commands = [.. items.Where(i => i.Command is TrayCommand.OpenWindow or TrayCommand.ScanNow or TrayCommand.Quit).Select(i => i.Text)];
        Assert.Equal(["Open DevSweep", "Scan now", "Quit DevSweep"], commands);
        Assert.StartsWith("DevSweep · " + free, model.Background.Tray.Tooltip(), StringComparison.Ordinal);
        Assert.True(model.Background.Tray.Tooltip().Length <= 127);
    }

    [Fact]
    public async Task Recent_projects_appear_when_Projects_is_on_newest_first()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        await model.Projects.LoadAsync();

        List<TrayItem> projects = [.. model.Background.Tray.Items().Where(i => i.Command == TrayCommand.RevealProject)];

        Assert.NotEmpty(projects);
        Assert.True(projects.Count <= TrayViewModel.MaxListed);
        Assert.All(projects, p => Assert.False(string.IsNullOrEmpty(p.Argument)));
        var expected = model.Projects.Report!.Snapshot.Projects.Where(p => p.OnDisk && !string.IsNullOrEmpty(p.LocalPath))
            .OrderByDescending(p => p.LastOpened ?? p.LastActivity ?? DateTimeOffset.MinValue).Select(p => p.Name).Take(TrayViewModel.MaxListed);
        Assert.Equal(expected, projects.Select(p => p.Text));
    }

    [Fact]
    public async Task Without_the_Projects_module_there_is_no_recent_projects_section()
    {
        var model = new MainViewModel(new NoProjectsBackend(), new AppSettingsStore(null));
        await model.Projects.LoadAsync();

        Assert.DoesNotContain(model.Background.Tray.Items(), i => i.Command == TrayCommand.RevealProject || i.Text == "Recent projects");
    }

    [Fact]
    public async Task Commands_raise_the_right_requests()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        TrayViewModel tray = model.Background.Tray;
        var shown = new List<ToastTarget?>();
        int quits = 0;
        string? revealed = null;
        tray.ShowRequested += (_, t) => shown.Add(t);
        tray.QuitRequested += (_, _) => quits++;
        tray.RevealRequested += (_, p) => revealed = p;

        tray.Execute(new TrayItem("Open DevSweep", TrayCommand.OpenWindow));
        tray.Execute(new TrayItem("alert", TrayCommand.OpenTarget, nameof(ToastTarget.HealthSecurity)));
        tray.Execute(new TrayItem("p", TrayCommand.RevealProject, @"D:\src\web-shop"));
        tray.Execute(new TrayItem("Quit DevSweep", TrayCommand.Quit));
        tray.Execute(new TrayItem("disabled", TrayCommand.Quit, Enabled: false));
        tray.Execute(new TrayItem("Scan now", TrayCommand.ScanNow));
        for (int i = 0; i < 100 && !model.Scan.HasScanned; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal([null, ToastTarget.HealthSecurity], shown);
        Assert.Equal(1, quits);
        Assert.Equal(@"D:\src\web-shop", revealed);
        Assert.True(model.Scan.HasScanned);   // Scan now ran a normal read-only scan
    }

    [Fact]
    public async Task Scan_now_is_disabled_while_scanning_and_while_review_is_open()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        await model.Scan.ScanAsync();
        await model.ReviewAsync([.. model.CleanUp.All.Where(f => f.CanSelect).Take(1).Select(f => new PlannedAction(f.Finding, f.ChosenAction))]);

        TrayItem scan = model.Background.Tray.Items().Single(i => i.Command == TrayCommand.ScanNow);

        Assert.False(scan.Enabled);
    }

    private sealed class NoProjectsBackend : IDevSweepBackend
    {
        private readonly SampleBackend _inner = new();

        public IReadOnlyList<ModuleInfo> Modules => _inner.Modules;

        public IHealthSource? Health => null;

        public IProjectsSource? Projects => null;

        public IUpdateChecker Updates => _inner.Updates;

        public string? ProfilePath => _inner.ProfilePath;

        public string DataFolder => _inner.DataFolder;

        public DateTimeOffset Now => _inner.Now;

        public void SetModuleEnabled(string id, bool enabled) => _inner.SetModuleEnabled(id, enabled);

        public Task<ScanResult> ScanAsync(IProgress<ScanProgress> progress, CancellationToken cancellationToken) => _inner.ScanAsync(progress, cancellationToken);

        public Task<Finding> RemeasureAsync(Finding finding, CancellationToken cancellationToken) => _inner.RemeasureAsync(finding, cancellationToken);

        public Task<ReviewPlan> PrepareAsync(IReadOnlyList<PlannedAction> plan, CancellationToken cancellationToken) => _inner.PrepareAsync(plan, cancellationToken);

        public Task<IReadOnlyList<ActionOutcome>> CleanAsync(IReadOnlyList<PlannedAction> plan, IProgress<string> progress, CancellationToken cancellationToken)
            => _inner.CleanAsync(plan, progress, cancellationToken);

        public IReadOnlyList<HistoryEntry> History() => _inner.History();

        public bool CanRestore(HistoryEntry entry) => _inner.CanRestore(entry);

        public Task<HistoryRestoreResult> RestoreAsync(Guid id) => _inner.RestoreAsync(id);

        public IReadOnlyList<QuarantineEntry> Quarantine() => _inner.Quarantine();

        public Task<long> EmptyQuarantineAsync() => _inner.EmptyQuarantineAsync();

        public IReadOnlyList<VolumeSpace> Volumes() => _inner.Volumes();

        public void OpenInTerminal(string title, IReadOnlyList<string> commands, bool needsAdmin) => _inner.OpenInTerminal(title, commands, needsAdmin);

        public ScanChanges? CompareAndRecord(IReadOnlyList<Finding> findings, long? diskFree) => _inner.CompareAndRecord(findings, diskFree);
    }
}
