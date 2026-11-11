using DevSweep.App.ViewModels;
using DevSweep.App.ViewModels.Sample;
using DevSweep.App.ViewModels.Services;
using DevSweep.App.ViewModels.Settings;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Cleaning;
using DevSweep.Core.Platform;
using DevSweep.Core.Review;
using DevSweep.Core.Rules;
using DevSweep.Core.Scanning;
using Xunit;

namespace DevSweep.App.Tests;

public sealed class SizeLabelTests
{
    [Fact]
    public void An_exact_size_has_no_badge_but_says_so_in_its_tooltip()
    {
        Finding f = Make(SizeConfidence.Exact, SizeMeasurement.Exact);
        Assert.Empty(SizeLabels.Badges(f));
        Assert.StartsWith("Exact", SizeLabels.Tooltip(f), StringComparison.Ordinal);
        Assert.Equal("2 MB", SizeLabels.Size(f));
    }

    [Theory]
    [InlineData(SizeConfidence.Exact, SizeMeasurement.Overview, "Overview")]
    [InlineData(SizeConfidence.Partial, SizeMeasurement.Exact, "Partial")]
    [InlineData(SizeConfidence.AccessDenied, SizeMeasurement.Exact, "Access denied")]
    [InlineData(SizeConfidence.CloudPlaceholders, SizeMeasurement.Exact, "Cloud files")]
    [InlineData(SizeConfidence.SharedLinks, SizeMeasurement.Exact, "Shared links")]
    [InlineData(SizeConfidence.Exact, SizeMeasurement.Reported, "Reported")]
    public void Every_size_that_is_not_exact_carries_a_badge(SizeConfidence confidence, SizeMeasurement measurement, string badge)
    {
        Finding f = Make(confidence, measurement);
        Assert.Contains(SizeLabels.Badges(f), b => b.Text == badge && b.Tooltip.Length > 20);
        Assert.Contains(badge, SizeLabels.Tooltip(f), StringComparison.Ordinal);
    }

    [Fact]
    public void Lower_bounds_say_at_least_and_shared_links_say_what_is_freed()
    {
        Assert.Equal("at least 2 MB", SizeLabels.Size(Make(SizeConfidence.Partial, SizeMeasurement.Exact)));
        Assert.Equal("at least 2 MB", SizeLabels.Size(Make(SizeConfidence.AccessDenied, SizeMeasurement.Exact)));
        Assert.Contains("frees 512 KB", SizeLabels.Badges(Make(SizeConfidence.SharedLinks, SizeMeasurement.Exact) with { FreeableBytes = 512 * 1024 })[0].Tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Category.PackageCaches, Risk.Rebuilds, CleanupKind.Caches)]
    [InlineData(Category.IdeVersions, Risk.OldVersion, CleanupKind.OldVersions)]
    [InlineData(Category.Dotnet, Risk.Rebuilds, CleanupKind.Caches)]
    [InlineData(Category.Dotnet, Risk.OldVersion, CleanupKind.OldVersions)]
    [InlineData(Category.Wsl, Risk.HoldsData, CleanupKind.DockerAndWsl)]
    [InlineData(Category.Leftovers, Risk.Leftover, CleanupKind.Leftovers)]
    [InlineData(Category.Projects, Risk.Rebuilds, CleanupKind.ProjectFiles)]
    [InlineData(Category.Windows, Risk.Rebuilds, CleanupKind.WindowsFiles)]
    public void Categories_map_to_the_Mac_kinds(Category category, Risk risk, CleanupKind kind)
        => Assert.Equal(kind, CleanupKinds.Of(Make(SizeConfidence.Exact, SizeMeasurement.Exact) with { Category = category, Risk = risk }));

