using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Background;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Settings;
using DevSweep.Core.Projects;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Xunit;

namespace DevSweep.App.Tests;

public sealed class SettingsSectionsTests
{
    [Fact]
    public void Every_section_has_a_unique_id_and_a_group_chip()
    {
        Assert.Equal(SettingsSections.All.Count, SettingsSections.All.Select(s => s.Id).Distinct().Count());
        Assert.All(SettingsSections.All, s => Assert.Contains(SettingsSections.Groups, g => g.Id == s.Group));
        string[] mac = ["appearance", "updates", "login", "schedule", "alerts", "accounts", "downloads", "folders", "ignored", "permissions", "troubleshooting", "modules", "about"];
        Assert.All(mac, id => Assert.Contains(SettingsSections.All, s => s.Id == id));
    }

    [Theory]
    [InlineData("", 15)]
    [InlineData("   ", 15)]
    [InlineData("theme", 1)]
    [InlineData("SCHEDULE", 1)]
    [InlineData("identity email", 1)]
    [InlineData("zzzz", 0)]
    public void Search_shows_only_sections_that_match_every_word(string query, int expected)
        => Assert.Equal(expected, SettingsSections.Matching(query).Count);

    [Fact]
    public void Searching_finds_the_section_by_what_a_person_would_type()
    {
        Assert.Equal("accounts", SettingsSections.Matching("github name email").Single().Id);
        Assert.Contains(SettingsSections.Matching("notification"), s => s.Id == "alerts");
        Assert.Contains(SettingsSections.Matching("controlled folder access"), s => s.Id == "permissions");
        Assert.Contains(SettingsSections.Matching("copy report"), s => s.Id == "troubleshooting");
    }
}

public sealed class IdentityValidationTests
{
    [Theory]
    [InlineData("", "", true)]
    [InlineData("Alex Doe", "alex@example.com", true)]
    [InlineData("Alex Doe", "alex.doe+work@mail.example.co.uk", true)]
    [InlineData("Alex Doe", "", false)]
    [InlineData("", "alex@example.com", false)]
    [InlineData("Alex", "alex", false)]
    [InlineData("Alex", "alex@example", false)]
    [InlineData("Alex", "a lex@example.com", false)]
    [InlineData("Alex", "alex@@example.com", false)]
    [InlineData("Alex", "@example.com", false)]
    [InlineData("Alex", "alex@example..com", false)]
    [InlineData("Alex", "<alex@example.com>", false)]
    public void An_identity_is_saved_only_with_a_name_and_a_plausible_email_or_cleared_with_both_blank(string name, string email, bool ok)
        => Assert.Equal(ok, IdentityValidation.Check(name, email) is null);

