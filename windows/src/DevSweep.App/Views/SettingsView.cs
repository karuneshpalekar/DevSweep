using DevSweep.App.Design;
using DevSweep.App.Toolkit;
using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DevSweep.App.Views;

/// <summary>
/// Settings as the Mac has it, one scrolling page of sections (Appearance, Updates, Open at login, Scheduled scans, Alerts, GitHub accounts,
/// Downloads, Project folders, Default editor, Ignored items, Scanning, Permissions and privacy, Troubleshooting, Modules, About), with a row
/// of chips that jump to a group in one click and a "Search settings" box that shows only the sections that match.
/// </summary>
internal static class SettingsView
{
    private static readonly string[] AppearanceNames = ["Use Windows setting", "Light", "Dark"];

    public static UIElement Build(MainViewModel model, Subscriptions subs, MainWindow window)
    {
        SettingsViewModel vm = model.SettingsPage;
        vm.RefreshFolders();
        var sections = new Dictionary<string, FrameworkElement>();
        var body = new StackPanel { Spacing = Tokens.Space6 };
        void Add(string id, FrameworkElement section)
        {
            sections[id] = section;
            body.Children.Add(section);
        }

        Add("appearance", Appearance(vm, subs));
        Add("updates", Updates(vm, subs));
        Add("login", OpenAtLogin(vm, subs));
        Add("schedule", Schedule(vm, subs));
        Add("alerts", Alerts(vm, subs));
        Add("accounts", SettingsParts.Accounts(model, subs));
        if (model.Projects.Source is { } source)
        {
            Add("downloads", SettingsParts.Downloads(model, source, window, subs));
        }

        Add("folders", SettingsParts.Folders(model, vm, subs, window));
        if (model.Projects.Source is { } editors)
        {
            Add("editor", SettingsParts.Editor(editors));
        }

        Add("ignored", SettingsParts.Ignored(vm, subs));
        Add("scanning", Scanning(vm));
        Add("permissions", SettingsParts.Permissions(vm, subs));
        Add("troubleshooting", SettingsParts.Troubleshooting(vm, subs));
        Add("modules", Modules(vm, subs));
        Add("about", About(vm));

        var search = new TextBox { PlaceholderText = "Search settings", MinWidth = 240, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(search, "Search settings");
        search.TextChanged += (_, _) => vm.Search = search.Text;

        void Jump(string id)
        {
            if (sections.TryGetValue(id, out FrameworkElement? target) && target.Visibility == Visibility.Visible)
            {
                target.UpdateLayout();
                target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = Motion.Enabled });
            }
        }

        var chips = new List<UIElement>();
        foreach ((string id, string title) in SettingsSections.Groups)
        {
            ToggleButton? self = null;
            string first = SettingsSections.All.First(s => s.Group == id).Id;
            self = Ds.Chip(title, null, false, "Go to " + title, () =>
            {
                self!.IsChecked = false;
                Jump(first);
            });
            chips.Add(self);
        }

        UIElement chipRow = FlowButtons.Build(chips.ToArray());
        TextBlock none = Ui.Secondary(string.Empty);
        none.TextWrapping = TextWrapping.Wrap;
        subs.On(vm, nameof(SettingsViewModel.Search), () =>
        {
            if (search.Text != vm.Search)
            {
                search.Text = vm.Search;
            }

            foreach ((string id, FrameworkElement section) in sections)
            {
                Ui.Show(section, vm.Shows(id));
            }

            bool searching = vm.Search.Trim().Length > 0;
            Ui.Show(chipRow, !searching);
            Ui.Show(none, searching && vm.VisibleSections.Count == 0);
            none.Text = $"No settings match \"{vm.Search.Trim()}\". Try a word like theme, schedule, folder, account or notification.";
        });

        var page = new StackPanel { Spacing = Tokens.Space4, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ds.PageHeader("Settings", Ui.Secondary("Nothing is ever cleaned automatically.")));
        page.Children.Add(Ds.CommandBar(chipRow, search));
        page.Children.Add(none);
        page.Children.Add(body);

        void TryJump()
        {
            if (vm.JumpTo is { } id)
            {
                vm.JumpTo = null;
                Jump(id);
            }
        }