    [Fact]
    public void Reconciliation_says_shown_expected_and_actually_freed()
    {
        var outcome = new ActionOutcome
        {
            Finding = Make(SizeConfidence.Exact, SizeMeasurement.Exact),
            Action = new CleanAction(CleanActionKind.Delete),
            Message = "Deleted.",
            Items = [new MovedItem { Original = @"C:\x", Destination = ItemDestination.Deleted }],
            Reconciliation = new FreedSpace { Volume = @"C:\", ShownBytes = 2 << 20, ExpectedFreedBytes = 2 << 20, FreeBefore = 0, FreeAfter = 2 << 20 },
        };
        Assert.Equal(@"Shown 2 MB · expected 2 MB · C: actually freed 2 MB, as expected.", ReconciliationText.Describe(outcome));
        ActionOutcome binned = outcome with { Items = [new MovedItem { Original = @"C:\x", Destination = ItemDestination.RecycleBin }] };
        Assert.Contains("Nothing is freed on C: yet", ReconciliationText.Describe(binned), StringComparison.Ordinal);
    }

    private static Finding Make(SizeConfidence confidence, SizeMeasurement measurement)
        => new() { Id = "x", Title = "X", Risk = Risk.Rebuilds, Size = 2 << 20, FreeableBytes = 2 << 20, Confidence = confidence, Measurement = measurement };
}

public sealed class ShellTests
{
    [Fact]
    public void Places_follow_modules_and_flags()
    {
        IReadOnlyList<ModuleInfo> modules =
        [
            new("cleanup", "Clean up", ModuleState.On),
            new("health", "Health", ModuleState.TurnedOff),
            new("projects", "Projects", ModuleState.NotInThisBuild),
        ];
        Assert.Equal(["home", "cleanup", "projects", "history", "settings"], MainViewModel.BuildPlaces(modules).Select(p => p.Id));

        IReadOnlyList<ModuleInfo> noCleanup = [new("cleanup", "Clean up", ModuleState.TurnedOff), new("packs", "Packs", ModuleState.TurnedOff), new("health", "Health", ModuleState.On), new("projects", "Projects", ModuleState.TurnedOff)];
        Assert.Equal(["home", "health", "history", "settings"], MainViewModel.BuildPlaces(noCleanup).Select(p => p.Id));
    }

    [Fact]
    public async Task Welcome_shows_once()
    {
        var settings = new AppSettingsStore(null);
        var model = new MainViewModel(new SampleBackend(), settings);
        Assert.True(model.ShowWelcome);
        await model.FinishWelcomeAsync();
        Assert.False(model.ShowWelcome);
        Assert.True(settings.Current.WelcomeDone);
        Assert.False(new MainViewModel(new SampleBackend(), settings).ShowWelcome);
    }

    [Fact]
    public void Settings_survive_a_round_trip_on_disk()
    {
        string file = Path.Join(Path.GetTempPath(), $"devsweep-app-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettingsStore(file).Update(s => s with { Appearance = AppearanceMode.Dark, ProjectFolders = new List<string> { @"D:\src" }, Ignored = new List<IgnoredItem> { new("a:b", "A") } });
            AppSettings read = new AppSettingsStore(file).Current;
            Assert.Equal(AppearanceMode.Dark, read.Appearance);
            Assert.Equal(@"D:\src", read.ProjectFolders[0]);
            Assert.Equal("A", read.Ignored[0].Title);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public sealed class ScanAndCleanUpTests
{
    [Fact]
    public void Progress_events_list_running_checks_and_what_they_found()
    {
        var scan = new ScanViewModel(new SampleBackend());
        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 0, 3, "npm", false, ["npm cache"]));
        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 0, 3, "gradle", false, ["npm cache", "Gradle caches"]));
        scan.OnProgress(new ScanProgress(ScanPhase.Checking, 1, 3, "npm", true, ["Gradle caches"]));
        scan.OnProgress(new ScanProgress(ScanPhase.Measuring, 1, 2, "npm", true, []));
        Assert.Equal(2, scan.Checks.Count);
        CheckProgressViewModel npm = scan.Checks.Single(c => c.Id == "npm");
        Assert.Equal("npm cache", npm.Title);
        Assert.False(npm.IsRunning);
        Assert.Equal("1 item found", npm.State);
        Assert.Equal("Gradle caches", scan.Checks.Single(c => c.Id == "gradle").Title);
        Assert.Equal("Measuring sizes · 1 of 2", scan.Status);
    }

    [Fact]
    public void Clean_up_groups_by_risk_filters_by_kind_and_ignores()
    {
        (MainViewModel model, AppSettingsStore settings) = Scanned();
        CleanUpViewModel vm = model.CleanUp;
        Assert.Equal([Risk.Rebuilds, Risk.OldVersion, Risk.Leftover, Risk.HoldsData, Risk.NeedsAdmin], vm.Groups.Select(g => g.Risk));
        Assert.Equal("All", vm.Filters[0].Title);
        vm.SelectedKind = CleanupKind.Leftovers;
        Assert.Single(vm.Groups);
        Assert.Equal("Leftover from a removed app", vm.Groups[0].Title);
        vm.SelectedKind = null;

        FindingViewModel admin = vm.All.Single(f => f.Finding.Risk == Risk.NeedsAdmin);
        Assert.False(admin.CanSelect);
        Assert.Equal("dism /Online /Cleanup-Image /StartComponentCleanup", admin.ManualCommand);

        FindingViewModel postman = vm.All.Single(f => f.Id == "leftover:postman");
        vm.Ignore(postman);
        Assert.DoesNotContain(vm.All, f => f.Id == "leftover:postman");
        Assert.Contains(settings.Current.Ignored, i => i.Id == "leftover:postman");
    }