    [Fact]
    public void Projects_will_not_save_a_bad_identity_and_says_why()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        model.Projects.SaveIdentity("alex", new GitIdentity("Alex", "nope"));
        Assert.Equal(NoticeKind.Error, model.Projects.Notice?.Kind);
        Assert.Contains("email", model.Projects.Notice!.Text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FolderRowsTests
{
    private const string Profile = @"C:\Users\alex";

    [Fact]
    public void Added_folders_come_first_the_usual_ones_are_marked_and_missing_ones_sort_last()
    {
        string[] present = [Path.Join(Profile, "Code"), @"D:\work"];
        FolderState Probe(string p) => p == Path.Join(Profile, "src") ? FolderState.Denied : present.Contains(p) ? FolderState.Ok : FolderState.Missing;

        IReadOnlyList<FolderRow> rows = FolderRows.Build([@"D:\work"], Profile, Probe);

        Assert.Equal(@"D:\work", rows[0].Path);
        Assert.False(rows[0].IsDefault);
        Assert.All(rows.Skip(1), r => Assert.True(r.IsDefault));
        Assert.Contains(rows, r => r.Path == Path.Join(Profile, "Code") && r.State == FolderState.Ok);
        Assert.Contains(rows, r => r.Path == Path.Join(Profile, "src") && r.State == FolderState.Denied && r.StateText.Contains("isn't letting", StringComparison.Ordinal));
        int firstMissing = rows.ToList().FindIndex(1, r => r.State == FolderState.Missing);
        Assert.All(rows.Skip(firstMissing), r => Assert.Equal(FolderState.Missing, r.State));
        Assert.Equal(1, rows.Count(r => r.Path == Path.Join(Profile, "Documents", "GitHub")));
    }

    [Fact]
    public void A_usual_folder_that_was_added_is_listed_once_as_added()
    {
        string added = Path.Join(Profile, "Code");
        IReadOnlyList<FolderRow> rows = FolderRows.Build([added], Profile, _ => FolderState.Ok);
        FolderRow code = Assert.Single(rows, r => r.Path == added);
        Assert.False(code.IsDefault);
    }

    [Fact]
    public void The_probe_tells_missing_from_present_on_a_real_disk()
    {
        string folder = Path.Join(Path.GetTempPath(), "DevSweep-folder-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(FolderState.Missing, FolderRows.Probe(folder));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(FolderState.Ok, FolderRows.Probe(folder));
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void The_missing_title_counts()
    {
        Assert.Equal("1 usual folder isn't on this PC", FolderRows.MissingTitle(1));
        Assert.Equal("7 usual folders aren't on this PC", FolderRows.MissingTitle(7));
    }
}

public sealed class SettingsPageTests
{
    private static string TempFile() => Path.Join(Path.GetTempPath(), "DevSweep-settings-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Folders_are_added_removed_and_restored_and_survive_a_restart()
    {
        string file = TempFile();
        try
        {
            var vm = new SettingsViewModel(new AppSettingsStore(file), new SampleBackend()) { FolderProbe = _ => FolderState.Ok };

            string src = Path.GetFullPath(@"D:\src");
            string clients = Path.GetFullPath(@"E:\clients");
            vm.AddFolder(src + Path.DirectorySeparatorChar);
            vm.AddFolder(clients);
            vm.AddFolder(src);

            Assert.Equal([clients, src], new AppSettingsStore(file).Current.ProjectFolders);
            Assert.Equal(2, vm.ProjectFolders.Count);
            Assert.Equal(2, vm.FolderRows.Count(r => !r.IsDefault));
            Assert.Contains(vm.FolderRows, r => r.IsDefault);

            vm.RemoveFolder(clients);
            Assert.Equal([src], new AppSettingsStore(file).Current.ProjectFolders);

            vm.RestoreDefaultFolders();
            Assert.Empty(new AppSettingsStore(file).Current.ProjectFolders);
            Assert.True(vm.UsingDefaultFolders);
            Assert.All(vm.FolderRows, r => Assert.True(r.IsDefault));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Ignored_items_come_back_with_Stop_ignoring()
    {
        string file = TempFile();
        try
        {
            var store = new AppSettingsStore(file);
            store.Update(s => s with { Ignored = [new IgnoredItem("npm-cache", "npm cache"), new IgnoredItem("pip", "pip cache")] });
            var vm = new SettingsViewModel(store, new SampleBackend());
            Assert.Equal(2, vm.Ignored.Count);

            vm.StopIgnoring(vm.Ignored[0]);

            Assert.Equal(["pip"], new AppSettingsStore(file).Current.Ignored.Select(i => i.Id));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Search_narrows_the_sections_and_Shows_follows_it()
    {
        var vm = new SettingsViewModel(new AppSettingsStore(null), new SampleBackend());
        Assert.True(vm.Shows("alerts"));

        vm.Search = "schedule";

        Assert.True(vm.Shows("schedule"));
        Assert.False(vm.Shows("alerts"));
        vm.Search = string.Empty;
        Assert.Equal(SettingsSections.All.Count, vm.VisibleSections.Count);
    }

    [Fact]
    public void Troubleshooting_says_to_run_a_scan_first_and_then_copies_a_report_without_names()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        SettingsViewModel vm = model.SettingsPage;
        Assert.False(vm.HasScanReport);
        Assert.Equal("Run a scan first.", vm.ScanReportSummary);
        Assert.Contains("No scan yet", vm.ScanReportText(), StringComparison.Ordinal);

        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);

        Assert.True(vm.HasScanReport);
        string text = vm.ScanReportText();
        Assert.Contains("DevSweep for Windows", text, StringComparison.Ordinal);
        Assert.Contains("Windows", text, StringComparison.Ordinal);
        Assert.Contains("Checks (slowest first", text, StringComparison.Ordinal);
        Assert.Contains("Docker build cache: SKIPPED, over 90 s", text, StringComparison.Ordinal);
        Assert.Contains("partly measured", text, StringComparison.Ordinal);
        Assert.Contains("Folders Windows blocked: 1", text, StringComparison.Ordinal);
        Assert.Contains("(blocked by Windows)", text, StringComparison.Ordinal);
        Assert.Contains("npm cache: 1 found", text, StringComparison.Ordinal);
        Assert.Contains("Measuring: 6.1 s", text, StringComparison.Ordinal);

        // No file, folder, user or project names: not the user's name, not a path below two levels, not a finding's own title.
        Assert.DoesNotContain("alex", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Postman", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Partitions", text, StringComparison.Ordinal);
        foreach (var finding in SampleData.Findings.Where(f => f.Title.Contains(':', StringComparison.Ordinal) || f.Title.Contains("IntelliJ", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(finding.Title, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Redact_keeps_two_levels_and_hides_the_profile()
    {
        Assert.Equal(@"~\AppData\Roaming\…", ReportText.Redact(@"C:\Users\alex\AppData\Roaming\Postman\Partitions", @"C:\Users\alex"));
        Assert.Equal(@"D:\clients\acme\…", ReportText.Redact(@"D:\clients\acme\secret-project\src", @"C:\Users\alex"));
        Assert.Equal(@"D:\work", ReportText.Redact(@"D:\work", @"C:\Users\alex"));
    }

    [Fact]
    public void Diagnostics_list_version_modules_and_the_flag_without_the_user_name()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        string text = model.SettingsPage.DiagnosticsText();
        Assert.Contains("DevSweep for Windows", text, StringComparison.Ordinal);
        Assert.Contains("Modules:", text, StringComparison.Ordinal);
        Assert.Contains("DEVSWEEP_FEATURES", text, StringComparison.Ordinal);
        Assert.Contains("elevated", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alex", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Permissions_count_blocked_folders_from_the_scan_and_the_folder_list()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        SettingsViewModel vm = model.SettingsPage;
        vm.FolderProbe = _ => FolderState.Ok;
        vm.RefreshFolders();
        Assert.Equal(0, vm.BlockedFolderCount);
        Assert.StartsWith("Nothing is blocked", vm.PermissionsText, StringComparison.Ordinal);

        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        Assert.Equal(1, vm.BlockedFolderCount);
        Assert.Contains("Controlled folder access", vm.PermissionsText, StringComparison.Ordinal);

        vm.FolderProbe = _ => FolderState.Denied;
        vm.RefreshFolders();
        Assert.True(vm.BlockedFolderCount > 1);
        Assert.Equal("windowsdefender", SettingsViewModel.RansomwareProtectionUri.Scheme);
    }

    [Fact]
    public void OpenSettings_clears_the_search_and_asks_for_the_section()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        model.SettingsPage.Search = "zzz";

        model.OpenSettings("permissions");

        Assert.Equal("settings", model.Current);
        Assert.Equal("permissions", model.SettingsPage.JumpTo);
        Assert.Equal(string.Empty, model.SettingsPage.Search);
    }
}

public sealed class ScheduleTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 2)));

    [Fact]
    public void Off_means_no_next_scan_and_the_status_line_says_so()
    {
        Assert.Null(ScheduleText.Next(new ScanSchedule(), null, Now));
        Assert.Equal("Never scanned · scheduled scans off", ScheduleText.StatusLine(false, null, new ScanSchedule(), null, Now));
        Assert.Equal("Scanning…", ScheduleText.StatusLine(true, Now, new ScanSchedule(), null, Now));
    }

    [Fact]
    public void A_weekly_schedule_names_the_day_and_time()
    {
        var schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Friday, Hour = 9, Minute = 0, Since = Now };
        Assert.Equal("Friday, 9:00 AM", ScheduleText.Next(schedule, null, Now));
        Assert.Equal("Last scan 5 minutes ago · next Friday, 9:00 AM", ScheduleText.StatusLine(false, Now.AddMinutes(-5), schedule, null, Now));
    }

    [Fact]
    public void Today_tomorrow_and_due_are_said_plainly()
    {
        var daily = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Daily, Hour = 18, Minute = 0, Since = Now };
        var ranToday = daily with { Since = Now.AddHours(8) };
        Assert.Equal("today, 6:00 PM", ScheduleText.Next(daily, null, Now));
        Assert.Equal("tomorrow, 6:00 PM", ScheduleText.Next(ranToday, null, Now));
        Assert.Equal("now", ScheduleText.Next(daily with { Since = Now.AddDays(-2) }, null, Now));
    }
}

public sealed class HomeStatesTests
{
    private static MainViewModel Model(bool schedule = false)
    {
        var settings = new AppSettingsStore(null);
        if (schedule)
        {
            settings.Update(s => s with { Schedule = new ScanSchedule { Enabled = true, Frequency = ScanFrequency.Weekly, Day = DayOfWeek.Monday, Hour = 9, Since = SampleData.Now } });
        }

        return new MainViewModel(new SampleBackend(), settings);
    }

    [Fact]
    public void Before_a_scan_it_says_not_scanned_and_offers_nothing_to_review()
    {
        MainViewModel model = Model();
        model.Home.Refresh();
        Assert.Equal(HomeState.NotScanned, model.Home.State);
        Assert.Equal("Not scanned yet", model.Home.Subtitle);
        Assert.Equal(string.Empty, model.Home.ReviewLabel);
        Assert.Null(model.Home.BlockedFoldersText);
        Assert.Null(model.Home.RuleErrorsText);
    }

    [Fact]
    public void While_scanning_the_subtitle_carries_the_live_status()
    {
        MainViewModel model = Model();
        model.Scan.IsScanning = true;
        model.Scan.OnProgress(new ScanProgress(ScanPhase.Checking, 20, 50, "x", true, new List<string> { "npm cache" }));
        Assert.Equal(HomeState.Scanning, model.Home.State);
        Assert.StartsWith("Scanning · Checking 21 of 50", model.Home.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_scan_says_when_and_when_the_next_one_is()
    {
        MainViewModel model = Model(schedule: true);
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);

        Assert.Equal(HomeState.Scanned, model.Home.State);
        Assert.Equal("Scanned just now · next scan Monday, 9:00 AM", model.Home.Subtitle);
        Assert.StartsWith("Review ", model.Home.ReviewLabel, StringComparison.Ordinal);
        Assert.Equal("Since 26 Oct", model.Home.ChangesTitle);
    }

    [Fact]
    public void With_scheduled_scans_off_the_subtitle_has_no_next_scan()
    {
        MainViewModel model = Model();
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        Assert.Equal("Scanned just now", model.Home.Subtitle);
    }

    [Fact]
    public void A_first_scan_and_a_scan_with_nothing_new_have_their_own_sentences()
    {
        var backend = new SampleBackend();
        var model = new MainViewModel(backend, new AppSettingsStore(null));
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), null, SampleData.Now);
        Assert.True(model.Home.ChangesEmpty is not null || model.Home.Changes.Count > 0);

        // The sample history has cleaned items this week, so "nothing changed" is checked on the changes record alone.
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), new DevSweep.Core.Health.Alerts.ScanChanges(SampleData.Now.AddDays(-7), new List<DevSweep.Core.Health.Alerts.ScanChange>(), new List<DevSweep.Core.Health.Alerts.ScanChange>()), SampleData.Now);
        Assert.DoesNotContain(model.Home.Changes, c => c.Tag is "Grew" or "New");
    }

    [Fact]
    public void Blocked_folders_and_rule_errors_get_banners_and_the_banners_go_when_the_report_has_none()
    {
        MainViewModel model = Model();
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        Assert.Equal("One folder can't be read, so a few leftovers can be missed.", model.Home.BlockedFoldersText);
        Assert.Null(model.Home.RuleErrorsText);

        ScanReport report = SampleData.Report with { AccessDenied = new List<string>(), RuleErrors = new List<RuleError> { new("pack.yml", "r", 3, 1, "bad field") } };
        model.Scan.Show(new ScanResult(SampleData.Findings, report), SampleData.Changes, SampleData.Now);
        Assert.Null(model.Home.BlockedFoldersText);
        Assert.Contains("bad field", model.Home.RuleErrorsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_lists_at_most_four_health_items_and_says_where_the_rest_is()
    {
        MainViewModel model = Model();
        await model.Health.LoadAsync();
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);

        int health = model.Home.Attention.Count(a => a.Tag is "Security" or "Health" or "Ports");
        Assert.InRange(health, 1, HomeViewModel.MaxHealthItems);
        if (model.Home.AttentionCaption is { } caption)
        {
            Assert.Equal($"Showing the {HomeViewModel.MaxHealthItems} most important. Health has the rest.", caption);
        }
    }
}

public sealed class SidebarTests
{
    [Fact]
    public async Task Counts_cards_and_the_status_line_follow_the_scan_and_the_checks()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        SidebarViewModel side = model.Sidebar;
        Assert.Equal("Not scanned yet", side.CleanableLine);
        Assert.Equal(string.Empty, side.CountFor("cleanup"));
        Assert.Equal("Never scanned · scheduled scans off", side.StatusLine);
        Assert.NotEqual("-", side.FreeText);
        Assert.Empty(side.Cards);

        await model.Health.LoadAsync();
        await model.Projects.LoadAsync();
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);

        Assert.EndsWith("can be cleaned", side.CleanableLine, StringComparison.Ordinal);
        Assert.Equal(DevSweep.App.ViewModels.Text.SizeLabels.Size(model.Scan.TotalBytes), side.CountFor("cleanup"));
        Assert.Equal(side.HealthAttentionCount > 0 ? side.HealthAttentionCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty, side.CountFor("health"));
        Assert.Equal(side.ProjectsNeedingPush > 0 ? side.ProjectsNeedingPush.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty, side.CountFor("projects"));
        Assert.InRange(side.Cards.Count, 1, SidebarViewModel.MaxCards);
        Assert.All(side.Cards, c => Assert.Equal("health", c.Place));
        Assert.Equal("Last scan just now · scheduled scans off".Replace(" · scheduled scans off", string.Empty, StringComparison.Ordinal), side.StatusLine);
        Assert.Equal(string.Empty, side.CountFor("history"));
    }

    [Fact]
    public void The_sidebar_announces_changes_so_the_views_refresh()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        int changes = 0;
        model.Sidebar.PropertyChanged += (_, _) => changes++;
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        Assert.True(changes > 0);
    }
}