        page.Loaded += (_, _) => page.DispatcherQueue.TryEnqueue(TryJump);
        subs.On(vm, nameof(SettingsViewModel.JumpTo), () =>
        {
            if (page.IsLoaded)
            {
                TryJump();
            }
        });
        return Ds.PageScroll(page);
    }

    // ---- Building blocks ------------------------------------------------------------------------------------------------------------

    internal static TextBlock Note(string text)
    {
        TextBlock note = Ui.Caption(text);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    /// <summary>One line of a section's card: the title and a wrapped caption on the left, the controls at the end.</summary>
    internal static Border Setting(string title, string? caption, params UIElement?[] controls)
    {
        TextBlock heading = Ui.Text(title);
        heading.TextWrapping = TextWrapping.Wrap;
        Grid row = Ui.Row(Ui.V(2, heading, caption is null ? null : Note(caption)), controls);
        return new Border { Padding = new Thickness(Tokens.Space3), Child = row };
    }

    /// <summary>The section: its title, a card of rows, and the Mac's footer sentence under it.</summary>
    internal static StackPanel Section(string title, UIElement card, string? footer = null)
    {
        StackPanel section = Ui.V(Tokens.Space2, Ds.SectionTitle(title), card, footer is null ? null : Note(footer));
        AutomationProperties.SetName(section, "Settings section: " + title);
        return section;
    }

    internal static StackPanel Section(string title, UIElement[] rows, string? footer = null) => Section(title, Ds.RowsCard(rows), footer);

    private static ToggleSwitch Switch(string name, bool on, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    // ---- Sections -------------------------------------------------------------------------------------------------------------------

    private static StackPanel Appearance(SettingsViewModel vm, Subscriptions subs)
    {
        var host = new Border { HorizontalAlignment = HorizontalAlignment.Right };
        int shown = -1;
        subs.On(vm, nameof(SettingsViewModel.Appearance), () =>
        {
            // The title bar's theme button drives the same value; rebuild only when the choice is not already what is on screen.
            if (shown == (int)vm.Appearance)
            {
                return;
            }

            shown = (int)vm.Appearance;
            host.Child = Ds.Segmented(AppearanceNames, shown, i =>
            {
                shown = i;
                vm.Appearance = (AppearanceMode)i;
            }, "Appearance");
        });
        return Section("Appearance", [Setting("Appearance", "By default DevSweep follows Windows. The sun and moon button in the title bar changes the same setting.", host)]);
    }

    private static StackPanel Updates(SettingsViewModel vm, Subscriptions subs)
    {
        ToggleSwitch auto = Switch("Check for updates automatically", vm.CheckForUpdates, on => vm.CheckForUpdates = on);
        TextBlock status = Note(string.Empty);
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
        return Section(
            "Updates",
            [
                Setting("Installed", null, Ui.Secondary("DevSweep " + SettingsViewModel.Version)),
                Setting("Check for updates automatically", null, auto),
                Setting("Check now", null, status, checking, checkNow, release),
            ],
            "Asks GitHub for the latest Windows release (versions tagged windows-v), about once a day. Nothing about your PC is sent, and nothing is downloaded or installed by itself: the card in the sidebar opens the release page.");
    }

    private static StackPanel OpenAtLogin(SettingsViewModel vm, Subscriptions subs)
    {
        ToggleSwitch login = Switch("Open DevSweep at login and keep it in the tray", vm.OpenAtLogin, on => vm.OpenAtLogin = on);
        TextBlock loginText = Note(string.Empty);
        TextBlock loginError = Ui.Text(string.Empty, "DsCriticalText");
        loginError.TextWrapping = TextWrapping.Wrap;
        Button startupApps = Ui.Button("Open Startup apps", () => _ = Windows.System.Launcher.LaunchUriAsync(SettingsViewModel.StartupSettingsUri), "Open Windows Settings, Apps, Startup");
        subs.On(vm, null, () =>
        {
            login.IsOn = vm.OpenAtLogin;
            loginText.Text = vm.LoginText;
            loginError.Text = vm.LoginMessage ?? string.Empty;
            Ui.Show(loginError, vm.LoginMessage is not null);
            Ui.Show(startupApps, vm.LoginState == LoginState.DisabledInWindows);
        });
        return Section(
            "Open at login",
            [
                Setting("Open DevSweep at login, in the tray", null, login),
                new Border { Padding = new Thickness(Tokens.Space3), Child = Ui.V(Tokens.Space2, loginText, loginError, startupApps) },
            ],
            "Scheduled scans and alerts only run while DevSweep is open. Opening it at login keeps it in the tray. It adds one entry for you only (Settings, Apps, Startup shows it) and never needs administrator rights; Windows may keep the tray icon behind the ^ until you drag it out.");
    }

    private static StackPanel Schedule(SettingsViewModel vm, Subscriptions subs)
    {
        ToggleSwitch enabled = Switch("Scan on a schedule", vm.ScheduleEnabled, on => vm.ScheduleEnabled = on);
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
        TextBlock next = Ui.Secondary(string.Empty);
        Border repeatRow = Setting("Repeat", null, frequency);
        Border dayRow = Setting("Day", null, day);
        Border timeRow = Setting("Time", null, time);
        Border nextRow = Setting("Next scan", null, next);
        subs.On(vm, null, () =>
        {
            enabled.IsOn = vm.ScheduleEnabled;
            frequency.SelectedIndex = vm.ScheduleFrequency == ScanFrequency.Daily ? 0 : 1;
            day.SelectedIndex = Array.IndexOf(days, vm.ScheduleDay);
            time.Time = vm.ScheduleTime;
            next.Text = vm.NextScanText ?? string.Empty;
            Ui.Show(repeatRow, vm.ScheduleEnabled);
            Ui.Show(dayRow, vm.ScheduleEnabled && vm.ScheduleFrequency == ScanFrequency.Weekly);
            Ui.Show(timeRow, vm.ScheduleEnabled);
            Ui.Show(nextRow, vm.ScheduleEnabled);
        });
        return Section(
            "Scheduled scans",
            [Setting("Scan on a schedule", "Off by default.", enabled), repeatRow, dayRow, timeRow, nextRow],
            "A scheduled scan is read-only, like every scan: nothing is cleaned, moved or deleted until you review and confirm. It runs only while DevSweep is running (in the tray when it opens at login). A PC that was asleep or off at the scheduled time scans when it wakes or when DevSweep starts.");
    }

    private static StackPanel Alerts(SettingsViewModel vm, Subscriptions subs)
    {
        ToggleSwitch disk = Switch("Alert when the disk is nearly full", vm.DiskAlert, on => vm.DiskAlert = on);
        var diskPercent = new NumberBox
        {
            Value = vm.DiskPercent, Minimum = SettingsViewModel.DiskPercentMin, Maximum = SettingsViewModel.DiskPercentMax, SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 130,
        };
        AutomationProperties.SetName(diskPercent, "Percent full that raises the disk alert");
        diskPercent.ValueChanged += (_, _) => vm.DiskPercent = diskPercent.Value;

        ToggleSwitch growth = Switch("Alert when something grew a lot", vm.GrowthAlert, on => vm.GrowthAlert = on);
        var growthGB = new NumberBox
        {
            Value = vm.GrowthGB, Minimum = SettingsViewModel.GrowthGBMin, Maximum = SettingsViewModel.GrowthGBMax, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 130,
        };
        AutomationProperties.SetName(growthGB, "Gigabytes of growth in a week that raise the growth alert");
        growthGB.ValueChanged += (_, _) => vm.GrowthGB = growthGB.Value;

        ToggleSwitch eol = Switch("Alert when a tool I use reaches end of life", vm.EndOfLifeAlert, on => vm.EndOfLifeAlert = on);
        ToggleSwitch secrets = Switch("Alert when a new file looks like a secret", vm.SecretsAlert, on => vm.SecretsAlert = on);

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

        TextBlock healthNote = Ui.Text(vm.HealthAlertsNote ?? string.Empty, "DsWarningText");
        healthNote.TextWrapping = TextWrapping.Wrap;
        Ui.Show(healthNote, vm.HealthAlertsNote is not null);
        TextBlock notifications = Note(string.Empty);
        TextBlock testResult = Note(string.Empty);
        Button openNotifications = Ui.Button("Open Notification settings", () => _ = Windows.System.Launcher.LaunchUriAsync(SettingsViewModel.NotificationSettingsUri), "Open Windows Settings, System, Notifications");
        subs.On(vm, null, () =>
        {
            notifications.Text = vm.NotificationsText;
            testResult.Text = vm.TestToastText ?? string.Empty;
            Ui.Show(openNotifications, vm.NotificationsBlocked);
        });
        return Section(
            "Alert me when",
            [
                Setting("The disk is nearly full", "Percent full, 70 to 95, for the drive Windows is on.", diskPercent, disk),
                Setting("Something grew a lot in a week", "Gigabytes, 1 to 10, in Clean up's items since last week.", growthGB, growth),
                Setting("A tool I use reaches end of life", "Needs Health. Uses endoflife.date's public support dates.", eol),
                Setting("A new file that looks like a secret appears", "Needs Health. Only files that appear after the first scheduled check.", secrets),
                Setting("Remind me again after", "If a problem is still there. Each problem is announced once, several at once become one notification, and a click opens the right screen.", reminder),
                new Border { Padding = new Thickness(Tokens.Space3), Child = Ui.V(Tokens.Space2, healthNote, notifications, FlowButtons.Build(Ui.Button("Send a test notification", vm.SendTestToast), openNotifications), testResult) },
            ],
            "Alerts are checked after each scheduled scan (turn those on above). They also list in the tray menu.");
    }

    private static StackPanel Scanning(SettingsViewModel vm)
    {
        ToggleSwitch exact = Switch("Measure exact sizes during the scan", vm.ExactScan, on => vm.ExactScan = on);
        var timeout = new NumberBox { Value = vm.CheckTimeoutSeconds, Minimum = 5, Maximum = 600, SmallChange = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 140 };
        AutomationProperties.SetName(timeout, "Seconds before a slow check is skipped");
        timeout.ValueChanged += (_, _) => vm.CheckTimeoutSeconds = timeout.Value;
        return Section("Scanning",
        [
            Setting("Exact sizes during the scan", "Overview skips alternate data streams (faster, no handle per file). Review always measures exactly before anything changes.", exact),
            Setting("Skip a check after (seconds)", "A slow check is skipped and the scan report says so.", timeout),
        ]);
    }

    private static StackPanel Modules(SettingsViewModel vm, Subscriptions subs)
    {
        var rows = new List<UIElement>();
        foreach (ModuleToggle module in vm.Modules)
        {
            var toggle = new ToggleSwitch { IsOn = module.IsOn, IsEnabled = module.CanChange, OnContent = "On", OffContent = "Off" };
            AutomationProperties.SetName(toggle, module.Title);
            toggle.Toggled += (_, _) => module.IsOn = toggle.IsOn;
            rows.Add(Setting(module.Title, module.Note.Length > 0 ? module.Note : null, toggle));
        }

        TextBlock restart = Ui.Text("Changes to modules apply the next time DevSweep starts.", "DsWarningText");
        subs.On(vm, nameof(SettingsViewModel.RestartNeeded), () => Ui.Show(restart, vm.RestartNeeded));
        StackPanel section = Section(
            "Modules",
            rows.ToArray(),
            "Every place (Clean up, Projects, Health) is a module and all of them are on by default. Switch one off and it is left out of scans and its place is hidden from the navigation. Saved in features.json; DEVSWEEP_FEATURES overrides it.");
        section.Children.Add(restart);
        return section;
    }

    private static StackPanel About(SettingsViewModel vm)
        => Section(
            "About",
            [
                Setting($"DevSweep for Windows {SettingsViewModel.Version}", "Unsigned: Windows SmartScreen may warn the first time (More info, then Run anyway).", Ui.Button("Open data folder", () => Shell.OpenFolder(vm.DataFolder), "Open the folder with settings, History and the quarantine")),
                Setting("Where things are kept", $"Settings, History and the quarantine live in {vm.DataFolder}."),
            ]);
}