    [Fact]
    public async Task Review_remeasures_groups_by_outcome_then_cleans_with_reconciliation()
    {
        (MainViewModel model, _) = Scanned();
        IReadOnlyList<PlannedAction>? requested = null;
        model.CleanUp.ReviewRequested += (_, plan) => requested = plan;
        foreach (FindingViewModel item in model.CleanUp.All.Where(f => f.CanSelect))
        {
            item.IsSelected = true;
        }

        model.CleanUp.Review();
        Assert.NotNull(requested);
        Assert.DoesNotContain(requested, p => p.Action.Kind == CleanActionKind.Manual && p.Finding.Risk == Risk.NeedsAdmin);

        ReviewViewModel review = model.Review;
        await review.OpenAsync(requested);
        Assert.Equal(ReviewStage.Ready, review.Stage);
        Assert.All(review.Groups.SelectMany(g => g.Rows), r => Assert.NotEqual(SizeMeasurement.Overview, r.Item.Finding.Measurement));
        Assert.Contains(review.Groups, g => g.Outcome == ReviewOutcome.Blocked && g.Rows.Any(r => r.Title.StartsWith("Chrome", StringComparison.Ordinal)));
        Assert.Contains(review.Groups, g => g.Outcome == ReviewOutcome.Quarantine && g.Rows.Any(r => r.Title.StartsWith("Android", StringComparison.Ordinal)));
        Assert.Contains(review.Groups, g => g.Outcome == ReviewOutcome.DeleteForGood);
        Assert.NotNull(review.Warning);
        Assert.Contains("Close chrome first.", review.Footer, StringComparison.Ordinal);
        Assert.Contains("delete for good", review.CommandsText, StringComparison.Ordinal);

        IReadOnlyCollection<string>? cleaned = null;
        review.Cleaned += (_, ids) => cleaned = ids;
        await review.CleanAsync();
        Assert.Equal(ReviewStage.Done, review.Stage);
        Assert.NotNull(cleaned);
        Assert.DoesNotContain("chrome-cache", cleaned);
        Assert.All(review.Outcomes, o => Assert.NotEmpty(o.Reconciliation));
        Assert.DoesNotContain(model.Scan.Findings, f => cleaned.Contains(f.Id));
    }

