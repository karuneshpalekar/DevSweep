using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Health.Alerts;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.App.Tests;

internal sealed class FakeRunKeyStore : IRunKeyStore
{
    public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Throw { get; set; }

    public string? GetRun(string name) => Run.GetValueOrDefault(name);

    public void SetRun(string name, string command)
    {
        if (Throw)
        {
            throw new UnauthorizedAccessException("Group Policy blocks it.");
        }

        Run[name] = command;
    }

    public void DeleteRun(string name) => Run.Remove(name);

    public byte[]? GetApproved(string name) => Approved.GetValueOrDefault(name);

    public void DeleteApproved(string name) => Approved.Remove(name);
}

public sealed class LoginItemTests
{
    private const string Exe = @"C:\Users\alex\AppData\Local\Programs\DevSweep\DevSweep.exe";

    [Fact]
    public void It_is_off_until_the_user_turns_it_on_and_adds_exactly_one_per_user_entry()
    {
        var store = new FakeRunKeyStore();
        var login = new RunKeyLoginItem(store, Exe);
        Assert.Equal(LoginState.Off, login.State);
        Assert.Empty(store.Run);

        login.SetEnabled(true);

        Assert.Equal(LoginState.On, login.State);
        KeyValuePair<string, string> entry = Assert.Single(store.Run);
        Assert.Equal("DevSweep", entry.Key);
        Assert.Equal("\"" + Exe + "\" --background", entry.Value);
    }

    [Fact]
    public void Turning_it_off_removes_the_entry_and_windows_record_of_it()
    {
        var store = new FakeRunKeyStore();
        var login = new RunKeyLoginItem(store, Exe);
        login.SetEnabled(true);
        store.Approved["DevSweep"] = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        login.SetEnabled(false);

        Assert.Equal(LoginState.Off, login.State);
        Assert.Empty(store.Run);
        Assert.Empty(store.Approved);
    }

    [Theory]
    [InlineData(0x02, LoginState.On)]
    [InlineData(0x06, LoginState.On)]
    [InlineData(0x03, LoginState.DisabledInWindows)]
    [InlineData(0x07, LoginState.DisabledInWindows)]
    public void Windows_can_turn_the_entry_off_in_Startup_apps_and_DevSweep_says_so(int first, LoginState expected)
    {
        var store = new FakeRunKeyStore();
        var login = new RunKeyLoginItem(store, Exe);
        login.SetEnabled(true);
        store.Approved["DevSweep"] = [(byte)first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(expected, login.State);
    }

    [Fact]
    public void Switching_it_on_again_clears_a_windows_off_switch_the_user_set_earlier()
    {
        var store = new FakeRunKeyStore();
        var login = new RunKeyLoginItem(store, Exe);
        store.Run["DevSweep"] = "old";
        store.Approved["DevSweep"] = [0x03, 0, 0, 0];

        login.SetEnabled(true);

        Assert.Equal(LoginState.On, login.State);
    }

    [Fact]
    public void A_moved_install_is_repaired_but_repair_never_turns_it_on()
    {
        var store = new FakeRunKeyStore();
        var login = new RunKeyLoginItem(store, Exe);
        Assert.False(login.RepairIfMoved());
        Assert.Empty(store.Run);

        store.Run["DevSweep"] = "\"D:\\old\\DevSweep.exe\" --background";
        Assert.True(login.RepairIfMoved());
        Assert.Equal(login.Command, store.Run["DevSweep"]);
        Assert.False(login.RepairIfMoved());
    }

    [Fact]
    public void A_refusal_from_windows_is_a_readable_error_and_changes_nothing()
    {
        var store = new FakeRunKeyStore { Throw = true };
        var login = new RunKeyLoginItem(store, Exe);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => login.SetEnabled(true));

        Assert.Contains("Group Policy blocks it", error.Message, StringComparison.Ordinal);
        Assert.Equal(LoginState.Off, login.State);
    }

    [Fact]
    public void The_entry_lives_in_the_current_users_hive_only()
    {
        // Per user, never machine wide: no administrator rights, no UAC prompt.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryRunKeyStore.DefaultRunPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", RegistryRunKeyStore.DefaultApprovedPath);
        Assert.Equal("--background", RunKeyLoginItem.BackgroundArgument);
    }

    [Fact]
    public void The_real_registry_store_round_trips_under_HKCU()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = @"Software\DevSweepTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new RegistryRunKeyStore(root + @"\Run", root + @"\Approved");
            var login = new RunKeyLoginItem(store, Exe);
            Assert.Equal(LoginState.Off, login.State);