public sealed class TrayFlyoutTests
{
    [Fact]
    public async Task The_flyout_has_the_mac_popover_content()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        TrayViewModel tray = model.Background.Tray;

        TrayFlyoutContent before = tray.Flyout();
        Assert.Equal(FlyoutState.NotScanned, before.State);
        Assert.Equal("Not scanned yet", before.Headline);
        Assert.True(before.CanScan);
        Assert.Null(before.LastScanText);

        await model.Health.LoadAsync();
        await model.Projects.LoadAsync();
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        model.Background.Alerts.RefreshActive();

        TrayFlyoutContent c = tray.Flyout();
        Assert.Equal(FlyoutState.HasCleanable, c.State);
        Assert.EndsWith(" free", c.FreeText, StringComparison.Ordinal);
        Assert.Equal("can be cleaned", c.SubLine);
        Assert.InRange(c.UsedFraction, 0.01, 1);
        Assert.InRange(c.CleanableFraction, 0.0001, c.UsedFraction);
        Assert.True(c.Alerts.Count <= TrayViewModel.FlyoutAlerts);
        Assert.NotEmpty(c.Recent);
        Assert.True(c.Recent.Count <= TrayViewModel.FlyoutProjects);
        Assert.All(c.Recent, p => Assert.False(string.IsNullOrEmpty(p.Path)));
        Assert.StartsWith("Last scan ", c.LastScanText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_menu_also_has_the_cleanable_line_settings_and_the_last_scan()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);