    [Fact]
    public async Task Home_shows_volumes_changes_and_attention()
    {
        (MainViewModel model, _) = Scanned();
        await model.Health.LoadAsync();
        HomeViewModel home = model.Home;
        home.Refresh();
        Assert.Equal(2, home.Volumes.Count);
        Assert.True(home.Volumes[0].CleanableFraction > 0);
        Assert.Contains(home.Changes, c => c.Tag == "Grew" && c.Title == "Gradle caches");
        Assert.Contains(home.Changes, c => c.Tag == "New");
        Assert.Contains(home.Changes, c => c.Tag == "Cleaned");
        Assert.Contains(home.Attention, a => a.Tag == "Security");
        Assert.Contains(home.Attention, a => a.Tag == "Scan" && a.Text.Contains("skipped", StringComparison.Ordinal));
        Assert.EndsWith("can be cleaned", home.CleanableText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_offers_restore_only_while_items_are_still_there()
    {
        var backend = new SampleBackend();
        var history = new HistoryViewModel(backend);
        history.Refresh();
        List<HistoryRow> rows = [.. history.Days.SelectMany(d => d.Rows)];
        Assert.Equal("Rebuilds itself", rows.Single(r => r.Title == "Yarn cache").Status);
        Assert.Equal(string.Empty, rows.Single(r => r.Title.StartsWith("PyCharm", StringComparison.Ordinal)).Status);
        Assert.Equal("Restored", rows.Single(r => r.Title == "Slack").Status);
        Assert.True(history.HasQuarantine);
        await history.RestoreAsync(rows.Single(r => r.Title.StartsWith("PyCharm", StringComparison.Ordinal)));
        Assert.Equal("Restored", history.Days.SelectMany(d => d.Rows).Single(r => r.Title.StartsWith("PyCharm", StringComparison.Ordinal)).Status);
        await history.EmptyQuarantineAsync();
        Assert.False(history.HasQuarantine);
    }

    private static (MainViewModel Model, AppSettingsStore Settings) Scanned()
    {
        var settings = new AppSettingsStore(null);
        settings.Update(s => s with { WelcomeDone = true });
        var model = new MainViewModel(new SampleBackend(), settings);
        model.Scan.Show(new ScanResult(SampleData.Findings, SampleData.Report), SampleData.Changes, SampleData.Now);
        return (model, settings);
    }
}

public sealed class EngineBackendTests
{
    [Fact]
    public void Modules_come_from_the_real_engine_with_every_place_on_by_default()
    {
        var engine = Core.Modules.DevSweepEngine.CreateDefault();
        IReadOnlyList<ModuleInfo> modules = EngineBackend.DescribeModules(engine);
        Assert.Equal(ModuleState.On, modules.Single(m => m.Id == "cleanup").State);
        Assert.Equal(ModuleState.On, modules.Single(m => m.Id == "packs").State);
        Assert.Equal(ModuleState.On, modules.Single(m => m.Id == "health").State);
        Assert.Equal(ModuleState.On, modules.Single(m => m.Id == "projects").State);
        Assert.Contains(modules, m => m.Id == "projects.cleanup");
        Assert.Contains(modules, m => m.Id == "health.ports");
        Assert.Equal(["home", "cleanup", "projects", "health", "history", "settings"], MainViewModel.BuildPlaces(modules).Select(p => p.Id));

        // Switched off, a place stays hidden as before.
        IReadOnlyList<ModuleInfo> off = EngineBackend.DescribeModules(Core.Modules.DevSweepEngine.CreateDefault(Core.Modules.FeatureFlags.Default.With("-module:health -module:projects")));
        Assert.Equal(ModuleState.TurnedOff, off.Single(m => m.Id == "health").State);
        Assert.Equal(ModuleState.TurnedOff, off.Single(m => m.Id == "projects").State);
        Assert.Equal(["home", "cleanup", "history", "settings"], MainViewModel.BuildPlaces(off).Select(p => p.Id));

        var on = Core.Modules.DevSweepEngine.CreateDefault(Core.Modules.FeatureFlags.Default.With("-module:health.ports"));
        Assert.NotNull(ViewModels.Places.CoreHealthSource.From(on, () => null));
        Assert.False(ViewModels.Places.CoreHealthSource.From(on, () => null)!.Features.Ports);
        Assert.Contains(MainViewModel.BuildPlaces(EngineBackend.DescribeModules(on)), p => p.Id == "health");

        var projects = Core.Modules.DevSweepEngine.CreateDefault(Core.Modules.FeatureFlags.Default.With("-module:projects.activity"));
        ViewModels.Places.CoreProjectsSource? source = ViewModels.Places.CoreProjectsSource.From(projects, () => null);
        Assert.NotNull(source);
        Assert.False(source.Features.Activity);
        Assert.Equal([ProjectsTab.Projects, ProjectsTab.Cleanup, ProjectsTab.Accounts], new ProjectsViewModel(source).Tabs);
    }

    [Fact]
    public async Task Projects_never_call_unknown_or_unchecked_safe_and_show_the_fetch_note()
    {
        var vm = new ProjectsViewModel(new SampleBackend().Projects);
        await vm.LoadAsync();
        Assert.Equal(PlaceState.Ready, vm.State);
        List<Core.Projects.Project> projects = [.. vm.Report!.Snapshot.Projects];
        Assert.Equal("In OneDrive, not checked", ProjectsViewModel.Safety(projects.Single(p => p.Name == "ml-notebooks")).Text);
        Assert.Equal("Unknown: not safe", ProjectsViewModel.Safety(projects.Single(p => p.Name == "lfs-assets")).Text);
        Assert.Equal("Safe to remove", ProjectsViewModel.Safety(projects.Single(p => p.Name == "old-web")).Text);
        Assert.Contains("as of the last fetch", ProjectsViewModel.Safety(projects.Single(p => p.Name == "old-web")).Tooltip, StringComparison.Ordinal);
        Assert.NotNull(vm.Idle);
        Assert.DoesNotContain(vm.Idle.Safe, p => p.Project.Name is "ml-notebooks" or "lfs-assets" or "design-system");
        vm.IdleDays = 90;
        await vm.RefreshIdleAsync();
        Assert.Equal(90, vm.Idle!.Days);
        Assert.Contains("safe to remove", vm.IdleSummary, StringComparison.Ordinal);
    }
}

public sealed class ThemeAndWindowTests
{
    [Fact]
    public void The_theme_button_cycles_system_light_dark_system_and_says_what_it_is()
    {
        var settings = new AppSettingsStore(null);
        SettingsViewModel vm = new MainViewModel(new SampleBackend(), settings).SettingsPage;
        Assert.Equal(AppearanceMode.System, vm.Appearance);
        Assert.Equal("Theme: System, click to change", vm.ThemeButtonLabel);
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ThemeButtonLabel))
            {
                seen.Add(vm.ThemeButtonLabel);
            }
        };