            login.SetEnabled(true);
            Assert.Equal(LoginState.On, login.State);
            Assert.Equal(login.Command, store.GetRun("DevSweep"));
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(root + @"\Run")!)
            {
                Assert.Equal(RegistryValueKind.String, key.GetValueKind("DevSweep"));
            }

            using (RegistryKey approved = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
            {
                approved.SetValue("DevSweep", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }

            Assert.Equal(LoginState.DisabledInWindows, login.State);

            login.SetEnabled(false);
            Assert.Equal(LoginState.Off, login.State);
            Assert.Null(store.GetRun("DevSweep"));
            Assert.Null(store.GetApproved("DevSweep"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\DevSweepTests", throwOnMissingSubKey: false);
        }
    }
}

public sealed class BackgroundSettingsTests
{
    private static MainViewModel Create(out AppSettingsStore settings, ILoginItem? login = null, IToastNotifier? toasts = null)
    {
        settings = new AppSettingsStore(null);
        settings.Update(s => s with { WelcomeDone = true });
        var integration = new SystemIntegration(login ?? new MemoryLoginItem(), toasts ?? new NoToasts(), Path.Join(Path.GetTempPath(), "DevSweep-test-alerts-" + Guid.NewGuid().ToString("N") + ".json"));
        return new MainViewModel(new SampleBackend(), settings, integration);
    }

    [Fact]
    public void Everything_is_off_by_default()
    {
        MainViewModel model = Create(out AppSettingsStore settings);
        SettingsViewModel vm = model.SettingsPage;

        Assert.False(vm.OpenAtLogin);
        Assert.False(model.Background.KeepInTray);
        Assert.False(vm.ScheduleEnabled);
        Assert.False(settings.Current.Schedule.Enabled);
        Assert.Equal("Scheduled scans are off.", vm.ScheduleSummary);
        Assert.Null(vm.NextScanText);
        // Alerts have the Mac defaults.
        Assert.Equal((true, 85, true, 2, true, true, 0), (vm.DiskAlert, (int)vm.DiskPercent, vm.GrowthAlert, (int)vm.GrowthGB, vm.EndOfLifeAlert, vm.SecretsAlert, vm.ReminderDays));
    }

    [Fact]
    public void Open_at_login_follows_the_switch_and_closing_keeps_the_tray_only_while_it_is_on()
    {
        var login = new MemoryLoginItem();
        MainViewModel model = Create(out _, login);

        model.SettingsPage.OpenAtLogin = true;
        Assert.Equal(LoginState.On, login.State);
        Assert.True(model.Background.KeepInTray);

        model.SettingsPage.OpenAtLogin = false;
        Assert.Equal(LoginState.Off, login.State);
        Assert.False(model.Background.KeepInTray);
    }

    [Fact]
    public void The_switch_shows_what_windows_has_not_what_it_was_asked_when_windows_refuses()
    {
        var store = new FakeRunKeyStore { Throw = true };
        MainViewModel model = Create(out _, new RunKeyLoginItem(store, @"C:\x\DevSweep.exe"));

        model.SettingsPage.OpenAtLogin = true;

        Assert.False(model.SettingsPage.OpenAtLogin);
        Assert.Equal(LoginState.Off, model.SettingsPage.LoginState);
        Assert.Contains("Group Policy", model.SettingsPage.LoginMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_that_windows_turned_off_is_shown_as_such()
    {
        var store = new FakeRunKeyStore();
        var item = new RunKeyLoginItem(store, @"C:\x\DevSweep.exe");
        item.SetEnabled(true);
        store.Approved["DevSweep"] = [3, 0, 0, 0];

        MainViewModel model = Create(out _, item);

        Assert.Equal(LoginState.DisabledInWindows, model.SettingsPage.LoginState);
        Assert.Contains("Startup", model.SettingsPage.LoginText, StringComparison.Ordinal);
        Assert.Equal("ms-settings:startupapps", SettingsViewModel.StartupSettingsUri.OriginalString);
    }

    [Fact]
    public void The_schedule_is_saved_counts_from_the_change_and_round_trips_through_the_file()
    {
        string file = Path.Join(Path.GetTempPath(), "DevSweep-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettingsStore(file);
            var vm = new SettingsViewModel(settings, new SampleBackend());

            vm.ScheduleEnabled = true;
            vm.ScheduleFrequency = ScanFrequency.Weekly;
            vm.ScheduleDay = DayOfWeek.Friday;
            vm.ScheduleTime = new TimeSpan(7, 30, 0);

            ScanSchedule saved = new AppSettingsStore(file).Current.Schedule;
            Assert.True(saved.Enabled);
            Assert.Equal(ScanFrequency.Weekly, saved.Frequency);
            Assert.Equal(DayOfWeek.Friday, saved.Day);
            Assert.Equal((7, 30), (saved.Hour, saved.Minute));
            Assert.Equal(SampleData.Now, saved.Since);
            Assert.Equal("Every Friday at 07:30", vm.ScheduleSummary);
            Assert.NotNull(vm.NextScanText);
            Assert.Contains("Friday", vm.NextScanText, StringComparison.Ordinal);
            Assert.Contains("07:30", vm.NextScanText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(50, 70)]
    [InlineData(70, 70)]
    [InlineData(82.4, 82)]
    [InlineData(95, 95)]
    [InlineData(120, 95)]
    [InlineData(double.NaN, 85)]
    public void The_disk_alert_keeps_to_70_to_95_percent(double typed, int saved)
    {
        MainViewModel model = Create(out AppSettingsStore settings);
        model.SettingsPage.DiskPercent = typed;
        Assert.Equal(saved, settings.Current.Alerts.DiskPercent);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4.6, 5)]
    [InlineData(10, 10)]
    [InlineData(50, 10)]
    public void The_growth_alert_keeps_to_1_to_10_GB(double typed, int saved)
    {
        MainViewModel model = Create(out AppSettingsStore settings);
        model.SettingsPage.GrowthGB = typed;
        Assert.Equal(saved, settings.Current.Alerts.GrowthGB);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-4, 0)]
    [InlineData(1, 3)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(7, 7)]
    [InlineData(30, 7)]
    public void Reminders_come_after_3_to_7_days_or_the_default(int chosen, int saved)
    {
        MainViewModel model = Create(out AppSettingsStore settings);
        model.SettingsPage.ReminderDays = chosen;
        Assert.Equal(saved, settings.Current.Alerts.ReminderDays);
    }

    [Fact]
    public void Each_alert_switches_on_its_own()
    {
        MainViewModel model = Create(out AppSettingsStore settings);
        SettingsViewModel vm = model.SettingsPage;

        vm.DiskAlert = false;
        Assert.Equal((false, true, true, true), Flags(settings.Current.Alerts));
        vm.GrowthAlert = false;
        Assert.Equal((false, false, true, true), Flags(settings.Current.Alerts));
        vm.EndOfLifeAlert = false;
        Assert.Equal((false, false, false, true), Flags(settings.Current.Alerts));
        vm.SecretsAlert = false;
        Assert.Equal((false, false, false, false), Flags(settings.Current.Alerts));

        static (bool, bool, bool, bool) Flags(AlertSettings a) => (a.DiskEnabled, a.GrowthEnabled, a.EndOfLifeEnabled, a.SecretsEnabled);
    }

    [Fact]
    public void Saved_settings_come_back_into_the_page()
    {
        string file = Path.Join(Path.GetTempPath(), "DevSweep-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new AppSettingsStore(file);
            first.Update(s => s with
            {
                Schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 18, Minute = 15 },
                Alerts = new AlertSettings { DiskPercent = 90, GrowthGB = 5, ReminderDays = 4, SecretsEnabled = false },
            });

            var vm = new SettingsViewModel(new AppSettingsStore(file), new SampleBackend());

            Assert.True(vm.ScheduleEnabled);
            Assert.Equal(new TimeSpan(18, 15, 0), vm.ScheduleTime);
            Assert.Equal(90, vm.DiskPercent);
            Assert.Equal(5, vm.GrowthGB);
            Assert.Equal(4, vm.ReminderDays);
            Assert.False(vm.SecretsAlert);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_notification_line_says_when_windows_will_not_show_toasts()
    {
        Assert.Contains("will show", Create(out _, toasts: new RecordingToasts()).SettingsPage.NotificationsText, StringComparison.Ordinal);
        Assert.Contains("not available", Create(out _).SettingsPage.NotificationsText, StringComparison.Ordinal);
        Assert.Contains("turned off", Create(out _, toasts: new DisabledToasts()).SettingsPage.NotificationsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_now_reports_through_settings()
    {
        MainViewModel model = Create(out _);

        await model.SettingsPage.CheckNowAsync();

        Assert.Equal("Version 0.2.0 is available.", model.SettingsPage.UpdateStatus);   // the sample checker
        Assert.Equal("0.2.0", model.SettingsPage.AvailableUpdate?.Version);
        Assert.True(model.Background.Updates.ShowCard);
    }

    private sealed class DisabledToasts : IToastNotifier
    {
        public ToastAvailability Availability => ToastAvailability.DisabledByUser;

        public bool Show(ToastMessage message) => false;
    }
}