        IReadOnlyList<TrayItem> items = model.Background.Tray.Items();

        Assert.EndsWith("can be cleaned", items[1].Text, StringComparison.Ordinal);
        Assert.Contains(items, i => i.Command == TrayCommand.OpenSettings && i.Text == "Settings");
        Assert.Contains(items, i => !i.Enabled && i.Text.StartsWith("Last scan", StringComparison.Ordinal));
        Assert.Equal("Quit DevSweep", items[^1].Text);
    }

    [Fact]
    public void Settings_and_Review_buttons_ask_the_window_to_open_those_screens()
    {
        var model = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        TrayViewModel tray = model.Background.Tray;
        var targets = new List<ToastTarget?>();
        tray.ShowRequested += (_, t) => targets.Add(t);

        tray.Execute(new TrayItem("Settings", TrayCommand.OpenSettings));
        tray.Execute(new TrayItem("Review", TrayCommand.OpenCleanUp));
        tray.Execute(new TrayItem("Projects", TrayCommand.OpenTarget, nameof(ToastTarget.Projects)));

        Assert.Equal([ToastTarget.Settings, ToastTarget.CleanUp, ToastTarget.Projects], targets);
        model.Open(ToastTarget.Settings);
        Assert.Equal("settings", model.Current);
        model.Open(ToastTarget.Projects);
        Assert.Equal("projects", model.Current);
    }
}