        vm.CycleAppearance();
        Assert.Equal((AppearanceMode.Light, "Theme: Light, click to change"), (vm.Appearance, vm.ThemeButtonLabel));
        Assert.Equal(AppearanceMode.Light, settings.Current.Appearance);
        vm.CycleAppearance();
        Assert.Equal((AppearanceMode.Dark, "Theme: Dark, click to change"), (vm.Appearance, vm.ThemeButtonLabel));
        vm.CycleAppearance();
        Assert.Equal((AppearanceMode.System, "Theme: System, click to change"), (vm.Appearance, vm.ThemeButtonLabel));
        Assert.Equal(AppearanceMode.System, settings.Current.Appearance);
        Assert.Equal(["Theme: Light, click to change", "Theme: Dark, click to change", "Theme: System, click to change"], seen);
    }

    [Fact]
    public void The_theme_survives_a_restart_and_the_settings_page_and_the_button_share_one_value()
    {
        string file = Path.Join(Path.GetTempPath(), $"devsweep-theme-{Guid.NewGuid():N}.json");
        try
        {
            var first = new MainViewModel(new SampleBackend(), new AppSettingsStore(file));
            first.SettingsPage.CycleAppearance();
            first.SettingsPage.CycleAppearance();
            Assert.Equal(AppearanceMode.Dark, new AppSettingsStore(file).Current.Appearance);

            var second = new MainViewModel(new SampleBackend(), new AppSettingsStore(file));
            Assert.Equal(AppearanceMode.Dark, second.SettingsPage.Appearance);
            Assert.Equal("Theme: Dark, click to change", second.SettingsPage.ThemeButtonLabel);

            // Choosing in Settings, General (which sets Appearance) moves the button's next step on from there.
            second.SettingsPage.Appearance = AppearanceMode.Light;
            Assert.Equal("Theme: Light, click to change", second.SettingsPage.ThemeButtonLabel);
            second.SettingsPage.CycleAppearance();
            Assert.Equal(AppearanceMode.Dark, second.SettingsPage.Appearance);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_welcome_line_lists_every_place_there_is()
    {
        var all = new MainViewModel(new SampleBackend(), new AppSettingsStore(null));
        Assert.Equal("Home, Clean up, Projects, Health, History and Settings", all.PlacesLine);
    }

    [Fact]
    public void The_window_fits_the_work_area_and_its_minimum_never_exceeds_it()
    {
        // A 1366x768 laptop at 100% (work area 728 high): the old fixed 820 px window ran off the bottom of the screen.
        (PixelRect bounds, int minW, int minH) = WindowSizing.Fit(new PixelRect(0, 0, 1366, 728), 1.0);
        Assert.Equal(new PixelRect(63, 0, 1240, 728), bounds);
        Assert.Equal((1000, 680), (minW, minH));

        // A large screen at 150%: the full size in DIPs, centred.
        (bounds, minW, minH) = WindowSizing.Fit(new PixelRect(0, 0, 3840, 2100), 1.5);
        Assert.Equal(new PixelRect(990, 435, 1860, 1230), bounds);
        Assert.Equal((1500, 1020), (minW, minH));

        // A tiny work area: the minimum shrinks with it rather than forcing the window off screen.
        (bounds, minW, minH) = WindowSizing.Fit(new PixelRect(0, 40, 800, 560), 1.0);
        Assert.Equal(new PixelRect(0, 40, 800, 560), bounds);
        Assert.Equal((800, 560), (minW, minH));
    }
}
