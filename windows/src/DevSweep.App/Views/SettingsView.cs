using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevSweep.App.Views;

/// <summary>
/// Settings, in five tabs: General (appearance, project folders, ignored items, scanning), Modules,
/// Background (open at login and the tray, scheduled scans), Alerts, and Updates and about.
/// </summary>
internal static class SettingsView
{
    private static readonly string[] Tabs = ["General", "Modules", "Open at login and schedule", "Alerts", "Updates and about"];

    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        SettingsViewModel vm = model.SettingsPage;
        var tabs = new SelectorBar();
        AutomationProperties.SetName(tabs, "Settings pages");
        foreach (SettingsTab tab in Enum.GetValues<SettingsTab>())
        {
            var item = new SelectorBarItem { Text = Tabs[(int)tab], Tag = tab, IsSelected = tab == vm.Tab };
            AutomationProperties.SetName(item, Tabs[(int)tab]);
            tabs.Items.Add(item);
        }

        var body = new StackPanel { Spacing = 22 };
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem?.Tag is SettingsTab tab)
            {
                vm.Tab = tab;
            }
        };
        var page = new StackPanel { Spacing = 16, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ui.PageHeader("Settings", Ui.Secondary("Nothing is ever cleaned automatically.")));
        page.Children.Add(tabs);
        page.Children.Add(body);

        // Each tab's bindings live only as long as the tab is shown.
        var tabSubs = new Subscriptions();
        subs.Add(() => tabSubs.Dispose());
        subs.On(vm, nameof(SettingsViewModel.Tab), () =>
        {
            foreach (SelectorBarItem item in tabs.Items)
            {
                item.IsSelected = item.Tag is SettingsTab t && t == vm.Tab;
            }

            tabSubs.Dispose();
            body.Children.Clear();
            switch (vm.Tab)
            {
                case SettingsTab.General:
                    General(vm, body, tabSubs, window);
                    break;
                case SettingsTab.Modules:
                    Modules(vm, body, tabSubs);
                    break;
                case SettingsTab.Background:
                    Background(vm, body, tabSubs);
                    break;
                case SettingsTab.Alerts:
                    Alerts(vm, body, tabSubs);
                    break;
                default:
                    Updates(vm, body, tabSubs);
                    break;
            }
        });
        return Ui.Scroll(page);
    }

    private static void General(SettingsViewModel vm, StackPanel page, Subscriptions subs, MainWindow window)
    {
        var appearance = new ComboBox { Width = 220 };
        foreach (string name in (string[])["Use Windows setting", "Light", "Dark"])
        {
            appearance.Items.Add(name);
        }

        appearance.SelectedIndex = (int)vm.Appearance;
        subs.On(vm, nameof(SettingsViewModel.Appearance), () => appearance.SelectedIndex = (int)vm.Appearance); // the title bar's theme button drives the same value
        appearance.SelectionChanged += (_, _) => vm.Appearance = (AppearanceMode)Math.Max(0, appearance.SelectedIndex);
        AutomationProperties.SetName(appearance, "Appearance");
        page.Children.Add(Section("Appearance", Ui.Row(Ui.Secondary("Light or dark; by default DevSweep follows Windows. The sun and moon button in the title bar changes the same setting."), appearance)));

        var folders = new StackPanel { Spacing = 6 };
        var path = new TextBox { PlaceholderText = @"D:\src", Width = 320 };
        AutomationProperties.SetName(path, "Project folder to add");
        Button add = Ui.Button("Add", () =>
        {
            if (path.Text.Trim() is { Length: > 0 } text && Path.IsPathFullyQualified(text))
            {
                vm.AddFolder(text);
                path.Text = string.Empty;
            }
        }, "Add the typed project folder");
        Button browse = Ui.Button("Browse…", async () =>
        {
            if (await window.PickFolderAsync() is { } picked)
            {
                vm.AddFolder(picked);
            }
        }, "Choose a project folder");
        subs.OnCollection(vm.ProjectFolders, () =>
        {
            folders.Children.Clear();
            if (vm.ProjectFolders.Count == 0)
            {
                folders.Children.Add(Ui.Secondary("Using the rules' own folders (your profile's usual project folders, such as source, src, Projects and repos)."));
            }

            foreach (string folder in vm.ProjectFolders)
            {
                folders.Children.Add(Ui.Row(Ui.Text(folder), Ui.Button("Remove", () => vm.RemoveFolder(folder), $"Remove {folder}")));
            }
        });
        page.Children.Add(Section("Project folders", Ui.V(10,
            Ui.Secondary("DevSweep looks for idle projects here. Only rebuildable folders (node_modules, .venv, bin and obj, target, .gradle) are suggested, and only in projects untouched for a while. Folders outside your profile are cleaned only because you list them here."),
            folders,
            Ui.H(8, path, add, browse, Ui.Button("Restore defaults", vm.RestoreDefaultFolders)))));

        var ignored = new StackPanel { Spacing = 6 };
        subs.OnCollection(vm.Ignored, () =>
        {
            ignored.Children.Clear();
            if (vm.Ignored.Count == 0)
            {
                ignored.Children.Add(Ui.Secondary("Nothing ignored. Items you choose to always ignore show up here, so you can bring them back."));
            }

            foreach (IgnoredItem item in vm.Ignored)
            {
                ignored.Children.Add(Ui.Row(Ui.Text(item.Title), Ui.Button("Stop ignoring", () => vm.StopIgnoring(item), $"Stop ignoring {item.Title}")));
            }
        });
        page.Children.Add(Section("Ignored items", Ui.V(8, Ui.Secondary("These are left out of every scan."), ignored)));

        var exact = new ToggleSwitch { IsOn = vm.ExactScan, OnContent = "Exact sizes", OffContent = "Overview sizes" };
        AutomationProperties.SetName(exact, "Measure exact sizes during the scan");
        exact.Toggled += (_, _) => vm.ExactScan = exact.IsOn;
        var timeout = new NumberBox { Value = vm.CheckTimeoutSeconds, Minimum = 5, Maximum = 600, SmallChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
        AutomationProperties.SetName(timeout, "Seconds before a slow check is skipped");
        timeout.ValueChanged += (_, _) => vm.CheckTimeoutSeconds = timeout.Value;
        page.Children.Add(Section("Scanning", Ui.V(10,
            Ui.Row(Ui.V(2, Ui.Text("Sizes during the scan"), Ui.Caption("Overview skips alternate data streams (faster, no handle per file). Review always measures exactly before anything changes.")), exact),
            Ui.Row(Ui.V(2, Ui.Text("Skip a check after (seconds)"), Ui.Caption("A slow check is skipped and the scan report says so.")), timeout))));
    }

    private static void Modules(SettingsViewModel vm, StackPanel page, Subscriptions subs)
    {
        var modules = new StackPanel { Spacing = 6 };
        foreach (ModuleToggle module in vm.Modules)
        {
            var toggle = new ToggleSwitch { IsOn = module.IsOn, IsEnabled = module.CanChange, OnContent = "On", OffContent = "Off" };
            AutomationProperties.SetName(toggle, module.Title);
            toggle.Toggled += (_, _) => module.IsOn = toggle.IsOn;
            modules.Children.Add(Ui.Row(Ui.V(0, Ui.Text(module.Title), module.Note.Length > 0 ? Ui.Caption(module.Note) : null), toggle));
        }

        TextBlock restart = Ui.Text("Changes to modules apply the next time DevSweep starts.", "DsWarningText");
        subs.On(vm, nameof(SettingsViewModel.RestartNeeded), () => Ui.Show(restart, vm.RestartNeeded));
        page.Children.Add(Section("Modules", Ui.V(8,
            Ui.Secondary("Every place (Clean up, Projects, Health) is a module and all of them are on by default. Switch one off and it is left out of scans and its place is hidden from the navigation. Saved in features.json; DEVSWEEP_FEATURES overrides it."),
            modules, restart)));
    }

    private static void Background(SettingsViewModel vm, StackPanel page, Subscriptions subs)
    {
        var login = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(login, "Open DevSweep at login and keep it in the tray");
        login.Toggled += (_, _) => vm.OpenAtLogin = login.IsOn;
        TextBlock loginText = Ui.Caption(string.Empty);
        loginText.TextWrapping = TextWrapping.Wrap;
        TextBlock loginError = Ui.Text(string.Empty, "DsCriticalText");
        Button startupApps = Ui.Button("Open Startup apps", () => _ = Windows.System.Launcher.LaunchUriAsync(SettingsViewModel.StartupSettingsUri), "Open Windows Settings, Apps, Startup");
        subs.On(vm, null, () =>
        {
            login.IsOn = vm.OpenAtLogin;
            loginText.Text = vm.LoginText;
            loginError.Text = vm.LoginMessage ?? string.Empty;
            Ui.Show(loginError, vm.LoginMessage is not null);
            Ui.Show(startupApps, vm.LoginState == LoginState.DisabledInWindows);
        });
        page.Children.Add(Section("Open at login", Ui.V(10,
            Ui.Row(Ui.V(2, Ui.Text("Open DevSweep at login, in the tray"), loginText), login),
            loginError,
            startupApps,
            Ui.Caption("It adds one entry for you only (Settings, Apps, Startup shows it, and you can switch it off there too). It never needs administrator rights. While DevSweep runs, its icon is in the notification area; Windows may keep it behind the ^ until you drag it out."))));

        var enabled = new ToggleSwitch { OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(enabled, "Scan on a schedule");
        enabled.Toggled += (_, _) => vm.ScheduleEnabled = enabled.IsOn;
        var frequency = new ComboBox { Width = 160 };
        frequency.Items.Add("Every day");
        frequency.Items.Add("Every week");
        AutomationProperties.SetName(frequency, "How often to scan");
        frequency.SelectionChanged += (_, _) => vm.ScheduleFrequency = frequency.SelectedIndex == 0 ? ScanFrequency.Daily : ScanFrequency.Weekly;
        var day = new ComboBox { Width = 160 };
        DayOfWeek[] days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        foreach (DayOfWeek d in days)
        {
            day.Items.Add(d.ToString());
        }

        AutomationProperties.SetName(day, "Day of the week");
        day.SelectionChanged += (_, _) =>
        {
            if (day.SelectedIndex >= 0)
            {
                vm.ScheduleDay = days[day.SelectedIndex];
            }
        };
        var time = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
        AutomationProperties.SetName(time, "Time of day");
        time.TimeChanged += (_, e) => vm.ScheduleTime = e.NewTime;
        TextBlock summary = Ui.Text(string.Empty);
        TextBlock next = Ui.Caption(string.Empty);
        StackPanel options = Ui.V(10, Ui.H(10, frequency, day, time), summary, next);
        subs.On(vm, null, () =>
        {
            enabled.IsOn = vm.ScheduleEnabled;
            frequency.SelectedIndex = vm.ScheduleFrequency == ScanFrequency.Daily ? 0 : 1;
            day.SelectedIndex = Array.IndexOf(days, vm.ScheduleDay);
            Ui.Show(day, vm.ScheduleFrequency == ScanFrequency.Weekly);
            time.Time = vm.ScheduleTime;
            summary.Text = vm.ScheduleSummary;
            next.Text = vm.NextScanText ?? string.Empty;
            Ui.Show(options, vm.ScheduleEnabled);
        });
        page.Children.Add(Section("Scheduled scans", Ui.V(10,
            Ui.Row(Ui.V(2, Ui.Text("Scan on a schedule"), Ui.Caption("Off by default.")), enabled),
            options,
            Ui.Caption("A scheduled scan is read-only, like every scan: nothing is cleaned, moved or deleted until you review and confirm. It runs only while DevSweep is running (in the tray when it opens at login). A PC that was asleep or off at the scheduled time scans when it wakes or when DevSweep starts."))));
    }

    private static void Alerts(SettingsViewModel vm, StackPanel page, Subscriptions subs)
    {
        var disk = new ToggleSwitch { IsOn = vm.DiskAlert, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(disk, "Alert when the disk is nearly full");
        disk.Toggled += (_, _) => vm.DiskAlert = disk.IsOn;
        var diskPercent = new NumberBox
        {
            Value = vm.DiskPercent, Minimum = SettingsViewModel.DiskPercentMin, Maximum = SettingsViewModel.DiskPercentMax, SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 130,
        };
        AutomationProperties.SetName(diskPercent, "Percent full that raises the disk alert");
        diskPercent.ValueChanged += (_, _) => vm.DiskPercent = diskPercent.Value;

        var growth = new ToggleSwitch { IsOn = vm.GrowthAlert, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(growth, "Alert when something grew a lot");
        growth.Toggled += (_, _) => vm.GrowthAlert = growth.IsOn;
        var growthGB = new NumberBox
        {
            Value = vm.GrowthGB, Minimum = SettingsViewModel.GrowthGBMin, Maximum = SettingsViewModel.GrowthGBMax, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 130,
        };
        AutomationProperties.SetName(growthGB, "Gigabytes of growth in a week that raise the growth alert");
        growthGB.ValueChanged += (_, _) => vm.GrowthGB = growthGB.Value;

        var eol = new ToggleSwitch { IsOn = vm.EndOfLifeAlert, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(eol, "Alert when a tool I use reaches end of life");
        eol.Toggled += (_, _) => vm.EndOfLifeAlert = eol.IsOn;
        var secrets = new ToggleSwitch { IsOn = vm.SecretsAlert, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(secrets, "Alert when a new file looks like a secret");
        secrets.Toggled += (_, _) => vm.SecretsAlert = secrets.IsOn;

        var reminder = new ComboBox { Width = 330 };
        reminder.Items.Add("Default (3 days for a full disk, 7 days for the rest)");
        for (int d = SettingsViewModel.ReminderDaysMin; d <= SettingsViewModel.ReminderDaysMax; d++)
        {
            reminder.Items.Add($"{d} days");
        }

        reminder.SelectedIndex = vm.ReminderDays == 0 ? 0 : vm.ReminderDays - SettingsViewModel.ReminderDaysMin + 1;
        AutomationProperties.SetName(reminder, "Remind me again after");
        reminder.SelectionChanged += (_, _) =>
            vm.ReminderDays = reminder.SelectedIndex <= 0 ? 0 : reminder.SelectedIndex - 1 + SettingsViewModel.ReminderDaysMin;

        page.Children.Add(Section("Alert me when", Ui.V(12,
            Ui.Row(Ui.V(2, Ui.Text("The disk is nearly full"), Ui.Caption("Percent full, 70 to 95. About the drive Windows is on.")), diskPercent, disk),
            Ui.Row(Ui.V(2, Ui.Text("Something grew a lot in a week"), Ui.Caption("Gigabytes, 1 to 10, in Clean up's items since last week.")), growthGB, growth),
            Ui.Row(Ui.V(2, Ui.Text("A tool I use reaches end of life"), Ui.Caption("Needs Health. Uses endoflife.date's public support dates.")), eol),
            Ui.Row(Ui.V(2, Ui.Text("A new file that looks like a secret appears"), Ui.Caption("Needs Health. Only files that appear after the first scheduled check.")), secrets))));

        TextBlock healthNote = Ui.Text(vm.HealthAlertsNote ?? string.Empty, "DsWarningText");
        Ui.Show(healthNote, vm.HealthAlertsNote is not null);
        TextBlock notifications = Ui.Caption(string.Empty);
        notifications.TextWrapping = TextWrapping.Wrap;
        TextBlock testResult = Ui.Caption(string.Empty);
        subs.On(vm, null, () =>
        {
            notifications.Text = vm.NotificationsText;
            testResult.Text = vm.TestToastText ?? string.Empty;
        });
        page.Children.Add(Section("How you are told", Ui.V(10,
            Ui.Row(Ui.V(2, Ui.Text("Remind me again after"), Ui.Caption("If a problem is still there. Each problem is announced once, several at once become one notification, and a click opens the right screen.")), reminder),
            healthNote,
            notifications,
            Ui.H(10, Ui.Button("Send a test notification", vm.SendTestToast), testResult),
            Ui.Caption("Alerts are checked after each scheduled scan (turn those on under Open at login and schedule). They also list in the tray menu."))));
    }

    private static void Updates(SettingsViewModel vm, StackPanel page, Subscriptions subs)
    {
        var updates = new ToggleSwitch { IsOn = vm.CheckForUpdates, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(updates, "Check for updates automatically");
        updates.Toggled += (_, _) => vm.CheckForUpdates = updates.IsOn;
        TextBlock status = Ui.Caption(string.Empty);
        status.TextWrapping = TextWrapping.Wrap;
        var checking = new ProgressRing { IsActive = false, Width = 18, Height = 18, Visibility = Visibility.Collapsed };
        Button checkNow = Ui.Button("Check now", () => _ = vm.CheckNowAsync());
        Button release = Ui.Button("See what's new", () => vm.OpenUpdateRelease(), "Open the release page for the new version", accent: true);
        subs.On(vm, null, () =>
        {
            status.Text = vm.UpdateStatus ?? string.Empty;
            checking.IsActive = vm.IsCheckingUpdate;
            Ui.Show(checking, vm.IsCheckingUpdate);
            checkNow.IsEnabled = !vm.IsCheckingUpdate;
            Ui.Show(release, vm.AvailableUpdate is not null);
        });
        page.Children.Add(Section("Updates", Ui.V(8,
            Ui.Row(Ui.V(2, Ui.Text("Check for updates automatically"), Ui.Caption("About once a day, DevSweep asks GitHub for the latest Windows release (versions tagged windows-v). It sends nothing about your PC, and never downloads or installs anything by itself: the card in the sidebar opens the release page.")), updates),
            Ui.H(10, checkNow, checking, release),
            status)));

        page.Children.Add(Section("About", Ui.V(6,
            Ui.Text($"DevSweep for Windows {SettingsViewModel.Version}"),
            Ui.Secondary(SettingsViewModel.Privacy),
            Ui.Caption($"Settings, History and the quarantine live in {vm.DataFolder}."),
            Ui.Caption("Unsigned: Windows SmartScreen may warn the first time (More info, then Run anyway)."))));
    }

    private static UIElement Section(string title, UIElement content) => Ui.V(8, Ui.Text(title, "DsStrong"), Ui.Card(content));
}
